"""Put a `cc-dev-reports` launcher on PATH that runs THIS checkout's cc-dev-reports.

Usage: python tools/cc-dev-reports/install.py [--bin DIR]

Point it at a checkout that follows origin/main, never at a working branch. A
dedicated worktree works well:

    git worktree add --detach ../devthrottle-cc-dev-reports-tool origin/main
    python ../devthrottle-cc-dev-reports-tool/tools/cc-dev-reports/install.py

and to update it later:

    git -C ../devthrottle-cc-dev-reports-tool fetch origin
    git -C ../devthrottle-cc-dev-reports-tool checkout --detach origin/main
"""

from __future__ import annotations

import argparse
import importlib.util
import os
import sys
from pathlib import Path

MAIN = Path(__file__).resolve().parent / "main.py"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bin", type=Path, default=Path.home() / ".local" / "bin",
                        help="folder on PATH for the launcher (default: ~/.local/bin)")
    args = parser.parse_args()
    if sys.version_info < (3, 11):
        print("cc-dev-reports needs Python 3.11 or newer; run this installer with that Python.")
        return 1
    if importlib.util.find_spec("typer") is None:
        print(f"cc-dev-reports needs typer in the Python that runs it: {sys.executable} -m pip install typer")
        return 1
    args.bin.mkdir(parents=True, exist_ok=True)
    if os.name == "nt":
        launcher = args.bin / "cc-dev-reports.cmd"
        launcher.write_text(f'@echo off\r\n"{sys.executable}" "{MAIN}" %*\r\n', encoding="ascii")
    else:
        launcher = args.bin / "cc-dev-reports"
        launcher.write_text(f'#!/bin/sh\nexec "{sys.executable}" "{MAIN}" "$@"\n', encoding="ascii")
        launcher.chmod(0o755)
    print(f"Installed {launcher} -> {MAIN}")
    on_path = [Path(p).resolve() for p in os.environ.get("PATH", "").split(os.pathsep) if p]
    if args.bin.resolve() not in on_path:
        print(f"{args.bin} is NOT on PATH. Add it, or sessions will not find cc-dev-reports.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
