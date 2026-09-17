"""AXI step 6b, re-check 5 (issue #2922, pull request 2965): a Gateway answer is read as what it says.

Two classes, each closed here with the case that found it:

  * A MISSING or UNKNOWN answer is not a definite negative. `session report` read a roster row with no
    supervision answer - or one saying a live supervisor exists without naming it - as "the USER owns
    you", sent nothing, and exited 0. Only an explicit false is the no-parent answer.
  * A FAILURE VERDICT inside a successful HTTP answer is a failure. `message ask` printed an answer
    heading over `waitStatus: timeout` and `failed` and exited 0. The Gateway defines "idle" as the
    one value meaning the target finished its turn (GatewayEndpoints.cs, DeliverPromptAsync).

No real message is sent: every Gateway call is stubbed, and a post that reaches the stub is recorded.
"""

import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

WORKER = "11111111-1111-1111-1111-111111111111"
PARENT = "22222222-2222-2222-2222-222222222222"
PARENT_ROW = {"sessionId": PARENT, "name": "Mission - Manager", "hasLiveSupervisor": False,
              "controllerSessionId": None}


@pytest.fixture
def posted(monkeypatch):
    calls = []

    def fake_post_json(path, body, **_kwargs):
        calls.append((path, body))
        return {"accepted": True}

    monkeypatch.setenv("CC_SESSION_ID", WORKER)
    monkeypatch.setattr(session_ops.gateway, "post_json", fake_post_json)
    return calls


def _roster(monkeypatch, worker_row):
    row = dict({"sessionId": WORKER, "name": "Mission - Worker"}, **worker_row)
    monkeypatch.setattr(session_ops, "_get_fleet", lambda: ([row, PARENT_ROW], True, None, None))


# --- session report: an absent supervision answer is not "no parent" --------------------------------

@pytest.mark.parametrize(
    "row, expected",
    [
        # The inspector's two reproductions.
        ({"hasLiveSupervisor": True}, "gave nothing for controllerSessionId"),
        ({"controllerSessionId": PARENT}, "gave nothing for hasLiveSupervisor"),
        # And the rest of the class: null, blank and non-boolean answers.
        ({}, "gave nothing for hasLiveSupervisor"),
        ({"hasLiveSupervisor": None, "controllerSessionId": PARENT}, "gave nothing for hasLiveSupervisor"),
        ({"hasLiveSupervisor": "true", "controllerSessionId": PARENT}, "gave 'true' for hasLiveSupervisor"),
        ({"hasLiveSupervisor": 1, "controllerSessionId": PARENT}, "gave 1 for hasLiveSupervisor"),
        ({"hasLiveSupervisor": True, "controllerSessionId": None}, "gave nothing for controllerSessionId"),
        ({"hasLiveSupervisor": True, "controllerSessionId": "  "}, "gave ' ' for controllerSessionId"),  # stderr compared with whitespace collapsed
        ({"hasLiveSupervisor": True, "controllerSessionId": 7}, "gave 7 for controllerSessionId"),
    ],
)
def test_report_without_a_valid_supervision_answer_exits_1_and_sends_nothing(monkeypatch, posted, row, expected):
    _roster(monkeypatch, row)

    result = runner.invoke(app, ["session", "report", "Finished the task."])

    assert result.exit_code == 1
    assert posted == []
    assert expected in " ".join(result.stderr.split())
    assert "Nothing was sent" in result.stderr
    # It must not supply an ownership verdict the Gateway did not give.
    assert "USER owns you" not in result.output
    assert "session done" not in result.output


def test_report_with_explicit_false_keeps_the_no_parent_path(monkeypatch, posted):
    # The control: an explicit false is a real answer, even with a dead supervisor still named.
    _roster(monkeypatch, {"hasLiveSupervisor": False, "controllerSessionId": PARENT})

    result = runner.invoke(app, ["session", "report", "Finished the task."])

    assert result.exit_code == 0
    assert posted == []
    assert "USER owns you" in result.stdout


def test_report_reads_the_pascal_case_supervision_fields(monkeypatch, posted):
    _roster(monkeypatch, {"HasLiveSupervisor": True, "ControllerSessionId": PARENT})

    result = runner.invoke(app, ["session", "report", "Finished the task."])

    assert result.exit_code == 0
    assert posted == [(f"sessions/{PARENT}/message", {"text": "Finished the task."})]


# --- message ask: the Gateway's waitStatus verdict is rendered --------------------------------------

def _ask(monkeypatch, answer):
    calls = []

    def fake_post_json(path, body, **_kwargs):
        calls.append((path, body))
        return answer

    monkeypatch.setattr(session_ops, "_resolve_target",
                        lambda t, command_name=None: {"sessionId": WORKER, "name": "wkr"})
    monkeypatch.setattr(session_ops.gateway, "post_json", fake_post_json)
    result = runner.invoke(app, ["message", "ask", WORKER, "What is the result?", "--timeout-ms", "1"])
    assert calls and calls[0][0] == f"sessions/{WORKER}/message"
    return result


@pytest.mark.parametrize(
    "status, expected",
    [
        ("timeout", "(waitStatus: timeout)"),
        ("failed", "(waitStatus: failed)"),
        ("cancelled", "waitStatus 'cancelled', which this tool does not know"),
        ("Idle", "waitStatus 'Idle', which this tool does not know"),
    ],
)
def test_ask_that_did_not_end_idle_exits_1_naming_the_verdict(monkeypatch, status, expected):
    result = _ask(monkeypatch, {"accepted": True, "waitStatus": status, "output": ""})

    assert result.exit_code == 1
    stderr = " ".join(result.stderr.split())
    assert stderr.startswith("Error: ")
    assert expected in stderr
    assert "help[" in stderr and f"cc-devthrottle session buffer {WORKER}" in stderr
    # Not an answer: no answer heading and no "produced no output" claim.
    assert "answer from" not in result.stdout
    assert "produced no output" not in result.stdout


@pytest.mark.parametrize("status", ["timeout", "failed", "unheard-of"])
def test_ask_that_did_not_end_idle_prints_the_partial_output_first(monkeypatch, status):
    result = _ask(monkeypatch, {"accepted": True, "waitStatus": status, "output": "half an answer\n"})

    assert result.exit_code == 1
    assert "partial output from wkr" in result.stdout
    assert "half an answer" in result.stdout
    assert "answer from" not in result.stdout.replace("partial output from", "")


def test_ask_that_ended_idle_prints_the_answer(monkeypatch):
    # The control: the one success value still prints the answer and exits 0.
    result = _ask(monkeypatch, {"accepted": True, "waitStatus": "idle", "output": "forty-two"})

    assert result.exit_code == 0
    assert "answer from wkr" in result.stdout
    assert "forty-two" in result.stdout
    assert result.stderr == ""
