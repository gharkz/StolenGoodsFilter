"""Install the built mod, manifest and thumbnail. Never restart or save the game."""
import argparse
import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent
FILES = {'StolenGoodsFilter.dll': ROOT / 'bin/Release/net48/StolenGoodsFilter.dll',
         'package.xml': ROOT / 'package.xml', 'preview.jpg': ROOT / 'preview.jpg'}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def install(game_dir):
    target = Path(game_dir).resolve() / 'Package/StolenGoodsFilter'
    if target.resolve().parent != (Path(game_dir).resolve() / 'Package').resolve():
        raise RuntimeError('Unexpected installation target')
    if ET.parse(FILES['package.xml']).findtext('id') != 'gharkz.stolengoodsfilter':
        raise RuntimeError('Wrong package identity')
    if any(not path.is_file() for path in FILES.values()):
        raise RuntimeError('Build Release with Platform=AnyCPU before installing')
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ')
    backup = ROOT / 'backups' / stamp
    before = {}
    for name in FILES:
        dest = target / name
        before[name] = digest(dest) if dest.exists() else None
        if dest.exists():
            backup.mkdir(parents=True, exist_ok=True)
            shutil.copy2(dest, backup / name)
    target.mkdir(parents=True, exist_ok=True)
    for name, source in FILES.items():
        temporary = target / (name + '.new')
        shutil.copy2(source, temporary)
        if digest(temporary) != digest(source):
            raise RuntimeError('Staged file hash mismatch')
        os.replace(temporary, target / name)
    after = {name: digest(target / name) for name in FILES}
    if after != {name: digest(source) for name, source in FILES.items()}:
        raise RuntimeError('Installed file hash mismatch; inspect before retrying')
    receipt = dict(installed=True, target=str(target), before=before, after=after,
                   backup=str(backup) if backup.exists() else None, restarted_game=False)
    (ROOT / 'install-receipt.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    print(json.dumps(receipt, indent=2))
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game-dir', default=os.environ.get('ELIN_DIR'), type=Path)
    options = parser.parse_args()
    if options.game_dir is None:
        parser.error('Set ELIN_DIR or provide --game-dir pointing to your Elin installation')
    if not (options.game_dir / 'Elin_Data/Managed/Elin.dll').is_file():
        parser.error('The game directory must contain Elin_Data/Managed/Elin.dll')
    install(options.game_dir)


if __name__ == '__main__':
    main()
