using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace StolenGoodsFilter
{
    [BepInPlugin(Id, "Stolen Goods Filter", "1.0.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "gharkz.stolengoodsfilter";
        public const string SettingKey = Id + ".onlyStolen";
        private static readonly ConditionalWeakTable<Window.SaveData, Setting> Settings = new ConditionalWeakTable<Window.SaveData, Setting>();
        private sealed class Setting { public bool OnlyStolen; }
        public static int RoutingPatchCount { get; private set; }
        public static bool Installed { get; private set; }

        private void Awake()
        {
            try { Install(); Logger.LogInfo("Stolen Goods Filter active; routing paths=" + RoutingPatchCount); }
            catch (Exception e) { Logger.LogError("Stolen Goods Filter disabled: " + e); }
        }

        public static bool Enabled(Window.SaveData data) => data != null && Settings.TryGetValue(data, out var setting) && setting.OnlyStolen;
        public static void SetEnabled(Window.SaveData data, bool enabled)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (enabled) Settings.GetValue(data, _ => new Setting()).OnlyStolen = true;
            else Settings.Remove(data);
        }

        // Called at the original onlyRottable test, before word/category acceptance.
        // Existing noRotten/ownership/priority tests and all later vanilla tests remain intact.
        public static bool PassEarly(Window.SaveData data, Thing item)
            => (!Enabled(data) || item.isStolen) && (!data.onlyRottable || item.trait.Decay != 0);

        public static void Install()
        {
            if (Installed) return;
            var harmony = new Harmony(Id);
            try
            {
                var targets = new[] { typeof(ThingContainer), typeof(Zone), typeof(TaskDump) }
                    .SelectMany(AllTypes).SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    .Where(m => m.Name.Contains("TrySearchContainer") || m.Name.Contains("SearchDest") || m.Name.Contains("ExcludeDump"))
                    .Where(m => !m.ContainsGenericParameters && m.GetMethodBody() != null && PatchProcessor.GetOriginalInstructions(m).Any(c => c.Calls(OnlyRottable))).ToList();
                // A changed game must not silently leave one distribution route unfiltered.
                if (targets.Count != 3) throw new InvalidOperationException("Expected three native destination-filter paths; found " + targets.Count);
                foreach (var method in targets) Rewrite(PatchProcessor.GetOriginalInstructions(method), method).ToList();
                foreach (var method in targets)
                    harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Plugin), nameof(Rewrite)));
                RoutingPatchCount = targets.Count;
                harmony.Patch(AccessTools.Method(typeof(DefaultContractResolver), nameof(DefaultContractResolver.ResolveContract)),
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(ResolveContractPostfix)));
                harmony.Patch(AccessTools.Method(typeof(Window.SaveData), nameof(Window.SaveData.CopyFrom)),
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(CopyPostfix)));
                harmony.Patch(AccessTools.Method(typeof(UIInventory), nameof(UIInventory.ShowAdvDistribution)),
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(MenuPostfix)));
                Installed = true;
            }
            catch
            {
                harmony.UnpatchSelf();
                RoutingPatchCount = 0;
                throw;
            }
        }

        private static IEnumerable<Type> AllTypes(Type type)
        {
            yield return type;
            foreach (var child in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(AllTypes)) yield return child;
        }

        private static readonly MethodInfo OnlyRottable = AccessTools.PropertyGetter(typeof(Window.SaveData), nameof(Window.SaveData.onlyRottable));
        private static readonly MethodInfo Decay = AccessTools.PropertyGetter(typeof(Trait), nameof(Trait.Decay));

        // Preserve the original data receiver and original branch destination. Replace only
        // "onlyRottable && item.trait.Decay == 0" with the combined early predicate.
        public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.Select(c => new CodeInstruction(c)).ToList();
            int start = code.FindIndex(c => c.Calls(OnlyRottable));
            if (start < 0 || code.Count(c => c.Calls(OnlyRottable)) != 1) throw new InvalidOperationException("Missing/ambiguous decay gate: " + __originalMethod);
            int end = code.FindIndex(start + 1, c => c.Calls(Decay));
            if (end < 0 || end - start > 8 || code[start + 1].opcode != OpCodes.Brfalse_S && code[start + 1].opcode != OpCodes.Brfalse)
                throw new InvalidOperationException("Changed decay gate: " + __originalMethod);
            if (code[end - 1].opcode != OpCodes.Ldfld || !Equals(code[end - 1].operand, AccessTools.Field(typeof(Card), nameof(Card.trait))))
                throw new InvalidOperationException("Changed item receiver: " + __originalMethod);
            var branch = code[end + 1];
            bool successBranch = branch.opcode == OpCodes.Brtrue || branch.opcode == OpCodes.Brtrue_S;
            bool rejectBranch = branch.opcode == OpCodes.Brfalse || branch.opcode == OpCodes.Brfalse_S;
            // Harmony can assign distinct labels to the same target instruction.
            int allowTarget = code.FindIndex(c => c.labels.Contains((Label)code[start + 1].operand));
            int decayTarget = successBranch ? code.FindIndex(c => c.labels.Contains((Label)branch.operand)) : -1;
            if ((!successBranch && !rejectBranch) || allowTarget < 0 || successBranch && allowTarget != decayTarget)
                throw new InvalidOperationException("Changed decay branch: " + __originalMethod);
            if (code.Skip(start + 1).Take(end - start + 1).Any(c => c.labels.Count != 0 || c.blocks.Count != 0))
                throw new InvalidOperationException("Unexpected interior control flow: " + __originalMethod);
            var replacement = code.Skip(start + 2).Take(end - start - 3).Select(c => new CodeInstruction(c)).ToList();
            if (replacement.Count < 1 || replacement.Any(c => c.opcode.FlowControl != FlowControl.Next))
                throw new InvalidOperationException("Unsafe item load: " + __originalMethod);
            replacement[0].labels.AddRange(code[start].labels);
            replacement[0].blocks.AddRange(code[start].blocks);
            replacement.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Plugin), nameof(PassEarly))));
            replacement.Add(new CodeInstruction(successBranch ? OpCodes.Brtrue : OpCodes.Brfalse, branch.operand));
            code.RemoveRange(start, end - start + 2);
            code.InsertRange(start, replacement);
            // Insertion can push unrelated short branches beyond their signed-byte range.
            foreach (var c in code)
            {
                if (c.opcode == OpCodes.Br_S) c.opcode = OpCodes.Br;
                else if (c.opcode == OpCodes.Brtrue_S) c.opcode = OpCodes.Brtrue;
                else if (c.opcode == OpCodes.Brfalse_S) c.opcode = OpCodes.Brfalse;
            }
            return code;
        }

        private static void MenuPostfix(UIInventory __instance, UIContextMenu dis, Window.SaveData data)
        {
            // The PC's pickup menu reuses ShowDistribution, but is not a destination box.
            if (__instance.owner.Container.IsPC) return;
            dis.AddToggle("Only put stolen goods", Enabled(data), value => SetEnabled(data, value));
        }

        private static void CopyPostfix(Window.SaveData __instance, Window.SaveData data) => SetEnabled(__instance, Enabled(data));

        // A genuine namespaced JSON property, not a hashed string/integer flag. The native
        // window data remains unmodified. ResolveContract also handles already-cached contracts.
        private static void ResolveContractPostfix(Type type, JsonContract __result)
        {
            if (type != typeof(Window.SaveData) || !(__result is JsonObjectContract contract)) return;
            lock (contract.Properties)
            {
                if (contract.Properties.GetClosestMatchProperty(SettingKey) != null) return;
                contract.Properties.Add(new JsonProperty
                {
                    PropertyName = SettingKey, PropertyType = typeof(bool), DeclaringType = type,
                    Readable = true, Writable = true, ValueProvider = new SettingValueProvider(),
                    ShouldSerialize = target => Enabled((Window.SaveData)target)
                });
            }
        }

        private sealed class SettingValueProvider : IValueProvider
        {
            public object GetValue(object target) => Enabled((Window.SaveData)target);
            public void SetValue(object target, object value) => SetEnabled((Window.SaveData)target, value is bool enabled && enabled);
        }
    }
}
