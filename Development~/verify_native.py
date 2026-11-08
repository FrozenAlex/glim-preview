"""Load the native library and check every Unity bridge export without a GPU."""

import ctypes
from pathlib import Path
import re
import sys


library = ctypes.CDLL(str(Path(sys.argv[1]).resolve()))
bindings = (Path(__file__).resolve().parent.parent / "Editor" / "Bindings.cs").read_text()
exports = re.findall(r"\bextern\s+\w+\s+(app_\w+)\s*\(", bindings)
if not exports:
    raise RuntimeError("No native bridge declarations found")
for name in exports:
    getattr(library, name)
print(f"Loaded {sys.argv[1]}; verified {len(exports)} Unity bridge exports.")
