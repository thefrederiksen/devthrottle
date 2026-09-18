"""Packaging entry for the installed tool. The modules in this directory import each other by
plain name (``import pool``), which is how they resolve when the tool is run from a checkout with
``python tools/cc-worktrees/main.py``. Installed, they live inside this package, so put this
directory on the import path before any of them is imported. Nothing else here changes: the
console script calls the same ``cli.main`` the checkout entry point calls.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
