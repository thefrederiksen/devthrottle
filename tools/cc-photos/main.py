#!/usr/bin/env python3
"""Entry point for cc-photos CLI."""

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
    sys.path.insert(0, str(base_path))
    sys.path.insert(0, str(base_path / 'src'))

# Add cc-vault and cc_shared to path
cc_vault_path = base_path.parent / 'cc-vault'
if cc_vault_path.exists():
    sys.path.insert(0, str(cc_vault_path.parent))

cc_shared_path = base_path.parent / 'cc_shared'
if cc_shared_path.exists():
    sys.path.insert(0, str(cc_shared_path.parent))

# Import after path setup
from cli import app

if __name__ == "__main__":
    app()
