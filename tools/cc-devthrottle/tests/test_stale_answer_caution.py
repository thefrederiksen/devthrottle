"""The negative-answer caution reaches EVERY fleet tool, not just the one it was wired to first.

Inspection 3, finding 1. The Gateway folds a second, narrower caution - "a machine is connected but
has not reported recently, so something that started there may not be in this list yet" - and the
Control API puts it on the wire as `rosterStaleAnswerCaution`. It was then read by a separate opt-in
helper that only `cc-history` called, so `cc-devthrottle message send`, `message ask`, rename, done,
`session list` and `cc-status` went on printing "no session matches" and "no sessions are running"
with no idea a stale machine might be hiding the answer. The contract advertised a caution most
consumers never received, which is worse than not having added it: the fold looks done.

What these tests pin, in the two directions that matter:

  * the caution IS printed wherever a fleet tool's own answer came back empty, and
  * it is NOT printed when the lookup found what it asked for - the whole reason it is a separate
    field rather than part of the completeness reason. A caution on the most-run command in the tool
    trains the reader to skip it, and then it is not read on the day it matters.

WHY cc-status AND cc-history ARE TESTED FROM HERE. Neither tool has a test directory, and the
continuous integration job runs only this one (`tools/cc-devthrottle/tests`). Putting their tests
somewhere nothing executes would be decoration. They are loaded by path below, under their own
module names, so the same monkeypatched `cc_shared.gateway` serves all three tools.
"""

import importlib.util
import sys
import types
from pathlib import Path

import pytest
import typer

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import gateway  # noqa: E402
from src import session_ops  # noqa: E402

TOOLS = Path(__file__).resolve().parent.parent.parent

SESSION_ID = "11111111-2222-3333-4444-555555555555"
STALE_CAUTION = ("1 machine is connected but has not reported recently, so something that started "
                 "there may not be in this list yet - MACHINE_B, last reported 4m ago")
OFFLINE_REASON = ("1 Director could not be reached, so its sessions below are the last it reported "
                  "and may be out of date - MACHINE_C, last seen 9m ago")


def _load_tool_cli(tool: str, module_name: str) -> types.ModuleType:
    """Import <tool>/src/cli.py as `module_name.cli`, keeping its relative import working.

    The three tools each call their package `src`, so they cannot simply be imported by name from
    one test run - the second would collide with the first in sys.modules. Loading by path under a
    distinct name avoids that, and the package module has to exist first because cli.py does
    `from . import __version__`.
    """
    loaded = sys.modules.get(f"{module_name}.cli")
    if loaded is not None:
        return loaded

    src = TOOLS / tool / "src"
    pkg_spec = importlib.util.spec_from_file_location(
        module_name, src / "__init__.py", submodule_search_locations=[str(src)]
    )
    pkg = importlib.util.module_from_spec(pkg_spec)
    sys.modules[module_name] = pkg
    pkg_spec.loader.exec_module(pkg)

    cli_spec = importlib.util.spec_from_file_location(f"{module_name}.cli", src / "cli.py")
    cli = importlib.util.module_from_spec(cli_spec)
    sys.modules[f"{module_name}.cli"] = cli
    cli_spec.loader.exec_module(cli)
    return cli


status_cli = _load_tool_cli("cc-status", "cc_status_pkg")
history_cli = _load_tool_cli("cc-history", "cc_history_pkg")


def _row():
    return {"sessionId": SESSION_ID, "name": "worker", "machineName": "MACHINE_A",
            "repoPath": r"D:\repo", "activityState": "Working", "triageBucket": "active"}


@pytest.fixture
def wire(monkeypatch):
    """Serve a chosen envelope to EVERY tool at once, through the one shared fetch."""

    def serve(sessions, complete=True, reason=None, stale=None):
        monkeypatch.delenv("CC_SESSION_ID", raising=False)
        monkeypatch.setattr(gateway, "get_fleet", lambda: (sessions, complete, reason, stale))

    return serve


# ===== the transport: one fetch, and it carries the fourth field =====

def test_the_one_fetch_reads_the_stale_answer_caution(monkeypatch):
    monkeypatch.setattr(gateway, "get_json", lambda path: {
        "sessions": [],
        "rosterComplete": True,
        "rosterStaleAnswerCaution": STALE_CAUTION,
    })

    sessions, complete, reason, stale = gateway.get_fleet()

    assert sessions == []
    assert complete is True
    assert reason is None
    assert stale == STALE_CAUTION


def test_a_blank_caution_is_reported_as_absent(monkeypatch):
    # A whitespace string is not a sentence, and printing an empty yellow line is a caution that
    # says nothing while looking like a warning.
    monkeypatch.setattr(gateway, "get_json", lambda path: {
        "sessions": [], "rosterComplete": True, "rosterStaleAnswerCaution": "   ",
    })

    _, _, _, stale = gateway.get_fleet()

    assert stale is None


def test_an_older_director_serving_a_bare_array_yields_no_caution(monkeypatch):
    monkeypatch.setattr(gateway, "get_json", lambda path: [_row()])

    sessions, complete, reason, stale = gateway.get_fleet()

    assert len(sessions) == 1
    assert (complete, reason, stale) == (None, None, None)


def test_there_is_exactly_one_roster_fetch_to_call():
    """The anti-drift pin, and the reason this finding existed at all.

    A SECOND fetch carrying the newer field is what let most tools keep the old answer while the
    contract advertised the new one. If someone adds `get_fleet_with_<anything>` again, this fails
    and points at this docstring rather than at a bug report six weeks later.
    """
    fetchers = [name for name in dir(gateway) if name.startswith("get_fleet")]

    assert fetchers == ["get_fleet"], f"more than one roster fetch exists: {fetchers}"


# ===== cc-devthrottle: every target-resolving verb comes through _resolve_target =====

def test_resolve_failure_prints_the_stale_caution(wire, capsys, plain):
    wire([], complete=True, stale=STALE_CAUTION)

    with pytest.raises(typer.Exit):
        session_ops._resolve_target("abc123", command_name="cc-devthrottle message send")

    out = plain(capsys.readouterr().out)
    assert "No session matches" in out
    assert "connected but has not reported recently" in out
    assert "MACHINE_B" in out


def test_a_successful_resolve_stays_silent_about_staleness(wire, capsys, plain):
    # The scope of the caution IS the behaviour. A lookup that found its target was plainly not
    # hidden from, so a caution here is noise - and noise is what stops the real one being read.
    wire([_row()], complete=True, stale=STALE_CAUTION)

    chosen = session_ops._resolve_target(SESSION_ID, command_name="cc-devthrottle message send")

    assert chosen["sessionId"] == SESSION_ID
    assert "connected but has not reported recently" not in plain(capsys.readouterr().out)


def test_an_empty_session_list_prints_the_stale_caution(wire, capsys, plain):
    wire([], complete=True, stale=STALE_CAUTION)

    session_ops.list_sessions(json_output=False)

    out = plain(capsys.readouterr().out)
    assert "connected but has not reported recently" in out
    # The false sentence this replaces: a claim about the whole fleet that the list cannot support.
    assert "No sessions are running in the fleet" not in out


def test_a_populated_session_list_does_not_print_the_stale_caution(wire, capsys, plain):
    wire([_row()], complete=True, stale=STALE_CAUTION)

    session_ops.list_sessions(json_output=False)

    assert "connected but has not reported recently" not in plain(capsys.readouterr().out)


def test_both_cautions_survive_together_on_an_empty_answer(wire, capsys, plain):
    # One Director offline and another connected-but-quiet is one envelope, not two. The offline
    # caution used to be the only one printed here, so the second was lost exactly when the answer
    # was emptiest.
    wire([], complete=False, reason=OFFLINE_REASON, stale=STALE_CAUTION)

    session_ops.list_sessions(json_output=False)

    out = plain(capsys.readouterr().out)
    assert "MACHINE_C" in out
    assert "MACHINE_B" in out


def test_the_machine_readable_empty_list_warns_on_stderr(wire, capsys):
    import json

    wire([], complete=True, stale=STALE_CAUTION)

    session_ops.list_sessions(json_output=True)

    captured = capsys.readouterr()
    assert json.loads(captured.out) == []      # would raise if the caution had gone to stdout
    assert "connected but has not reported recently" in captured.err


# ===== cc-status =====

def test_cc_status_prints_the_stale_caution_when_a_target_is_not_found(wire, capsys, plain):
    wire([], complete=True, stale=STALE_CAUTION)

    with pytest.raises(typer.Exit):
        status_cli._run(target="abc123", version=False)

    out = plain(capsys.readouterr().out)
    assert "No session matches" in out
    assert "connected but has not reported recently" in out


def test_cc_status_prints_the_stale_caution_when_nothing_is_running(wire, capsys, plain):
    wire([], complete=True, stale=STALE_CAUTION)

    status_cli._run(target="all", version=False)

    out = plain(capsys.readouterr().out)
    assert "connected but has not reported recently" in out
    assert "(no sessions running)" not in out


def test_cc_status_stays_silent_about_staleness_when_it_found_the_target(wire, capsys, plain):
    wire([_row()], complete=True, stale=STALE_CAUTION)

    status_cli._run(target=SESSION_ID, version=False)

    out = plain(capsys.readouterr().out)
    assert "11111111" in out
    assert "connected but has not reported recently" not in out


# ===== cc-history =====

def test_cc_history_prints_the_stale_caution_when_a_target_is_not_found(wire, capsys, plain):
    wire([], complete=True, stale=STALE_CAUTION)

    with pytest.raises(typer.Exit):
        history_cli._run(target="abc123", last=10, version=False)

    out = plain(capsys.readouterr().out)
    assert "No session matches" in out
    assert "connected but has not reported recently" in out


def test_cc_history_stays_silent_about_staleness_when_it_found_the_target(wire, capsys, plain, monkeypatch):
    wire([_row()], complete=True, stale=STALE_CAUTION)
    monkeypatch.setattr(gateway, "get_json", lambda path: {"agent": "Claude", "messages": []})

    history_cli._run(target=SESSION_ID, last=10, version=False)

    assert "connected but has not reported recently" not in plain(capsys.readouterr().out)
