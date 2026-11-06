"""Tests for `cc-devthrottle inbox send` (Factory Inbox mission, phase 3).

A session tells the owner something in his DevThrottle inbox instead of emailing him. These tests pin what the
command sends and prints: the route and body are written out as literals, never read from the module, so a changed
route turns a test red. No HTTP happens: the Gateway call is stubbed. Who sent it is the Gateway's ruling and is
proven there; here, the command must send only the subject, the body and the report, as this session, and a refusal
must reach the caller in words.
"""

import json
import sys
from pathlib import Path

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import inbox_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

ME = "a1000000-0000-4000-8000-000000000001"
REPORT = "99999999-9999-4999-8999-999999999999"


def _answer(subject="v2.25 notes ready"):
    return {"message": {
        "id": "m-1", "subject": subject, "state": "unread", "stateLabel": "Unread",
        "from": {"kind": "session", "label": "412 Release - Developer"},
        "links": [{"kind": "session", "label": "Open 412 Release - Developer", "target": ME}],
    }}


def _stub(monkeypatch, refuse=None, answer=None, session=ME):
    calls = []
    gw = inbox_ops.gateway

    def fake_post_json(path, body=None, timeout=30, **_):
        calls.append((path, body))
        if refuse:
            raise gw.GatewayError(refuse)
        return answer if answer is not None else _answer(body["subject"])

    monkeypatch.setattr(gw, "post_json", fake_post_json)
    if session is None:
        monkeypatch.delenv("CC_SESSION_ID", raising=False)
    else:
        monkeypatch.setenv("CC_SESSION_ID", session)
    return calls


def test_send_posts_subject_and_body_as_this_session(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["inbox", "send", "--subject", "v2.25 notes ready", "--body", "Read them\nwhen you can."])

    assert result.exit_code == 0, result.output
    assert calls == [(f"sessions/{ME}/inbox", {"subject": "v2.25 notes ready", "body": "Read them\nwhen you can."})]
    out = plain(result.output)
    assert "sent: Your inbox" in out
    assert "from: 412 Release - Developer" in out
    assert "state: Unread" in out
    assert "Open 412 Release - Developer" in out
    assert "cc-devthrottle message inbox" in out


def test_send_with_a_report_names_it(monkeypatch):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["inbox", "send", "--subject", "s", "--body", "b", "--report", REPORT])

    assert result.exit_code == 0, result.output
    assert calls == [(f"sessions/{ME}/inbox", {"subject": "s", "body": "b", "reportId": REPORT})]


def test_send_json_prints_the_gateways_answer(monkeypatch):
    _stub(monkeypatch)

    result = runner.invoke(app, ["inbox", "send", "--subject", "s", "--body", "b", "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.output)["message"]["id"] == "m-1"


def test_send_outside_a_session_fails_without_calling_the_gateway(monkeypatch):
    calls = _stub(monkeypatch, session=None)

    result = runner.invoke(app, ["inbox", "send", "--subject", "s", "--body", "b"])

    assert result.exit_code == 1
    assert calls == []
    assert "CC_SESSION_ID is not set" in result.output


def test_send_an_empty_subject_or_body_is_a_usage_error(monkeypatch):
    calls = _stub(monkeypatch)

    no_subject = runner.invoke(app, ["inbox", "send", "--subject", "  ", "--body", "b"])
    no_body = runner.invoke(app, ["inbox", "send", "--subject", "s", "--body", ""])
    two_lines = runner.invoke(app, ["inbox", "send", "--subject", "one\ntwo", "--body", "b"])

    assert no_subject.exit_code == 2
    assert no_body.exit_code == 2
    assert two_lines.exit_code == 2
    assert calls == []


def test_send_a_refusal_reaches_the_caller_in_words(monkeypatch):
    _stub(monkeypatch, refuse="This account has no dev report 9999.")

    result = runner.invoke(app, ["inbox", "send", "--subject", "s", "--body", "b", "--report", REPORT])

    assert result.exit_code == 1
    assert "This account has no dev report 9999." in result.output


def test_send_an_answer_without_the_message_never_reads_as_sent(monkeypatch):
    _stub(monkeypatch, answer={})

    result = runner.invoke(app, ["inbox", "send", "--subject", "s", "--body", "b"])

    assert result.exit_code == 1
    assert "sent: Your inbox" not in result.output
