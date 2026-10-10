"""Entry point for cc-docgen executable."""

import sys
from pathlib import Path

# Add this directory to sys.path so direct imports work in frozen exe
_this_dir = str(Path(__file__).resolve().parent)
if _this_dir not in sys.path:
    sys.path.insert(0, _this_dir)
# tools/, so cc_shared imports as a package.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from cli import tool_main  # noqa: E402

if __name__ == "__main__":
    tool_main()
