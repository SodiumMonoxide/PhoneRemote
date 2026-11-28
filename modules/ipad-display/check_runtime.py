"""Check the interpreter and the installed versions before starting either worker."""

import struct
import sys
from importlib import metadata
from pathlib import Path


def main():
    if sys.version_info[:2] != (3, 12) or struct.calcsize("P") != 8 or sys.platform != "win32":
        return 1
    if "--interpreter" in sys.argv:
        return 0
    root = Path(__file__).resolve().parent
    for path in (root / "host" / "requirements.txt", root / "requirements-setup.txt"):
        for line in path.read_text(encoding="utf-8").splitlines():
            name, version = line.split("==")
            try:
                if metadata.version(name) != version:
                    return 1
            except metadata.PackageNotFoundError:
                return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
