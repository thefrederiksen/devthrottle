#!/usr/bin/env python3
"""Entry point for cc-playwright CLI."""

import sys
from pathlib import Path

if sys.platform == 'win32':
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    if hasattr(sys.stderr, 'reconfigure'):
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')

if getattr(sys, 'frozen', False):
    base_path = Path(sys._MEIPASS)
    sys.path.insert(0, str(base_path))
    sys.path.insert(0, str(base_path / 'src'))
else:
    base_path = Path(__file__).parent
    sys.path.insert(0, str(base_path))
    sys.path.insert(0, str(base_path / 'src'))

from cli import main

if __name__ == "__main__":
    main()
