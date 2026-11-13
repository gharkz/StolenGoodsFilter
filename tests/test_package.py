import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('stolen_goods_install', ROOT / 'install.py')
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class PackageControls(unittest.TestCase):
    def test_release_metadata(self):
        meta = ET.parse(ROOT / 'package.xml').getroot()
        self.assertEqual(meta.findtext('id'), 'gharkz.stolengoodsfilter')
        self.assertEqual(meta.findtext('version'), '0.23.355')
        self.assertEqual(meta.findtext('visibility'), 'Private')
        text = meta.findtext('description').strip()
        self.assertEqual(text.count('[quote]'), 3)
        self.assertEqual(text.count('[/quote]'), 3)
        english, japanese, chinese = text.split('[/quote]')[:3]
        self.assertIn('Reject Stolen Items', english)
        self.assertNotIn('盗賊ギルド', english)
        self.assertIn('盗賊ギルド', japanese)
        self.assertIn('\n\nSource:', english)
        self.assertIn('\n\nソースコード:', japanese)
        self.assertIn('拒绝赃物', chinese)
        self.assertIn('\n\n源代码:', chinese)
        self.assertIn('他のMODは不要です。', text)

    def test_jpeg_thumbnail(self):
        image = (ROOT / 'preview.jpg').read_bytes()
        self.assertTrue(image.startswith(b'\xff\xd8'))
        self.assertTrue(image.endswith(b'\xff\xd9'))
        self.assertLess(len(image), 1024 * 1024)

    def test_atomic_install_and_backup(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            game = root / 'game'
            target = game / 'Package/StolenGoodsFilter'
            files = {}
            for name in ('StolenGoodsFilter.dll', 'package.xml', 'preview.jpg'):
                source = root / ('source-' + name)
                source.write_bytes((ROOT / name).read_bytes() if name != 'StolenGoodsFilter.dll' else b'test-only-dll')
                files[name] = source
            target.mkdir(parents=True)
            (target / 'preview.jpg').write_bytes(b'previous-thumbnail')
            with patch.multiple(installer, ROOT=root, FILES=files), patch('builtins.print'):
                installer.install(game)
            for name, source in files.items():
                self.assertEqual(source.read_bytes(), (target / name).read_bytes())
            backups = list((root / 'backups').glob('*/preview.jpg'))
            self.assertEqual(len(backups), 1)
            self.assertEqual(backups[0].read_bytes(), b'previous-thumbnail')

    def test_wrong_package_identity_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / 'package.xml'
            manifest.write_text('<Meta><id>other.mod</id></Meta>', encoding='utf-8')
            with patch.dict(installer.FILES, {'package.xml': manifest}), self.assertRaisesRegex(RuntimeError, 'Wrong package identity'):
                installer.install(root / 'game')


if __name__ == '__main__':
    unittest.main()
