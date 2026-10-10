#!/usr/bin/env python3
"""Entry point for cc-excel CLI."""

import sys
from pathlib import Path

# Add src to path for PyInstaller compatibility
if getattr(sys, 'frozen', False):
    base_path = Path(sys._MEIPASS)
    sys.path.insert(0, str(base_path))
    sys.path.insert(0, str(base_path / 'src'))
else:
    base_path = Path(__file__).parent
    sys.path.insert(0, str(base_path.parent))  # tools/, so cc_shared imports as a package
    sys.path.insert(0, str(base_path))
    sys.path.insert(0, str(base_path / 'src'))

from cli import tool_main

if __name__ == "__main__":
    tool_main()
