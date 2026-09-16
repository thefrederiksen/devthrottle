"""Usage errors for every cc-devthrottle command, in one place (issue #2922, docs/axi-standard.md
principle 6).

A usage error - an unknown option, an unknown command, a missing argument, a value of the wrong kind
- is written the same way wherever it happens:

    Error: No such option: --bogus
    Usage: cc-devthrottle session list [OPTIONS]
    Valid options: --json, -j, --state, --repo, --machine, --fields, --help
    help[1]:
      cc-devthrottle session list --help

It goes to standard error as plain ASCII, with no Rich panel around it, and the process exits 2. It
LISTS what would have been valid, so an agent can correct itself in one step instead of reading the
help screen first: the command's options, and for a command group its commands too; and for a value
that must be one of a fixed set, that set.

HOW IT IS APPLIED. `AxiGroup` is the class of the root app and of every sub-app in cli.py. Every
usage error in the tree passes through one of two places on it: `make_context`, where a command's
own arguments are parsed, and `invoke`, which resolves a subcommand and parses ITS arguments. So a
group catches the errors of every command beneath it, and the context the error carries says which
command it was about.

RAISING ONE FROM A COMMAND BODY. A check that only the command itself can make - an unknown
`--fields` name, an unknown `--state` value, two flags that cannot be given together - is still a
usage error, and is written exactly the same way. A command body never prints its own `Error:` line
and exits 2; it calls one of these two, and the group writes the rest:

    usage_errors.usage_error("unknown --state value 'bogus'. Valid states: working, idle")
    chosen = usage_errors.parse_fields(fields, VALID_FIELDS, DEFAULT_FIELDS)

`usage_error` raises `CommandUsageError`, a Click `UsageError` carrying the running command's
context, so the group adds that command's Usage line, its Valid options, and its help[1]. The message
becomes the Error line as it is given: free text in it must already be escaped with
`axi_output.escape_ascii`, and a message that is not one line of ASCII is refused rather than
altered. `parse_fields` is `axi_output.parse_fields` with its FieldsError turned into this usage
error, so every `--fields` answer keeps naming the valid fields. Any cc-devthrottle command - and
any helper that today writes its own usage error, such as `setup_ops` - should use these two.

WHY NOT `import click`. Typer 0.16.1, the declared floor, uses the Click package; later Typer
releases carry their own copy of Click and do not install the package at all. The exception classes
are therefore taken from whichever Click Typer itself is using.
"""

from __future__ import annotations

import importlib
import sys
from pathlib import Path
from typing import Any, List, NoReturn, Optional, Sequence

import typer.core

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output  # noqa: E402

_click = getattr(typer.core, "_click", None) or getattr(typer.core, "click")
_UsageError = _click.exceptions.UsageError
_BadParameter = _click.exceptions.BadParameter
# Typer's own copy of Click does not re-export get_current_context from its package.
_get_current_context = importlib.import_module(_click.__name__ + ".globals").get_current_context


class CommandUsageError(_UsageError):
    """A usage error found by a command body rather than by the parser. Raise it with `usage_error`."""


def usage_error(message: str) -> NoReturn:
    """Stop the running command with a usage error: the group writes `message` as the Error line,
    then the command's Usage line, Valid options and help[1], and exits 2."""
    if "\n" in message or not message.isascii():
        raise ValueError(f"a usage error message must be one line of ASCII: {message!r}")
    raise CommandUsageError(message, ctx=_get_current_context(silent=True))


def parse_fields(requested: Optional[str], valid: Sequence[str], default: Sequence[str]) -> List[str]:
    """`axi_output.parse_fields` for a command: a bad `--fields` value is a usage error naming the
    valid fields."""
    try:
        return axi_output.parse_fields(requested, valid, default)
    except axi_output.FieldsError as exc:
        usage_error(str(exc))


def _is_help_request(error: Exception) -> bool:
    # A group with no_args_is_help raises this to show its help screen. It is not a mistake, and it
    # keeps the handling Typer gives it. Matched by name, as Typer does, because Click before 8.2
    # has no such class.
    return type(error).__name__ == "NoArgsIsHelpError"


def _option_names(ctx: Any) -> List[str]:
    names: List[str] = []
    for param in ctx.command.get_params(ctx):
        if getattr(param, "param_type_name", "") != "option" or getattr(param, "hidden", False):
            continue
        names.extend(param.opts)
        names.extend(param.secondary_opts)
    return names


def _command_names(ctx: Any) -> List[str]:
    group = ctx.command
    names = []
    for name in group.list_commands(ctx):
        command = group.get_command(ctx, name)
        if command is not None and not command.hidden:
            names.append(name)
    return names


def _param_hint(param: Any) -> str:
    if getattr(param, "param_type_name", "") == "option":
        return " / ".join(param.opts)
    return param.human_readable_name


def format_usage_error(error: Any) -> str:
    """The whole text written for a usage error: pure ASCII, one fact per line, newline at the end."""
    param = getattr(error, "param", None)
    if (
        isinstance(error, _BadParameter)
        and error.param_hint is None
        and getattr(param, "param_type_name", "") == "option"
    ):
        # Name the option by its flags. Left to itself, Typer 0.16.1 with Click 8.2 appends
        # "(env var: 'None')" to an option that has no environment variable at all.
        error.param_hint = _param_hint(param)
    message = error.format_message()
    # A command body's message was escaped where it was written (see usage_error); escaping it again
    # would double every backslash in it. The parser's messages carry raw input and are escaped here.
    lines = ["Error: " + (message if isinstance(error, CommandUsageError) else axi_output.escape_ascii(message))]
    ctx = getattr(error, "ctx", None)
    if ctx is not None:
        lines.extend(axi_output.escape_ascii(line) for line in ctx.get_usage().splitlines())
        options = _option_names(ctx)
        if options:
            lines.append("Valid options: " + ", ".join(options))
        if hasattr(ctx.command, "list_commands"):  # a command group, in either Click
            commands = _command_names(ctx)
            lines.append("Valid commands: " + (", ".join(commands) if commands else "none"))
    # A fixed set of values is recognised by its `choices`: the Click package calls the type Choice,
    # and the copy of Click inside later Typer releases has its own class for it.
    choices = getattr(getattr(param, "type", None), "choices", None)
    if isinstance(error, _BadParameter) and choices:
        listed = ", ".join(axi_output.escape_ascii(str(getattr(choice, "value", choice))) for choice in choices)
        lines.append(f"Valid values for {_param_hint(param)}: {listed}")
    if ctx is not None:
        lines.append(axi_output.format_help([axi_output.escape_ascii(f"{ctx.command_path} --help")]))
    return "\n".join(lines) + "\n"


def _args_of(ctx: Any) -> List[str]:
    return [*getattr(ctx, "_protected_args", []), *ctx.args]


def _exit_with_usage_error(error: Any) -> NoReturn:
    sys.stderr.write(format_usage_error(error))
    sys.stderr.flush()
    raise SystemExit(axi_output.USAGE_ERROR_EXIT_CODE)


class AxiGroup(typer.core.TyperGroup):
    """The command-group class for the root app and every sub-app: usage errors are written plainly,
    list the valid values, and exit 2."""

    def make_context(self, info_name: Any, args: Any, parent: Any = None, **extra: Any) -> Any:
        try:
            return super().make_context(info_name, args, parent=parent, **extra)
        except _UsageError as error:
            if _is_help_request(error):
                raise
            if error.ctx is None:
                # The option parser raises some errors ("requires an argument") with no context.
                # The command being parsed is this group, so its context is rebuilt without
                # arguments, only to say which command the error is about.
                error.ctx = super().make_context(info_name, [], parent=parent, resilient_parsing=True)
            _exit_with_usage_error(error)

    def invoke(self, ctx: Any) -> Any:
        # Taken before Click consumes them: they name the subcommand, should its own parse fail.
        pending = _args_of(ctx)
        try:
            return super().invoke(ctx)
        except _UsageError as error:
            if _is_help_request(error):
                raise
            if error.ctx is None and pending:
                # A subcommand's parse failed with no context attached; the subcommand had already
                # been resolved, so resolving it again cannot fail.
                name, command, _ = self.resolve_command(ctx, pending)
                error.ctx = command.make_context(name, [], parent=ctx, resilient_parsing=True)
            _exit_with_usage_error(error)
