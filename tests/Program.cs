using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StolenGoodsFilter;

internal static class Program
{
    private sealed class NonDecaying : Trait { public override int Decay => 0; }
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        passed++; Console.WriteLine("PASS: " + name);
    }
    private static void Main()
    {
        string gameDirectory = Environment.GetEnvironmentVariable("ELIN_DIR");
        if (string.IsNullOrWhiteSpace(gameDirectory))
            throw new InvalidOperationException("Set ELIN_DIR to your Elin installation before running controls.");
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var dir in new[] { Path.Combine(gameDirectory, "Elin_Data", "Managed"), Path.Combine(gameDirectory, "BepInEx", "core") })
            { var path = Path.Combine(dir, name); if (File.Exists(path)) return Assembly.LoadFrom(path); }
            return null;
        };
        Run();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        var data = new Window.SaveData();
        var item = (Thing)FormatterServices.GetUninitializedObject(typeof(Thing));
        item.trait = new NonDecaying();
        Check(!Plugin.Enabled(data) && Plugin.PassEarly(data, item), "default-off preserves ordinary acceptance");
        var ints = data.ints.ToArray();
        Plugin.SetEnabled(data, true);
        Check(!Plugin.PassEarly(data, item), "enabled rejects ordinary item");
        item.isStolen = true;
        Check(Plugin.PassEarly(data, item), "enabled accepts stolen item for further vanilla checks");
        data.onlyRottable = true;
        Check(!Plugin.PassEarly(data, item), "stolen item cannot bypass decay restriction");
        data.onlyRottable = false;
        Check(ints.SequenceEqual(data.ints), "custom setting consumes no vanilla integer/flag slots");
        var resolver = new DefaultContractResolver();
        var contract = resolver.ResolveContract(typeof(Window.SaveData));
        var attach = typeof(Plugin).GetMethod("ResolveContractPostfix", BindingFlags.NonPublic | BindingFlags.Static);
        attach.Invoke(null, new object[] { typeof(Window.SaveData), contract });
        attach.Invoke(null, new object[] { typeof(Window.SaveData), contract });
        Check(((JsonObjectContract)contract).Properties.Count(p => p.PropertyName == Plugin.SettingKey) == 1, "idempotent namespaced save property");
        var settings = new JsonSerializerSettings { ContractResolver = resolver };
        string json = JsonConvert.SerializeObject(data, settings);
        var loaded = JsonConvert.DeserializeObject<Window.SaveData>(json, settings);
        Check(Plugin.Enabled(loaded) && json.Contains(Plugin.SettingKey), "enabled JSON round trip");
        var copied = new Window.SaveData();
        copied.CopyFrom(loaded);
        typeof(Plugin).GetMethod("CopyPostfix", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { copied, loaded });
        Check(Plugin.Enabled(copied), "copy configuration preserves custom setting");
        Plugin.SetEnabled(loaded, false);
        Check(!JsonConvert.SerializeObject(loaded, settings).Contains(Plugin.SettingKey), "disabled setting omitted from saves");
        Check(!Plugin.Enabled(JsonConvert.DeserializeObject<Window.SaveData>("{}", settings)), "old saves default off");
        foreach (var mode in new[] { Plugin.StolenMode.Allow, Plugin.StolenMode.Only, Plugin.StolenMode.Reject })
        {
            Plugin.SetMode(data, mode);
            foreach (bool stolen in new[] { false, true })
            {
                item.isStolen = stolen;
                Check(Plugin.PassEarly(data, item) == (mode == Plugin.StolenMode.Allow || (mode == Plugin.StolenMode.Only ? stolen : !stolen)), "mode acceptance " + mode + "/" + stolen);
                data.onlyRottable = true;
                Check(!Plugin.PassEarly(data, item), "decay remains enforced " + mode);
                data.onlyRottable = false;
            }
            var roundtrip = JsonConvert.DeserializeObject<Window.SaveData>(JsonConvert.SerializeObject(data, settings), settings);
            Check(Plugin.GetMode(roundtrip) == mode, "mode persistence " + mode);
            typeof(Plugin).GetMethod("CopyPostfix", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { copied, data });
            Check(Plugin.GetMode(copied) == mode, "mode copy " + mode);
        }
        var legacy = JsonConvert.DeserializeObject<Window.SaveData>("{\"" + Plugin.SettingKey + "\":true}", settings);
        Check(Plugin.GetMode(legacy) == Plugin.StolenMode.Only, "legacy true migrates to Only");
        Check(Plugin.GetMode(JsonConvert.DeserializeObject<Window.SaveData>("{\"" + Plugin.SettingKey + "\":false}", settings)) == Plugin.StolenMode.Allow, "legacy false stays Allow");
        foreach (var jsonOrder in new[] { "{\"" + Plugin.SettingKey + "\":true,\"" + Plugin.ModeKey + "\":2}", "{\"" + Plugin.ModeKey + "\":2,\"" + Plugin.SettingKey + "\":true}" })
            Check(Plugin.GetMode(JsonConvert.DeserializeObject<Window.SaveData>(jsonOrder, settings)) == Plugin.StolenMode.Reject, "explicit mode overrides legacy regardless of order");
        Plugin.SetMode(data, Plugin.StolenMode.Allow);
        foreach (var mode in new[] { Plugin.StolenMode.Only, Plugin.StolenMode.Reject, Plugin.StolenMode.Allow })
        { Plugin.Cycle(data); Check(Plugin.GetMode(data) == mode, "cycle " + mode); }
        foreach (var lang in new[] { "EN", "JP", "CN" })
            Check(new[] { Plugin.StolenMode.Allow, Plugin.StolenMode.Only, Plugin.StolenMode.Reject }.Select(m => Plugin.ModeLabel(lang, m)).Distinct().Count() == 3, "three localized labels " + lang);
        var allTypes = typeof(Plugin).GetMethod("AllTypes", BindingFlags.NonPublic | BindingFlags.Static);
        var targets = new[] { typeof(ThingContainer), typeof(Zone), typeof(TaskDump) }
            .SelectMany(t => (System.Collections.Generic.IEnumerable<Type>)allTypes.Invoke(null, new object[] { t }))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.Contains("TrySearchContainer") || m.Name.Contains("SearchDest") || m.Name.Contains("ExcludeDump"))
            .Where(m => !m.ContainsGenericParameters && m.GetMethodBody() != null)
            .Where(m => PatchProcessor.GetOriginalInstructions(m).Any(c => c.Calls(AccessTools.PropertyGetter(typeof(Window.SaveData), "onlyRottable")))).ToList();
        Check(targets.Count == 3, "all three native destination eligibility paths located");
        foreach (var method in targets)
        {
            var patched = Plugin.Rewrite(PatchProcessor.GetOriginalInstructions(method), method).ToList();
            Check(patched.Count(c => c.Calls(AccessTools.Method(typeof(Plugin), nameof(Plugin.PassEarly)))) == 1, "early patch: " + method.DeclaringType + "." + method.Name);
        }
        bool rejected = false;
        try { Plugin.Rewrite(new[] { new CodeInstruction(System.Reflection.Emit.OpCodes.Ret) }, targets[0]).ToList(); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "changed/missing native gate refuses patch");
        Console.WriteLine("Controls passed: " + passed);
    }
}
