"""Tests for `cc-devthrottle director list` and `machine list` in the AXI shape (issue #2922).

The same groups `session list` is held to (docs/axi-standard.md):

- Recoverability: every record's full id, full name and state read back EXACTLY from the default
  output with the helper's own `parse_list`. The same check against the old Rich tables fails, which
  proves the check can fail.
- `--json` is byte-for-byte what the Gateway sent when no filter is given, and a filter narrows the
  same bare array.
- An empty answer says `count: 0`, and `count: 0 of N total` when a filter matched nothing.
- An unknown state, field or flag exits 2 and lists the valid values.
- The state is the Gateway's verdict, named in one word; a value this tool does not know, or a
  record the Gateway gave no verdict for, fails loudly with exit 1.
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

from cc_shared import axi_output  # noqa: E402
from cc_shared.axi_output import ListParseError, parse_list  # noqa: E402
from src import machine_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


# ---------------------------------------------------------------------------------------------------
# Fixtures: what the Gateway serves
# ---------------------------------------------------------------------------------------------------


def _director(did, display, machine, **extra):
    row = {
        "directorId": did,
        "pid": 11288,
        "startedAt": "2026-09-16T18:40:42.5024109Z",
        "controlEndpoint": "",
        "machineName": machine,
        "user": "soren",
        "displayName": display,
        "version": "2.4.0",
        "schemaVersion": 1,
        "lastSeen": "2026-09-16T18:57:34.3778335Z",
        "source": "stream",
        "stoppedAtUtc": None,
    }
    row.update(extra)
    return row


# Names that break a naive list: a comma, quotes, non-ASCII, a name far longer than a table column,
# padding (kept exactly), and an unnamed instance (shown by its machine name, as the toolbar does).
DIRECTORS = [
    _director("136af82d-29d5-43bc-9f4b-6783bb1111da", "Laptop, slot 2", "SORENLAPTOP"),
    _director("61640aab-061d-4d2d-a91e-2160d16cec00", 'the "main" one', "SOREN_NORTH"),
    _director("6d4523e2-ed03-4ae6-ac1c-71d00a37bad1", "S\u00f8ren's caf\u00e9 \u2014 \U0001f680", "SOREN_NORTH"),
    _director("4fbad29d-6baa-4cdd-bbee-cef6b0b50978",
              "A very long Director name that no eighty column table could ever show without cutting it",
              "devthrottle-mac-mini"),
    _director("aaaaaaaa-0000-4000-8000-000000000005", "", "devthrottle-mac-mini"),
    _director("aaaaaaaa-0000-4000-8000-000000000006", "  padded  ", "SOREN_NORTH"),
]
DIRECTOR_STATES = ["online", "wobbly", "offline", "stopped", "online", "offline"]
# What each row's name must read back as: the display name exactly, or the machine name when unnamed.
DIRECTOR_NAMES = [
    "Laptop, slot 2",
    'the "main" one',
    "S\u00f8ren's caf\u00e9 \u2014 \U0001f680",
    "A very long Director name that no eighty column table could ever show without cutting it",
    "devthrottle-mac-mini",
    "  padded  ",
]


def _envelope(directors, states):
    return {
        "sessions": [],
        "directors": [
            {"directorId": d["directorId"], "machineName": d["machineName"], "state": st}
            for d, st in zip(directors, states)
        ],
    }


def _launcher(name, **extra):
    row = {
        "machineName": name,
        "pid": 8920,
        "version": "2.4.0+6d8724ca5befe792181b0378167248ad470ab49a",
        "startedAt": "2026-09-16T18:38:35.8410042Z",
        "lastSeenAt": "2026-09-16T18:57:36.2670101Z",
    }
    row.update(extra)
    return row


LAUNCHERS = [
    _launcher("SORENLAPTOP"),
    _launcher("devthrottle-mac-mini", version="2.1.0+db141e2868e6f99e42373a5053a68a378f74ed62"),
    _launcher("S\u00d8REN, the long-named workstation in the back office"),
    _launcher("old-box"),
]
MACHINE_REACH = ["Connected", "NotConnected", "Connected", "NotStreamCapable"]
MACHINE_STATES = ["online", "offline", "online", "too-old"]


def _machines_view(launchers, reaches):
    # The Gateway names machines in its own case-insensitive order; the lookup must not rely on either.
    return {
        "machines": [
            {"machine": row["machineName"].upper(), "reach": reach, "directors": []}
            for row, reach in reversed(list(zip(launchers, reaches)))
        ],
    }


@pytest.fixture
def serve(monkeypatch):
    """Serve chosen Gateway answers by path, with no real HTTP, and record which paths were asked."""
    asked = []

    def serve_(answers):
        def fake_get_json(path):
            asked.append(path)
            if path not in answers:
                raise AssertionError(f"unexpected Gateway path {path!r}")
            return answers[path]

        monkeypatch.setattr(machine_ops.gateway, "get_json", fake_get_json)
        return asked

    return serve_


def _serve_directors(serve, directors=DIRECTORS, states=DIRECTOR_STATES):
    return serve({"directors": directors, "sessions?envelope=true": _envelope(directors, states)})


def _serve_machines(serve, launchers=LAUNCHERS, reaches=MACHINE_REACH):
    return serve({"launchers": launchers, "machines": _machines_view(launchers, reaches)})


def _check_directors_recoverable(output):
    _, records = parse_list(output, "directors")
    assert [(r["id"], r["name"], r["state"]) for r in records] == [
        (d["directorId"], name, st) for d, name, st in zip(DIRECTORS, DIRECTOR_NAMES, DIRECTOR_STATES)
    ]


def _check_machines_recoverable(output):
    _, records = parse_list(output, "machines")
    assert [(r["name"], r["state"]) for r in records] == [
        (m["machineName"], st) for m, st in zip(LAUNCHERS, MACHINE_STATES)
    ]


def _render_80(table):
    """Rendered the way an agent read it: stdout a pipe, so Rich lays it out at 80 columns."""
    console = Console(width=80, record=True, file=io.StringIO())
    console.print(table)
    return console.export_text()


def _old_director_table():
    """The `director list` table as it was on main before #2922, frozen here as the negative control."""
    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    table.add_column("NAME")
    table.add_column("MACHINE")
    table.add_column("DIRECTOR ID", overflow="fold", no_wrap=False)
    table.add_column("VERSION")
    for row in DIRECTORS:
        name = row["displayName"].strip() or row["machineName"]
        table.add_row(name, row["machineName"], row["directorId"], row["version"])
    return _render_80(table) + f"{len(DIRECTORS)} Directors\n"


def _old_machine_table():
    """The `machine list` table as it was on main before #2922, frozen here as the negative control."""
    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("MACHINE", "PORT", "ADDRESS", "VERSION", "LAST SEEN"):
        table.add_column(column)
    for row in LAUNCHERS:
        table.add_row(row["machineName"], "-", "(same machine)", row["version"], "-")
    return _render_80(table) + f"{len(LAUNCHERS)} machines\n"


# ---------------------------------------------------------------------------------------------------
# director list: recoverability
# ---------------------------------------------------------------------------------------------------


def test_list_directors_DefaultOutput_EveryIdNameAndStateReadBackExactly(serve, capsys):
    _serve_directors(serve)

    machine_ops.list_directors(json_output=False)

    out = capsys.readouterr().out
    assert out.isascii()
    _check_directors_recoverable(out)
    fields, records = parse_list(out, "directors")
    assert fields == ["id", "name", "machine", "state"]
    assert [r["machine"] for r in records] == [d["machineName"] for d in DIRECTORS]


def test_director_recoverability_check_OldRichTable_Fails():
    old = _old_director_table()

    with pytest.raises((ListParseError, AssertionError)):
        _check_directors_recoverable(old)
    # And not merely for want of a header: the table itself loses the long name, and carries no state.
    assert DIRECTOR_NAMES[3] not in old
    assert "wobbly" not in old


def test_director_recoverability_check_ListThatShortensNames_Fails():
    records = [
        {"id": d["directorId"], "name": name[:6] + "...", "state": st}
        for d, name, st in zip(DIRECTORS, DIRECTOR_NAMES, DIRECTOR_STATES)
    ]
    shortened = axi_output.render_list("directors", ["id", "name", "state"], records)

    with pytest.raises(AssertionError):
        _check_directors_recoverable(shortened)


def test_list_directors_Fields_ShowsExactlyTheRequestedFieldsInOrder(serve, capsys):
    _serve_directors(serve)

    machine_ops.list_directors(json_output=False, fields="version,id,pid,user,started,last-seen")

    fields, records = parse_list(capsys.readouterr().out, "directors")
    assert fields == ["version", "id", "pid", "user", "started", "last-seen"]
    assert records[0] == {
        "version": "2.4.0",
        "id": DIRECTORS[0]["directorId"],
        "pid": "11288",
        "user": "soren",
        "started": "2026-09-16T18:40:42.5024109Z",
        "last-seen": "2026-09-16T18:57:34.3778335Z",
    }


# ---------------------------------------------------------------------------------------------------
# director list: counts, empty states, help
# ---------------------------------------------------------------------------------------------------


def test_list_directors_Unfiltered_CountsByStateAndHelp(serve, capsys):
    _serve_directors(serve)

    machine_ops.list_directors(json_output=False)

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 6 (online 2, wobbly 1, offline 2, stopped 1)"
    help_index = next(i for i, line in enumerate(lines) if line.startswith("help["))
    assert lines[help_index:] == [
        "help[4]:",
        "  cc-devthrottle director list --state offline",
        "  cc-devthrottle director list --fields id,name,machine,state,version,pid,user,started,last-seen",
        "  cc-devthrottle session spawn <repo> --director <id> --controlled-by self",
        "  cc-devthrottle session list --machine <machine>",
    ]


def test_list_directors_Filtered_CountSaysOfTotal(serve, capsys):
    _serve_directors(serve)

    machine_ops.list_directors(json_output=False, state="offline,stopped", machine="soren_north")

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 2 of 6 total (offline 2)"
    _, records = parse_list(out, "directors")
    assert [r["id"] for r in records] == [DIRECTORS[2]["directorId"], DIRECTORS[5]["directorId"]]


def test_list_directors_NoneRegistered_PrintsCountZero(serve, capsys):
    _serve_directors(serve, [], [])

    machine_ops.list_directors(json_output=False)

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 0"
    assert lines[1] == "directors[0]{id,name,machine,state}:"
    assert lines[2].startswith("No Directors are registered.")


def test_list_directors_FilterMatchesNothing_PrintsCountZeroOfTotal(serve, capsys):
    _serve_directors(serve)

    machine_ops.list_directors(json_output=False, machine="NO_SUCH_MACHINE")

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 0 of 6 total"
    assert "No Director matches the filter." in out.splitlines()
    assert "  cc-devthrottle director list" in out.splitlines()


# ---------------------------------------------------------------------------------------------------
# director list: --json keeps its shape
# ---------------------------------------------------------------------------------------------------


def test_list_directors_JsonUnfiltered_ByteForByteAndNoStateLookup(serve, capsys):
    asked = serve({"directors": DIRECTORS})

    machine_ops.list_directors(json_output=True)

    captured = capsys.readouterr()
    assert captured.out == json.dumps(DIRECTORS, indent=2) + "\n"
    assert captured.err == ""
    assert asked == ["directors"]


def test_list_directors_JsonStateFilter_SameBareArrayNarrowed(serve, capsys):
    _serve_directors(serve)

    machine_ops.list_directors(json_output=True, state="offline")

    assert capsys.readouterr().out == json.dumps([DIRECTORS[2], DIRECTORS[5]], indent=2) + "\n"


def test_list_directors_JsonMachineFilter_MatchesThatMachineOnlyWithoutStateLookup(serve, capsys):
    asked = serve({"directors": DIRECTORS})

    machine_ops.list_directors(json_output=True, machine="DEVTHROTTLE-mac-MINI")

    assert capsys.readouterr().out == json.dumps([DIRECTORS[3], DIRECTORS[4]], indent=2) + "\n"
    assert asked == ["directors"]


def test_list_directors_JsonStateAndMachine_BothApply(serve, capsys):
    # Both SOREN_NORTH rows 2 and 5 are offline and row 1 is wobbly on the same machine; row 3 is
    # stopped elsewhere. Ignoring either filter changes the answer.
    _serve_directors(serve)

    machine_ops.list_directors(json_output=True, state="wobbly,stopped", machine="SOREN_NORTH")

    assert capsys.readouterr().out == json.dumps([DIRECTORS[1]], indent=2) + "\n"


def test_list_directors_JsonFilterMatchesNothing_EmptyArray(serve, capsys):
    _serve_directors(serve)

    machine_ops.list_directors(json_output=True, machine="NO_SUCH_MACHINE")

    assert capsys.readouterr().out == "[]\n"


# ---------------------------------------------------------------------------------------------------
# director list: loud failures
# ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("state, named", [("sleeping", "'sleeping'"), ("Online", "'Online'"), (None, "missing")])
def test_director_list_Cli_UnknownOrMissingState_ExitsOneNamingIt(serve, state, named):
    _serve_directors(serve, DIRECTORS, DIRECTOR_STATES[:-1] + [state])

    result = runner.invoke(app, ["director", "list"])

    assert result.exit_code == 1
    assert named in result.stderr
    assert DIRECTORS[5]["directorId"] in result.stderr
    assert result.stdout == ""


def test_director_list_Cli_DirectorWithNoStateInRoster_ExitsOne(serve):
    envelope = _envelope(DIRECTORS[:-1], DIRECTOR_STATES[:-1])
    serve({"directors": DIRECTORS, "sessions?envelope=true": envelope})

    result = runner.invoke(app, ["director", "list", "--state", "online", "--json"])

    assert result.exit_code == 1
    assert DIRECTORS[5]["directorId"] in result.stderr
    assert result.stdout == ""


@pytest.mark.parametrize("envelope", [[], {"sessions": []}, {"directors": None}])
def test_director_list_Cli_RosterWithNoDirectorStates_ExitsOne(serve, envelope):
    serve({"directors": DIRECTORS, "sessions?envelope=true": envelope})

    result = runner.invoke(app, ["director", "list"])

    assert result.exit_code == 1
    assert "no list of Director states" in result.stderr
    assert result.stdout == ""


@pytest.mark.parametrize("answer", [None, {"directors": []}, "nope"])
def test_director_list_Cli_AnswerNotAList_ExitsOneEvenForJson(serve, answer):
    # Absent is not empty: nothing may claim "no Directors" or print [] for a broken answer.
    serve({"directors": answer})

    for args in (["director", "list"], ["director", "list", "--json"]):
        result = runner.invoke(app, args)
        assert result.exit_code == 1
        assert "was not a list" in result.stderr
        assert result.stdout == ""


@pytest.mark.parametrize("bad", [
    {"machineName": "X", "displayName": "no id"},
    _director("", "blank id", "X"),
    _director("   ", "whitespace id", "X"),
    _director(None, "null id", "X"),
])
def test_director_list_Cli_RowWithNoDirectorId_ExitsOne(serve, bad):
    serve({"directors": DIRECTORS + [bad]})

    result = runner.invoke(app, ["director", "list"])

    assert result.exit_code == 1
    assert "no directorId (row 7)" in result.stderr
    assert result.stdout == ""


@pytest.mark.parametrize("bad", [
    {"directorId": "d1", "displayName": "Unit"},
    _director("d1", "blank machine", ""),
    _director("d1", "whitespace machine", "   "),
    _director("d1", "null machine", None),
])
@pytest.mark.parametrize("args", [
    ["director", "list"],
    ["director", "list", "--json"],
    ["director", "list", "--machine", "BUILD-BOX", "--json"],
    ["director", "list", "--machine", "BUILD-BOX"],
    ["director", "list", "--state", "online", "--json"],
    ["director", "list", "--state", "online"],
])
def test_director_list_Cli_RowWithNoMachineName_ExitsOneOnEveryPath(serve, bad, args):
    # Without the check, --machine silently dropped the row and answered "none" with exit 0.
    directors = DIRECTORS + [bad]
    serve({"directors": directors, "sessions?envelope=true": {
        "sessions": [],
        "directors": _envelope(DIRECTORS, DIRECTOR_STATES)["directors"] + [{"directorId": "d1", "state": "online"}],
    }})

    result = runner.invoke(app, args)

    assert result.exit_code == 1
    assert "no machineName (row 7)" in result.stderr
    assert result.stdout == ""


def test_director_list_Cli_GatewayError_ExitsOneWithAsciiMessage(monkeypatch):
    def fail(path):
        raise machine_ops.gateway.GatewayError("Cannot reach the Gateway at S\u00d8REN")

    monkeypatch.setattr(machine_ops.gateway, "get_json", fail)

    result = runner.invoke(app, ["director", "list"])

    assert result.exit_code == 1
    assert "S\\u00d8REN" in result.stderr
    assert result.stdout == ""


# ---------------------------------------------------------------------------------------------------
# director list: usage errors exit 2 and list the valid values
# ---------------------------------------------------------------------------------------------------


def test_director_list_Cli_UnknownState_ExitsTwoListingStates(serve):
    asked = _serve_directors(serve)

    result = runner.invoke(app, ["director", "list", "--state", "running"])

    assert result.exit_code == 2
    assert "'running'" in result.stderr
    assert "online, wobbly, offline, stopped" in result.stderr
    assert result.stdout == ""
    assert asked == []


def test_director_list_Cli_UnknownField_ExitsTwoListingFields(serve):
    _serve_directors(serve)

    result = runner.invoke(app, ["director", "list", "--fields", "id,colour"])

    assert result.exit_code == 2
    assert "colour" in result.stderr
    assert "id, name, machine, state, version, pid, user, started, last-seen" in result.stderr
    assert result.stdout == ""


def test_director_list_Cli_FieldsWithJson_ExitsTwo(serve):
    _serve_directors(serve)

    result = runner.invoke(app, ["director", "list", "--json", "--fields", "id"])

    assert result.exit_code == 2
    assert "--fields does not apply to --json" in result.stderr


def test_director_list_Cli_EmptyMachine_ExitsTwo(serve):
    _serve_directors(serve)

    result = runner.invoke(app, ["director", "list", "--machine", " "])

    assert result.exit_code == 2
    assert "--machine needs a value" in result.stderr


def test_director_list_Cli_UnknownFlag_ExitsTwo(serve):
    _serve_directors(serve)

    result = runner.invoke(app, ["director", "list", "--status", "online"])

    assert result.exit_code == 2


# ---------------------------------------------------------------------------------------------------
# machine list: recoverability
# ---------------------------------------------------------------------------------------------------


def test_list_machines_DefaultOutput_EveryNameAndStateReadBackExactly(serve, capsys):
    _serve_machines(serve)

    machine_ops.list_machines(json_output=False)

    out = capsys.readouterr().out
    assert out.isascii()
    _check_machines_recoverable(out)
    fields, records = parse_list(out, "machines")
    assert fields == ["name", "state", "version"]
    # Versions are shown in full, build hash and all.
    assert [r["version"] for r in records] == [m["version"] for m in LAUNCHERS]


def test_machine_recoverability_check_OldRichTable_Fails():
    old = _old_machine_table()

    with pytest.raises((ListParseError, AssertionError)):
        _check_machines_recoverable(old)
    # The table carried no state at all, and cut the full version to fit.
    assert "too-old" not in old
    assert LAUNCHERS[0]["version"] not in old


def test_machine_recoverability_check_ListThatShortensNames_Fails():
    records = [{"name": m["machineName"][:6], "state": st} for m, st in zip(LAUNCHERS, MACHINE_STATES)]
    shortened = axi_output.render_list("machines", ["name", "state"], records)

    with pytest.raises(AssertionError):
        _check_machines_recoverable(shortened)


def test_list_machines_Fields_ShowsExactlyTheRequestedFieldsInOrder(serve, capsys):
    _serve_machines(serve)

    machine_ops.list_machines(json_output=False, fields="last-seen,name,pid,started")

    fields, records = parse_list(capsys.readouterr().out, "machines")
    assert fields == ["last-seen", "name", "pid", "started"]
    assert records[1] == {
        "last-seen": "2026-09-16T18:57:36.2670101Z",
        "name": "devthrottle-mac-mini",
        "pid": "8920",
        "started": "2026-09-16T18:38:35.8410042Z",
    }


# ---------------------------------------------------------------------------------------------------
# machine list: counts, empty states, help
# ---------------------------------------------------------------------------------------------------


def test_list_machines_Unfiltered_CountsByStateAndHelp(serve, capsys):
    _serve_machines(serve)

    machine_ops.list_machines(json_output=False)

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 4 (online 2, offline 1, too-old 1)"
    help_index = next(i for i, line in enumerate(lines) if line.startswith("help["))
    assert lines[help_index:] == [
        "help[5]:",
        "  cc-devthrottle machine list --state offline",
        "  cc-devthrottle machine list --fields name,state,version,pid,started,last-seen",
        "  cc-devthrottle director list --machine <name>",
        '  cc-devthrottle machine apps <name> "<query>"',
        "  cc-devthrottle machine restart-capability <name>",
    ]


def test_list_machines_Filtered_CountSaysOfTotal(serve, capsys):
    _serve_machines(serve)

    machine_ops.list_machines(json_output=False, state="offline,too-old")

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 2 of 4 total (offline 1, too-old 1)"
    _, records = parse_list(out, "machines")
    assert [r["name"] for r in records] == ["devthrottle-mac-mini", "old-box"]


def test_list_machines_NoneRegistered_PrintsCountZero(serve, capsys):
    _serve_machines(serve, [], [])

    machine_ops.list_machines(json_output=False)

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 0"
    assert lines[1] == "machines[0]{name,state,version}:"
    assert lines[2].startswith("No machines are registered.")


def test_list_machines_FilterMatchesNothing_PrintsCountZeroOfTotal(serve, capsys):
    _serve_machines(serve, LAUNCHERS[:1], ["Connected"])

    machine_ops.list_machines(json_output=False, state="offline,too-old")

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 0 of 1 total"
    assert "No machine matches the filter." in out.splitlines()


# ---------------------------------------------------------------------------------------------------
# machine list: machines with a Director but no launcher are counted after the list, never listed
# ---------------------------------------------------------------------------------------------------


def _view_with_no_launcher(launchers, reaches, no_launcher_names):
    view = _machines_view(launchers, reaches)
    view["machines"] += [{"machine": name, "reach": "NoLauncher", "directors": []} for name in no_launcher_names]
    return view


NO_LAUNCHER_NAMES = ["BUILD-BOX", "S\u00d8REN's second, very long-named build machine"]
NO_LAUNCHER_LINE = (
    "2 more machines have a Director but no launcher, so they are not listed: "
    "BUILD-BOX, S\\u00d8REN's second, very long-named build machine. See them with: cc-devthrottle director list"
)


def test_machine_list_Cli_NoLauncherMachines_OneLineAfterTheList(serve):
    serve({"launchers": LAUNCHERS, "machines": _view_with_no_launcher(LAUNCHERS, MACHINE_REACH, NO_LAUNCHER_NAMES)})

    result = runner.invoke(app, ["machine", "list"])

    assert result.exit_code == 0
    lines = result.stdout.splitlines()
    assert lines[0] == "count: 4 (online 2, offline 1, too-old 1)"
    _check_machines_recoverable(result.stdout)
    help_index = next(i for i, line in enumerate(lines) if line.startswith("help["))
    assert lines[help_index - 1] == NO_LAUNCHER_LINE
    assert result.stdout.isascii()
    assert "BUILD-BOX" not in [line.split(",")[0].strip() for line in lines[1:help_index - 1]]


def test_machine_list_Cli_OneNoLauncherMachineAndFilter_SingularLine(serve):
    serve({"launchers": LAUNCHERS, "machines": _view_with_no_launcher(LAUNCHERS, MACHINE_REACH, ["BUILD-BOX"])})

    result = runner.invoke(app, ["machine", "list", "--state", "too-old"])

    assert result.exit_code == 0
    assert result.stdout.splitlines()[0] == "count: 1 of 4 total (too-old 1)"
    assert ("1 more machine has a Director but no launcher, so it is not listed: BUILD-BOX. "
            "See them with: cc-devthrottle director list") in result.stdout.splitlines()


def test_machine_list_Cli_OnlyNoLauncherMachines_CountZeroAndTheLine(serve):
    # The inspection's reproduction: no launchers at all, and the Gateway knows of a Director-only machine.
    serve({"launchers": [], "machines": {"machines": [{"machine": "BUILD-BOX", "reach": "NoLauncher"}]}})

    result = runner.invoke(app, ["machine", "list"])

    assert result.exit_code == 0
    lines = result.stdout.splitlines()
    assert lines[0] == "count: 0"
    assert "1 more machine has a Director but no launcher, so it is not listed: BUILD-BOX. " \
        "See them with: cc-devthrottle director list" in lines


def test_machine_list_Cli_NoLauncherMachinesWithJson_ArrayUnchanged(serve):
    serve({"launchers": LAUNCHERS, "machines": _view_with_no_launcher(LAUNCHERS, MACHINE_REACH, NO_LAUNCHER_NAMES)})

    for args, expected in (
        (["machine", "list", "--json"], LAUNCHERS),
        (["machine", "list", "--state", "online", "--json"], [LAUNCHERS[0], LAUNCHERS[2]]),
    ):
        result = runner.invoke(app, args)
        assert result.exit_code == 0
        assert result.stdout == json.dumps(expected, indent=2) + "\n"


def test_machine_list_Cli_NoNoLauncherMachines_NoLine(serve):
    _serve_machines(serve)

    result = runner.invoke(app, ["machine", "list"])

    assert "no launcher" not in result.stdout


# ---------------------------------------------------------------------------------------------------
# machine list: --json keeps its shape
# ---------------------------------------------------------------------------------------------------


def test_list_machines_JsonUnfiltered_ByteForByteAndNoStateLookup(serve, capsys):
    asked = serve({"launchers": LAUNCHERS})

    machine_ops.list_machines(json_output=True)

    captured = capsys.readouterr()
    assert captured.out == json.dumps(LAUNCHERS, indent=2) + "\n"
    assert captured.err == ""
    assert asked == ["launchers"]


def test_list_machines_JsonStateFilter_SameBareArrayNarrowed(serve, capsys):
    _serve_machines(serve)

    machine_ops.list_machines(json_output=True, state="online")

    assert capsys.readouterr().out == json.dumps([LAUNCHERS[0], LAUNCHERS[2]], indent=2) + "\n"


def test_machine_list_Cli_JsonFilterMatchesNothing_EmptyArray(serve):
    _serve_machines(serve, LAUNCHERS[:1], ["Connected"])

    result = runner.invoke(app, ["machine", "list", "--state", "offline", "--json"])

    assert result.exit_code == 0
    assert result.stdout == "[]\n"


# ---------------------------------------------------------------------------------------------------
# machine list: loud failures
# ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("reach, named", [
    ("Sleeping", "'Sleeping'"),
    ("connected", "'connected'"),
    (None, "missing"),
    # A launcher row cannot be a machine with no launcher; the Gateway saying so is a contradiction.
    ("NoLauncher", "'NoLauncher'"),
])
def test_machine_list_Cli_UnknownOrMissingReach_ExitsOneNamingIt(serve, reach, named):
    _serve_machines(serve, LAUNCHERS, MACHINE_REACH[:-1] + [reach])

    result = runner.invoke(app, ["machine", "list"])

    assert result.exit_code == 1
    assert named in result.stderr
    assert "old-box" in result.stderr
    assert result.stdout == ""


def test_machine_list_Cli_MachineMissingFromView_ExitsOne(serve):
    serve({"launchers": LAUNCHERS, "machines": _machines_view(LAUNCHERS[:-1], MACHINE_REACH[:-1])})

    result = runner.invoke(app, ["machine", "list", "--state", "online", "--json"])

    assert result.exit_code == 1
    assert "does not include old-box" in result.stderr
    assert result.stdout == ""


@pytest.mark.parametrize("view", [[], {"highlights": []}, {"machines": "none"}])
def test_machine_list_Cli_ViewWithNoMachines_ExitsOne(serve, view):
    serve({"launchers": LAUNCHERS, "machines": view})

    result = runner.invoke(app, ["machine", "list"])

    assert result.exit_code == 1
    assert "no list of machines" in result.stderr
    assert result.stdout == ""


def test_machine_list_Cli_ViewAnswersWithError_ExitsOne(serve):
    serve({"launchers": LAUNCHERS, "machines": {"error": "the machines view is not available"}})

    result = runner.invoke(app, ["machine", "list"])

    assert result.exit_code == 1
    assert "the machines view is not available" in result.stderr


@pytest.mark.parametrize("answer", [None, {"launchers": []}])
def test_machine_list_Cli_AnswerNotAList_ExitsOneEvenForJson(serve, answer):
    serve({"launchers": answer})

    for args in (["machine", "list"], ["machine", "list", "--json"]):
        result = runner.invoke(app, args)
        assert result.exit_code == 1
        assert "was not a list" in result.stderr
        assert result.stdout == ""


@pytest.mark.parametrize("bad", [{"pid": 1}, _launcher(""), _launcher(None), "SOREN_NORTH"])
def test_machine_list_Cli_RowWithNoName_ExitsOne(serve, bad):
    serve({"launchers": LAUNCHERS + [bad]})

    result = runner.invoke(app, ["machine", "list"])

    assert result.exit_code == 1
    assert "row 5" in result.stderr
    assert result.stdout == ""


# ---------------------------------------------------------------------------------------------------
# machine list: usage errors exit 2 and list the valid values
# ---------------------------------------------------------------------------------------------------


def test_machine_list_Cli_UnknownState_ExitsTwoListingStates(serve):
    asked = _serve_machines(serve)

    result = runner.invoke(app, ["machine", "list", "--state", "online,asleep"])

    assert result.exit_code == 2
    assert "'asleep'" in result.stderr
    assert "Valid states: online, offline, too-old\n" in result.stderr
    assert result.stdout == ""
    assert asked == []


def test_machine_list_Cli_StateNoLauncher_ExitsTwoListingStates(serve):
    # A machine with no launcher is never a row of this list, so it is not a state this list filters on.
    asked = _serve_machines(serve)

    for args in (["machine", "list", "--state", "no-launcher"],
                 ["machine", "list", "--state", "no-launcher", "--json"]):
        result = runner.invoke(app, args)
        assert result.exit_code == 2
        assert "'no-launcher'" in result.stderr
        assert "Valid states: online, offline, too-old\n" in result.stderr
        assert result.stdout == ""
    assert asked == []


def test_machine_list_Cli_UnknownField_ExitsTwoListingFields(serve):
    _serve_machines(serve)

    result = runner.invoke(app, ["machine", "list", "--fields", "name,port"])

    assert result.exit_code == 2
    assert "port" in result.stderr
    assert "name, state, version, pid, started, last-seen" in result.stderr
    assert result.stdout == ""


def test_machine_list_Cli_FieldsWithJson_ExitsTwo(serve):
    _serve_machines(serve)

    result = runner.invoke(app, ["machine", "list", "--json", "--fields", "name"])

    assert result.exit_code == 2
    assert "--fields does not apply to --json" in result.stderr


def test_machine_list_Cli_UnknownFlag_ExitsTwo(serve):
    _serve_machines(serve)

    result = runner.invoke(app, ["machine", "list", "--machine", "old-box"])

    assert result.exit_code == 2
