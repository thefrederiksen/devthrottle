"""Print which of the named environment variables are set - the NAMES only, never a value or its length.

Used under `cc-secrets run` to show which variable each entry arrived in. Also reports whether anything
arrived on standard input (a byte count of zero or more than zero, never the bytes).

Usage:  python env_names_probe.py NAME [NAME ...]
"""

import os
import sys

for name in sys.argv[1:]:
    print(f"{name}: {'SET' if os.environ.get(name) else 'not set'}")
stdin_has_data = (not sys.stdin.isatty()) and bool(sys.stdin.read(1)) if sys.stdin is not None else False
print(f"standard input: {'carries data' if stdin_has_data else 'empty'}")
