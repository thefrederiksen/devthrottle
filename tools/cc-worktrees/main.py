"""Entry point for cc-worktrees. Runs from a checkout: python tools/cc-worktrees/main.py ..."""

import sys
from pathlib import Path

if sys.version_info < (3, 11):
    sys.exit("cc-worktrees needs Python 3.11 or newer.")

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / "src"))
# The shared AXI output helper lives in tools/cc_shared.
sys.path.insert(0, str(HERE.parent))

from cli import tool_main  # noqa: E402

tool_main()
