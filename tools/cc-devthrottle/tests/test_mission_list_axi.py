"""Tests for `cc-devthrottle mission list` in the AXI shape (issue #2922, docs/axi-standard.md).

What "done" means for a list command, and what each group below pins:

- Recoverability: every mission's full id, full name and state can be read back EXACTLY from the
  default output, with the same `parse_list` the helper ships. The same check is run against the old
  Rich table, and against a list that shortens names, to prove the check can fail.
- `--json` asks the Gateway exactly what it always asked and prints its answer unchanged when no
  filter is given; `--name` narrows the same bare array without changing its shape.
- An empty answer says `count: 0`, and `count: 0 of N total` when a filter matched nothing.
- An unknown state, an unknown field or an unknown flag exits 2 and lists the valid values.
- A mission with no id, or with a state this tool does not know, fails loudly instead of being listed.
"""

import io
import json
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest
import typer
from rich import box
from rich.console import Console
from rich.table import Table
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import axi_output  # noqa: E402
from cc_shared.axi_output import ListParseError, parse_list  # noqa: E402
from src import mission_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


def _mission(mid, name, state, why=""):
    return {
        "missionId": mid,
        "missionName": name,
        "why": why,
        "whyUpdatedAt": "2026-09-16T11:21:56.6053801+00:00" if why else None,
        "state": state,
        "stateChangedAt": None if state == "active" else "2026-09-10T08:00:00+00:00",
        "workflowRunId": None,
    }


# Every state, with the names that break a naive list: a comma, quotes, non-ASCII, a name far longer
# than any table column, leading whitespace, and a name that is the empty string.
MISSIONS = [
    _mission("aaaaaaaa-1111-4111-8111-000000000001", "URGENT - Recorder must capture all day, no cutoff", "active",
             why="The owner lost a day of audio."),
    _mission("aaaaaaaa-1111-4111-8111-000000000002", 'AXI - "agent-shaped" tools', "active"),
    _mission("aaaaaaaa-1111-4111-8111-000000000003", "S\u00f8ren's caf\u00e9 \u2014 \U0001f680 launch", "complete",
             why="done"),
    _mission("aaaaaaaa-1111-4111-8111-000000000004",
             "Mentor on the Gateway - the weekly mentor report runs inside the Gateway, for every tenant",
             "active", why="Every tenant gets a report."),
    _mission("aaaaaaaa-1111-4111-8111-000000000005", "  padded  ", "removed"),
    _mission("aaaaaaaa-1111-4111-8111-000000000006", "", "active"),
]
ACTIVE = [MISSIONS[i] for i in (0, 1, 3, 5)]


@pytest.fixture
def serve(monkeypatch):
    """Serve missions through the Gateway client the way the Gateway filters them, with no real HTTP.

    Records the state each call asked for, so a test can pin what the Gateway was asked.
    """
    asked = []

    def serve_(missions):
        def fake_list_all(self, state=None):
            asked.append(state)
            if state == "all":
                return list(missions)
            return [m for m in missions if m["state"] == (state or "active")]

        monkeypatch.setattr(mission_ops.MissionClient, "__init__", lambda self, base_url=None: None)
        monkeypatch.setattr(mission_ops.MissionClient, "list_all", fake_list_all)
        return asked

    return serve_


def _check_recoverable(output, missions):
    """Read id, name and state back from `output` and require an exact match with the missions."""
    _, records = parse_list(output, "missions")
    got = [(r["id"], r["name"], r["state"]) for r in records]
    want = [(m["missionId"], m["missionName"], m["state"]) for m in missions]
    assert got == want


def _old_table(missions):
    """The `mission list` table as it was on main before #2922, frozen here as the negative control.

    Rendered the way an agent read it: stdout a pipe, so Rich lays it out at 80 columns.
    """
    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("Id", "Name", "State", "Why"):
        table.add_column(column)
    for m in missions:
        mid = m["missionId"]
        table.add_row(mid.split("-")[0], m["missionName"] or "-", m["state"], m["why"] or "no why set")
    console = Console(width=80, record=True, file=io.StringIO())
    console.print(table)
    return console.export_text()


# ---------------------------------------------------------------------------------------------------
# Recoverability
# ---------------------------------------------------------------------------------------------------


def test_list_missions_DefaultOutput_EveryIdNameAndStateReadBackExactly(serve, capsys):
    serve(MISSIONS)

    mission_ops.list_missions(json_output=False)

    out = capsys.readouterr().out
    assert out.isascii()
    _check_recoverable(out, ACTIVE)
    fields, records = parse_list(out, "missions")
    assert fields == ["id", "name", "state"]
    # Pinned independently: the empty name survives as the empty string, not as no name.
    assert records[-1]["name"] == ""


def test_list_missions_All_EveryMissionInEveryStateReadsBack(serve, capsys):
    serve(MISSIONS)

    mission_ops.list_missions(json_output=False, state="all")

    out = capsys.readouterr().out
    _check_recoverable(out, MISSIONS)
    _, records = parse_list(out, "missions")
    assert [r["state"] for r in records] == ["active", "active", "complete", "active", "removed", "active"]


def test_recoverability_check_OldRichTable_Fails():
    # The proof the check above can fail: the table it replaced cannot be read back at all.
    old = _old_table(MISSIONS)

    with pytest.raises((ListParseError, AssertionError)):
        _check_recoverable(old, MISSIONS)
    # And not merely for want of a header: the table itself loses the facts. Ids were cut to their
    # first group and the long name is wrapped across lines to fit 80 columns.
    assert MISSIONS[0]["missionId"] not in old
    assert MISSIONS[3]["missionName"] not in old


def test_recoverability_check_ListThatShortensNames_Fails():
    records = [
        {"id": m["missionId"], "name": m["missionName"][:6] + "...", "state": m["state"]} for m in MISSIONS
    ]
    shortened = axi_output.render_list("missions", ["id", "name", "state"], records)

    with pytest.raises(AssertionError):
        _check_recoverable(shortened, MISSIONS)


def test_list_missions_Fields_ShowsExactlyTheRequestedFieldsInOrder(serve, capsys):
    serve(MISSIONS)

    mission_ops.list_missions(json_output=False, state="all", fields="why,id,state-changed,run")

    fields, records = parse_list(capsys.readouterr().out, "missions")
    assert fields == ["why", "id", "state-changed", "run"]
    assert records[2] == {
        "why": "done",
        "id": MISSIONS[2]["missionId"],
        "state-changed": "2026-09-10T08:00:00+00:00",
        "run": None,
    }
    assert records[1]["why"] == ""


# ---------------------------------------------------------------------------------------------------
# Counts, empty states, help
# ---------------------------------------------------------------------------------------------------


def test_list_missions_Default_ActiveOnlyCountedAgainstTheTotal(serve, capsys):
    serve(MISSIONS)

    mission_ops.list_missions(json_output=False)

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 4 of 6 total (active 4)"
    help_index = next(i for i, line in enumerate(lines) if line.startswith("help["))
    assert "  cc-devthrottle mission list --all" in lines[help_index:]


def test_list_missions_All_CountsByStateWithNoTotal(serve, capsys):
    serve(MISSIONS)

    mission_ops.list_missions(json_output=False, state="all")

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 6 (active 4, complete 1, removed 1)"
    assert "  cc-devthrottle mission list --all" not in lines


def test_list_missions_Default_FlagsMissionsWithNoWhy(serve, capsys):
    # The Cockpit rule: a mission whose reason nobody wrote down is flagged, not hidden.
    serve(MISSIONS)

    mission_ops.list_missions(json_output=False)

    assert "2 of these missions have no why set." in capsys.readouterr().out.splitlines()


def test_list_missions_WhyField_NoSeparateFlag(serve, capsys):
    serve(MISSIONS)

    mission_ops.list_missions(json_output=False, fields="id,why")

    assert "no why set" not in capsys.readouterr().out


def test_list_missions_NoMissionsAtAll_PrintsCountZero(serve, capsys):
    serve([])

    mission_ops.list_missions(json_output=False, state="all")

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 0"
    assert "missions[0]{id,name,state}:" in lines
    assert "No missions on the Gateway." in lines
    assert "  cc-devthrottle mission create <name>" in lines


def test_list_missions_StateMatchesNothing_SaysWhichListIsEmpty(serve, capsys):
    serve(ACTIVE)

    mission_ops.list_missions(json_output=False, state="removed")

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 0 of 4 total"
    # "No missions" under a filter would read as "you have none at all".
    assert "No missions with state 'removed'." in lines
    assert "  cc-devthrottle mission list --all" in lines


def test_list_missions_NonAsciiNameFilter_IsWrittenAsAscii(serve, capsys):
    serve(MISSIONS)

    mission_ops.list_missions(json_output=False, name="caf\u00e9 x")

    out = capsys.readouterr().out
    assert out.isascii()
    assert out.splitlines()[0] == "count: 0 of 6 total"
    assert "No missions with state 'active' and a name containing 'caf\\u00e9 x'." in out.splitlines()


# ---------------------------------------------------------------------------------------------------
# Filters
# ---------------------------------------------------------------------------------------------------


def test_list_missions_NameFilter_MatchesAnyPartIgnoringCase(serve, capsys):
    serve(MISSIONS)

    mission_ops.list_missions(json_output=False, state="all", name="GATEWAY")

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 1 of 6 total (active 1)"
    _check_recoverable(out, [MISSIONS[3]])


def test_list_missions_StateAndName_BothApply(serve, capsys):
    serve(MISSIONS)

    mission_ops.list_missions(json_output=False, state="complete", name="caf")

    _check_recoverable(capsys.readouterr().out, [MISSIONS[2]])


def test_list_missions_PlainOutput_AsksTheGatewayForEveryMission(serve, capsys):
    asked = serve(MISSIONS)

    mission_ops.list_missions(json_output=False, state="complete")

    # One request, for every state, so the count can say how many the filter left out.
    assert asked == ["all"]


# ---------------------------------------------------------------------------------------------------
# --json keeps its shape
# ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("state", [None, "all", "active", "complete", "removed"])
def test_list_missions_JsonUnfiltered_AsksWhatItAlwaysAskedAndPrintsItUnchanged(serve, capsys, state):
    # Includes a mission whose state this tool does not know: --json prints what the Gateway sent.
    roster = MISSIONS + [_mission("aaaaaaaa-1111-4111-8111-000000000007", "future", "paused")]
    asked = serve(roster)

    mission_ops.list_missions(json_output=True, state=state)

    captured = capsys.readouterr()
    expected = roster if state == "all" else [m for m in roster if m["state"] == (state or "active")]
    assert captured.out == json.dumps(expected, indent=2) + "\n"
    assert captured.err == ""
    assert asked == [state]


def test_list_missions_JsonNameFilter_SameBareArrayNarrowed(serve, capsys):
    serve(MISSIONS)

    mission_ops.list_missions(json_output=True, name="gateway")

    assert capsys.readouterr().out == json.dumps([MISSIONS[3]], indent=2) + "\n"


def test_list_missions_JsonNameFilter_ExcludesMatchesInOtherStates(serve, capsys):
    # "caf" matches only a completed mission; the default active view must not bring it back.
    serve(MISSIONS)

    mission_ops.list_missions(json_output=True, name="caf")

    assert capsys.readouterr().out == "[]\n"


def test_list_missions_JsonStateAndName_OnlyThatMission(serve, capsys):
    serve(MISSIONS)

    mission_ops.list_missions(json_output=True, state="all", name="PADDED")

    assert json.loads(capsys.readouterr().out) == [MISSIONS[4]]


def test_mission_list_Cli_AllAndName_ThroughAPipe(serve):
    serve(MISSIONS)

    result = runner.invoke(app, ["mission", "list", "--all", "--name", "a", "--json"])

    assert result.exit_code == 0
    assert [m["missionId"] for m in json.loads(result.stdout)] == [
        MISSIONS[i]["missionId"] for i in (0, 1, 2, 3, 4)
    ]


@pytest.fixture
def gateway_answers(monkeypatch):
    """Run the REAL MissionClient.list_all against a Gateway whose 200 answer is `body`."""
    def init(self, base_url=None):
        self.base_url = "http://gateway.example"
        self._token = "key"

    monkeypatch.setattr(mission_ops.MissionClient, "__init__", init)
    monkeypatch.setattr(mission_ops.requests, "request", MagicMock(return_value=MagicMock(status_code=200)))

    def answer(body):
        monkeypatch.setattr(mission_ops.gateway, "parse_json_body", lambda resp, base_url: body)

    return answer


NOT_A_LIST = [None, {}, {"missions": []}, "none", 0]


@pytest.mark.parametrize("body", NOT_A_LIST)
@pytest.mark.parametrize("args", [{"json_output": False}, {"json_output": True},
                                  {"json_output": True, "state": "all"},
                                  {"json_output": False, "name": "a"}, {"json_output": True, "name": "a"}])
def test_list_missions_AnswerIsNotAList_ExitsOne(gateway_answers, capsys, body, args):
    # Absent is not empty: an answer with no list of missions never reads as "No missions on the Gateway".
    gateway_answers(body)

    with pytest.raises(typer.Exit) as exc:
        mission_ops.list_missions(**args)

    assert exc.value.exit_code == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert "no list of missions" in captured.err


@pytest.mark.parametrize("body", NOT_A_LIST)
def test_resolve_mission_AnswerIsNotAList_ExitsOne(gateway_answers, capsys, body):
    # The other caller of list_all: a broken answer must not read as "no mission matches".
    gateway_answers(body)

    with pytest.raises(typer.Exit) as exc:
        mission_ops._resolve_mission("anything")

    assert exc.value.exit_code == 1
    captured = capsys.readouterr()
    assert "no list of missions" in captured.out + captured.err
    assert "matches" not in (captured.out + captured.err).lower()


def test_list_all_AnswerIsAList_ReturnsItUnchanged(gateway_answers):
    gateway_answers(MISSIONS)

    assert mission_ops.MissionClient().list_all(state="all") == MISSIONS


def test_list_missions_GatewayError_ExitsOneWithTheSentence(monkeypatch, capsys):
    def fail(self, state=None):
        raise mission_ops.GatewayError("Gateway not reachable at http://gateway.example")

    monkeypatch.setattr(mission_ops.MissionClient, "__init__", lambda self, base_url=None: None)
    monkeypatch.setattr(mission_ops.MissionClient, "list_all", fail)

    for json_output in (True, False):
        with pytest.raises(typer.Exit) as exc:
            mission_ops.list_missions(json_output=json_output)
        assert exc.value.exit_code == 1
        captured = capsys.readouterr()
        assert captured.out == ""
        assert "Gateway not reachable" in captured.err


# ---------------------------------------------------------------------------------------------------
# Broken answers fail loudly
# ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("state, named", [("paused", "paused"), ("Active", "Active"), (None, "missing")])
def test_mission_list_Cli_UnknownOrMissingState_ExitsOneNamingIt(serve, state, named):
    odd = _mission("aaaaaaaa-1111-4111-8111-000000000007", "future", "active")
    if state is None:
        del odd["state"]
    else:
        odd["state"] = state
    serve(MISSIONS + [odd])

    result = runner.invoke(app, ["mission", "list", "--all"])

    assert result.exit_code == 1
    assert named in result.stderr
    assert "aaaaaaaa-1111-4111-8111-000000000007" in result.stderr
    assert result.stdout == ""


@pytest.mark.parametrize("bad_id", [None, "", "   ", 42])
def test_mission_list_Cli_MissionWithNoId_ExitsOne(serve, bad_id):
    orphan = _mission("unused", "orphan", "active")
    orphan["missionId"] = bad_id
    serve(MISSIONS + [orphan])

    result = runner.invoke(app, ["mission", "list"])

    assert result.exit_code == 1
    assert "no mission id (row 7)" in result.stderr
    assert result.stdout == ""


# ---------------------------------------------------------------------------------------------------
# Usage errors exit 2 and list the valid values
# ---------------------------------------------------------------------------------------------------


def test_mission_list_Cli_UnknownState_ExitsTwoListingStates(serve):
    asked = serve(MISSIONS)

    result = runner.invoke(app, ["mission", "list", "--state", "finished"])

    assert result.exit_code == 2
    assert "'finished'" in result.stderr
    assert "active, complete, removed, all" in result.stderr
    assert result.stdout == ""
    assert asked == []


def test_mission_list_Cli_UnknownField_ExitsTwoListingFields(serve):
    serve(MISSIONS)

    result = runner.invoke(app, ["mission", "list", "--fields", "id,owner"])

    assert result.exit_code == 2
    assert "owner" in result.stderr
    assert "id, name, state, why, why-updated, state-changed, run" in result.stderr


def test_mission_list_Cli_FieldsWithJson_ExitsTwo(serve):
    serve(MISSIONS)

    result = runner.invoke(app, ["mission", "list", "--json", "--fields", "id"])

    assert result.exit_code == 2
    assert "--fields does not apply to --json" in result.stderr


def test_mission_list_Cli_UnknownFlag_ExitsTwo(serve):
    serve(MISSIONS)

    result = runner.invoke(app, ["mission", "list", "--status", "active"])

    assert result.exit_code == 2


def test_mission_list_Cli_BlankName_ExitsTwo(serve):
    serve(MISSIONS)

    result = runner.invoke(app, ["mission", "list", "--name", " "])

    assert result.exit_code == 2
    assert "--name needs a value" in result.stderr
