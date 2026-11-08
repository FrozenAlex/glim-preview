import hashlib
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile

from package_release import DLL, PACKAGE, package


class ReleasePackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "source"
        self.root.mkdir()
        for name in ("Editor.meta", "package.json.meta", "README.md", "README.md.meta", "LICENSE.md", "LICENSE.md.meta",
                     DLL, DLL + ".meta", "Editor/Preview.cs", "Editor/Preview.cs.meta",
                     "Development~/licenses/MIT.txt", "Development~/.work/secret.txt", ".git/config", ".github/workflows/release.yml"):
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("fixture", encoding="utf-8")
        (self.root / "package.json").write_text(json.dumps({"name": PACKAGE, "version": "0.1.0"}))
        native = bytearray(128)
        native[:2] = b"MZ"
        struct.pack_into("<I", native, 0x3C, 64)
        native[64:68] = b"PE\0\0"
        struct.pack_into("<H", native, 68, 0x8664)
        struct.pack_into("<H", native, 86, 0x2000)
        self.native = Path(self.temp.name) / "fresh.dll"
        self.native.write_bytes(native)
        self.output = Path(self.temp.name) / "dist"

    def build(self, tag="v0.1.0"):
        return package(self.root, self.native, self.output, "FrozenAlex/glim-preview", tag)

    def test_archive_is_installable_and_uses_fresh_binary(self):
        archive, tag = self.build()
        with zipfile.ZipFile(archive) as zip_file:
            names = zip_file.namelist()
            self.assertIn("package.json", names)
            self.assertIn("Editor/Preview.cs.meta", names)
            self.assertIn("Third Party Notices~/MIT.txt", names)
            self.assertFalse(any(n.startswith((".git", "Development~", "dist/")) for n in names))
            self.assertEqual(zip_file.read(DLL), self.native.read_bytes())
            manifest = json.loads(zip_file.read("package.json"))
            self.assertNotIn("zipSHA256", manifest)
            self.assertEqual(manifest["url"], f"https://github.com/FrozenAlex/glim-preview/releases/download/{tag}/{archive.name}")
        self.assertEqual((self.root / DLL).read_text(), "fixture")

    def test_checksum_and_reproducibility(self):
        archive, _ = self.build()
        first = archive.read_bytes()
        checksum = hashlib.sha256(first).hexdigest()
        self.assertEqual(json.loads((self.output / "package.json").read_text())["zipSHA256"], checksum)
        self.assertEqual((self.output / "SHA256SUMS").read_text(), f"{checksum}  {archive.name}\n")
        self.build()
        self.assertEqual(archive.read_bytes(), first)

    def test_rejects_mismatched_tag(self):
        with self.assertRaisesRegex(ValueError, "does not match"):
            self.build("v0.2.0")
        self.assertFalse(self.output.exists())

    def test_rejects_non_dll_and_wrong_architecture(self):
        self.native.write_bytes(b"not a DLL")
        with self.assertRaisesRegex(ValueError, "Windows DLL"):
            self.build()
        native = bytearray(128)
        native[:2] = b"MZ"
        struct.pack_into("<I", native, 0x3C, 64)
        native[64:68] = b"PE\0\0"
        struct.pack_into("<H", native, 68, 0x14C)
        self.native.write_bytes(native)
        with self.assertRaisesRegex(ValueError, "Windows x64"):
            self.build()


if __name__ == "__main__":
    unittest.main()
