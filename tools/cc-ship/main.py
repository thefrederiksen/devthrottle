"""Entry point for cc-ship. Runs from a checkout: python tools/cc-ship/main.py ...

Installed from the toolbelt, the console script `cc-ship` runs the same `main` out of the
installed `cc_ship` package. Here the package folder is still called `src`, and every import
inside it is relative, so the two names load identical code.
"""

import sys
from pathlib import Path

if sys.version_info < (3, 11):
    sys.exit("cc-ship needs Python 3.11 or newer (it reads the Codex settings with tomllib).")

sys.path.insert(0, str(Path(__file__).resolve().parent))

from src.cli import main  # noqa: E402

sys.exit(main())
