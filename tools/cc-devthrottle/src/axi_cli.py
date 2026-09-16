"""Next steps and errors for cc-devthrottle commands, in the AXI shape (docs/axi-standard.md).

Two things every command in a group that uses this module does the same way:

- A command that CHANGES something ends its plain output with `help[N]:` lines naming the commands
  an agent would run next (principle 9). A value is written into a help line only when the command's
  own result supplied it and it is safe to paste back into a shell; otherwise the line carries a
  placeholder such as `<schedule-id>`. Nothing is guessed.
- A runtime error - the Gateway refused, a file could not be written, the setup engine failed - goes
  to standard error as `Error: <what failed>`, followed by `help[N]:` lines naming what to run next,
  and the command exits 1 (principle 6). An error without a next step is refused here, so a handler
  cannot be written without one.
- A usage error - a flag or an argument the caller has to fix - is NOT formatted here. `usage_error`
  hands it to `usage_errors.usage_error`, the one formatter for exit 2, which adds the command's
  Usage line, its Valid options and help[1].

The API, in full: `bare` and `quoted` (a value, or a placeholder, for any command-looking line),
`ascii_text` (free text made one line of ASCII), `shown` (a value made safe inside a Rich
`console.print` line), `write_lines` and `print_next` (standard output), `warn` (standard error, no
exit), `fail` (exit 1), `usage_error` (exit 2), `help_for`, `confirm_or_fail`, `confirmed` (a value
read from the Gateway's answer to a change, or exit 1 when the answer does not carry it), `is_cleared`
(its `accept` for a change that clears a value), `CHECK_GATEWAY`
(the next step for a failed Gateway call when nothing more specific is known), and
`FIELDS_WITH_JSON` (the one refusal of `--fields` given with `--json`, used by every list command).

`fail` takes an optional `label` in place of `Error:`, for the few commands whose first word is a
fact of its own: `session stop` says `Not stopped:` or `Outcome unknown:`, because those differ.

Some commands (session, mission, machine) still print their answer through a Rich console, which
this module does not replace. For those, `shown` escapes a value from elsewhere so Rich prints it
rather than reading `[fix] tests` as markup, and `print_next` and `fail` flush standard output first
so the help lines come after what Rich already wrote.

Text reaches the stream through `axi_output.write_blocks`, never through Rich: Rich reads square
brackets in a message as markup and drops them, and wraps long lines at the console width. Anything
that is not printable ASCII is escaped - a control character as much as a non-ASCII one - because a
Gateway message or a user's own value can carry either, and a raw newline would split one result
line into two.

`--json` output never passes through here; a machine format keeps its shape.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path
from typing import Any, Callable, NoReturn, Optional, Sequence

import typer
from rich.markup import escape as _markup_escape

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output  # noqa: E402

from . import usage_errors  # noqa: E402

TOOL = "cc-devthrottle"

#: The next step for a failed Gateway call when nothing more specific is known. Every fleet command
#: goes through the Gateway, and this is the command that says whether this machine can reach it.
CHECK_GATEWAY = f"{TOOL} setup status"

#: The usage error for `--fields` given with `--json`. It says which to keep, because "drop one of
#: them" left an agent to guess: --fields picks a few fields, --json always carries every field.
FIELDS_WITH_JSON = (
    "--fields does not apply to --json, which always carries every field. "
    "Use --fields to show a few fields, or --json to get every field."
)

# An identifier that can be pasted into a shell as it is: no spaces, quotes, or shell characters.
_BARE_ARGUMENT = re.compile(r"[A-Za-z0-9][A-Za-z0-9._:@+-]*")
# Characters a shell still acts on inside double quotes, or that would end the quoting:
#   "   ends the quoting in every shell;
#   $   expands a variable in bash, zsh and PowerShell;
#   `   runs a command in bash and zsh, and is PowerShell's escape character;
#   !   is history expansion in bash and zsh, and delayed expansion in Windows cmd;
#   %   expands a variable in Windows cmd (`"%USERNAME%"` prints the user's name).
# Nothing else printable is read specially inside double quotes by bash, zsh, cmd or PowerShell. A
# backslash is handled separately: a Windows path is full of them, and one is only read specially
# when another backslash follows it or it would escape the closing quote.
_UNSAFE_IN_DOUBLE_QUOTES = set('"$`!%')


def ascii_text(text: str) -> str:
    """`text` as one line of printable ASCII: every other character - a newline, a carriage return,
    an escape sequence, a non-ASCII letter - is escaped with `axi_output.escape_ascii`, and every
    printable ASCII character is left exactly as it was, so a Windows path keeps single backslashes."""
    return "".join(ch if 0x20 <= ord(ch) <= 0x7E else axi_output.escape_ascii(ch) for ch in text)


def bare(value: object, placeholder: str) -> str:
    """`value` when it is a plain identifier that can be pasted into a command, else `placeholder`."""
    if isinstance(value, str) and _BARE_ARGUMENT.fullmatch(value):
        return value
    return placeholder


def quoted(value: object, placeholder: str) -> str:
    """`value` in double quotes when it is printable ASCII a shell leaves alone inside them, else
    `placeholder` in double quotes. For names, which routinely contain spaces."""
    if (
        isinstance(value, str)
        and value
        and value == value.strip()
        and all(0x20 <= ord(ch) <= 0x7E for ch in value)
        and not _UNSAFE_IN_DOUBLE_QUOTES.intersection(value)
        and "\\\\" not in value
        and not value.endswith("\\")
    ):
        return f'"{value}"'
    return f'"{placeholder}"'


def shown(value: object) -> str:
    """A value from elsewhere, made safe to put inside a Rich `console.print` line: ASCII, and with any
    `[` escaped so a name like `[fix] tests` is printed rather than read as markup."""
    return _markup_escape(ascii_text(str(value)))


def write_lines(*lines: str) -> None:
    """Write result sentences to standard output, ASCII only."""
    axi_output.write_blocks(sys.stdout, *(ascii_text(line) for line in lines))


def print_next(commands: Sequence[str]) -> None:
    """End a command's plain output with its `help[N]:` next steps."""
    sys.stdout.flush()
    axi_output.write_blocks(sys.stdout, axi_output.format_help(list(commands)))


def warn(text: str) -> None:
    """A warning that does not stop the command. Standard error, so standard output stays the answer."""
    sys.stdout.flush()
    axi_output.write_blocks(sys.stderr, ascii_text(f"Warning: {text.strip()}"))


def fail(message: str, next_commands: Sequence[str], *, label: str = "Error:") -> NoReturn:
    """Write `Error: <message>` and the next steps to standard error, then exit 1.

    For a runtime failure only; a usage error is `usage_error`. `next_commands` must name at least
    one thing to run: an error an agent cannot act on is the defect this module exists to prevent.
    `label` replaces `Error:` only where the first word is a fact of its own (see the module text).
    """
    if not message.strip() or not [c for c in next_commands if c.strip()]:
        raise ValueError("an error must say what failed and name at least one next step")
    sys.stdout.flush()
    axi_output.write_blocks(
        sys.stderr,
        ascii_text(f"{label} {message.strip()}"),
        axi_output.format_help(list(next_commands)),
    )
    raise typer.Exit(1)


def is_cleared(value: Any) -> bool:
    """An `accept` for `confirmed` on a clearing change: the answer carries the field, and it is null or blank."""
    return value is None or (isinstance(value, str) and not value.strip())


def confirmed(
    answer: Any,
    keys: Sequence[str],
    what: str,
    next_commands: Sequence[str],
    *,
    accept: Optional[Callable[[Any], bool]] = None,
) -> Any:
    """The value the Gateway's answer to a change gives under the first of `keys` - the proof it happened.

    A change is reported from what the Gateway ANSWERED, never from what was asked. An answer that is
    not an object, that lacks every one of `keys`, or whose value `accept` rejects cannot say the change
    happened - `{}` included - so the command exits 1 saying so, rather than printing the requested
    value back as if the Gateway had confirmed it. `what` names the change ("the rename of session X").
    Without `accept`, a missing value, None and a blank string are all unconfirmed.

    ABSENT IS NOT CLEARED. A key the answer does not carry is a missing answer and fails whatever
    `accept` says; a key the answer carries as null reaches `accept` as None. So a clearing change
    passes `accept=is_cleared` and is confirmed only by a field that is there and empty.
    """
    value = None
    present = False
    if isinstance(answer, dict):
        for key in keys:
            if key in answer:
                value, present = answer[key], True
                if value is not None:
                    break
    if accept is None:
        ok = present and value is not None and not (isinstance(value, str) and not value.strip())
    else:
        ok = present and accept(value)
    if not ok:
        shown_value = "nothing" if not present else ("null" if value is None else repr(value))
        fail(
            f"the Gateway's answer to {what} gave {shown_value} for {keys[0]}, so whether it changed "
            "is unknown. Check before trying again.",
            next_commands,
        )
    return value


def usage_error(message: str) -> NoReturn:
    """A flag or an argument is wrong: exit 2 through the one usage-error formatter,
    `usage_errors.usage_error`, which adds the running command's Usage line, Valid options and
    help[1]. Free text in `message` is escaped here, so a caller may quote the value it refused."""
    usage_errors.usage_error(ascii_text(message.strip()))


def help_for(command: str) -> str:
    """The `--help` line for one command, e.g. `help_for("schedule create")`."""
    return f"{TOOL} {command} --help"


def confirm_or_fail(prompt: str, yes: bool, flag_hint: str) -> bool:
    """Ask for confirmation only when a person is at the keyboard.

    AXI principle 6 says never prompt: an agent's standard input is not a terminal, so the old
    `typer.confirm` read end-of-file and printed a bare "Aborted!". Without a terminal the command now
    refuses and names the flag. With one, the person is asked as before; the return value is their
    answer.
    """
    if yes:
        return True
    if not sys.stdin.isatty():
        usage_error(f"this needs confirmation and there is no terminal to ask on. Re-run it with {flag_hint}.")
    return typer.confirm(prompt)

