"""Entry point for cc-ship. Runs from a checkout: python tools/cc-ship/main.py ..."""

import sys
from pathlib import Path

if sys.version_info < (3, 11):
    sys.exit("cc-ship needs Python 3.11 or newer (it reads the Codex settings with tomllib).")

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from cli import main  # noqa: E402

sys.exit(main())
