#!/usr/bin/env python3
"""Entry point for cc-scrub."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
# tools/, so cc_shared imports as a package.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from src.cli import main_entry

if __name__ == "__main__":
    main_entry()
