"""Errors and exit codes. Every outcome has its own code so a script never has to guess."""

from __future__ import annotations

EXIT_OK = 0
EXIT_ERROR = 1
EXIT_USAGE = 2
EXIT_HELD = 3        # the worktree was not returned: it is held, with the reason
EXIT_POOL_FULL = 4   # no free slot and the pool is at its size


class ToolError(Exception):
    def __init__(self, code: str, message: str, help_lines: list[str] | None = None,
                 exit_code: int = EXIT_ERROR, details: dict | None = None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.help_lines = help_lines or []
        self.exit_code = exit_code
        self.details = details or {}
