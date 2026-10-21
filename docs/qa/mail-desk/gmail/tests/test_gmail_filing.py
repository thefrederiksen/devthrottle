"""R3 and R4 in a whole run: observe mode never files, live mode archives and junks with exactly the two bodies, and a
message the Gmail API no longer has is already-gone. Filing in Gmail keeps the message id, so a retry is harmless."""

import json

from gmail_rig import gw, world  # noqa: F401 - the fixtures
from gmail_rig import GMAIL_BOX, channel, cursor, gmsg, section

API = "https://gmail.googleapis.com/gmail/v1/users/me"
CURSOR = cursor("2026-10-18T09:00:00Z", "aa01")


def queued(gw, mode, *items):
    """A Gmail channel whose key table holds aa01 and aa02, and a filing queue of (key, decision)."""
    gw.plant_gmail()
    gw.gmail.box(GMAIL_BOX).extend([gmsg("aa01", "2026-10-18T08:00:00Z"), gmsg("aa02", "2026-10-18T09:00:00Z")])
    keys = {gw.screen.short_key(GMAIL_BOX, mid): {"id": mid, "reportedUtc": "2026-10-18T09:05:00Z"}
            for mid in ("aa01", "aa02", "gone99")}
    queue = [{"messageKey": gw.screen.short_key(GMAIL_BOX, mid), "decision": decision} for mid, decision in items]
    gw.memory(channel(GMAIL_BOX, mode=mode, provider="gmail", cursor=CURSOR, keys=keys, filingQueue=queue))
    return queue


def test_ObserveMode_AQueueIsNotCarriedOut_NoModifyIsSentAndTheQueueStays(gw):
    queue = queued(gw, "observe", ("aa01", "archive"), ("aa02", "junk"))
    code, output, context, _ = gw.run()
    assert code == 0
    assert gw.gmail.posts() == []
    assert all("/modify" not in url for _, url, _, _ in gw.gmail.log)
    assert len(gw.gmail.log) > 0      # the run did read the mailbox: the empty list of posts is not an idle instrument
    mine = section(context, GMAIL_BOX)
    assert mine["filing"] == [] and mine["filingNotCarriedOut"] == {"count": 2, "reason": "the channel is in observe mode"}
    assert gw.read_memory()["channels"][0]["filingQueue"] == queue
    assert output["count"] == 0
    assert [m["labelIds"] for m in gw.gmail.box(GMAIL_BOX)] == [["INBOX"], ["INBOX"]]


def test_LiveMode_ArchiveAndJunk_AreSentWithExactlyTheTwoBodies(gw):
    queued(gw, "live", ("aa01", "archive"), ("aa02", "junk"))
    code, output, context, _ = gw.run()
    assert code == 0
    assert [(m, u, json.loads(b)) for m, u, b, _ in gw.gmail.posts()] == [
        ("POST", f"{API}/messages/aa01/modify", {"removeLabelIds": ["INBOX"]}),
        ("POST", f"{API}/messages/aa02/modify", {"addLabelIds": ["SPAM"], "removeLabelIds": ["INBOX"]})]
    assert [b for _, _, b, _ in gw.gmail.posts()] == ['{"removeLabelIds": ["INBOX"]}',
                                                      '{"addLabelIds": ["SPAM"], "removeLabelIds": ["INBOX"]}']
    assert [r["result"] for r in section(context, GMAIL_BOX)["filing"]] == ["moved", "moved"]
    assert [m["labelIds"] for m in gw.gmail.box(GMAIL_BOX)] == [[], ["SPAM"]]
    assert gw.read_memory()["channels"][0]["filingQueue"] == []
    assert output["count"] == 2


def test_LiveMode_AMessageTheApiNoLongerHas_IsAlreadyGone(gw):
    queued(gw, "live", ("gone99", "archive"), ("aa01", "junk"))
    _, _, context, _ = gw.run()
    assert [r["result"] for r in section(context, GMAIL_BOX)["filing"]] == ["already-gone", "moved"]


def test_LiveMode_ARetriedArchive_KeepsTheIdAndChangesNothing(gw):
    """Gmail keeps a filed message's id (unlike Graph's move): the same archive sent twice is a harmless no-op."""
    queued(gw, "live", ("aa01", "archive"))
    gw.run()
    memory = gw.read_memory()
    memory["channels"][0]["filingQueue"] = [{"messageKey": gw.screen.short_key(GMAIL_BOX, "aa01"), "decision": "archive"}]
    gw.state.write_text(json.dumps(memory), encoding="utf-8")
    _, _, context, _ = gw.run()
    assert [r["result"] for r in section(context, GMAIL_BOX)["filing"]] == ["moved"]
    assert gw.gmail.box(GMAIL_BOX)[0]["labelIds"] == []


def test_LiveMode_AModifyFailing_FailsThatChannelAndTheQueueStaysWhole(gw):
    queue = queued(gw, "live", ("aa01", "archive"), ("aa02", "junk"))
    gw.gmail.fail_for[GMAIL_BOX] = 500
    code, _, context, _ = gw.run()
    assert code == 0
    failure = section(context, GMAIL_BOX)["failure"]
    assert failure["sentence"] == f"the Gmail API answered HTTP 500 for {GMAIL_BOX} while archiving a message"
    assert "aa01" not in failure["detail"] and "/messages/{id}/modify" in failure["detail"]
    assert gw.read_memory()["channels"][0]["filingQueue"] == queue


def test_LiveMode_ADecisionOtherThanTheTwo_IsRefusedAndNeverSent(gw):
    queued(gw, "live", ("aa01", "trash"), ("aa02", "send"))
    _, _, context, _ = gw.run()
    assert [r["result"] for r in section(context, GMAIL_BOX)["filing"]] == ["refused", "refused"]
    assert gw.gmail.posts() == []
