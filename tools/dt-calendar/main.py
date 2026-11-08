"""Entry point for dt-calendar. Runs from a checkout: python tools/dt-calendar/main.py ..."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from src.cli import tool_main  # noqa: E402

if __name__ == "__main__":
    tool_main()
