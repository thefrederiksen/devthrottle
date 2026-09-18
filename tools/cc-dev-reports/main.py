"""Entry point for cc-dev-reports. Runs from a checkout: python tools/cc-dev-reports/main.py ..."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from src.cli import app  # noqa: E402

if __name__ == "__main__":
    app(prog_name="cc-dev-reports")
