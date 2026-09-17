"""AXI step 6b, re-check 6 (issue #2922, pull request 2965): an absent answer is not an empty one.

`message ask` read an answer with no `output` field as empty text, exited 0, and printed
"(the target produced no output)" - a claim about what the target printed that the answer never made.
The output field must be present and text. An empty string is a real empty answer; a missing, null or
non-text output exits 1 naming what was wrong, on the success path and the partial-output path alike.

No real message is sent: every Gateway call is stubbed.
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

# Each broken output, and the words the error must use for it.
BROKEN_OUTPUTS = [
    pytest.param({}, "gave no output field", id="absent"),
    pytest.param({"output": None}, "gave output as null, not text", id="null"),
    pytest.param({"output": 42}, "gave output as int, not text", id="number"),
    pytest.param({"output": ["forty-two"]}, "gave output as list, not text", id="list"),
    pytest.param({"output": {"text": "forty-two"}}, "gave output as dict, not text", id="object"),
]


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


def _assert_unknown_output(result, expected, status):
    assert result.exit_code == 1
    stderr = " ".join(result.stderr.split())
    assert stderr.startswith("Error: ")
    assert expected in stderr
    assert f"(waitStatus: {status})" in stderr
    assert "what the target printed is unknown" in stderr
    assert "help[" in stderr and f"cc-devthrottle session buffer {WORKER}" in stderr
    # No claim about what the target printed.
    assert "produced no output" not in result.stdout
    assert "answer from" not in result.stdout
    assert "partial output" not in result.stdout


@pytest.mark.parametrize("output, expected", BROKEN_OUTPUTS)
def test_ask_that_ended_idle_without_text_output_exits_1(monkeypatch, output, expected):
    result = _ask(monkeypatch, {"accepted": True, "waitStatus": "idle", **output})

    _assert_unknown_output(result, expected, "idle")


@pytest.mark.parametrize("status", ["timeout", "failed", "unheard-of"])
@pytest.mark.parametrize("output, expected", BROKEN_OUTPUTS)
def test_ask_that_did_not_end_idle_without_text_output_exits_1(monkeypatch, output, expected, status):
    result = _ask(monkeypatch, {"accepted": True, "waitStatus": status, **output})

    _assert_unknown_output(result, expected, status)


def test_ask_with_capitalised_output_key_is_read(monkeypatch):
    result = _ask(monkeypatch, {"Accepted": True, "WaitStatus": "idle", "Output": "forty-two"})

    assert result.exit_code == 0
    assert "forty-two" in result.stdout


def test_ask_with_empty_string_output_is_a_real_empty_answer(monkeypatch):
    # The control: an output field that is there and empty is the Gateway saying nothing was printed.
    result = _ask(monkeypatch, {"accepted": True, "waitStatus": "idle", "output": ""})

    assert result.exit_code == 0
    assert "answer from wkr" in result.stdout
    assert "(the target produced no output)" in result.stdout
    assert result.stderr == ""


@pytest.mark.parametrize("status", ["timeout", "failed"])
def test_ask_that_did_not_end_idle_with_empty_string_output_still_names_the_verdict(monkeypatch, status):
    result = _ask(monkeypatch, {"accepted": True, "waitStatus": status, "output": ""})

    assert result.exit_code == 1
    assert f"(waitStatus: {status})" in " ".join(result.stderr.split())
    assert "unknown" not in result.stderr.replace("which this tool does not know", "")
