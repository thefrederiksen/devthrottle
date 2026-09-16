"""Usage errors across the whole command tree (issue #2922 step 6a, docs/axi-standard.md principle 6).

Every command is found by WALKING the tree the tool actually builds, never from a hand-written list,
so a command added tomorrow is covered the day it is added. For each one:

- an unknown option exits 2 and lists every valid option of THAT command;
- a group given an unknown command exits 2 and lists every command it has;
- a missing required argument, and a value of the wrong kind, exit 2 and list the options;
- the text is plain ASCII on standard error, with no Rich panel around it.

A fixed set of values is covered by a small app of its own, because no shipped option is one yet.

The tests read standard error when the test runner keeps it separate and the combined output when it
does not (Click before 8.2 merges the two on some systems), so they pass with either.
"""

import enum
import sys
from pathlib import Path

import pytest
import typer
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402
from src.usage_errors import AxiGroup  # noqa: E402

runner = CliRunner()
PROG = "cc-devthrottle"
UNKNOWN_FLAG = "--no-such-flag-anywhere"

# Characters Rich draws panels with, in both its Unicode and its ASCII box styles. A plain error
# never starts a line with any of them.
_BOX_CHARACTERS = set("\u2500\u2502\u256d\u256e\u256f\u2570\u250c\u2510\u2514\u2518")


def _walk(command, path):
    yield path, command
    for name, child in getattr(command, "commands", {}).items():
        yield from _walk(child, path + [name])


TREE = list(_walk(typer.main.get_command(app), []))
GROUPS = [(path, command) for path, command in TREE if hasattr(command, "list_commands")]
LEAVES = [(path, command) for path, command in TREE if not hasattr(command, "list_commands")]


def _label(path):
    return " ".join(path) or "(root)"


def _options(command):
    names = []
    for param in command.params:
        if param.param_type_name == "option" and not param.hidden:
            names.extend(param.opts)
            names.extend(param.secondary_opts)
    return names + ["--help"]


def _commands(group):
    return [name for name, child in group.commands.items() if not child.hidden]


def _invoke(args, application=app):
    return runner.invoke(application, args, prog_name=PROG)


def _error_text(result):
    """Standard error, or the combined output where the runner does not keep them apart."""
    try:
        return result.stderr
    except ValueError:
        return result.output


def _stdout_or_none(result):
    try:
        result.stderr
    except ValueError:
        return None
    return result.stdout


def _listed(text, label):
    lines = [line for line in text.splitlines() if line.startswith(label + ": ")]
    assert len(lines) == 1, f"expected exactly one '{label}:' line in:\n{text}"
    return lines[0][len(label) + 2:].split(", ")


def _assert_plain_usage_error(result, path):
    assert result.exit_code == 2, result.output
    assert isinstance(result.exception, SystemExit)
    text = _error_text(result)
    assert text.isascii(), text
    lines = text.splitlines()
    assert lines[0].startswith("Error: "), text
    for line in lines:
        assert not (_BOX_CHARACTERS & set(line)), text
        assert not line.startswith(("+-", "|", " Usage")), text
        assert all(0x20 <= ord(ch) <= 0x7E for ch in line), repr(line)
    assert lines[-2:] == ["help[1]:", "  " + " ".join([PROG, *path, "--help"])], text
    stdout = _stdout_or_none(result)
    if stdout is not None:
        assert stdout == "", stdout
    return text


def test_tree_walk_FindsEveryCommandGroupAndSubcommand():
    # A walk that found nothing would pass every test below without checking anything.
    assert len(GROUPS) >= 17
    assert len(LEAVES) >= 90
    assert ["session", "list"] in [path for path, _ in LEAVES]
    assert ["schedule"] in [path for path, _ in GROUPS]


def test_every_group_UsesTheSharedUsageErrorClass():
    for path, group in GROUPS:
        assert isinstance(group, AxiGroup), _label(path)


@pytest.mark.parametrize("path,command", LEAVES, ids=[_label(p) for p, _ in LEAVES])
def test_every_command_UnknownOption_ExitsTwoAndListsEveryValidOption(path, command):
    result = _invoke([*path, UNKNOWN_FLAG])

    text = _assert_plain_usage_error(result, path)
    assert f"No such option: {UNKNOWN_FLAG}" in text.splitlines()[0]
    assert _listed(text, "Valid options") == _options(command)
    assert "Valid commands" not in text
    assert text.splitlines()[1].startswith(f"Usage: {' '.join([PROG, *path])} ")


@pytest.mark.parametrize("path,group", GROUPS, ids=[_label(p) for p, _ in GROUPS])
def test_every_group_UnknownOption_ExitsTwoAndListsOptionsAndCommands(path, group):
    result = _invoke([*path, UNKNOWN_FLAG])

    text = _assert_plain_usage_error(result, path)
    assert f"No such option: {UNKNOWN_FLAG}" in text.splitlines()[0]
    assert _listed(text, "Valid options") == _options(group)
    assert _listed(text, "Valid commands") == _commands(group)


@pytest.mark.parametrize("path,group", GROUPS, ids=[_label(p) for p, _ in GROUPS])
def test_every_group_UnknownCommand_ExitsTwoAndListsEveryCommand(path, group):
    result = _invoke([*path, "no-such-command"])

    text = _assert_plain_usage_error(result, path)
    assert "No such command 'no-such-command'" in text.splitlines()[0]
    assert _listed(text, "Valid commands") == _commands(group)
    assert _listed(text, "Valid options") == _options(group)


NEEDS_A_COMMAND = [
    (path, group) for path, group in GROUPS if not group.no_args_is_help and not group.invoke_without_command
]


def test_groups_that_need_a_command_AreFound():
    assert [" ".join(path) for path, _ in NEEDS_A_COMMAND] == ["session", "repo", "worktree", "message"]


@pytest.mark.parametrize("path,group", NEEDS_A_COMMAND, ids=[_label(p) for p, _ in NEEDS_A_COMMAND])
def test_every_group_that_needs_a_command_NoCommand_ExitsTwoAndListsEveryCommand(path, group):
    result = _invoke(path)

    text = _assert_plain_usage_error(result, path)
    assert text.splitlines()[0] == "Error: Missing command."
    assert _listed(text, "Valid commands") == _commands(group)


def _required(command):
    return [param for param in command.params if param.required]


WITH_REQUIRED = [(p, c) for p, c in LEAVES if _required(c)]


def test_commands_with_required_values_AreFound():
    assert len(WITH_REQUIRED) >= 50


@pytest.mark.parametrize("path,command", WITH_REQUIRED, ids=[_label(p) for p, _ in WITH_REQUIRED])
def test_every_command_MissingRequiredValue_ExitsTwoAndListsOptions(path, command):
    # Nothing but the command itself, so the first required value is missing. Parsing fails before
    # the command body runs, so nothing is sent anywhere.
    result = _invoke(path)

    text = _assert_plain_usage_error(result, path)
    assert text.splitlines()[0].startswith("Error: Missing "), text
    assert _listed(text, "Valid options") == _options(command)


def _filled(command, skip):
    """Arguments that satisfy every required value except `skip`."""
    args = []
    for param in _required(command):
        if param is skip:
            continue
        if param.param_type_name == "option":
            args += [param.opts[0], "x"]
        else:
            args.append("x")
    return args


def _typed_options(command, type_name):
    return [
        param for param in command.params
        if param.param_type_name == "option" and param.type.name.lower() in type_name
    ]


NUMBER_OPTIONS = [
    (path, command, param)
    for path, command in LEAVES
    for param in _typed_options(command, ("integer", "int"))
]


def test_number_options_AreFound():
    assert len(NUMBER_OPTIONS) >= 10


@pytest.mark.parametrize(
    "path,command,param", NUMBER_OPTIONS, ids=[f"{_label(p)} {o.opts[0]}" for p, _, o in NUMBER_OPTIONS]
)
def test_every_number_option_NotANumber_ExitsTwoAndNamesTheOption(path, command, param):
    result = _invoke([*path, *_filled(command, None), param.opts[0], "not-a-number"])

    text = _assert_plain_usage_error(result, path)
    first = text.splitlines()[0]
    assert first.startswith(f"Error: Invalid value for {' / '.join(param.opts)}: "), text
    assert "not-a-number" in first
    # Typer 0.16.1 with Click 8.2 names an option with no environment variable "(env var: 'None')".
    assert "env var" not in text
    assert _listed(text, "Valid options") == _options(command)


VALUED_OPTIONS = [
    (path, command, param)
    for path, command in LEAVES
    for param in command.params
    if param.param_type_name == "option" and not param.is_flag and not param.hidden
]


@pytest.mark.parametrize(
    "path,command,param", VALUED_OPTIONS, ids=[f"{_label(p)} {o.opts[0]}" for p, _, o in VALUED_OPTIONS]
)
def test_every_valued_option_GivenNoValue_ExitsTwoAndListsOptions(path, command, param):
    # The parser raises this one with no command attached; the listing must still name the command.
    result = _invoke([*path, *_filled(command, param), param.opts[0]])

    text = _assert_plain_usage_error(result, path)
    assert text.splitlines()[0] == f"Error: Option '{param.opts[0]}' requires an argument.", text
    assert _listed(text, "Valid options") == _options(command)


def test_group_option_GivenNoValue_ExitsTwoAndListsTheGroupsCommands():
    result = _invoke(["schedule", "--gateway"])

    text = _assert_plain_usage_error(result, ["schedule"])
    assert _listed(text, "Valid options") == ["--gateway", "--help"]
    assert "list" in _listed(text, "Valid commands")


def test_group_option_OnASubcommand_IsUnknownThere():
    # --gateway belongs to the schedule group, so after the subcommand it is not a valid option and the
    # listing is the subcommand's own.
    result = _invoke(["schedule", "list", "--gateway", "http://x"])

    text = _assert_plain_usage_error(result, ["schedule", "list"])
    assert "--gateway" not in _listed(text, "Valid options")


def test_unknown_option_NonAsciiAndControlCharacters_AreEscaped():
    result = _invoke(["session", "list", "--st\u00e4te\x1b[31m\nx"])

    text = _assert_plain_usage_error(result, ["session", "list"])
    assert "\\u00e4" in text.splitlines()[0]
    assert "\\u001b" in text.splitlines()[0]
    assert "\\n" in text.splitlines()[0]


class _Colour(str, enum.Enum):
    red = "red"
    dark_green = "dark green"


_choices = typer.Typer(cls=AxiGroup, add_completion=False)


@_choices.command("paint")
def _paint(colour: _Colour = typer.Option(_Colour.red, "--colour", "-c")) -> None:
    """Paint."""


@_choices.command("other")
def _other() -> None:
    """Another command, so the group keeps its subcommand name."""


def test_choice_option_BadValue_ExitsTwoAndListsTheValidValues():
    result = _invoke(["paint", "--colour", "blue"], application=_choices)

    text = _assert_plain_usage_error(result, ["paint"])
    assert text.splitlines()[0].startswith("Error: Invalid value for --colour / -c: "), text
    assert _listed(text, "Valid values for --colour / -c") == ["red", "dark green"]
    assert _listed(text, "Valid options") == ["--colour", "-c", "--help"]


def test_choice_option_GoodValue_RunsTheCommand():
    result = _invoke(["paint", "--colour", "dark green"], application=_choices)

    assert result.exit_code == 0, result.output


def test_group_with_no_args_is_help_StillShowsItsHelp():
    # Asking a group with no_args_is_help for nothing is a request for its help, not a mistake.
    result = _invoke(["machine"])

    assert "Error:" not in result.output
    assert "restart-request" in result.output


def test_help_and_version_AreNotUsageErrors():
    assert _invoke(["session", "list", "--help"]).exit_code == 0
    assert _invoke(["--version"]).exit_code == 0


def test_valid_command_StillRuns(monkeypatch):
    # The shared class changes only how a usage error is written; a correct call still runs.
    from src import session_ops

    monkeypatch.setattr(session_ops.gateway, "get_fleet", lambda: ([], True, None, None))
    result = _invoke(["session", "list"])

    assert result.exit_code == 0, result.output
    assert result.output.startswith("count: 0\n")
