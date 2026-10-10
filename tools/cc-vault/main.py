#!/usr/bin/env python3
"""Entry point for cc-vault CLI."""

import sys
from pathlib import Path


def main() -> None:
    """Main entry point for cc-vault CLI."""
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
    from cli import tool_main, _check_search_entity_mistake
    _check_search_entity_mistake()
    tool_main()


if __name__ == "__main__":
    main()
