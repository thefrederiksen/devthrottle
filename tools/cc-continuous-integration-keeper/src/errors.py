"""Exit codes and the one error type the keeper raises.

The codes follow the AXI standard (docs/axi-standard.md): 0 success, 1 error, 2 usage error, and
one further code per distinct outcome a caller acts on differently.
"""

from __future__ import annotations

EXIT_OK = 0
EXIT_ERROR = 1
EXIT_USAGE = 2
EXIT_RAISED = 3


class KeeperError(Exception):
    """Something the caller must fix: an unreadable file, a budget that does not validate, a
    command that failed. Carries the exit code the command should end with."""

    def __init__(self, message: str, exit_code: int = EXIT_ERROR) -> None:
        super().__init__(message)
        self.exit_code = exit_code
