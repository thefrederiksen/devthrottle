"""Tests for `cc-devthrottle message request` and `message link requests | answer` (issue #3548).

A session that may not message another asks the owner for a link; the owner - or a raised session - answers.
These tests pin what the commands send and print: the routes and bodies are literals, never read from the
module, so a changed route turns a test red. No HTTP happens: the Gateway calls are stubbed. Who may ask and
who may answer is the Gateway's decision and is proven there; here, a refusal must reach the user in words.
"""

import sys
from pathlib import Path

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import link_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

INVESTIGATOR = "a1000000-0000-4000-8000-000000000001"
COORDINATOR = "b2000000-0000-4000-8000-000000000002"
REQUEST_ID = "fedcba9876543210fedcba9876543210"
FLEET = [
    {"sessionId": INVESTIGATOR, "name": "BDO Argentina bug"},
    {"sessionId": COORDINATOR, "name": "Cube Coordinator"},
]


def _request(status="pending", amount=None, link_id=None):
    return {
        "requestId": REQUEST_ID, "requesterSessionId": INVESTIGATOR, "targetSessionId": COORDINATOR,
        "reason": "I need the open tickets list", "status": status, "amount": amount, "linkId": link_id,
    }


def _stub(monkeypatch, refuse=None):
    calls = []
    gw = link_ops.gateway

    def fake_post_json(path, body=None, timeout=30):
        calls.append(("POST", path, body))
        if refuse:
            raise gw.GatewayError(refuse)
        if path.endswith("/answer"):
            if body.get("decline"):
                return {"request": _request(status="declined")}
            return {"request": _request(status="allowed", amount=body.get("amount", "once-with-reply"), link_id="l1"),
                    "link": {"linkId": "l1", "status": "live", "summary": "One message and a reply. Live."}}
        return {"request": _request(), "created": True,
                "note": "Asked. The user decides, and the answer arrives in your inbox."}

    def fake_get_json(path, timeout=30, **_):
        calls.append(("GET", path, None))
        return {"requests": [_request()], "answeredWithinDays": 7}

    monkeypatch.setattr(gw, "post_json", fake_post_json)
    monkeypatch.setattr(gw, "get_json", fake_get_json)
    monkeypatch.setattr(gw, "get_fleet", lambda: (FLEET, True, None, None))
    return calls


def test_request_resolves_the_target_by_name_and_posts_the_reason(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["message", "request", "Cube Coordinator", "I need the open tickets list"])

    assert result.exit_code == 0, result.output
    assert calls == [("POST", "fleet/link-requests", {
        "targetSessionId": COORDINATOR, "reason": "I need the open tickets list", "amount": "once-with-reply",
    })]
    out = plain(result.output)
    assert f"id: {REQUEST_ID}" in out
    assert "status: pending" in out
    assert "the answer arrives in your inbox" in out


def test_request_asks_for_the_amount_the_session_names(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["message", "request", "Cube Coordinator", "the cutover", "--amount", "ongoing"])

    assert result.exit_code == 0, result.output
    assert calls == [("POST", "fleet/link-requests", {
        "targetSessionId": COORDINATOR, "reason": "the cutover", "amount": "ongoing",
    })]


def test_request_with_an_unknown_amount_is_refused_before_calling_the_gateway(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["message", "request", "Cube Coordinator", "why", "--amount", "forever"])

    assert result.exit_code == 1
    assert calls == []
    assert "amount must be one of" in plain(result.output)


def test_answer_approve_sends_approve(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["message", "link", "answer", REQUEST_ID, "--approve"])

    assert result.exit_code == 0, result.output
    assert calls == [("POST", f"fleet/link-requests/{REQUEST_ID}/answer", {"approve": True})]


def test_request_without_a_reason_is_refused_before_calling_the_gateway(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["message", "request", "Cube Coordinator", "   "])

    assert result.exit_code == 1
    assert calls == []
    assert "say why" in plain(result.output)


def test_a_refused_request_reaches_the_user_in_the_gateways_words(monkeypatch, plain):
    _stub(monkeypatch, refuse="A message link already lets you message that session. Send the message instead of asking.")

    result = runner.invoke(app, ["message", "request", "Cube Coordinator", "why"])

    assert result.exit_code == 1
    assert "already lets you message that session" in plain(result.output)


def test_requests_lists_from_the_owner_route(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["message", "link", "requests"])

    assert result.exit_code == 0, result.output
    assert calls == [("GET", "fleet/link-requests", None)]
    out = plain(result.output)
    assert "requests[1]" in out
    assert "reason: I need the open tickets list" in out


def test_answer_allows_with_an_amount(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["message", "link", "answer", REQUEST_ID, "--amount", "once-with-reply"])

    assert result.exit_code == 0, result.output
    assert calls == [("POST", f"fleet/link-requests/{REQUEST_ID}/answer", {"amount": "once-with-reply"})]
    out = plain(result.output)
    assert "status: allowed" in out
    assert "link set up:" in out


def test_answer_declines(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["message", "link", "answer", REQUEST_ID, "--decline"])

    assert result.exit_code == 0, result.output
    assert calls == [("POST", f"fleet/link-requests/{REQUEST_ID}/answer", {"decline": True})]
    assert "status: declined" in plain(result.output)


def test_answer_needs_exactly_one_of_amount_and_decline(monkeypatch, plain):
    calls = _stub(monkeypatch)

    neither = runner.invoke(app, ["message", "link", "answer", REQUEST_ID])
    both = runner.invoke(app, ["message", "link", "answer", REQUEST_ID, "--amount", "once", "--decline"])

    assert neither.exit_code == 1 and both.exit_code == 1
    assert calls == []
