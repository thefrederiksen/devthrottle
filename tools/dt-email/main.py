"""Entry point for dt-email. Runs from a checkout: python tools/dt-email/main.py ..."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from src.cli import tool_main  # noqa: E402

if __name__ == "__main__":
    tool_main()
