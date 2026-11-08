"""Filing a Zoho mailbox, and the guard on dt-zoho: observe mode files nothing; live mode moves one Inbox message at a
time to Archive or Spam, never with dt-zoho spam (which moves the whole conversation, the owner's sent replies too); a
message the Inbox no longer holds is already-gone and nothing moves; and no command that sends, replies, deletes,
marks spam, changes admin settings or touches the calendar can get past check_dt_zoho()."""

import re

import pytest

from zoho_rig import zw, world  # noqa: F401 - the fixtures
from zoho_rig import ARCHIVE, BOX, INBOX, SENT, SPAM, TRASH, cursor, mail, section, zoho

FOLDERS = {"inbox": INBOX, "sent": SENT, "archive": ARCHIVE, "spam": SPAM}


def queued(zw, mode, *items):
    """A channel whose Inbox holds two messages, whose key table holds them, and a queue of (index, decision)."""
    box = zw.tools.account()
    ids = box.inbox(mail("2026-10-18T08:00:00Z", thread="t1"), mail("2026-10-18T09:00:00Z", thread="t2"))
    keys = {zw.key(BOX, mid): {"id": mid, "reportedUtc": "2026-10-18T09:05:00Z"} for mid in ids}
    queue = [{"messageKey": zw.key(BOX, ids[i]), "decision": decision} for i, decision in items]
    zw.memory(zoho(mode=mode, cursor=cursor("2026-10-18T09:00:00Z", ids[1]), keys=keys, filingQueue=queue))
    return box, ids, queue


def test_ObserveMode_NothingIsFiled_TheQueueStays(zw):
    box, ids, queue = queued(zw, "observe", (0, "archive"), (1, "junk"))
    code, output, context, _ = zw.run()
    assert code == 0
    assert zw.tools.filed() == []
    assert "list" in zw.tools.commands()     # the run did reach the mailbox: an empty filing log is not an idle rig
    assert section(context)["filingNotCarriedOut"] == {"count": 2, "reason": "the channel is in observe mode"}
    assert zw.read_memory()["channels"][0]["filingQueue"] == queue
    assert box.held(INBOX) == sorted(ids)


def test_LiveMode_ArchiveToArchive_JunkToSpam_EachAMoveFromTheInbox(zw):
    box, ids, _ = queued(zw, "live", (0, "archive"), (1, "junk"))
    code, output, context, _ = zw.run()
    assert code == 0
    assert zw.tools.filed() == [["move", ids[0], "--to", ARCHIVE, "--from-folder", INBOX, "--account", BOX, "--json"],
                                ["move", ids[1], "--to", SPAM, "--from-folder", INBOX, "--account", BOX, "--json"]]
    assert [r["result"] for r in section(context)["filing"]] == ["moved", "moved"]
    assert box.held(INBOX) == [] and box.held(ARCHIVE) == [ids[0]] and box.held(SPAM) == [ids[1]]
    assert zw.read_memory()["channels"][0]["filingQueue"] == []
    assert output["count"] == 2


def test_LiveMode_JunkOnAConversationTheOwnerAnswered_TheSentReplyStaysInSent(zw):
    """dt-zoho's README: Zoho's spam moves every message in the conversation into Spam, your own sent replies too. The
    Mail Desk moves the one message instead."""
    box, ids, _ = queued(zw, "live", (0, "junk"))
    reply = box.add(SENT, mail("2026-10-18T08:30:00Z", sender=f"Desk <{BOX}>", thread="t1"))
    zw.run()
    assert box.held(SPAM) == [ids[0]]
    assert box.messages[reply]["folder"] == SENT
    assert "spam" not in zw.tools.commands()


@pytest.mark.parametrize("where", [ARCHIVE, TRASH, "deleted"])
def test_LiveMode_AMessageTheInboxNoLongerHolds_IsAlreadyGone_AndNothingMoves(zw, where):
    """The owner archived it, deleted it, or it is gone for good, since the Gateway queued it."""
    box, ids, _ = queued(zw, "live", (0, "junk"))
    if where == "deleted":
        del box.messages[ids[0]]
    else:
        box.messages[ids[0]]["folder"] = where
    _, _, context, _ = zw.run()
    assert section(context)["filing"] == [{"messageKey": zw.key(BOX, ids[0]), "decision": "junk",
                                           "result": "already-gone"}]
    assert "failure" not in section(context)
    assert box.held(SPAM) == [] and (where == "deleted" or box.messages[ids[0]]["folder"] == where)


def test_LiveMode_AnotherMoveError_FailsTheChannel_AndTheQueueComesBack(zw):
    box, ids, queue = queued(zw, "live", (0, "archive"))
    zw.tools.fail, zw.tools.fail_only = "Error: Zoho answered 500 to PUT /api/accounts/1/updatemessage: Internal Error.", "move"
    _, _, context, _ = zw.run()
    assert section(context)["failure"]["sentence"].startswith(f"dt-zoho failed for {BOX} while archiving a message: ")
    assert zw.read_memory()["channels"][0]["filingQueue"] == queue


A = ["--account", BOX, "--json"]


@pytest.mark.parametrize("args", [
    ["send", "--to", "someone@example.com", "--subject", "x", "--body", "y", *A],
    ["reply", "1", "--body", "y", *A],
    ["reply", "1", "--all", "--body", "y", *A],
    ["forward", "1", "--to", "someone@example.com", *A],
    ["draft", "save", "--to", "someone@example.com", *A],
    ["draft", "delete", "1", *A],
    ["mark", "1", "--as", "read", *A],
    ["delete", "1", *A],
    ["delete", "1", "--permanent", *A],
    ["spam", "1", *A],
    ["not-spam", "1", *A],
    ["archive", "1", *A],
    ["unarchive", "1", *A],
    ["label", "apply", "1", "--label", "x", *A],
    ["folder", "delete", "/Inbox", *A],
    ["folder", "empty", "/Inbox", *A],
    ["thread", "mark", "1", "--as", "read", *A],
    ["read", "1", *A],
    ["read", "1", "--raw", *A],
    ["download", "1", "--out-dir", ".", *A],
    ["search", "sender:someone", *A],
    ["alias", "add", "x@example.com", *A],
    ["imap", "off", *A],
    ["forwarding", "add", "x@example.com", *A],
    ["vacation", "set", "--subject", "x", "--content", "y", "--from", "2026-10-20", "--to", "2026-10-21", *A],
    ["signature", "add", "--name", "x", "--content", "y", *A],
    ["event", "create", "--title", "x", "--start", "2026-10-20 10:00", "--meeting", *A],
    ["calendar", "share", "x", "--with", "someone@example.com", *A],
    ["settings", "set", "--data-centre", "com", "--json"],
    ["signin", "forget", "--json"],
    ["allowed-recipients", "clear", "--json"],
    ["move", "1", "--to", TRASH, "--from-folder", INBOX, *A],
    ["move", "1", "--to", SENT, "--from-folder", INBOX, *A],
    ["move", "1", "--to", SPAM, *A],
    ["move", "1", "--to", SPAM, "--from-folder", ARCHIVE, *A],
    ["move", "1", "--to", INBOX, "--from-folder", SPAM, *A],
    ["move", "1,2", "--to", SPAM, "--from-folder", INBOX, *A],
    ["move", "1", "2", "--to", SPAM, "--from-folder", INBOX, *A],
    ["move", "x", "--to", SPAM, "--from-folder", INBOX, *A],
    ["move", "1", "--to", "/Spam", "--from-folder", "/Inbox", *A],
    ["list", "--unread", *A],
    ["list", "--folder", INBOX, "--label", "x", *A],
    ["list", "--folder", INBOX, "--include-archive", *A],
    ["list", "--folder", "/Inbox", *A],
    ["list", "--folder", INBOX, "--folder", SENT, *A],
    ["list", "--count", *A],
    ["list", "--folder", INBOX, "--account", BOX],
    ["accounts"],
    ["folders", "--json"],
])
def test_Guard_RefusesEveryOtherCommand_EvenInLiveMode(zw, args):
    with pytest.raises(zw.screen.RefusedRequest):
        zw.screen.check_dt_zoho(args, True, FOLDERS)


@pytest.mark.parametrize("args", [
    ["move", "1", "--to", ARCHIVE, "--from-folder", INBOX, *A],
    ["move", "1", "--to", SPAM, "--from-folder", INBOX, *A],
])
def test_Guard_FilingInObserveMode_OrBeforeTheFoldersAreKnown_IsRefused(zw, args):
    zw.screen.check_dt_zoho(args, True, FOLDERS)
    with pytest.raises(zw.screen.RefusedRequest):
        zw.screen.check_dt_zoho(args, False, FOLDERS)
    with pytest.raises(zw.screen.RefusedRequest):
        zw.screen.check_dt_zoho(args, True, None)


def test_Guard_ReadsAreLetOut(zw):
    for args in (["accounts", "--json"], ["folders", *A],
                 ["list", "--folder", INBOX, "--start", "201", "--count", "200", *A],
                 ["list", "--folder", SENT, "--start", "1", "--count", "100", *A]):
        zw.screen.check_dt_zoho(args, False, None)


def test_Source_AProcessStartsInOnePlace_ReachedOnlyAfterAGuard(zw):
    source = open(zw.screen.__file__, encoding="utf-8").read()
    assert len(re.findall(r"subprocess\.(run|Popen|call|check_call|check_output)\(", source)) == 1
    runner = source.split("\ndef run_dt_tool(")[1].split("\ndef ")[0]
    assert "subprocess.run(" in runner
    assert len(re.findall(r"(?<!def )run_dt_tool\(", source)) == 2
    for reader, guard in (("ImapReader", "check_dt_email("), ("ZohoReader", "check_dt_zoho(")):
        body = source.split(f"\nclass {reader}:")[1].split("\nclass ")[0]
        tool = body.split("    def tool(self, args, what")[1].split("\n    def ")[0]
        assert tool.index(guard) < tool.index("run_dt_tool(")
