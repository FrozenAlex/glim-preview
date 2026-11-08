"""Create the VPM archive from freshly built Windows and Linux x64 libraries."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import zipfile


DLL = "Editor/glim_preview_addon_012_v1.dll"
SO = "Editor/glim_preview_addon_012_v1.so"
SO_META = "Development~/glim_preview_addon_012_v1.so.meta"
PACKAGE = "com.frozenalex.glim-preview"


def package(root, native_dll, native_so, output, repository, tag=None):
    manifest = json.loads((root / "package.json").read_text(encoding="utf-8-sig"))
    version = manifest["version"]
    if manifest["name"] != PACKAGE or not re.fullmatch(
        r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?", version
    ):
        raise ValueError("Expected the Glim Preview package and a valid release version")
    expected_tag = f"v{version}"
    if tag and tag != expected_tag:
        raise ValueError(f"Tag {tag} does not match package version {expected_tag}")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Repository must be owner/name")

    native = native_dll.read_bytes()
    if len(native) < 64 or native[:2] != b"MZ":
        raise ValueError("Native library is not a Windows DLL")
    pe = struct.unpack_from("<I", native, 0x3C)[0]
    if (pe + 24 > len(native) or native[pe:pe + 4] != b"PE\0\0"
            or struct.unpack_from("<H", native, pe + 4)[0] != 0x8664
            or not struct.unpack_from("<H", native, pe + 22)[0] & 0x2000):
        raise ValueError("Native library must be a Windows x64 DLL")

    linux = native_so.read_bytes()
    if (len(linux) < 64 or linux[:7] != b"\x7fELF\x02\x01\x01"
            or struct.unpack_from("<HH", linux, 16) != (3, 62)):
        raise ValueError("Native library must be a Linux x64 ELF shared library")
    for name in (DLL + ".meta", SO_META):
        if not (root / name).is_file():
            raise ValueError(f"Missing Unity importer metadata: {name}")

    filename = f"{PACKAGE}-{version}.zip"
    manifest.pop("zipSHA256", None)
    manifest["url"] = f"https://github.com/{repository}/releases/download/{expected_tag}/{filename}"
    files = {p.relative_to(root).as_posix(): p.read_bytes()
             for p in sorted((root / "Editor").rglob("*"))
             if p.is_file() and p.suffix.lower() not in (".pdb", ".dll", ".so", ".dylib")
             and not p.name.endswith(".pdb.meta")}
    for name in ("Editor.meta", "package.json.meta", "README.md", "README.md.meta", "LICENSE.md", "LICENSE.md.meta"):
        files[name] = (root / name).read_bytes()
    files[DLL] = native
    files[SO] = linux
    # Keep the Linux template outside Editor so Unity cannot delete an orphan
    # .meta from a Windows source checkout that has no built Linux binary yet.
    files[SO + ".meta"] = (root / SO_META).read_bytes()
    files["package.json"] = (json.dumps(manifest, indent=2) + "\n").encode()
    notices = root / "Development~" / "licenses"
    for path in sorted(notices.iterdir()):
        if path.is_file():
            files[f"Third Party Notices~/{path.name}"] = path.read_bytes()

    output.mkdir(parents=True, exist_ok=True)
    archive = output / filename
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zip_file:
        for name, data in sorted(files.items()):
            info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            zip_file.writestr(info, data)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    manifest["zipSHA256"] = digest
    (output / "package.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (output / "SHA256SUMS").write_text(f"{digest}  {filename}\n", encoding="utf-8")
    return archive, expected_tag


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--native-dll", type=Path, required=True)
    parser.add_argument("--native-so", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--repository", default="FrozenAlex/glim-preview")
    parser.add_argument("--tag")
    args = parser.parse_args()
    archive, tag = package(args.root, args.native_dll, args.native_so, args.output, args.repository, args.tag)
    print(f"Created {archive}")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write(f"tag={tag}\n")


if __name__ == "__main__":
    main()
