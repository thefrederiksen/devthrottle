"""Tests for `cc-devthrottle message link add | list | remove` (issue #3548).

A message link lets two sessions that are not owner and worker message each other. These tests pin what
the command sends and prints: the route and body are written out as literals, never read from the
module, so a changed route turns a test red. No HTTP happens: the Gateway calls are stubbed. Who may set
a link up is the Gateway's decision and is proven there; here, a refusal must reach the user in words.
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
LINK_ID = "0123456789abcdef0123456789abcdef"
FLEET = [
    {"sessionId": INVESTIGATOR, "name": "BDO Argentina bug"},
    {"sessionId": COORDINATOR, "name": "Cube Coordinator"},
]


def _link(status="live", amount="once-with-reply"):
    return {
        "linkId": LINK_ID, "senderSessionId": INVESTIGATOR, "recipientSessionId": COORDINATOR,
        "amount": amount, "status": status, "summary": "One message and a reply. Live.",
        "setUpBy": f"session {COORDINATOR}",
    }


def _stub(monkeypatch, refuse=None):
    calls = []
    gw = link_ops.gateway

    def fake_post_json(path, body=None, timeout=30):
        calls.append(("POST", path, body))
        if refuse:
            raise gw.GatewayError(refuse)
        return {"link": _link(amount=body["amount"]), "replaced": []}

    def fake_get_json(path, timeout=30, **_):
        calls.append(("GET", path, None))
        return {"links": [_link()], "stoppedWithinDays": 30}

    def fake_delete(path, timeout=30, body=None):
        calls.append(("DELETE", path, body))
        return _link(status="removed")

    monkeypatch.setattr(gw, "post_json", fake_post_json)
    monkeypatch.setattr(gw, "get_json", fake_get_json)
    monkeypatch.setattr(gw, "delete", fake_delete)
    monkeypatch.setattr(gw, "get_fleet", lambda: (FLEET, True, None, None))
    return calls


def test_add_resolves_both_sessions_by_name_and_posts_the_link(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["message", "link", "add", "BDO Argentina bug", "Cube Coordinator", "--amount", "once-with-reply"])

    assert result.exit_code == 0, result.output
    assert calls == [("POST", "fleet/links", {
        "senderSessionId": INVESTIGATOR, "recipientSessionId": COORDINATOR, "amount": "once-with-reply",
    })]
    out = plain(result.output)
    assert f"id: {LINK_ID}" in out
    assert "status: live" in out
    assert f"cc-devthrottle message link remove {LINK_ID}" in out


def test_add_refuses_an_unknown_amount_before_calling_the_gateway(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["message", "link", "add", "BDO Argentina bug", "Cube Coordinator", "--amount", "forever"])

    assert result.exit_code == 1
    assert "amount must be one of once, once-with-reply, ongoing" in plain(result.output)
    assert calls == []


def test_a_gateway_refusal_reaches_the_user_in_words(monkeypatch, plain):
    _stub(monkeypatch, refuse="Only the owner, on their own signed-in phone or browser, or a session the owner "
                              "has raised, can set up or remove a message link.")

    result = runner.invoke(app, ["message", "link", "add", "BDO Argentina bug", "Cube Coordinator", "--amount", "ongoing"])

    assert result.exit_code == 1
    assert "Only the owner" in plain(result.output)


def test_list_prints_every_link_with_its_summary(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["message", "link", "list"])

    assert result.exit_code == 0, result.output
    assert calls == [("GET", "fleet/links", None)]
    out = plain(result.output)
    assert "links[1]" in out
    assert "summary: One message and a reply. Live." in out


def test_remove_deletes_the_link_by_id(monkeypatch, plain):
    calls = _stub(monkeypatch)

    result = runner.invoke(app, ["message", "link", "remove", LINK_ID])

    assert result.exit_code == 0, result.output
    assert calls == [("DELETE", f"fleet/links/{LINK_ID}", None)]
    assert "status: removed" in plain(result.output)
