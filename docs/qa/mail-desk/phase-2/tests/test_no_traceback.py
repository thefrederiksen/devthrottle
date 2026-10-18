"""Review finding 2: no failure reaches the Gateway as a traceback. Each one is one sentence naming what failed, the
memory is left byte for byte as it was, and nothing is hidden: the cause follows on the next line.

The realistic case is the sign-in service out of reach: MSAL calls it in its constructor (the authority's
configuration) and in the silent renewal, and raises its transport's error, which is a requests exception and so an
OSError. The rest are memories this check did not write, and anything else the check did not foresee.
"""

import pytest
import requests

from conftest import MACHINE, MAILBOX, channel, gateway_reason, msg

CURSOR = {"newest": "2026-10-18T07:00:00Z", "idsAtNewest": []}


def failed_with_sentence(world):
    before = world.state.read_bytes()
    code, output, _, err = world.run()
    assert code == 1 and output is None
    assert world.state.read_bytes() == before
    assert not err.startswith("Traceback")
    assert "Traceback" not in gateway_reason(err)
    return err.splitlines()


@pytest.mark.parametrize("where", ["construct", "renew"])
@pytest.mark.parametrize("error", [requests.exceptions.ConnectionError("Max retries exceeded"),
                                   requests.exceptions.ProxyError("Unable to connect to proxy"),
                                   requests.exceptions.Timeout("read timed out"),
                                   OSError("[Errno 101] Network is unreachable")],
                         ids=["ConnectionError", "ProxyError", "Timeout", "OSError"])
def test_SignIn_ServiceOutOfReach_IsOneSentenceNamingTheMachineAndTheError(world, where, error):
    world.plant()
    if where == "construct":
        world.sign_in.construct_raises = error
    else:
        world.sign_in.renew_raises[MAILBOX] = error
    world.memory(channel(cursor=CURSOR))
    lines = failed_with_sentence(world)
    assert lines[0] == (f"could not reach the Microsoft sign-in service from {MACHINE} for {MAILBOX}: "
                        f"{type(error).__name__}")
    assert str(error) in lines[1]                        # the cause, whole, on the detail line
    assert world.graph.log == []


def test_SignIn_ServiceAnsweringAnError_IsOneSentence(world):
    """MSAL raises ValueError when the authority's configuration cannot be read (authority.py tenant_discovery)."""
    world.plant()
    world.sign_in.construct_raises = ValueError("OIDC Discovery failed on https://login.microsoftonline.com/common. "
                                                "HTTP status: 503")
    world.memory(channel(cursor=CURSOR))
    lines = failed_with_sentence(world)
    assert lines[0].startswith(f"the Microsoft sign-in service failed on {MACHINE} for {MAILBOX}: OIDC Discovery")
    assert "HTTP status: 503" in lines[1]


@pytest.mark.parametrize("fields,named", [
    ({"keys": {"0123456789abcdef": {"id": "AAMk1"}}}, "'keys'"),                       # no reportedUtc
    ({"keys": {"0123456789abcdef": {"reportedUtc": "2026-10-18T06:00:00Z"}}}, "'keys'"),  # no id
    ({"keys": ["0123456789abcdef"]}, "'keys'"),
    ({"keys": {"0123456789abcdef": "AAMk1"}}, "'keys'"),
    ({"sentCache": "yesterday"}, "'sentCache'"),
    ({"sentCache": {"refreshedUtc": "2026-10-18T11:00:00Z"}}, "'sentCache'"),
    ({"sentCache": {"refreshedUtc": "2026-10-18T11:00:00Z", "hashes": "abc"}}, "'sentCache'"),
    ({"sentCache": {"refreshedUtc": 5, "hashes": []}}, "'sentCache'"),
    ({"cursor": {"newest": "2026-10-18T07:00:00Z", "idsAtNewest": [7]}}, "'cursor'"),
], ids=["key-without-time", "key-without-id", "keys-a-list", "key-not-an-object", "sent-a-string",
        "sent-without-hashes", "sent-hashes-not-a-list", "sent-time-not-text", "cursor-id-not-text"])
def test_Memory_AFieldThisCheckDidNotWrite_IsOneSentenceAndNoNetwork(world, fields, named):
    world.plant()
    world.memory(channel(**dict({"cursor": CURSOR}, **fields)))
    lines = failed_with_sentence(world)
    table = " table" if named == "'keys'" else ""
    assert lines[0] == f"the channel {MAILBOX} has a {named}{table} this check did not write"
    assert world.graph.log == []


def test_Memory_NotUtf8_IsOneSentence(world):
    world.state.write_bytes(b'{"channels": "\xff\xfe"}')
    lines = failed_with_sentence(world)
    assert lines[0].startswith("the Mail Desk check's memory could not be read: ")


@pytest.mark.parametrize("raw", [
    {"id": "AAMk1", "receivedDateTime": "2026-10-18T09:00:00Z", "subject": 5},
    {"id": "AAMk1", "receivedDateTime": "2026-10-18T09:00:00Z", "from": {"emailAddress": {"name": ["x"]}}},
    {"id": "AAMk1", "receivedDateTime": "2026-10-18T09:00:00Z", "bodyPreview": {"text": "x"}},
    {"id": 7, "receivedDateTime": "2026-10-18T09:00:00Z"},
], ids=["subject-a-number", "name-a-list", "preview-an-object", "id-a-number"])
def test_Graph_AMessageWithAFieldOfTheWrongKind_IsOneSentence(world, raw):
    world.plant()
    world.graph.box(MAILBOX)["inbox"].append(raw)
    world.memory(channel(cursor=CURSOR, sentCache={"refreshedUtc": "2026-10-18T11:00:00Z", "hashes": []}))
    lines = failed_with_sentence(world)
    assert lines[0] == (f"Microsoft Graph answered a message without a usable id or receive time for {MAILBOX} "
                        f"while reading the Inbox")


def test_Graph_APageThatIsAList_IsOneSentence(world, monkeypatch):
    world.plant()
    world.memory(channel(cursor=CURSOR))
    monkeypatch.setattr(world.graph, "answer", lambda *a: (200, [1, 2]))
    lines = failed_with_sentence(world)
    assert lines[0].startswith("Microsoft Graph answered a page without a 'value' list for ")


def test_AnythingUnforeseen_IsStillOneSentenceFirst_WithTheTracebackAfterIt(world, monkeypatch):
    """The entry point's own net: whatever escapes is named by its kind and message, the next line says where it
    stopped, the whole traceback comes last so the cause is not hidden, and the memory is not written."""
    world.plant()
    world.memory(channel(cursor=CURSOR))

    def broken(*args):
        raise KeyError("reportedUtc")

    monkeypatch.setattr(world.screen, "prune_keys", broken)
    lines = failed_with_sentence(world)
    assert lines[0] == "screen.py stopped on an error it does not handle: KeyError: 'reportedUtc'"
    assert lines[1].startswith("in broken, test_no_traceback.py line ")
    assert "Traceback (most recent call last):" in lines
    assert lines.index("Traceback (most recent call last):") > 2
