"""Launch a background worker without inheriting PhoneRemote's status pipes."""

import subprocess
import sys
from pathlib import Path


def main():
    log_dir, name, script, *args = sys.argv[1:]
    logs = Path(log_dir)
    with (logs / f"{name}.out.log").open("wb") as output, (logs / f"{name}.err.log").open("wb") as errors:
        process = subprocess.Popen(
            [sys.executable, script, *args],
            cwd=Path(__file__).resolve().parent,
            stdin=subprocess.DEVNULL,
            stdout=output,
            stderr=errors,
            close_fds=True,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
    print(process.pid)


if __name__ == "__main__":
    main()
