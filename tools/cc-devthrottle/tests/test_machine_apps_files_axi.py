"""Tests for `cc-devthrottle machine apps` and `machine files` in the AXI shape (issue #2922).

Both were Rich tables that wrapped a long path across rows, and a path is exactly what a launch needs
pasted back. What each group below pins:

- Recoverability: every name and full path (and every size and time) can be read back EXACTLY from the
  default output with the shared `parse_list`. The same check fails against the old table, frozen here.
- `--json` is the Gateway's answer, unchanged.
- An empty answer says `count: 0`.
"""

import io
import json
import sys
from pathlib import Path

import pytest
from rich import box
from rich.console import Console
from rich.table import Table
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import ListParseError, parse_list  # noqa: E402
from src import machine_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

LONG_PATH = (
    r"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\A Vendor With A Long Name\Their Product, "
    r"Enterprise Edition 2026\Their Product Enterprise Edition.lnk"
)

APPS = {
    "machine": "SOREN_NORTH",
    "apps": [
        {"name": "Their Product, Enterprise Edition", "path": LONG_PATH, "source": "start-menu-machine"},
        {"name": 'The "quoted" app', "path": "/Applications/Quoted.app", "source": "applications"},
        {"name": "Caf\u00e9 \u2014 app", "path": "/Applications/Caf\u00e9.app", "source": "applications"},
    ],
    "totalMatches": 3,
    "truncated": False,
    "skipped": [],
}

FILES = {
    "machine": "SOREN_NORTH",
    "query": "*.pptx",
    "files": [
        {"name": "deck, final.pptx", "path": r"D:\Work\A folder with a long name for the quarterly review\deck, final.pptx",
         "sizeBytes": 123456789, "modifiedUtc": "2026-07-01T10:00:00.1234567Z"},
        {"name": "  spaced.pptx", "path": "/Users/me/  spaced.pptx", "sizeBytes": 0,
         "modifiedUtc": "2026-07-02T10:00:00Z"},
    ],
    "roots": ["C:\\", "D:\\"],
    "directoriesVisited": 900,
    "elapsedMilliseconds": 1500,
    "truncated": False,
    "truncationReason": None,
    "unreadableDirectories": 0,
    "abandonedRoots": 0,
}


@pytest.fixture
def answer(monkeypatch):
    calls = {}

    def fake_get_json(path, timeout=30):
        calls["path"] = path
        return calls["payload"]

    monkeypatch.setattr(machine_ops.gateway, "get_json", fake_get_json)
    return calls


def _check_apps(output, payload):
    _, records = parse_list(output, "apps")
    assert records == [{"name": a["name"], "source": a["source"], "path": a["path"]} for a in payload["apps"]]


def _check_files(output, payload):
    _, records = parse_list(output, "files")
    assert records == [
        {"name": f["name"], "size": str(f["sizeBytes"]), "modified": f["modifiedUtc"], "path": f["path"]}
        for f in payload["files"]
    ]


def _render_old(table):
    console = Console(width=80, record=True, file=io.StringIO())
    console.print(table)
    return console.export_text()


def _old_apps_table(payload):
    """`machine apps` as it was before this change, frozen as the negative control."""
    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("APPLICATION", "SOURCE", "PATH"):
        table.add_column(column)
    for app_row in payload["apps"]:
        table.add_row(app_row["name"], app_row["source"], app_row["path"])
    return _render_old(table)


def _old_files_table(payload):
    """`machine files` as it was before this change, frozen as the negative control."""
    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("FILE", "SIZE", "MODIFIED", "PATH"):
        table.add_column(column)
    for hit in payload["files"]:
        table.add_row(hit["name"], str(hit["sizeBytes"]), hit["modifiedUtc"][:19].replace("T", " "), hit["path"])
    return _render_old(table)


# --- machine apps ---------------------------------------------------------------------------------


def test_machineApps_DefaultOutput_EveryNameSourceAndPathReadBackExactly(answer):
    answer["payload"] = APPS

    result = runner.invoke(app, ["machine", "apps", "SOREN_NORTH"], env={"COLUMNS": "80"})

    assert result.exit_code == 0, result.output
    assert result.stdout.isascii()
    _check_apps(result.stdout, APPS)
    lines = result.stdout.splitlines()
    assert lines[0] == "count: 3"
    assert lines[-4:] == [
        "help[3]:",
        '  cc-devthrottle machine launch SOREN_NORTH --app "<name>"',
        '  cc-devthrottle machine apps SOREN_NORTH "<query>"',
        "  cc-devthrottle machine apps SOREN_NORTH --json",
    ]


def test_machineApps_OldTable_FailsTheSameCheck():
    old = _old_apps_table(APPS)

    with pytest.raises((ListParseError, AssertionError)):
        _check_apps(old, APPS)
    assert LONG_PATH not in old


def test_machineApps_MoreMatchedThanReturned_CountSaysOfTotalAndSaysSo(answer):
    answer["payload"] = dict(APPS, totalMatches=40, truncated=True)

    result = runner.invoke(app, ["machine", "apps", "SOREN_NORTH"])

    assert result.exit_code == 0, result.output
    assert result.stdout.splitlines()[0] == "count: 3 of 40 total"
    assert "More matched than were returned; narrow the search or raise --count." in result.stdout


def test_machineApps_Nothing_PrintsCountZero(answer):
    answer["payload"] = dict(APPS, apps=[], totalMatches=0)

    result = runner.invoke(app, ["machine", "apps", "SOREN_NORTH", "zzz"])

    assert result.exit_code == 0, result.output
    lines = result.stdout.splitlines()
    assert lines[:2] == ["count: 0", "apps[0]{name,source,path}:"]
    assert "Nothing on SOREN_NORTH matches zzz." in lines


def test_machineApps_Fields_ShowsExactlyTheRequestedFieldsInOrder(answer):
    answer["payload"] = APPS

    result = runner.invoke(app, ["machine", "apps", "SOREN_NORTH", "--fields", "path,name"])

    assert result.exit_code == 0, result.output
    fields, records = parse_list(result.stdout, "apps")
    assert fields == ["path", "name"]
    assert records[0] == {"path": LONG_PATH, "name": "Their Product, Enterprise Edition"}
    assert "  cc-devthrottle machine apps SOREN_NORTH --fields name,source,path" in result.stdout.splitlines()


def test_machineApps_Json_TheGatewayAnswerUnchanged(answer):
    answer["payload"] = APPS

    result = runner.invoke(app, ["machine", "apps", "SOREN_NORTH", "--json"])

    assert result.exit_code == 0, result.output
    assert result.stdout == json.dumps(APPS, indent=2) + "\n"


# --- machine files --------------------------------------------------------------------------------


def test_machineFiles_DefaultOutput_EveryNameSizeTimeAndPathReadBackExactly(answer):
    answer["payload"] = FILES

    result = runner.invoke(app, ["machine", "files", "SOREN_NORTH", "*.pptx"], env={"COLUMNS": "80"})

    assert result.exit_code == 0, result.output
    assert result.stdout.isascii()
    _check_files(result.stdout, FILES)
    lines = result.stdout.splitlines()
    assert lines[:2] == ["count: 2", "Searched 900 directories on SOREN_NORTH in 1500 ms."]
    assert lines[-3:] == [
        "help[2]:",
        '  cc-devthrottle machine files SOREN_NORTH "<name>" --seconds <seconds>',
        '  cc-devthrottle machine files SOREN_NORTH "<name>" --json',
    ]


def test_machineFiles_OldTable_FailsTheSameCheck():
    old = _old_files_table(FILES)

    with pytest.raises((ListParseError, AssertionError)):
        _check_files(old, FILES)
    assert FILES["files"][0]["path"] not in old


def test_machineFiles_Nothing_PrintsCountZero(answer):
    answer["payload"] = dict(FILES, files=[])

    result = runner.invoke(app, ["machine", "files", "SOREN_NORTH", "*.pptx"])

    assert result.exit_code == 0, result.output
    lines = result.stdout.splitlines()
    assert lines[0] == "count: 0"
    assert "files[0]{name,size,modified,path}:" in lines
    assert "No file on SOREN_NORTH matches *.pptx." in lines


def test_machineFiles_AbandonedRoots_SaysTheirFilesAreMissing(answer):
    answer["payload"] = dict(FILES, truncated=True, truncationReason="timeout", abandonedRoots=1)

    result = runner.invoke(app, ["machine", "files", "SOREN_NORTH", "*.pptx"])

    assert result.exit_code == 0, result.output
    assert "Search roots given up on because they never answered: 1. Their files are missing from this answer." in result.stdout
    assert "NOT the whole answer" in result.stdout


def test_machineFiles_OlderLauncherWithoutAbandonedRoots_IsStillRead(answer):
    answer["payload"] = {k: v for k, v in FILES.items() if k != "abandonedRoots"}

    result = runner.invoke(app, ["machine", "files", "SOREN_NORTH", "*.pptx"])

    assert result.exit_code == 0, result.output
    _check_files(result.stdout, FILES)


def test_machineFiles_Json_TheGatewayAnswerUnchanged(answer):
    answer["payload"] = FILES

    result = runner.invoke(app, ["machine", "files", "SOREN_NORTH", "*.pptx", "--json"])

    assert result.exit_code == 0, result.output
    assert result.stdout == json.dumps(FILES, indent=2) + "\n"


# --- an empty answer from an incomplete search (re-check 4, finding 2) -----------------------------
#
# "Nothing matches" is a claim about the whole machine. When any part of the search was skipped, cut
# short or abandoned, the machine did not establish it, so the empty note says the search was incomplete
# and why - and the plain no-match sentence is never printed.


@pytest.mark.parametrize("extra, why", [
    ({"skipped": ["C:/Unreadable"]}, "(1 directories could not be read)"),
    ({"totalMatches": 4, "truncated": True}, "(the launcher returned fewer results than it found)"),
    ({"skipped": ["C:/A", "C:/B"], "totalMatches": 4, "truncated": True},
     "(2 directories could not be read; the launcher returned fewer results than it found)"),
])
def test_machineApps_NothingFromAnIncompleteSearch_SaysIncompleteNotNoMatch(answer, extra, why):
    answer["payload"] = {**APPS, "apps": [], "totalMatches": 0, **extra}

    result = runner.invoke(app, ["machine", "apps", "BOX"])

    assert result.exit_code == 0, result.output
    assert "matches (everything)." not in result.stdout
    assert "Nothing on BOX" not in result.stdout
    assert (
        f"No application was returned for (everything) on BOX, but the search was incomplete {why}, "
        "so this does not show that nothing matches."
    ) in result.stdout.splitlines()


@pytest.mark.parametrize("extra, why", [
    ({"truncated": True, "truncationReason": "timeout", "abandonedRoots": 1},
     "(it stopped early (timeout); 1 search roots never answered)"),
    ({"truncated": True, "truncationReason": "limit"}, "(it stopped early (limit))"),
    ({"unreadableDirectories": 3}, "(3 directories could not be read)"),
    ({"abandonedRoots": 2}, "(2 search roots never answered)"),
])
def test_machineFiles_NothingFromAnIncompleteSearch_SaysIncompleteNotNoMatch(answer, extra, why):
    # FILES is a complete search; only `extra` makes this one incomplete.
    answer["payload"] = {**FILES, "files": [], **extra}

    result = runner.invoke(app, ["machine", "files", "BOX", "*.pptx"])

    assert result.exit_code == 0, result.output
    assert "No file on BOX matches" not in result.stdout
    assert (
        f"No file was found for *.pptx on BOX, but the search was incomplete {why}, "
        "so this does not show that no file matches."
    ) in result.stdout.splitlines()


def test_machineFiles_NothingFromACompleteSearchWithZeroAbandoned_SaysNoMatch(answer):
    answer["payload"] = dict(FILES, files=[])
    assert (FILES["truncated"], FILES["unreadableDirectories"], FILES["abandonedRoots"]) == (False, 0, 0)

    result = runner.invoke(app, ["machine", "files", "BOX", "*.pptx"])

    assert result.exit_code == 0, result.output
    assert "No file on BOX matches *.pptx." in result.stdout.splitlines()
    assert "incomplete" not in result.stdout
