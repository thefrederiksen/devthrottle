"""Test 1: nothing but GET and the two moves can leave the request guard.

The saved sign-in's token CAN send mail (phase 0), so the guard is the whole of the promise that the Mail Desk never
sends, replies, forwards, changes or deletes. Each refused shape below is sent straight to graph_request(), the one
function every Graph call goes through, and two things are asserted: it raises RefusedRequest, and the fake Graph's
log is EMPTY - the request never reached the network. The allowed shapes are sent too, to show the log is a working
instrument and not an empty one by accident.
"""

import re

import pytest

from conftest import MAILBOX, NOW, channel, msg

GRAPH = "https://graph.microsoft.com/v1.0"
MOVE = f"{GRAPH}/me/messages/AAMkAGI2THVSAAA=/move"

REFUSED = [
    # other methods
    ("PATCH", f"{GRAPH}/me/messages/AAMkAGI2THVSAAA=", {"isRead": True}),
    ("DELETE", f"{GRAPH}/me/messages/AAMkAGI2THVSAAA=", None),
    ("PUT", f"{GRAPH}/me/messages/AAMkAGI2THVSAAA=", {"subject": "x"}),
    ("HEAD", f"{GRAPH}/me/messages", None),
    ("post", MOVE, {"destinationId": "archive"}),
    # other POSTs
    ("POST", f"{GRAPH}/me/sendMail", {"message": {"subject": "x"}}),
    ("POST", f"{GRAPH}/me/messages/AAMkAGI2THVSAAA=/send", None),
    ("POST", f"{GRAPH}/me/messages/AAMkAGI2THVSAAA=/reply", {"comment": "x"}),
    ("POST", f"{GRAPH}/me/messages/AAMkAGI2THVSAAA=/replyAll", {"comment": "x"}),
    ("POST", f"{GRAPH}/me/messages/AAMkAGI2THVSAAA=/forward", {"comment": "x"}),
    ("POST", f"{GRAPH}/me/messages/AAMkAGI2THVSAAA=/createReply", None),
    ("POST", f"{GRAPH}/me/messages/AAMkAGI2THVSAAA=/copy", {"destinationId": "archive"}),
    ("POST", f"{GRAPH}/me/messages", {"subject": "a draft"}),
    ("POST", f"{GRAPH}/users/{MAILBOX}/messages/AAMkAGI2THVSAAA=/move", {"destinationId": "archive"}),
    ("POST", "https://graph.microsoft.com/beta/me/messages/AAMkAGI2THVSAAA=/move", {"destinationId": "archive"}),
    # a move with any other body
    ("POST", MOVE, {"destinationId": "deleteditems"}),
    ("POST", MOVE, {"destinationId": "inbox"}),
    ("POST", MOVE, {"destinationId": "Archive"}),
    ("POST", MOVE, {"destinationId": "archive", "comment": "x"}),
    ("POST", MOVE, {"destinationid": "archive"}),
    ("POST", MOVE, {}),
    ("POST", MOVE, None),
    ("POST", MOVE, '{"destinationId": "archive"}'),
    ("POST", MOVE, [{"destinationId": "archive"}]),
    ("POST", MOVE + "?$select=id", {"destinationId": "archive"}),
    # paths that try to reach another path
    ("POST", f"{GRAPH}/me/messages/../../me/sendMail/move", {"destinationId": "archive"}),
    ("POST", f"{GRAPH}/me/messages/..%2Fsend/move", {"destinationId": "archive"}),
    ("POST", f"{GRAPH}/me/messages/abc%2Fsend/move", {"destinationId": "archive"}),
    ("POST", f"{GRAPH}/me/messages/abc%2fsend/move", {"destinationId": "archive"}),
    ("POST", f"{GRAPH}/me/messages/abc.def/move", {"destinationId": "archive"}),
    ("POST", f"{GRAPH}/me/messages/abc/def/move", {"destinationId": "archive"}),
    ("POST", f"{GRAPH}/me/messages//move", {"destinationId": "archive"}),
    ("GET", f"{GRAPH}/me/../me/mailFolders/inbox/messages", None),
    ("GET", f"{GRAPH}/me/mailFolders/inbox%2F..%2Fx/messages", None),
    # other hosts
    ("GET", "https://evil.example.com/v1.0/me/messages", None),
    ("GET", "http://graph.microsoft.com/v1.0/me/messages", None),
    ("GET", "https://graph.microsoft.com.evil.example.com/v1.0/me/messages", None),
    ("GET", "https://graph.microsoft.com:8443/v1.0/me/messages", None),
    ("GET", "https://someone@graph.microsoft.com/v1.0/me/messages", None),
    ("GET", "https://login.microsoftonline.com/common/oauth2/v2.0/token", None),
    ("POST", "https://evil.example.com/v1.0/me/messages/abc/move", {"destinationId": "archive"}),
    # a GET that carries a body, or leaves the version path
    ("GET", f"{GRAPH}/me/messages", {"x": 1}),
    ("GET", "https://graph.microsoft.com/beta/me/messages", None),
    ("GET", f"{GRAPH}/me/messages#x", None),
]


@pytest.mark.parametrize("method,url,body", REFUSED, ids=[f"{m} {u[-48:]} {b}" for m, u, b in REFUSED])
def test_GraphRequest_RefusedShape_RaisesAndNothingReachesTheNetwork(world, method, url, body):
    with pytest.raises(world.screen.RefusedRequest):
        world.screen.graph_request(method, url, "token-for-email@devthrottle.com", body)
    assert world.graph.log == []


@pytest.mark.parametrize("destination", ["archive", "junkemail"])
def test_GraphRequest_TheTwoMoves_ReachTheNetwork(world, destination):
    world.graph.box(MAILBOX)["inbox"].append(msg("AAMkAGI2THVSAAA=", "2026-10-18T09:00:00Z"))
    world.screen.graph_request("POST", MOVE, "token-for-email@devthrottle.com", {"destinationId": destination})
    assert [(m, u, b) for m, u, b, _ in world.graph.log] == [("POST", MOVE, f'{{"destinationId": "{destination}"}}')]


def test_GraphRequest_AGet_ReachesTheNetwork(world):
    url = f"{GRAPH}/me/mailFolders/inbox/messages?%24orderby=receivedDateTime+asc&%24top=5"
    world.screen.graph_request("GET", url, "token-for-email@devthrottle.com")
    assert [(m, u) for m, u, _, _ in world.graph.log] == [("GET", url)]


def test_Source_OnlyTheGuardReachesTheNetwork(world):
    """The guard holds only if nothing goes round it: urlopen appears once, inside transport(); transport() is called
    once from each of the three guards - graph_request(), and since the Gmail reader gmail_request() and
    google_token_request() (docs/qa/mail-desk/gmail/CONTRACT.md) - and each guard checks before it calls."""
    source = world.screen.__file__ and open(world.screen.__file__, encoding="utf-8").read()
    assert len(re.findall(r"urlopen\(", source)) == 1
    assert len(re.findall(r"(?<!def )transport\(", source)) == 3
    guard = source.split("def graph_request(")[1].split("\ndef ")[0]
    assert guard.index("check_request(") < guard.index("transport(")
    gmail = source.split("def gmail_request(")[1].split("\ndef ")[0]
    assert gmail.index("check_gmail_request(") < gmail.index("transport(")
    token = source.split("def google_token_request(")[1].split("\ndef ")[0]
    assert token.index("raise RefusedRequest(") < token.index("transport(")
    for forbidden in ("http.client", "requests", "socket.create_connection", "subprocess"):
        assert f"import {forbidden}" not in source


def test_FilingQueue_ADestinationOtherThanTheTwo_IsRefusedAndNeverSent(world):
    """A filing queue the Gateway got wrong cannot make the check send anything either."""
    world.plant()
    world.graph.box(MAILBOX)["inbox"].append(msg("id-1", "2026-10-18T09:00:00Z"))
    key = world.screen.short_key(MAILBOX, "id-1")
    world.memory(channel(mode="live", cursor={"newest": "2026-10-18T09:00:00Z", "idsAtNewest": ["id-1"]},
                         keys={key: {"id": "id-1", "reportedUtc": "2026-10-18T09:05:00Z"}},
                         filingQueue=[{"messageKey": key, "decision": "deleteditems"}, {"messageKey": key, "decision": "sendMail"}]))
    code, output, context, _ = world.run()
    assert code == 0
    assert world.graph.posts() == []
    assert [r["result"] for r in context["channels"][0]["filing"]] == ["refused", "refused"]
    assert world.read_memory()["channels"][0]["filingQueue"] == []


def test_FilingQueue_AnIdWithASlash_IsRefusedByTheGuardAndNeverSent(world):
    """A key table entry whose id would reach another path is refused by the guard, reported, and nothing is sent."""
    world.plant()
    key = "0123456789abcdef"
    world.memory(channel(mode="live", cursor={"newest": "2026-10-18T09:00:00Z", "idsAtNewest": []},
                         keys={key: {"id": "abc/../../sendMail", "reportedUtc": "2026-10-18T09:05:00Z"}},
                         filingQueue=[{"messageKey": key, "decision": "archive"}]))
    code, _, context, _ = world.run()
    assert code == 0
    assert world.graph.posts() == []
    assert context["channels"][0]["filing"][0]["result"] == "refused"


# ---------------------------------------------------------------------------------------------------- review finding 3


@pytest.mark.parametrize("bad", ["\n", "\r", "\t", " ", "\x00", "\x1f", "\x7f"],
                         ids=["line-feed", "carriage-return", "tab", "space", "null", "unit-separator", "delete"])
def test_Guard_AnIdHoldingAControlCharacter_IsRefusedByTheGuardItself(world, bad):
    """urlsplit silently drops tab, carriage return and line feed, so these must be refused before it parses."""
    url = f"{GRAPH}/me/messages/abc{bad}def/move"
    with pytest.raises(world.screen.RefusedRequest, match="space or a control character"):
        world.screen.graph_request("POST", url, "token-for-email@devthrottle.com", {"destinationId": "archive"})
    with pytest.raises(world.screen.RefusedRequest):
        world.screen.graph_request("GET", f"{GRAPH}/me/mailFolders/inbox{bad}/messages", "token-for-x")
    assert world.graph.log == []


def test_FilingQueue_AnIdWithALineFeed_IsRefusedForThatItemOnly(world):
    """Per item, as the contract promises: the bad id is refused and reported, the good one is moved, the run
    succeeds, and both leave the queue."""
    world.plant()
    world.graph.box(MAILBOX)["inbox"].append(msg("AAMkGood=", "2026-10-18T06:00:00Z"))
    bad, good = "0123456789abcdef", world.screen.short_key(MAILBOX, "AAMkGood=")
    world.memory(channel(mode="live", cursor={"newest": "2026-10-18T09:00:00Z", "idsAtNewest": []},
                         keys={bad: {"id": "AAMk\nBad", "reportedUtc": "2026-10-18T06:05:00Z"},
                               good: {"id": "AAMkGood=", "reportedUtc": "2026-10-18T06:05:00Z"}},
                         filingQueue=[{"messageKey": bad, "decision": "archive"},
                                      {"messageKey": good, "decision": "archive"}]))
    code, _, context, _ = world.run()
    assert code == 0
    assert [r["result"] for r in context["channels"][0]["filing"]] == ["refused", "moved"]
    assert [p[1].split("/")[-2] for p in world.graph.posts()] == ["AAMkGood="]
    assert world.read_memory()["channels"][0]["filingQueue"] == []


# ---------------------------------------------------------------------------------------------------- observation 5


def test_FilingQueue_ARefusedMove_DoesNotQuoteTheGraphId(world):
    world.plant()
    key = "0123456789abcdef"
    world.memory(channel(mode="live", cursor={"newest": "2026-10-18T09:00:00Z", "idsAtNewest": []},
                         keys={key: {"id": "AAMkSecret/other", "reportedUtc": "2026-10-18T09:05:00Z"}},
                         filingQueue=[{"messageKey": key, "decision": "archive"}]))
    code, output, context, err = world.run()
    assert code == 0
    assert context["channels"][0]["filing"][0]["result"] == "refused"
    assert "AAMkSecret" not in output["context"] and "AAMkSecret" not in err
