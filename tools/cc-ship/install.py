"""Put a `cc-ship` launcher on PATH that runs THIS checkout's cc-ship.

Usage: python tools/cc-ship/install.py [--bin DIR]

Point it at a checkout that follows origin/main, never at a working branch. A
dedicated worktree works well:

    git worktree add --detach ../devthrottle-cc-ship-tool origin/main
    python ../devthrottle-cc-ship-tool/tools/cc-ship/install.py

and to update it later:

    git -C ../devthrottle-cc-ship-tool fetch origin
    git -C ../devthrottle-cc-ship-tool checkout --detach origin/main
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

MAIN = Path(__file__).resolve().parent / "main.py"


def write_launchers(bin_dir: Path, python: Path, main_py: Path, windows: bool) -> list[Path]:
    """A POSIX launcher everywhere; on Windows also a .cmd for PowerShell and cmd.
    Git Bash, the shell sessions use on Windows, runs only the POSIX one."""
    written = []
    if windows:
        cmd = bin_dir / "cc-ship.cmd"
        cmd.write_text(f'@echo off\r\n"{python}" "{main_py}" %*\r\n', encoding="ascii")
        written.append(cmd)
    sh = bin_dir / "cc-ship"
    sh.write_text(f'#!/bin/sh\nexec "{python.as_posix()}" "{main_py.as_posix()}" "$@"\n',
                  encoding="ascii", newline="\n")
    sh.chmod(0o755)
    written.append(sh)
    return written


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bin", type=Path, default=Path.home() / ".local" / "bin",
                        help="folder on PATH for the launcher (default: ~/.local/bin)")
    args = parser.parse_args()
    if sys.version_info < (3, 11):
        print("cc-ship needs Python 3.11 or newer; run this installer with that Python.")
        return 1
    args.bin.mkdir(parents=True, exist_ok=True)
    written = write_launchers(args.bin, Path(sys.executable), MAIN, windows=os.name == "nt")
    for launcher in written:
        print(f"Installed {launcher} -> {MAIN}")
    on_path = [Path(p).resolve() for p in os.environ.get("PATH", "").split(os.pathsep) if p]
    if args.bin.resolve() not in on_path:
        print(f"{args.bin} is NOT on PATH. Add it, or sessions will not find cc-ship.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
