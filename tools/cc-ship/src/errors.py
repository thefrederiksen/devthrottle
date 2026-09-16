"""One error type for everything cc-ship refuses or cannot do.

Every error carries a short machine code, a plain sentence, and the exact next thing
to do. The command line prints all three and exits non-zero.
"""

from __future__ import annotations


class ShipError(Exception):
    def __init__(self, code: str, error: str, help: str, exit_code: int = 1):
        super().__init__(error)
        self.code = code
        self.error = error
        self.help = help
        self.exit_code = exit_code

    def as_dict(self) -> dict:
        return {"error": self.error, "code": self.code, "help": self.help}
