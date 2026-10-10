"""Entry point for cc-history."""

import sys
from pathlib import Path

# tools/, so cc_shared imports as a package when this runs from a checkout.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from src.cli import tool_main  # noqa: E402

if __name__ == "__main__":
    tool_main()
