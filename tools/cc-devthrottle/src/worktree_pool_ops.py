"""The pooled-worktree commands: `cc-devthrottle worktree get`, `return`, `lease`, `destroy`, and
`cc-devthrottle worktree list --pool`.

NONE of these commands does any worktree work of its own. Each one runs the cc-worktrees tool as a
subprocess, hands it the caller's arguments untouched, prints exactly what it printed, and exits with
its exit code. There is no second implementation here and there must never be one: the landed-work
rule - the part that decides whether a worktree can be reset without throwing away work that never
reached the remote - lives in cc-worktrees, in one place. Two implementations of that rule would be
two answers to the same question, and the one that is wrong is the one that deletes a commit.

`cc-devthrottle worktree list` WITHOUT `--pool` is a different thing and is unchanged: it is the
fleet view, every machine's worktrees as the Gateway sees them (`repo_ops.list_worktrees`). `--pool`
asks this machine's own cc-worktrees pool instead.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Sequence

import typer

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output  # noqa: E402

from .setup_ops import DevThrottleInstaller, _venv_script_path  # noqa: E402

TOOL = "cc-worktrees"

# An explicit override for a cc-worktrees that is not the installed one: the path to the executable,
# or to the tool's main.py, which is then run with this interpreter. It is how the tests point at the
# copy in this checkout, and how a developer points at a build of their own.
EXECUTABLE_ENVIRONMENT_VARIABLE = "CC_WORKTREES_EXECUTABLE"

EXIT_ERROR = 1
EXIT_USAGE = 2

INSTALL_HELP = [
    "cc-devthrottle setup update",
    f"{EXECUTABLE_ENVIRONMENT_VARIABLE}=<path to {TOOL} or to its main.py>",
]


class ToolNotFound(Exception):
    """cc-worktrees could not be found. Carries the help lines the caller prints."""

    def __init__(self, message: str, help_lines: Sequence[str]):
        super().__init__(message)
        self.message = message
        self.help_lines = list(help_lines)


def _argv_for(path: Path) -> list[str]:
    """The command that runs cc-worktrees at `path`: a checkout's main.py needs an interpreter."""
    if path.suffix.lower() == ".py":
        return [sys.executable, str(path)]
    return [str(path)]


def resolve_tool() -> list[str]:
    r"""The command that runs cc-worktrees, or ToolNotFound.

    The order is deliberate, and the middle step is the one that matters on Windows. The installer
    puts the real console script at `pyenv\Scripts\cc-worktrees.exe` and puts a `bin\cc-worktrees.cmd`
    shim on PATH (PythonToolsInstaller.BuildWindowsShimBody). A `.cmd` runs through cmd.exe, which
    re-parses the command line: it forwards its arguments as `%*`, so an argument holding `&`, `^` or
    a newline - a holder name, a path, a reason - does not arrive as it was written. Resolving the
    executable itself skips cmd.exe entirely, so the arguments this command was given are the
    arguments cc-worktrees receives.
    """
    override = os.environ.get(EXECUTABLE_ENVIRONMENT_VARIABLE, "").strip()
    if override:
        path = Path(override)
        if not path.exists():
            raise ToolNotFound(
                f"{EXECUTABLE_ENVIRONMENT_VARIABLE} names a file that is not there: {override}",
                [f"{EXECUTABLE_ENVIRONMENT_VARIABLE}=<path to {TOOL} or to its main.py>"],
            )
        return _argv_for(path)

    installed = _venv_script_path(DevThrottleInstaller(), TOOL)
    if installed.exists():
        return [str(installed)]

    found = shutil.which(TOOL)
    if found:
        return [found]

    raise ToolNotFound(
        f"{TOOL} is not installed on this computer. "
        f"cc-devthrottle worktree get, return, lease, destroy and list --pool all run {TOOL}; "
        "this command never does the work itself.",
        INSTALL_HELP,
    )


def _emit_error(as_json: bool, code: str, message: str, help_lines: Sequence[str]) -> None:
    """Report a failure of this wrapper in the shape cc-worktrees reports its own (AXI): on standard
    output, with a code and the commands that would fix it, and as one JSON object under --json."""
    if as_json:
        sys.stdout.write(
            json.dumps({"error": message, "code": code, "help": list(help_lines)}) + "\n"
        )
        return
    blocks = [f"error: {axi_output.escape_ascii(message)}", f"code: {code}"]
    if help_lines:
        blocks.append(axi_output.format_help([axi_output.escape_ascii(line) for line in help_lines]))
    axi_output.write_blocks(sys.stdout, *blocks)


def usage_error(message: str, help_lines: Sequence[str], as_json: bool) -> None:
    """A flag this command cannot honour. Exit 2, the same usage code cc-worktrees uses - never a
    silently ignored filter, which would answer a question the caller did not ask."""
    _emit_error(as_json, "usage", message, help_lines)
    raise typer.Exit(EXIT_USAGE)


def run(args: Sequence[str]) -> int:
    """Run cc-worktrees with `args`, print what it printed, and return its exit code."""
    argv = resolve_tool() + list(args)

    # cc-worktrees writes pure ASCII (its JSON is ensure_ascii, its text goes through
    # axi_output.escape_ascii), but a pipe on Windows otherwise hands a Python child the console code
    # page rather than a known encoding. Naming the encoding on both sides makes the bytes that come
    # back the bytes that were written, which is what makes `worktree list --pool --json` equal to
    # `cc-worktrees list --json` character for character rather than nearly.
    child_environment = dict(os.environ)
    child_environment["PYTHONIOENCODING"] = "utf-8"

    try:
        completed = subprocess.run(
            argv,
            capture_output=True,
            text=True,
            encoding="utf-8",
            # Only so a byte from a child that died below Python's own output layer prints as a
            # marker instead of hiding the whole answer behind a decoding traceback.
            errors="replace",
            env=child_environment,
        )
    except OSError as error:
        raise ToolNotFound(f"{TOOL} could not be started ({argv[0]}): {error}", INSTALL_HELP) from error

    sys.stdout.write(completed.stdout)
    sys.stderr.write(completed.stderr)
    return completed.returncode


def run_pool_command(args: Sequence[str]) -> None:
    """Run one cc-worktrees command and exit with its exit code, unchanged."""
    as_json = "--json" in args
    try:
        code = run(args)
    except ToolNotFound as error:
        _emit_error(as_json, "cc-worktrees-not-found", error.message, error.help_lines)
        raise typer.Exit(EXIT_ERROR) from error
    raise typer.Exit(code)
