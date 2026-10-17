"""Tests for choosing how a session is connected (API mode mission #4070, phase 5).

`session spawn --connection terminal|api` sends the contract's `connectionMode`, and only when asked; an unknown
value exits 2 before anything is sent; a refusal from the Gateway or the Director (API mode not offered yet, or an
agent with no API mode) reaches the caller as its own sentence. `session list` shows the connection as a field and
filters by it, and the filter narrows `--json` too. No real HTTP: the Gateway calls are stubbed.
"""

import json
import sys
from pathlib import Path

import pytest
import typer
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import parse_list  # noqa: E402
from src import session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


# ---------------------------------------------------------------------------------------------------
# session spawn --connection
# ---------------------------------------------------------------------------------------------------


@pytest.fixture
def captured(monkeypatch):
    """Capture the body posted to the Gateway, as a person's spawn on this session's own Director."""
    body = {}

    def fake_post_json(path, b):
        body.clear()
        body.update(b)
        body["_path"] = path
        return {"sessionId": "11111111-2222-3333-4444-555555555555", "name": "test"}

    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    monkeypatch.setenv("CC_DIRECTOR_ID", "my-director")
    monkeypatch.setattr(session_ops.gateway, "post_json", fake_post_json)
    return body


def _spawn(connection):
    session_ops.spawn_session(
        repo="C:/repo", agent="Pi", prompt=None, name="n", purpose=None,
        command=None, command_args=None, connection=connection,
    )


def test_spawn_session_ConnectionApi_SendsTheContractsWord(captured):
    _spawn("api")

    assert captured["connectionMode"] == "api"


def test_spawn_session_ConnectionTerminal_SendsTerminal(captured):
    _spawn("terminal")

    assert captured["connectionMode"] == "terminal"


def test_spawn_session_NoConnection_LeavesTheFieldOffSoAPlainSpawnIsUnchanged(captured):
    _spawn(None)

    assert "connectionMode" not in captured
    assert captured["_path"] == "directors/my-director/sessions"


def test_session_spawn_Cli_UnknownConnection_ExitsTwoListingTheValuesAndSendsNothing(captured, tmp_path):
    result = runner.invoke(app, ["session", "spawn", str(tmp_path), "--name", "n", "--connection", "rpc"])

    assert result.exit_code == 2
    assert "'rpc'" in result.stderr
    assert "terminal, api" in result.stderr
    assert "_path" not in captured


def test_session_spawn_Cli_ConnectionApi_ReachesTheGatewayAsConnectionMode(captured, tmp_path):
    # The wiring: typer option -> spawn_session -> the body. Asserted through the CLI because the flag and the
    # parameter are only connected in cli.py.
    result = runner.invoke(app, ["session", "spawn", str(tmp_path), "--name", "n", "--agent", "Pi", "--connection", "api"])

    assert result.exit_code == 0, result.output
    assert captured["connectionMode"] == "api"
    assert captured["agent"] == "Pi"


@pytest.mark.parametrize(
    "refusal",
    [
        # The Gateway, while the one switch is off.
        "API mode is not offered yet: it opens once taking the wheel is built (mission #4070, phase 4). "
        "Start the session in terminal mode.",
        # The Director, for an agent whose wrapper has no API dialect.
        "Claude Code cannot be started in API mode: its wrapper speaks only through the terminal.",
    ],
)
def test_spawn_session_ApiModeRefused_ExitsOneWithTheStatedReason(monkeypatch, capsys, refusal):
    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    monkeypatch.setenv("CC_DIRECTOR_ID", "my-director")

    def refuse(path, body):
        raise session_ops.gateway.GatewayError(refusal)

    monkeypatch.setattr(session_ops.gateway, "post_json", refuse)

    with pytest.raises(typer.Exit) as ex:
        _spawn("api")

    assert ex.value.exit_code == 1
    err = capsys.readouterr().err
    assert "no session was opened" in err
    assert refusal.split(":")[0] in err


# ---------------------------------------------------------------------------------------------------
# session list: the connection field and --connection
# ---------------------------------------------------------------------------------------------------


def _row(sid, name, **extra):
    row = {
        "sessionId": sid,
        "number": 100,
        "name": name,
        "machineName": "SOREN_NORTH",
        "repoPath": "/home/soren/ReposFred/devthrottle",
        "activityState": "Working",
        "triageBucket": "active",
        "crashed": False,
    }
    row.update(extra)
    return row


FLEET = [
    _row("a1a1a1a1-0000-4000-8000-000000000001", "Pi in API mode", connectionMode="api", connectionTag="API mode"),
    _row("a1a1a1a1-0000-4000-8000-000000000002", "Pi in its terminal", connectionMode="terminal"),
    # A Gateway older than API mode sends no connection field: that row is terminal.
    _row("a1a1a1a1-0000-4000-8000-000000000003", "From an older Gateway"),
]


@pytest.fixture
def serve(monkeypatch):
    def serve_(sessions):
        monkeypatch.delenv("CC_SESSION_ID", raising=False)
        monkeypatch.setattr(session_ops.gateway, "get_fleet", lambda: (sessions, True, None, None))

    return serve_


def test_list_sessions_ConnectionField_ShowsEachRowsModeAndAbsentIsTerminal(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=False, fields="id,connection")

    fields, records = parse_list(capsys.readouterr().out, "sessions")
    assert fields == ["id", "connection"]
    assert [r["connection"] for r in records] == ["api", "terminal", "terminal"]


def test_list_sessions_ConnectionApi_OnlyTheApiModeRow(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=False, connection="api")

    _, records = parse_list(capsys.readouterr().out, "sessions")
    assert [r["id"] for r in records] == [FLEET[0]["sessionId"]]


def test_list_sessions_JsonConnectionApi_NarrowsTheSameBareArray(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=True, connection="api")

    assert capsys.readouterr().out == json.dumps([FLEET[0]], indent=2) + "\n"


def test_list_sessions_JsonConnectionTerminal_IncludesTheRowWithNoField(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=True, connection="terminal")

    assert json.loads(capsys.readouterr().out) == [FLEET[1], FLEET[2]]


def test_session_list_Cli_UnknownConnection_ExitsTwoListingTheValues(serve):
    serve(FLEET)

    result = runner.invoke(app, ["session", "list", "--connection", "API"])

    assert result.exit_code == 2
    assert "'API'" in result.stderr
    assert "terminal, api" in result.stderr
    assert result.stdout == ""


def test_session_list_Cli_ConnectionFilter_ReachesTheList(serve):
    serve(FLEET)

    result = runner.invoke(app, ["session", "list", "--connection", "api", "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout) == [FLEET[0]]
