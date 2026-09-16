"""The errors cc-secrets raises on purpose.

Only the message of a CcSecretsError is ever shown to the person or agent running the tool, because
cc-secrets wrote that message itself and it never carries a secret or the store's contents. Any other
exception - from the standard library, the browser connection, a malformed store - is shown by its type
name alone: its message, its traceback and its local variables could all hold store content.
"""


class CcSecretsError(Exception):
    """An error with a message written by cc-secrets, safe to show."""


class InputError(CcSecretsError, ValueError):
    """The owner or the agent gave input cc-secrets cannot use."""


class StoreFormatError(CcSecretsError):
    """The store file is not in a form this cc-secrets reads."""
