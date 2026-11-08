"""The one error dt-calendar raises for a failure it can explain."""


class DtCalendarError(Exception):
    """A failure with a sentence that says what went wrong and how to fix it.

    The command line prints it as `Error: <message>` and exits with code 1 (2 for a usage error)."""

    def __init__(self, message: str, exit_code: int = 1):
        super().__init__(message)
        self.message = message
        self.exit_code = exit_code
