"""The `maildesk` command (Mail Desk mission, phase 1; CONTRACT.md sections 5 and 6).

The command decides nothing: it sends every filter to the Gateway and prints what the Gateway answered. The fake
Gateway here answers at the `requests` call the command makes, so each command's whole path runs - the request it
builds, the answer it reads, the way it fails - and no port is opened. Every request is recorded, so a test can prove
both what was sent and that nothing was sent at all.
"""

import json
import sys
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

import pytest
import requests
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import maildesk_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

# A long address, so a cut would show. Only the reserved example domains and email@devthrottle.com appear here.
LONG_ADDRESS = "a-very-long-mailbox-name-that-must-never-be-cut-short@subdomain.example.com"
WHY = "the owner asked for it"

BY_DECISION = {"route": 1, "archive": 2, "junk": 0, "keep-for-owner": 0, "mail-desk-agent": 0, "failed": 0}

CHANNEL = {
    "id": "c0ffee00-0000-4000-8000-000000000001",
    "kind": "email",
    "provider": "outlook",
    "address": LONG_ADDRESS,
    "machines": ["devlinux", "SOREN_NORTH"],
    "mode": "observe",
    "modeLabel": "Observe - decides and records, changes nothing",
    "factory": "mail-desk",
    "seat": "mail-desk",
    "signedIn": {"state": "unknown", "label": "Not known yet - no check has run"},
    "lastCheck": {"utc": None, "outcome": None, "label": "Not checked yet - the mail-desk factory is not published"},
    "today": {"total": 3, "byDecision": BY_DECISION},
    "createdUtc": "2026-10-18T08:00:00Z",
    "createdBy": "email@devthrottle.com",
    "changedUtc": "2026-10-18T08:00:00Z",
    "changedBy": "email@devthrottle.com",
    "removedUtc": None,
}

CHANGE = {
    "id": "d0d0d0d0-0000-4000-8000-000000000002",
    "atUtc": "2026-10-18T09:00:00Z",
    "kind": "allow",
    "action": "add",
    "subject": "*@notifications.example.com",
    "detail": {"from": "*@notifications.example.com"},
    "versionBefore": 1,
    "versionAfter": 2,
    "who": {"kind": "person", "id": "email@devthrottle.com", "name": None},
    "whoInWords": "email@devthrottle.com",
    "why": WHY,
}

DECISION = {
    "id": "e0e0e0e0-0000-4000-8000-000000000003",
    "channel": LONG_ADDRESS,
    "messageKey": "message-1",
    "receivedUtc": "2026-10-18T07:00:00Z",
    "sender": "someone-with-a-long-sender-address@another-subdomain.example.org",
    "subject": "Your invoice",
    "layer": "allow",
    "decision": "route",
    "route": {"factory": "books", "seat": "clerk"},
    "reason": "allowed sender: invoices",
    "confidence": None,
    "costUsd": None,
    "mode": "observe",
    "outcome": None,
    "outcomeDetail": None,
    "decidedUtc": "2026-10-18T07:00:01Z",
}


class _Response:
    """What `requests.request` returns, as far as the command reads it."""

    def __init__(self, status, body=None):
        self.status_code = status
        self.text = "" if body is None else json.dumps(body)
        self.content = self.text.encode("utf-8")
        self.headers = {"Content-Type": "application/json; charset=utf-8"} if body is not None else {}
        self._body = body

    def json(self):
        if self._body is None:
            raise ValueError("no body")
        return self._body


@pytest.fixture
def fake_gateway(monkeypatch):
    """A Gateway that answers each (method, path) from `answers`, and records every request.

    A request it has no answer for is answered 500, so a test that forgets an answer fails loudly."""
    monkeypatch.setenv("CC_GATEWAY_URL", "http://gateway.test")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "session-key")
    maildesk_ops.set_gateway_override(None)
    calls = []
    answers = {}

    def fake(method, url, json=None, headers=None, timeout=None):
        parts = urlsplit(url)
        query = {k: v[0] for k, v in parse_qs(parts.query).items()}
        calls.append({"method": method, "path": parts.path, "query": query, "body": json, "headers": headers})
        status, body = answers.get((method, parts.path), (500, {"error": "the fake Gateway has no answer"}))
        return _Response(status, body)

    monkeypatch.setattr(requests, "request", fake)

    class _Gateway:
        def answer(self, method, path, status, body=None):
            answers[(method, "/gateway/mail-desk" + path)] = (status, body)

        @property
        def calls(self):
            return calls

    return _Gateway()


def _only_call(gw):
    assert len(gw.calls) == 1, gw.calls
    return gw.calls[0]


def _rules_answer(kind="allow", action="add", subject="*@notifications.example.com"):
    return {"version": 2, "change": dict(CHANGE, kind=kind, action=action, subject=subject)}


# ---------- reads: the request each one makes ----------


def test_status_GetsStatus_PrintsLabelsAndByDecisionVerbatim(fake_gateway):
    # Arrange
    fake_gateway.answer("GET", "/status", 200, {
        "enabled": True, "rulesVersion": 2, "channels": [CHANNEL],
        "today": {"date": "2026-10-18", "timeZone": "Europe/Copenhagen", "total": 3, "byDecision": BY_DECISION},
        "help": ["cc-devthrottle maildesk decisions"],
    })

    # Act
    result = runner.invoke(app, ["maildesk", "status"])

    # Assert
    assert result.exit_code == 0, result.output
    call = _only_call(fake_gateway)
    assert (call["method"], call["path"], call["query"], call["body"]) == ("GET", "/gateway/mail-desk/status", {}, None)
    assert "rules-version: 2" in result.stdout
    assert "total 3" in result.stdout
    assert "by-decision: route 1, archive 2, junk 0, keep-for-owner 0, mail-desk-agent 0, failed 0" in result.stdout
    assert CHANNEL["modeLabel"] in result.stdout
    assert CHANNEL["signedIn"]["label"] in result.stdout
    assert CHANNEL["lastCheck"]["label"] in result.stdout
    assert "help[1]:\n  cc-devthrottle maildesk decisions" in result.stdout
    assert result.stdout.isascii()


def test_status_Json_PrintsTheGatewaysAnswerInItsOwnShape(fake_gateway):
    # Arrange
    answer = {"enabled": True, "rulesVersion": 0, "channels": [], "today": {"total": 0}, "help": []}
    fake_gateway.answer("GET", "/status", 200, answer)

    # Act
    result = runner.invoke(app, ["maildesk", "status", "--json"])

    # Assert
    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout) == answer


@pytest.mark.parametrize("args, query", [
    ([], {}),
    (["--all"], {"all": "true"}),
])
def test_channel_list_SendsAllOnlyWhenAsked(fake_gateway, args, query):
    # Arrange
    fake_gateway.answer("GET", "/channels", 200, {"count": 1, "channels": [CHANNEL]})

    # Act
    result = runner.invoke(app, ["maildesk", "channel", "list", *args])

    # Assert
    assert result.exit_code == 0, result.output
    call = _only_call(fake_gateway)
    assert (call["method"], call["path"], call["query"]) == ("GET", "/gateway/mail-desk/channels", query)
    assert result.stdout.startswith("count: 1\n")


def test_channel_list_Json_WithAll_SendsTheFilterAndPrintsTheAnswer(fake_gateway):
    # Arrange
    answer = {"count": 1, "channels": [dict(CHANNEL, removedUtc="2026-10-18T10:00:00Z")]}
    fake_gateway.answer("GET", "/channels", 200, answer)

    # Act
    result = runner.invoke(app, ["maildesk", "channel", "list", "--all", "--json"])

    # Assert
    assert result.exit_code == 0, result.output
    assert _only_call(fake_gateway)["query"] == {"all": "true"}
    assert json.loads(result.stdout) == answer


def test_channel_list_Empty_SaysCountZero(fake_gateway):
    # Arrange
    fake_gateway.answer("GET", "/channels", 200, {"count": 0, "channels": []})

    # Act
    result = runner.invoke(app, ["maildesk", "channel", "list"])

    # Assert
    assert result.exit_code == 0, result.output
    assert result.stdout.startswith("count: 0\n")
    assert "channels[0]" in result.stdout
    assert "maildesk channel add <address>" in result.stdout


def test_channel_list_LongAddresses_AreNeverCutShort(fake_gateway):
    # Arrange
    fake_gateway.answer("GET", "/channels", 200, {"count": 1, "channels": [CHANNEL]})

    # Act
    result = runner.invoke(app, ["maildesk", "channel", "list"])

    # Assert
    assert result.exit_code == 0, result.output
    assert LONG_ADDRESS in result.stdout
    assert "..." not in result.stdout


@pytest.mark.parametrize("args, query", [
    ([], {}),
    (["--version", "3"], {"version": "3"}),
    (["--version", "0"], {"version": "0"}),
])
def test_rules_show_SendsTheVersionOnlyWhenAsked(fake_gateway, args, query):
    # Arrange
    fake_gateway.answer("GET", "/rules", 200, {"version": 0, "savedUtc": None, "savedBy": None, "document": {}})

    # Act
    result = runner.invoke(app, ["maildesk", "rules", "show", *args])

    # Assert
    assert result.exit_code == 0, result.output
    call = _only_call(fake_gateway)
    assert (call["method"], call["path"], call["query"]) == ("GET", "/gateway/mail-desk/rules", query)
    assert "rules[0]" in result.stdout and "allow[0]" in result.stdout


def test_rules_show_PrintsEveryListOfTheDocument(fake_gateway):
    # Arrange
    document = {
        "rules": [{"name": "citizenship", "when": {"subjectContains": ["citizen"], "fromAny": ["a@example.com"]},
                   "route": {"factory": "citizenship", "seat": "intake"}}],
        "allow": [{"from": "*@notifications.example.com", "what": "Notifications", "do": "archive"},
                  {"from": "billing@example.net", "what": "invoices",
                   "do": {"route": {"factory": "books", "seat": "clerk"}}}],
        "block": [{"from": "*@spammy-domain.example", "why": "cold sales, never answered"}],
        "routes": [{"factory": "devthrottle", "seat": "mail-desk", "handles": "customers and users"}],
    }
    fake_gateway.answer("GET", "/rules", 200, {"version": 4, "savedUtc": "2026-10-18T09:00:00Z",
                                               "savedBy": "email@devthrottle.com", "document": document})

    # Act
    result = runner.invoke(app, ["maildesk", "rules", "show"])

    # Assert
    assert result.exit_code == 0, result.output
    assert "version: 4" in result.stdout
    assert "citizenship/intake" in result.stdout
    assert "route:books/clerk" in result.stdout
    assert "*@spammy-domain.example" in result.stdout
    assert "devthrottle/mail-desk" in result.stdout


def test_rules_history_SendsCountAndKind_AndJsonKeepsTheFilter(fake_gateway):
    # Arrange
    answer = {"count": 1, "total": 9, "changes": [CHANGE]}
    fake_gateway.answer("GET", "/rules/history", 200, answer)

    # Act
    plain = runner.invoke(app, ["maildesk", "rules", "history", "-n", "5", "--kind", "allow"])
    as_json = runner.invoke(app, ["maildesk", "rules", "history", "-n", "5", "--kind", "allow", "--json"])

    # Assert
    assert plain.exit_code == 0, plain.output
    assert as_json.exit_code == 0, as_json.output
    assert [c["query"] for c in fake_gateway.calls] == [{"n": "5", "kind": "allow"}] * 2
    assert plain.stdout.startswith("count: 1 of 9 total\n")
    assert "1->2" in plain.stdout and WHY in plain.stdout
    assert json.loads(as_json.stdout) == answer


def test_decisions_EveryFilter_ReachesTheQuery(fake_gateway):
    # Arrange
    fake_gateway.answer("GET", "/decisions", 200, {"count": 1, "total": 1, "byDecision": BY_DECISION,
                                                   "decisions": [DECISION]})

    # Act
    result = runner.invoke(app, [
        "maildesk", "decisions", "--since", "2026-10-18T00:00:00Z", "--layer", "allow", "--decision", "route",
        "--channel", LONG_ADDRESS, "-n", "7",
    ])

    # Assert
    assert result.exit_code == 0, result.output
    call = _only_call(fake_gateway)
    assert (call["method"], call["path"]) == ("GET", "/gateway/mail-desk/decisions")
    assert call["query"] == {"since": "2026-10-18T00:00:00Z", "layer": "allow", "decision": "route",
                             "channel": LONG_ADDRESS, "n": "7"}
    assert result.stdout.startswith("count: 1\n")
    assert "by-decision: route 1, archive 2" in result.stdout
    assert DECISION["sender"] in result.stdout and LONG_ADDRESS in result.stdout
    assert "books/clerk" in result.stdout


def test_decisions_Json_WithAFilter_SendsTheFilterAndPrintsTheAnswer(fake_gateway):
    # Arrange
    answer = {"count": 0, "total": 0, "byDecision": dict.fromkeys(BY_DECISION, 0), "decisions": []}
    fake_gateway.answer("GET", "/decisions", 200, answer)

    # Act
    result = runner.invoke(app, ["maildesk", "decisions", "--decision", "junk", "--json"])

    # Assert
    assert result.exit_code == 0, result.output
    assert _only_call(fake_gateway)["query"] == {"decision": "junk"}
    assert json.loads(result.stdout) == answer


def test_decisions_Empty_SaysCountZero(fake_gateway):
    # Arrange
    fake_gateway.answer("GET", "/decisions", 200, {"count": 0, "total": 0, "byDecision": {}, "decisions": []})

    # Act
    result = runner.invoke(app, ["maildesk", "decisions"])

    # Assert
    assert result.exit_code == 0, result.output
    assert result.stdout.startswith("count: 0\n")
    assert "decisions[0]" in result.stdout


# ---------- changes: the request each one makes ----------


def test_channel_add_PostsAddressProviderMachinesInOrderAndWhy(fake_gateway):
    # Arrange
    fake_gateway.answer("POST", "/channels", 201, CHANNEL)

    # Act
    result = runner.invoke(app, ["maildesk", "channel", "add", LONG_ADDRESS, "--provider", "outlook",
                                 "--machine", "devlinux", "--machine", "SOREN_NORTH", "--why", WHY])

    # Assert
    assert result.exit_code == 0, result.output
    call = _only_call(fake_gateway)
    assert (call["method"], call["path"]) == ("POST", "/gateway/mail-desk/channels")
    assert call["body"] == {"address": LONG_ADDRESS, "provider": "outlook",
                            "machines": ["devlinux", "SOREN_NORTH"], "why": WHY}
    assert f"Addition of channel {LONG_ADDRESS} recorded." in result.stdout


@pytest.mark.parametrize("args, method, path", [
    (["status"], "GET", "/status"),
    (["channel", "add", LONG_ADDRESS, "--provider", "outlook", "--machine", "devlinux", "--why", WHY],
     "POST", "/channels"),
])
def test_request_CarriesThisSessionsKeyAsTheBearerToken(fake_gateway, args, method, path):
    # Arrange
    fake_gateway.answer(method, path, 200, {"enabled": True, "rulesVersion": 0, "channels": [], "today": {}}
                        if method == "GET" else CHANNEL)

    # Act
    result = runner.invoke(app, ["maildesk", *args])

    # Assert
    assert result.exit_code == 0, result.output
    assert _only_call(fake_gateway)["headers"]["Authorization"] == "Bearer session-key"


@pytest.mark.parametrize("args, method, path, unchanged", [
    (["channel", "remove", LONG_ADDRESS, "--why", WHY], "POST", "/channels/remove", CHANNEL),
    (["mode", LONG_ADDRESS, "live", "--why", WHY], "POST", "/channels/mode", CHANNEL),
    (["mode", LONG_ADDRESS, "live", "--why", WHY], "POST", "/channels/mode", {k: v for k, v in CHANNEL.items()
                                                                              if k != "mode"}),
])
def test_channelChange_AnsweredWithAnUnchangedRow_ExitsOneAndIsNotReportedAsRecorded(
        fake_gateway, args, method, path, unchanged):
    # Arrange - the Gateway answers 200 with the row as it was: still observe, not removed
    fake_gateway.answer(method, path, 200, unchanged)

    # Act
    result = runner.invoke(app, ["maildesk", *args])

    # Assert
    assert result.exit_code == 1, result.output
    assert "recorded" not in result.output
    assert "whether it changed is unknown" in result.output


def test_channel_remove_PostsAddressAndWhy(fake_gateway):
    # Arrange
    fake_gateway.answer("POST", "/channels/remove", 200, dict(CHANNEL, removedUtc="2026-10-18T10:00:00Z"))

    # Act
    result = runner.invoke(app, ["maildesk", "channel", "remove", LONG_ADDRESS, "--why", WHY])

    # Assert
    assert result.exit_code == 0, result.output
    call = _only_call(fake_gateway)
    assert (call["method"], call["path"], call["body"]) == (
        "POST", "/gateway/mail-desk/channels/remove", {"address": LONG_ADDRESS, "why": WHY})


def test_mode_PostsAddressModeAndWhy(fake_gateway):
    # Arrange
    fake_gateway.answer("POST", "/channels/mode", 200, dict(CHANNEL, mode="live", modeLabel="Live - acts"))

    # Act
    result = runner.invoke(app, ["maildesk", "mode", LONG_ADDRESS, "live", "--why", WHY])

    # Assert
    assert result.exit_code == 0, result.output
    call = _only_call(fake_gateway)
    assert (call["method"], call["path"], call["body"]) == (
        "POST", "/gateway/mail-desk/channels/mode", {"address": LONG_ADDRESS, "mode": "live", "why": WHY})
    assert "Live - acts" in result.stdout


@pytest.mark.parametrize("do, sent", [
    ("archive", "archive"),
    ("keep-for-owner", "keep-for-owner"),
    ("route:books/clerk", {"route": {"factory": "books", "seat": "clerk"}}),
])
def test_allow_add_PostsTheEntryWithDoParsed(fake_gateway, do, sent):
    # Arrange
    fake_gateway.answer("POST", "/rules/allow/add", 200, _rules_answer())

    # Act
    result = runner.invoke(app, ["maildesk", "allow", "add", "*@notifications.example.com", "--what", "Notifications",
                                 "--do", do, "--why", WHY])

    # Assert
    assert result.exit_code == 0, result.output
    call = _only_call(fake_gateway)
    assert (call["method"], call["path"]) == ("POST", "/gateway/mail-desk/rules/allow/add")
    assert call["body"] == {"from": "*@notifications.example.com", "what": "Notifications", "do": sent, "why": WHY}
    assert "Saved rules version 2: allow add *@notifications.example.com." in result.stdout


def test_block_add_SendsTheWhyAsTheEntrysOwnReason(fake_gateway):
    # Arrange
    fake_gateway.answer("POST", "/rules/block/add", 200, _rules_answer("block", "add", "*@spammy-domain.example"))

    # Act
    result = runner.invoke(app, ["maildesk", "block", "add", "*@spammy-domain.example", "--why", "cold sales"])

    # Assert
    assert result.exit_code == 0, result.output
    call = _only_call(fake_gateway)
    assert (call["path"], call["body"]) == (
        "/gateway/mail-desk/rules/block/add", {"from": "*@spammy-domain.example", "why": "cold sales"})


def test_rule_add_PostsNameConditionsRouteAndWhy(fake_gateway):
    # Arrange
    fake_gateway.answer("POST", "/rules/rule/add", 200, _rules_answer("rule", "add", "citizenship"))

    # Act
    result = runner.invoke(app, [
        "maildesk", "rule", "add", "citizenship", "--subject-contains", "citizen", "--subject-contains", "passport",
        "--from", "a@example.com", "--from", "*@example.org", "--route", "citizenship/intake", "--why", WHY,
    ])

    # Assert
    assert result.exit_code == 0, result.output
    call = _only_call(fake_gateway)
    assert call["path"] == "/gateway/mail-desk/rules/rule/add"
    assert call["body"] == {
        "name": "citizenship",
        "when": {"subjectContains": ["citizen", "passport"], "fromAny": ["a@example.com", "*@example.org"]},
        "route": {"factory": "citizenship", "seat": "intake"},
        "why": WHY,
    }


def test_route_add_PostsFactorySeatHandlesAndWhy(fake_gateway):
    # Arrange
    fake_gateway.answer("POST", "/rules/route/add", 200, _rules_answer("route", "add", "devthrottle/mail-desk"))

    # Act
    result = runner.invoke(app, ["maildesk", "route", "add", "devthrottle/mail-desk", "--handles",
                                 "customers and users", "--why", WHY])

    # Assert
    assert result.exit_code == 0, result.output
    call = _only_call(fake_gateway)
    assert (call["path"], call["body"]) == ("/gateway/mail-desk/rules/route/add", {
        "factory": "devthrottle", "seat": "mail-desk", "handles": "customers and users", "why": WHY})


@pytest.mark.parametrize("group, key", [
    ("allow", "*@notifications.example.com"),
    ("block", "*@spammy-domain.example"),
    ("rule", "citizenship"),
    ("route", "devthrottle/mail-desk"),
])
def test_remove_PostsKeyAndWhyToItsList(fake_gateway, group, key):
    # Arrange
    fake_gateway.answer("POST", f"/rules/{group}/remove", 200, _rules_answer(group, "remove", key))

    # Act
    result = runner.invoke(app, ["maildesk", group, "remove", key, "--why", WHY])

    # Assert
    assert result.exit_code == 0, result.output
    call = _only_call(fake_gateway)
    assert (call["method"], call["path"], call["body"]) == (
        "POST", f"/gateway/mail-desk/rules/{group}/remove", {"key": key, "why": WHY})


# ---------- refused before any request ----------


_CHANGES_WITHOUT_WHY = [
    ["channel", "add", LONG_ADDRESS, "--provider", "outlook", "--machine", "devlinux"],
    ["channel", "remove", LONG_ADDRESS],
    ["mode", LONG_ADDRESS, "live"],
    ["allow", "add", "*@notifications.example.com", "--what", "Notifications", "--do", "archive"],
    ["allow", "remove", "*@notifications.example.com"],
    ["block", "add", "*@spammy-domain.example"],
    ["block", "remove", "*@spammy-domain.example"],
    ["rule", "add", "citizenship", "--subject-contains", "citizen", "--route", "citizenship/intake"],
    ["rule", "remove", "citizenship"],
    ["route", "add", "devthrottle/mail-desk", "--handles", "customers"],
    ["route", "remove", "devthrottle/mail-desk"],
]


@pytest.mark.parametrize("args", _CHANGES_WITHOUT_WHY, ids=lambda a: " ".join(a[:2]))
def test_change_WithoutWhy_ExitsTwoAndSendsNothing(fake_gateway, args):
    # Act
    result = runner.invoke(app, ["maildesk", *args])

    # Assert
    assert result.exit_code == 2, result.output
    assert "--why is required" in result.output
    assert fake_gateway.calls == []


@pytest.mark.parametrize("args", _CHANGES_WITHOUT_WHY, ids=lambda a: " ".join(a[:2]))
def test_change_WithBlankWhy_ExitsTwoAndSendsNothing(fake_gateway, args):
    # Act
    result = runner.invoke(app, ["maildesk", *args, "--why", "   "])

    # Assert
    assert result.exit_code == 2, result.output
    assert "--why is required" in result.output
    assert fake_gateway.calls == []


@pytest.mark.parametrize("args, expected", [
    (["allow", "add", "*@notifications.example.com", "--what", "x", "--do", "route:books", "--why", WHY],
     "<factory>/<seat>"),
    (["allow", "add", "*@notifications.example.com", "--what", "x", "--do", "route:/clerk", "--why", WHY],
     "<factory>/<seat>"),
    (["allow", "add", "*@notifications.example.com", "--what", "x", "--do", "forward", "--why", WHY],
     "archive, keep-for-owner, route:<factory>/<seat>"),
    (["rule", "add", "r", "--from", "a@example.com", "--route", "a/b/c", "--why", WHY], "<factory>/<seat>"),
    (["route", "add", "devthrottle", "--handles", "x", "--why", WHY], "<factory>/<seat>"),
    (["route", "remove", "devthrottle/", "--why", WHY], "<factory>/<seat>"),
])
def test_malformed_route_or_do_ExitsTwoWithTheExpectedForm(fake_gateway, args, expected):
    # Act
    result = runner.invoke(app, ["maildesk", *args])

    # Assert
    assert result.exit_code == 2, result.output
    assert expected in result.output
    assert fake_gateway.calls == []


@pytest.mark.parametrize("args, valid", [
    (["decisions", "--layer", "model"], "rule, allow, block, sender-screen, route-model"),
    (["decisions", "--decision", "spam"], "route, archive, junk, keep-for-owner, mail-desk-agent, failed"),
    (["rules", "history", "--kind", "rules"], "rule, allow, block, route, channel, mode"),
    (["mode", LONG_ADDRESS, "active", "--why", WHY], "observe, live"),
])
def test_unknown_value_ExitsTwoListingTheValidValues(fake_gateway, args, valid):
    # Act
    result = runner.invoke(app, ["maildesk", *args])

    # Assert
    assert result.exit_code == 2, result.output
    assert f"Valid values: {valid}" in result.output
    assert fake_gateway.calls == []


@pytest.mark.parametrize("args", [
    ["status", "--bogus"],
    ["channel", "list", "--fields", "address"],
    ["decisions", "--sender", "a@example.com"],
])
def test_unknown_flag_ExitsTwoAndSendsNothing(fake_gateway, args):
    # Act
    result = runner.invoke(app, ["maildesk", *args])

    # Assert
    assert result.exit_code == 2, result.output
    assert "No such option" in result.output
    assert "Valid options:" in result.output
    assert fake_gateway.calls == []


def test_count_BelowOne_ExitsTwoAndSendsNothing(fake_gateway):
    # Act
    result = runner.invoke(app, ["maildesk", "decisions", "-n", "0"])

    # Assert
    assert result.exit_code == 2, result.output
    assert fake_gateway.calls == []


# ---------- the Gateway's answers ----------


@pytest.mark.parametrize("args, method, path", [
    (["status"], "GET", "/status"),
    (["channel", "list"], "GET", "/channels"),
    (["decisions", "--json"], "GET", "/decisions"),
    (["allow", "remove", "*@notifications.example.com", "--why", WHY], "POST", "/rules/allow/remove"),
])
def test_switchOff_404WithNoBody_SaysMailDeskIsNotSwitchedOnAndExitsOne(fake_gateway, args, method, path):
    # Arrange
    fake_gateway.answer(method, path, 404, None)

    # Act
    result = runner.invoke(app, ["maildesk", *args])

    # Assert
    assert result.exit_code == 1, result.output
    assert "Mail Desk is not switched on for this account." in result.output
    assert result.stdout == ""


def test_404WithABody_IsTheGatewaysRefusalNotTheSwitch(fake_gateway):
    # Arrange
    fake_gateway.answer("POST", "/channels/remove", 404, {"error": "no_such_channel"})

    # Act
    result = runner.invoke(app, ["maildesk", "channel", "remove", "nobody@example.com", "--why", WHY])

    # Assert
    assert result.exit_code == 1, result.output
    assert "no_such_channel (HTTP 404)" in result.output
    assert "not switched on" not in result.output


def test_403Refusal_PrintsTheGatewaysErrorAndDetailVerbatim(fake_gateway):
    # Arrange
    detail = "A factory's session may not add or remove a channel; ask the owner."
    fake_gateway.answer("POST", "/channels", 403, {"error": "FactorySessionRefused", "detail": detail})

    # Act
    result = runner.invoke(app, ["maildesk", "channel", "add", LONG_ADDRESS, "--provider", "outlook",
                                 "--machine", "devlinux", "--why", WHY])

    # Assert
    assert result.exit_code == 1, result.output
    assert f"Error: FactorySessionRefused - {detail} (HTTP 403)" in result.output
    assert "help[" in result.output
    assert result.stdout == ""


def test_400Refusal_PrintsTheValidatorsSentence(fake_gateway):
    # Arrange
    sentence = "allow[0].from: 'notifications' is not an address or *@domain; write it as *@notifications.example.com."
    fake_gateway.answer("POST", "/rules/allow/add", 400, {"error": sentence})

    # Act
    result = runner.invoke(app, ["maildesk", "allow", "add", "notifications", "--what", "x", "--do", "archive",
                                 "--why", WHY])

    # Assert
    assert result.exit_code == 1, result.output
    assert sentence in result.output


def test_change_AnswerWithoutVersion_IsNotReportedAsSaved(fake_gateway):
    # Arrange
    fake_gateway.answer("POST", "/rules/block/add", 200, {})

    # Act
    result = runner.invoke(app, ["maildesk", "block", "add", "*@spammy-domain.example", "--why", WHY])

    # Assert
    assert result.exit_code == 1, result.output
    assert "Saved rules version" not in result.output


def test_list_AnswerWithNoList_IsNotReadAsEmpty(fake_gateway):
    # Arrange
    fake_gateway.answer("GET", "/channels", 200, {"count": 0})

    # Act
    result = runner.invoke(app, ["maildesk", "channel", "list"])

    # Assert
    assert result.exit_code == 1, result.output
    assert "count: 0" not in result.stdout


# ---------- help ----------


@pytest.mark.parametrize("args", [
    ["status"], ["mode"], ["decisions"], ["channel", "list"], ["channel", "add"], ["channel", "remove"],
    ["rules", "show"], ["rules", "history"], ["allow", "add"], ["allow", "remove"], ["block", "add"],
    ["block", "remove"], ["rule", "add"], ["rule", "remove"], ["route", "add"], ["route", "remove"],
])
def test_everySubcommand_HasHelp(args):
    # Act
    result = runner.invoke(app, ["maildesk", *args, "--help"])

    # Assert
    assert result.exit_code == 0, result.output
    assert "Usage:" in result.output


def test_actions_ListEveryMaildeskCommand():
    # Act
    result = runner.invoke(app, ["actions", "--json"])

    # Assert
    assert result.exit_code == 0, result.output
    ids = {a["id"] for a in json.loads(result.stdout)["actions"]}
    assert {
        "maildesk-status", "maildesk-channel-list", "maildesk-channel-add", "maildesk-channel-remove", "maildesk-mode",
        "maildesk-rules-show", "maildesk-rules-history", "maildesk-allow-add", "maildesk-allow-remove",
        "maildesk-block-add", "maildesk-block-remove", "maildesk-rule-add", "maildesk-rule-remove",
        "maildesk-route-add", "maildesk-route-remove", "maildesk-decisions",
    } <= ids
