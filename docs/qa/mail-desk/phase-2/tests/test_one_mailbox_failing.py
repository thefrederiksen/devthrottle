"""Phase 3, section 6.1 of docs/qa/mail-desk/phase-3/CONTRACT.md: one mailbox failing does not stop the others.

The Delivery Lead's ruling of 18 October 2026: each mailbox fails on its own, as its own named problem, while the others
are screened and keep their memory. So a failure that belongs to one channel goes into that channel's section of the
context as "failure": {"sentence", "detail"}, that channel's own memory fields come back byte for byte as they were,
every other channel is read as if the failing one were not there, and the exit is 0. A failure of the whole memory, or
one the check did not foresee, is still exit 1 with the memory untouched.

Every test here has two channels unless it says otherwise: MAILBOX is healthy and has one new message, SECOND fails.
"""

import json
import os

import pytest
import requests

from conftest import MACHINE, MAILBOX, SECOND, channel, channel_failed, entry_text, msg

CLIENT = "f575e3f4-8f62-4bfb-8f30-f776e55b51bd"
CURSOR = {"newest": "2026-10-18T07:00:00Z", "idsAtNewest": []}
FRESH_SENT = {"refreshedUtc": "2026-10-18T11:00:00Z", "hashes": []}
NEW_MAIL = "2026-10-18T09:00:00Z"


def compact_memory(world, *channels):
    """The memory written exactly as the check writes it, so "byte for byte" is a comparison of bytes in the file."""
    world.state.write_text(json.dumps({"channels": list(channels)}, ensure_ascii=False, separators=(",", ":")),
                           encoding="utf-8")


def healthy(world):
    world.plant(MAILBOX)
    world.graph.box(MAILBOX)["inbox"].append(msg("AAMkHealthy1=", NEW_MAIL))
    return channel(MAILBOX, cursor=CURSOR, sentCache=FRESH_SENT)


def assert_healthy_was_read(world, context):
    first = context["channels"][0]
    assert first["address"] == MAILBOX and "failure" not in first
    assert [m["messageKey"] for m in first["messages"]] == [world.screen.short_key(MAILBOX, "AAMkHealthy1=")]
    assert world.read_memory()["channels"][0]["cursor"] == {"newest": NEW_MAIL, "idsAtNewest": ["AAMkHealthy1="]}


# ---------------------------------------------------------------------------------------------------- the first promise


def test_OneChannelNotSignedIn_TheOtherIsReadAndItsCursorMoves(world):
    world.register({MAILBOX: {"client_id": CLIENT}, SECOND: {"client_id": CLIENT}})
    world.memory(healthy(world), channel(SECOND, cursor=CURSOR))

    code, output, context, _ = world.run()

    assert code == 0
    assert output["count"] == 1
    assert context["version"] == 2
    assert_healthy_was_read(world, context)
    second = context["channels"][1]
    assert second["failure"]["sentence"] == f"{SECOND} is not signed in on {MACHINE}: run cc-outlook -a example auth"
    assert "found none" in second["failure"]["detail"]
    assert {entry[3] for entry in world.graph.log} == {f"token-for-{MAILBOX}"}   # nothing was asked for SECOND


def test_ChannelFailing_ItsFieldsComeBackByteForByte_AfterAMoveTheSentFolderAndThenTheInboxFailing(world, monkeypatch):
    """The hardest case for "as it was": SECOND is live, its queued move is carried out, its Sent folder is read again
    (so the check had already changed its queue and its sentCache), and THEN its Inbox fails. Every field it owns and
    every field the Gateway owns comes back exactly as the memory held it, bytes and order alike."""
    world.plant(SECOND)
    graph_id = "AAMkSecond1="
    world.graph.box(SECOND)["inbox"].append(msg(graph_id, "2026-10-18T06:00:00Z"))
    key = world.screen.short_key(SECOND, graph_id)
    failing = channel(SECOND, mode="live", id="c-0002", machines=["devlinux"], cursor=CURSOR,
                      sentCache={"refreshedUtc": "2026-10-01T00:00:00Z", "hashes": ["5d41402abc4b"]},
                      keys={key: {"id": graph_id, "reportedUtc": "2026-10-18T06:05:00Z"}},
                      filingQueue=[{"messageKey": key, "decision": "archive"}])
    compact_memory(world, healthy(world), failing)
    before = world.state.read_bytes()
    second_before = entry_text(json.loads(before), SECOND).encode("utf-8")
    assert second_before in before
    real = world.graph.answer

    def inbox_fails_for_second(method, url, body, token):
        if token == f"token-for-{SECOND}" and "/mailFolders/inbox/" in url:
            return 503, {}
        return real(method, url, body, token)

    monkeypatch.setattr(world.graph, "answer", inbox_fails_for_second)

    code, output, context, _ = world.run()

    assert code == 0
    assert world.graph.box(SECOND)["archive"], "the move did happen before the Inbox failed"
    assert any("/sentitems/" in entry[1] and entry[3] == f"token-for-{SECOND}" for entry in world.graph.log)
    after = world.state.read_bytes()
    assert second_before in after                                   # the very same bytes
    assert json.loads(after)["channels"][1] == json.loads(before)["channels"][1]
    second = context["channels"][1]
    assert second["failure"]["sentence"] == f"Microsoft Graph answered HTTP 503 for {SECOND} while reading the Inbox"
    assert second["filing"] == [] and second["messages"] == [] and second["more"] is False
    assert output["count"] == 1                                     # the healthy message; the lost move is not counted
    assert_healthy_was_read(world, context)


def test_ChannelFailing_TheMoveDoneBeforeItIsAlreadyGoneNextRun(world, monkeypatch):
    """Phase 2's retry still holds per channel: the queue came back whole, so next run the move is tried again and
    Graph no longer has that id. Nothing is moved twice."""
    world.plant(SECOND)
    graph_id = "AAMkSecond1="
    world.graph.box(SECOND)["inbox"].append(msg(graph_id, "2026-10-18T06:00:00Z"))
    key = world.screen.short_key(SECOND, graph_id)
    world.memory(healthy(world), channel(SECOND, mode="live", cursor=CURSOR, sentCache=FRESH_SENT,
                                         keys={key: {"id": graph_id, "reportedUtc": "2026-10-18T06:05:00Z"}},
                                         filingQueue=[{"messageKey": key, "decision": "junk"}]))
    real = world.graph.answer
    monkeypatch.setattr(world.graph, "answer", lambda m, u, b, t: (500, {}) if m == "GET" and t.endswith(SECOND)
                        else real(m, u, b, t))
    channel_failed(world, SECOND)
    monkeypatch.setattr(world.graph, "answer", real)

    code, _, context, _ = world.run()

    assert code == 0
    assert context["channels"][1]["filing"] == [{"messageKey": key, "decision": "junk", "result": "already-gone"}]
    assert len(world.graph.box(SECOND)["junkemail"]) == 1


def test_ChannelFailing_ThenFixed_ItsMailIsReadFromWhereItWas(world):
    world.register({SECOND: {"client_id": CLIENT}})
    world.graph.box(SECOND)["inbox"].append(msg("AAMkSecond1=", "2026-10-18T08:00:00Z"))
    world.memory(healthy(world), channel(SECOND, cursor=CURSOR, sentCache=FRESH_SENT))
    channel_failed(world, SECOND)

    world.plant(SECOND)
    code, _, context, _ = world.run()

    assert code == 0
    assert "failure" not in context["channels"][1]
    assert [m["messageKey"] for m in context["channels"][1]["messages"]] == [
        world.screen.short_key(SECOND, "AAMkSecond1=")]


def test_ChannelFailing_FirstInOrder_DoesNotHoldBackTheOther(world):
    """The failing channel comes first in the memory here, so its failure is met before the healthy one is read."""
    world.memory(channel(SECOND, cursor=CURSOR), healthy(world))
    code, output, context, _ = world.run()
    assert code == 0 and output["count"] == 1
    assert "is not signed in" in context["channels"][0]["failure"]["sentence"]
    assert len(context["channels"][1]["messages"]) == 1


# ---------------------------------------------------------------------------------------------------- every kind


def no_sign_in(world):
    world.register({SECOND: {"client_id": CLIENT}})
    return f"{SECOND} is not signed in on {MACHINE}: run cc-outlook -a example auth", {}


def not_in_cc_outlook(world):
    return f"{SECOND} is not signed in on {MACHINE}: first run cc-outlook accounts add {SECOND} -c {CLIENT}", {}


def signed_in_twice(world):
    world.plant(SECOND, instance="default")
    world.plant(SECOND, instance="slot5")
    return f"{SECOND} is signed in more than once on {MACHINE}", {}


def cache_holds_no_account(world):
    world.plant(SECOND)
    world.sign_in.accounts[SECOND] = False
    return f"{SECOND} is not signed in on {MACHINE}: first run cc-outlook accounts add", {}


def expired(world):
    world.plant(SECOND)
    world.sign_in.renew_fails.add(SECOND)
    return f"{SECOND} is not signed in on {MACHINE}", {}


def account_list_unreadable(world, monkeypatch):
    listed = world.register({SECOND: {"client_id": CLIENT}})
    real = type(listed).read_text

    def read_text(self, *args, **kwargs):
        if self.name == "profiles.json":
            raise PermissionError(13, "Permission denied", str(self))
        return real(self, *args, **kwargs)

    monkeypatch.setattr(type(listed), "read_text", read_text)
    return f"{SECOND} is not signed in on {MACHINE}, and cc-outlook's account list cannot be read: PermissionError", {}


def sign_in_file_unreadable(world):
    """Review finding 2: the saved sign-in is there and cannot be read - here a folder in its place, which fails the
    same way on every system (a file the user may not read is tested on its own below)."""
    folder = world.base / "instances" / "default" / "config" / "outlook" / "tokens" / "second_example_org_msal.json"
    folder.mkdir(parents=True)
    world.sign_in.accounts[SECOND] = True
    return f"the saved sign-in of {SECOND} on {MACHINE} cannot be read (IsADirectoryError): check its permissions or sign in again", {}


def sign_in_write_back_fails(world, monkeypatch):
    """Review finding 2: the renewed sign-in cannot be written back (a full disk)."""
    world.plant(SECOND)
    world.sign_in.renews.add(SECOND)
    real = type(world.state).write_text

    def write_text(self, *args, **kwargs):
        if self.name.endswith("_msal.json"):
            raise OSError(28, "No space left on device", str(self))
        return real(self, *args, **kwargs)

    monkeypatch.setattr(type(world.state), "write_text", write_text)
    return f"the renewed sign-in of {SECOND} could not be saved on {MACHINE} (OSError): check the file's permissions", {}


def sign_in_service_unreachable(world):
    world.plant(SECOND)
    world.sign_in.renew_raises[SECOND] = requests.exceptions.ConnectionError("Max retries exceeded")
    return f"could not reach the Microsoft sign-in service from {MACHINE} for {SECOND}: ConnectionError", {}


def sign_in_service_failing(world):
    world.plant(SECOND)
    world.sign_in.renew_raises[SECOND] = ValueError("AADSTS50000: the service failed")
    return f"the Microsoft sign-in service failed on {MACHINE} for {SECOND}: AADSTS50000", {}


def graph_http(world):
    world.plant(SECOND)
    world.graph.fail_for[SECOND] = 500
    return f"Microsoft Graph answered HTTP 500 for {SECOND} while reading the Inbox", {}


def graph_unreachable(world):
    world.plant(SECOND)
    world.graph.fail_for[SECOND] = "unreachable"
    return f"Microsoft Graph could not be reached from {MACHINE} for {SECOND} while reading the Inbox", {}


def graph_not_json(world):
    world.plant(SECOND)
    world.graph.fail_for[SECOND] = "not-json"
    return f"Microsoft Graph answered something that is not JSON for {SECOND} while reading the Inbox", {}


def graph_no_value(world):
    world.plant(SECOND)
    world.graph.fail_for[SECOND] = "no-value"
    return f"Microsoft Graph answered a page without a 'value' list for {SECOND} while reading the Inbox", {}


def bad_message(world):
    world.plant(SECOND)
    world.graph.box(SECOND)["inbox"].append({"receivedDateTime": NEW_MAIL, "subject": "no id"})
    return f"Microsoft Graph answered a message without a usable id or receive time for {SECOND}", {}


def move_failing(world, monkeypatch):
    world.plant(SECOND)
    key = world.screen.short_key(SECOND, "AAMkSecond1=")
    real = world.graph.answer
    monkeypatch.setattr(world.graph, "answer", lambda m, u, b, t: (403, {}) if m == "POST" else real(m, u, b, t))
    return (f"Microsoft Graph answered HTTP 403 for {SECOND} while moving a message to archive",
            {"mode": "live", "keys": {key: {"id": "AAMkSecond1=", "reportedUtc": "2026-10-18T06:05:00Z"}},
             "filingQueue": [{"messageKey": key, "decision": "archive"}]})


def unknown_provider(world):
    return f"provider imap has no reader in this version of screen.py (channel {SECOND})", {"provider": "imap"}


def bad_mode(world):
    world.plant(SECOND)
    return f"the channel {SECOND} has mode 'sort'; it must be observe or live", {"mode": "sort"}


def bad_cursor(world):
    world.plant(SECOND)
    return f"the channel {SECOND} has a 'cursor' this check did not write", {"cursor": {"newest": 5}}


def bad_keys(world):
    world.plant(SECOND)
    return f"the channel {SECOND} has a 'keys' table this check did not write", {"keys": ["0123456789abcdef"]}


def bad_sent_cache(world):
    world.plant(SECOND)
    return f"the channel {SECOND} has a 'sentCache' this check did not write", {"sentCache": "yesterday"}


def queue_not_a_list(world):
    world.plant(SECOND)
    return f"the channel {SECOND} has a 'filingQueue' that is not a list", {"filingQueue": {"a": 1}}


NEEDS_MONKEYPATCH = {account_list_unreadable, move_failing, sign_in_write_back_fails}
KINDS = [no_sign_in, not_in_cc_outlook, signed_in_twice, cache_holds_no_account, expired, account_list_unreadable,
         sign_in_file_unreadable, sign_in_write_back_fails, sign_in_service_unreachable, sign_in_service_failing, graph_http, graph_unreachable, graph_not_json,
         graph_no_value, bad_message, move_failing, unknown_provider, bad_mode, bad_cursor, bad_keys, bad_sent_cache,
         queue_not_a_list]


@pytest.mark.parametrize("kind", KINDS, ids=[k.__name__ for k in KINDS])
def test_EachChannelFailureKind_IsInItsOwnSection_AndTheOtherChannelIsRead(world, monkeypatch, kind):
    first = healthy(world)
    expected, fields = kind(world, monkeypatch) if kind in NEEDS_MONKEYPATCH else kind(world)
    world.memory(first, channel(SECOND, **dict({"cursor": CURSOR, "sentCache": FRESH_SENT}, **fields)))
    first_before = world.read_memory()["channels"][0]

    failure = channel_failed(world, SECOND)

    sentence = failure.splitlines()[0]
    assert sentence.startswith(expected), sentence
    assert len(sentence) <= world.screen.SENTENCE_LIMIT
    assert "Traceback" not in failure
    _, _, context, _ = world.run()   # the same again: the healthy channel now has nothing new, and still no failure
    assert "failure" not in context["channels"][0]
    after = world.read_memory()["channels"][0]
    assert after["cursor"] != first_before["cursor"], "the healthy channel's cursor moved on the first run"


@pytest.mark.parametrize("kind", KINDS, ids=[k.__name__ for k in KINDS])
def test_EachChannelFailureKind_TheHealthyChannelsReportIsWhole(world, monkeypatch, kind):
    first = healthy(world)
    _, fields = kind(world, monkeypatch) if kind in NEEDS_MONKEYPATCH else kind(world)
    world.memory(first, channel(SECOND, **dict({"cursor": CURSOR, "sentCache": FRESH_SENT}, **fields)))

    code, output, context, _ = world.run()

    assert code == 0
    assert output["count"] == 1
    assert_healthy_was_read(world, context)
    assert [s["address"] for s in context["channels"]] == [MAILBOX, SECOND]


# ---------------------------------------------------------------------------------------------------- both failing


def test_BothChannelsFailing_ExitZeroCountZero_EachFailureInItsSection_MemoryAsItWas(world):
    world.register({MAILBOX: {"client_id": CLIENT, "nickname": "devthrottle"}})
    world.plant(SECOND)
    world.graph.fail_for[SECOND] = "unreachable"
    compact_memory(world, channel(MAILBOX, id="c-0001", cursor=CURSOR),
                   channel(SECOND, id="c-0002", cursor=CURSOR, sentCache=FRESH_SENT,
                           filingQueue=[{"messageKey": "0123456789abcdef", "decision": "junk"}]))
    before = world.state.read_bytes()

    code, output, context, _ = world.run()

    assert code == 0
    assert output == {"count": 0, "context": output["context"]}
    assert world.state.read_bytes() == before                    # every channel failed: nothing in the memory moved
    first, second = context["channels"]
    assert (first["id"], second["id"]) == ("c-0001", "c-0002")
    assert first["failure"]["sentence"] == f"{MAILBOX} is not signed in on {MACHINE}: run cc-outlook -a devthrottle auth"
    assert second["failure"]["sentence"].startswith(f"Microsoft Graph could not be reached from {MACHINE} for {SECOND}")
    for section in (first, second):
        assert section["messages"] == [] and section["filing"] == [] and section["more"] is False


def test_TheOnlyChannelFailing_IsExitZeroWithCountZero(world):
    world.memory(channel(MAILBOX, cursor=CURSOR))
    code, output, context, _ = world.run()
    assert code == 0 and output["count"] == 0
    assert "is not signed in" in context["channels"][0]["failure"]["sentence"]


# ---------------------------------------------------------------------------------------------------- the whole run


WHOLE_RUN = [
    ("missing", None, "does not exist"),
    ("empty", "", "memory is empty"),
    ("not-utf8", b'{"channels": "\xff\xfe"}', "memory could not be read"),
    ("not-json", "not json", "memory is not JSON"),
    ("no-channels", '{"cursor": {}}', "holds no 'channels'"),
    ("empty-channels", '{"channels": []}', "holds no 'channels'"),
    ("one-address-twice", json.dumps({"channels": [channel(MAILBOX, cursor=CURSOR),
                                                   channel("Email@DevThrottle.com", cursor=CURSOR),
                                                   channel(SECOND, cursor=CURSOR)]}), "memory twice"),
    ("a-channel-without-address", json.dumps({"channels": [channel(MAILBOX, cursor=CURSOR),
                                                           {"provider": "outlook", "mode": "observe"}]}),
     "has no 'address'"),
    ("over-the-cap", json.dumps({"channels": [channel(MAILBOX, cursor=CURSOR),
                                              channel(SECOND, cursor=CURSOR, padding="x" * (64 * 1024))]}),
     "over the Director's 65536"),
]


@pytest.mark.parametrize("name,content,said", WHOLE_RUN, ids=[w[0] for w in WHOLE_RUN])
def test_WholeRunFailures_StillExitOneWithTheMemoryUntouched(world, name, content, said):
    """A healthy, signed-in channel in the same memory does not turn any of these into a success."""
    world.plant(MAILBOX)
    world.plant(SECOND)
    if content is None:
        world.state.unlink()
    elif isinstance(content, bytes):
        world.state.write_bytes(content)
    else:
        world.state.write_text(content, encoding="utf-8")
    before = world.state.read_bytes() if world.state.exists() else None

    code, output, _, err = world.run()

    assert code == 1 and output is None
    assert said in err.splitlines()[0]
    assert (world.state.read_bytes() if world.state.exists() else None) == before


def test_AnUnforeseenErrorInsideOneChannel_StillFailsTheWholeRun(world, monkeypatch):
    """Only the failures the check names are one channel's. Anything else is a defect in the check, and a defect must
    not be passed off as one mailbox's problem."""
    world.memory(healthy(world), channel(SECOND, cursor=CURSOR))
    world.plant(SECOND)
    before = world.state.read_bytes()
    real = world.screen.OutlookReader.sent_hashes

    def broken(self):
        if self.address == SECOND:
            raise KeyError("toRecipients")
        return real(self)

    monkeypatch.setattr(world.screen.OutlookReader, "sent_hashes", broken)
    code, _, _, err = world.run()
    assert code == 1
    assert err.splitlines()[0] == "screen.py stopped on an error it does not handle: KeyError: 'toRecipients'"
    assert world.state.read_bytes() == before


# ---------------------------------------------------------------------------------------------------- the shape


def test_AFailingChannel_KeepsItsIdAddressProviderAndModeInItsSection(world):
    world.memory(healthy(world), channel(SECOND, id="c-0002", mode="live", cursor=CURSOR))
    _, _, context, _ = world.run()
    second = context["channels"][1]
    assert {k: second[k] for k in ("id", "address", "provider", "mode")} == {
        "id": "c-0002", "address": SECOND, "provider": "outlook", "mode": "live"}
    assert list(second) == ["id", "address", "provider", "mode", "messages", "filing", "more", "failure"]


def test_AFailingChannelInObserveMode_SaysNothingOfItsQueue(world):
    """The failure is the whole story of that channel this run; its queue is untouched and reported next time."""
    world.memory(healthy(world), channel(SECOND, cursor=CURSOR, filingQueue=[{"messageKey": "0123456789abcdef",
                                                                              "decision": "junk"}]))
    channel_failed(world, SECOND)


def test_AFailingChannelsDetail_IsCutSoItNeverCrowdsOutMail(world):
    world.plant(SECOND)
    world.sign_in.renew_raises[SECOND] = requests.exceptions.ConnectionError("x" * 2000)
    world.memory(healthy(world), channel(SECOND, cursor=CURSOR))
    detail = channel_failed(world, SECOND).splitlines()[1]
    assert len(detail) == world.screen.FAILURE_DETAIL_CHARS and detail.endswith("...")


def test_AFailureWithNoDetail_HasAnEmptyDetail(world):
    world.memory(healthy(world), channel(SECOND, provider="imap", cursor=CURSOR))
    _, _, context, _ = world.run()
    assert context["channels"][1]["failure"]["detail"] == ""


def test_TheLogOfARunWithAFailingChannel_NamesItAndHoldsNoMailText(world):
    world.graph.box(MAILBOX)["inbox"].clear()
    world.graph.box(MAILBOX)["inbox"].append(msg("AAMkHealthy1=", NEW_MAIL, sender="billing@example.com",
                                                 subject="Secret subject", name="Secret Name", preview="Secret body"))
    world.plant(MAILBOX)
    world.memory(channel(MAILBOX, cursor=CURSOR, sentCache=FRESH_SENT), channel(SECOND, cursor=CURSOR))
    _, _, _, log = world.run()
    assert f"screen.py: {SECOND}: could not be checked, the other channels go on: {SECOND} is not signed in" in log
    for text in ("billing@example.com", "Secret", "token-for-"):
        assert text not in log


@pytest.mark.skipif(os.name != "posix" or os.geteuid() == 0, reason="file permissions, as a user who is not root")
def test_ASavedSignInTheUserMayNotRead_FailsThatChannelOnly(world):
    """Review finding 2, as the reviewer measured it: the second mailbox's sign-in file at mode 000."""
    path = world.plant(SECOND)
    os.chmod(path, 0)
    try:
        world.memory(healthy(world), channel(SECOND, cursor=CURSOR, sentCache=FRESH_SENT))
        failure = channel_failed(world, SECOND)
    finally:
        os.chmod(path, 0o600)
    assert failure.splitlines()[0].startswith(f"the saved sign-in of {SECOND} on {MACHINE} cannot be read "
                                              f"(PermissionError): ")
    assert str(path) in failure.splitlines()[1]
    assert world.read_memory()["channels"][0]["cursor"]["newest"] == NEW_MAIL


# ---------------------------------------------------------------------------------------------------- many failing


def many(count, start=0):
    return [f"box{i:02d}@example.org" for i in range(start, start + count)]


def test_FourteenMailboxesAllFailing_ExitZeroWithEverySentence_TheLastLosingTheirDetailFirst(world):
    """Review finding 1: fourteen mailboxes not signed in made a context of 7,530 bytes and failed the whole run. The
    sentences all stay; detail lines go, from the last failing channel back, until the context fits."""
    addresses = many(14)
    world.memory(*[channel(a, cursor=CURSOR) for a in addresses])
    before = world.state.read_bytes()

    code, output, context, err = world.run()

    assert code == 0 and output["count"] == 0
    assert len(output["context"].encode("utf-8")) <= world.screen.CONTEXT_BUDGET_BYTES
    sentences = [s["failure"]["sentence"] for s in context["channels"]]
    assert sentences == [f"{a} is not signed in on {MACHINE}: first run cc-outlook accounts add {a} -c {CLIENT}"
                         for a in addresses]
    details = [s["failure"]["detail"] for s in context["channels"]]
    assert details[0] and details[-1] == ""
    assert details == sorted(details, key=lambda d: d == "")     # every kept detail comes before every dropped one
    assert world.state.read_bytes() == json.dumps(json.loads(before), ensure_ascii=False,
                                                  separators=(",", ":")).encode("utf-8")
    assert "reported without their detail" in err


@pytest.mark.parametrize("failing", [13, 20])
def test_AHealthyMailboxBesideManyFailing_IsReadAndItsCursorMoves(world, failing):
    """Review finding 1: one healthy mailbox and thirteen failing ones exited 1 with "a message's sender address alone
    is larger than the Mail Desk context budget". The failures now leave room for new mail."""
    world.memory(healthy(world), *[channel(a, cursor=CURSOR) for a in many(failing)])

    code, output, context, _ = world.run()

    assert code == 0 and output["count"] == 1
    assert_healthy_was_read(world, context)
    assert "trimmed" not in context["channels"][0]["messages"][0]
    assert all("failure" in s for s in context["channels"][1:])


def test_AHealthyMailboxBesideTenFailingWithFullDetails_IsRead(world):
    """The reviewer's earlier threshold: details at their full 500 characters."""
    failing = many(10)
    for address in failing:
        world.plant(address)
        world.sign_in.renew_raises[address] = requests.exceptions.ConnectionError("x" * 2000)
    world.memory(healthy(world), *[channel(a, cursor=CURSOR) for a in failing])

    code, output, context, _ = world.run()

    assert code == 0 and output["count"] == 1
    assert_healthy_was_read(world, context)


def test_MoreFailingMailboxesThanTheContextCanName_IsAWholeRunFailureSayingHowMany(world):
    """Past what the context can hold even without details, the run cannot name every failing mailbox. It fails as a
    whole, saying how many failed and which is first, never with a sentence about a sender address."""
    addresses = many(40)
    world.memory(*[channel(a, cursor=CURSOR) for a in addresses])
    before = world.state.read_bytes()

    code, _, _, err = world.run()

    assert code == 1
    assert world.state.read_bytes() == before
    lines = err.splitlines()
    assert lines[0] == (f"40 of 40 mailboxes failed on {MACHINE}, too many to name in the context; first: "
                        f"{addresses[0]}")
    assert len(lines[0]) <= world.screen.SENTENCE_LIMIT
    assert lines[1].startswith(f"{addresses[0]} is not signed in on {MACHINE}")



# ---------------------------------------------------------------------------------------------------- re-review


LONG_MACHINE = "DESKTOP-ABCD123"   # the longest name Windows gives a computer


def test_TooManyFailing_WithTheReviewersLongAddresses_TheSentenceStaysWithinTheLimitAndNamesIt(world, monkeypatch):
    """Re-review finding 4: the reviewer's long addresses (58 characters) made this sentence 175 characters."""
    monkeypatch.setattr(world.screen.socket, "gethostname", lambda: LONG_MACHINE)
    addresses = [f"a-rather-long-mailbox-name-number-{i:02d}@subdomain.example.org" for i in range(40)]
    assert len(addresses[0]) == 58
    world.memory(*[channel(a, cursor=CURSOR) for a in addresses])

    code, _, _, err = world.run()

    assert code == 1
    sentence = err.splitlines()[0]
    assert len(sentence) <= world.screen.SENTENCE_LIMIT
    assert sentence.endswith(f"; first: {addresses[0]}")


def test_TooManyFailing_WithAnAddressTooLongToFit_TheSentenceLeavesItToLineTwo(world, monkeypatch):
    monkeypatch.setattr(world.screen.socket, "gethostname", lambda: LONG_MACHINE)
    addresses = [f"{'x' * 90}{i:02d}@subdomain.example.org" for i in range(40)]
    world.memory(*[channel(a, cursor=CURSOR) for a in addresses])

    code, _, _, err = world.run()

    assert code == 1
    lines = err.splitlines()
    assert lines[0] == f"40 of 40 mailboxes failed on {LONG_MACHINE}, too many to name in the context"
    assert lines[1].startswith(f"{addresses[0]} is not signed in")


@pytest.mark.parametrize("content", ["[]", "1", '"x"', "null"])
def test_ASavedSignInThatIsJsonButNotAnObject_FailsThatChannelOnly(world, content):
    """Re-review finding 5: MSAL's cache takes any JSON and fails only at its first lookup, with an AttributeError
    the check did not name, which stopped every mailbox. It is refused first, as not a sign-in cache."""
    world.plant(SECOND, content=content)
    world.memory(healthy(world), channel(SECOND, cursor=CURSOR, sentCache=FRESH_SENT))
    failure = channel_failed(world, SECOND).splitlines()
    assert failure[0].startswith(f"{SECOND} is not signed in on {MACHINE}: ")
    assert "is not a sign-in cache" in failure[1]
    assert world.read_memory()["channels"][0]["cursor"]["newest"] == NEW_MAIL


@pytest.mark.parametrize("content", ["[]", "1", '"x"'])
def test_MsalsOwnCache_TakesJsonThatIsNotAnObjectAndFailsOnItsFirstLookup(content):
    """The premise of the test above, from the real library rather than assumed: the rig replaces the sign-in
    application, so this reads the real token cache the check deserializes into."""
    import msal
    cache = msal.SerializableTokenCache()
    cache.deserialize(content)
    with pytest.raises(AttributeError):
        list(cache.search(msal.TokenCache.CredentialType.ACCOUNT, query={"username": SECOND}))


@pytest.mark.parametrize("failing", [10, 20])
def test_AHeavyFilingReportBesideManyFailing_FilesLessAndTheRunSucceeds(world, failing):
    """Re-review observation D: a live mailbox with 60 queued moves and one new message, beside 10 mailboxes not
    signed in, failed the whole run every five minutes. Every channel now signs in before any files, so the filing
    report is sized to leave the failures and new mail their room: fewer are filed this run, the rest stay queued in
    order, and the next run goes on."""
    world.plant(MAILBOX)
    keys, queue = {}, []
    for i in range(60):
        graph_id = f"AAMkQueued{i:03d}="
        world.graph.box(MAILBOX)["inbox"].append(msg(graph_id, "2026-10-18T05:00:00Z"))
        key = world.screen.short_key(MAILBOX, graph_id)
        keys[key] = {"id": graph_id, "reportedUtc": "2026-10-18T05:05:00Z"}
        queue.append({"messageKey": key, "decision": "archive"})
    world.graph.box(MAILBOX)["inbox"].append(msg("AAMkHealthy1=", NEW_MAIL))
    world.memory(channel(MAILBOX, mode="live", cursor=CURSOR, sentCache=FRESH_SENT, keys=keys, filingQueue=queue),
                 *[channel(a, cursor=CURSOR) for a in many(failing)])

    code, output, context, _ = world.run()

    assert code == 0
    filed = context["channels"][0]["filing"]
    assert 0 < len(filed) < 48                                  # 48 were filed with no failures beside it
    assert [f["messageKey"] for f in filed] == [q["messageKey"] for q in queue[:len(filed)]]
    assert world.read_memory()["channels"][0]["filingQueue"] == queue[len(filed):]
    assert output["count"] == len(filed) + 1
    assert all("failure" in s for s in context["channels"][1:])

    code, _, context, _ = world.run()
    assert code == 0 and context["channels"][0]["filing"]       # the next run goes on filing
