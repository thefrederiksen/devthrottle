"""`--version` answers on the tool itself, and it answers the same string as pyproject.toml.

The Director's Tools page attaches a universal "--version responds" check to every tool it lists and
marks the row failed on a non-zero exit. cc-secrets had a `version` command but no `--version` flag, so
the board reported "cc-secrets (version check: exit 2)" on a machine where the tool worked perfectly.
These tests keep the flag, and keep the two ways of asking in step with each other and with the
packaged version.
"""

import re
import sys
from pathlib import Path

from typer.testing import CliRunner

from conftest import TOOL_DIR, run_entry_point
from src import __version__, cli

runner = CliRunner()


def _pyproject_version() -> str:
    text = (TOOL_DIR / "pyproject.toml").read_text(encoding="utf-8")
    match = re.search(r'(?m)^version\s*=\s*"([^"]+)"', text)
    assert match, "pyproject.toml has no project.version"
    return match.group(1)


def test_VersionFlag_OnTheTool_ExitsZeroAndNamesTheVersion():
    result = run_entry_point(["--version"], env={})

    assert result.returncode == 0, result.stderr.decode()
    assert result.stdout.decode().strip() == f"cc-secrets {__version__}"


def test_VersionCommand_PrintsTheSameLineAsTheFlag():
    flag = runner.invoke(cli.app, ["--version"])
    command = runner.invoke(cli.app, ["version"])

    assert flag.exit_code == 0 and command.exit_code == 0
    assert flag.stdout.strip() == command.stdout.strip()


def test_Version_MatchesThePackagedVersion():
    assert __version__ == _pyproject_version()


def test_VersionFlag_OnASubcommand_IsAUsageError():
    """`--version` belongs to the tool, not to its commands. A command that quietly accepted it would
    give a second, drifting answer to the same question."""
    result = runner.invoke(cli.app, ["list", "--version"])

    assert result.exit_code != 0


def test_Help_StillCarriesTheSmokePhraseTheDirectorChecksFor():
    """The manifest's smoke check for cc-secrets is `--help` containing this phrase. Adding a callback
    to the command line is exactly the kind of change that silently replaces the tool's help text."""
    result = run_entry_point(["--help"], env={})

    assert result.returncode == 0, result.stderr.decode()
    assert "without the model ever seeing it" in " ".join(result.stdout.decode().split())
