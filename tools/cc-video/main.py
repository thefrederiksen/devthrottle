#!/usr/bin/env python3
"""Entry point for cc-video CLI."""

import sys
from pathlib import Path

# Add src to path for PyInstaller compatibility
if getattr(sys, 'frozen', False):
    # Running as compiled executable
    base_path = Path(sys._MEIPASS)
    sys.path.insert(0, str(base_path))
    sys.path.insert(0, str(base_path / 'src'))
else:
    # Running as script
    base_path = Path(__file__).parent
    sys.path.insert(0, str(base_path.parent))  # tools/, so cc_shared imports as a package
    sys.path.insert(0, str(base_path))
    sys.path.insert(0, str(base_path / 'src'))

# Import after path setup
from cli import tool_main

if __name__ == "__main__":
    tool_main()
