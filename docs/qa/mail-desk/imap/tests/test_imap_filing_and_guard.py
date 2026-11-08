"""Filing an IMAP mailbox, and the guard on dt-email: observe mode files nothing, live mode archives and junks one Inbox
message at a time, a UID the Inbox no longer holds (or holds under another uidvalidity) is already-gone, and no
command that sends, replies, forwards, drafts, marks or deletes can get past check_dt_email()."""

import pytest

from imap_rig import iw, world  # noqa: F401 - the fixtures
from imap_rig import BOX, cursor, imap, mail, section


def queued(iw, mode, *items, validity=1):
    """A channel whose Inbox holds UIDs 1 and 2, whose key table holds them and UID 99, and a queue of (uid, decision)."""
    box = iw.tools.account()
    box.inbox(mail("2026-10-18T08:00:00Z"), mail("2026-10-18T09:00:00Z"))
    keys = {iw.key(BOX, validity, uid): {"id": f"{validity}.{uid}", "reportedUtc": "2026-10-18T09:05:00Z"}
            for uid in (1, 2, 99)}
    queue = [{"messageKey": iw.key(BOX, validity, uid), "decision": decision} for uid, decision in items]
    iw.memory(imap(mode=mode, cursor=cursor(2), keys=keys, filingQueue=queue))
    return box, queue


def test_ObserveMode_NothingIsFiled_TheQueueStays(iw):
    box, queue = queued(iw, "observe", (1, "archive"), (2, "junk"))
    code, output, context, _ = iw.run()
    assert code == 0
    assert iw.tools.filed() == []
    assert "list" in iw.tools.commands()     # the run did reach the mailbox: an empty filing log is not an idle rig
    assert section(context)["filingNotCarriedOut"] == {"count": 2, "reason": "the channel is in observe mode"}
    assert iw.read_memory()["channels"][0]["filingQueue"] == queue
    assert sorted(box.folders["INBOX"]) == [1, 2]


def test_LiveMode_ArchiveToArchive_JunkToTheJunkFolderDtEmailNames(iw):
    box, _ = queued(iw, "live", (1, "archive"), (2, "junk"))
    code, output, context, _ = iw.run()
    assert code == 0
    assert iw.tools.filed() == [["archive", "1", "--account", "desk", "--json"],
                                ["move", "2", "--to", "Spam", "--account", "desk", "--json"]]
    assert [r["result"] for r in section(context)["filing"]] == ["moved", "moved"]
    assert box.folders["INBOX"] == {} and len(box.folders["Archive"]) == 1 and len(box.folders["Spam"]) == 1
    assert iw.read_memory()["channels"][0]["filingQueue"] == []
    assert output["count"] == 2


def test_LiveMode_AUidTheInboxNoLongerHolds_IsAlreadyGone(iw):
    queued(iw, "live", (99, "archive"))
    _, _, context, _ = iw.run()
    assert section(context)["filing"] == [{"messageKey": iw.key(BOX, 1, 99), "decision": "archive",
                                           "result": "already-gone"}]
    assert "failure" not in section(context)


def test_LiveMode_AfterTheInboxWasRenumbered_AnOldUidIsAlreadyGone_AndNothingIsMoved(iw):
    """UID 1 under the old uidvalidity is not UID 1 now: moving it would file another message."""
    box, _ = queued(iw, "live", (1, "archive"), (2, "junk"))
    box.renumber()
    _, _, context, _ = iw.run()
    assert iw.tools.filed() == []
    assert [r["result"] for r in section(context)["filing"]] == ["already-gone", "already-gone"]
    assert sorted(box.folders["INBOX"]) == [1, 2]


@pytest.mark.parametrize("args", [
    ["send", "--to", "someone@example.com", "--subject", "x", "--body", "y", "--account", "desk", "--json"],
    ["reply", "1", "--body", "y", "--account", "desk", "--json"],
    ["forward", "1", "--to", "someone@example.com", "--account", "desk", "--json"],
    ["draft", "create", "--account", "desk", "--json"],
    ["mark", "1", "--as", "read", "--account", "desk", "--json"],
    ["delete", "1", "--account", "desk", "--json"],
    ["folder", "create", "x", "--account", "desk", "--json"],
    ["read", "1", "--account", "desk", "--json"],
    ["move", "1", "--to", "Trash", "--account", "desk", "--json"],
    ["move", "1", "--to", "Spam", "--folder", "Archive", "--account", "desk", "--json"],
    ["archive", "1,2", "--account", "desk", "--json"],
    ["archive", "1", "--folder", "Sent Items", "--account", "desk", "--json"],
    ["list", "--unread", "--account", "desk", "--json"],
    ["list", "--count", "--account", "desk", "--json"],
    ["list", "--folder", "INBOX", "--account", "desk"],
    ["account", "remove", "desk"],
    ["account", "check", "--account", "desk", "--json"],
])
def test_Guard_RefusesEveryOtherCommand_EvenInLiveMode(iw, args):
    with pytest.raises(iw.screen.RefusedRequest):
        iw.screen.check_dt_email(args, True, "Spam")


@pytest.mark.parametrize("args", [
    ["archive", "1", "--account", "desk", "--json"],
    ["move", "1", "--to", "Spam", "--account", "desk", "--json"],
])
def test_Guard_FilingInObserveMode_IsRefused(iw, args):
    iw.screen.check_dt_email(args, True, "Spam")
    with pytest.raises(iw.screen.RefusedRequest):
        iw.screen.check_dt_email(args, False, "Spam")


def test_Guard_ReadsAreLetOut(iw):
    for args in (["account", "list", "--json"], ["folders", "--account", "desk", "--json"],
                 ["list", "--folder", "INBOX", "--after-uid", "4", "--oldest", "--count", "10", "--preview",
                  "--account", "desk", "--json"],
                 ["list", "--folder", "INBOX", "--since", "2026-09-18", "--count", "200", "--account", "desk", "--json"]):
        iw.screen.check_dt_email(args, False, None)


def test_Source_AProcessStartsInOnePlace_AfterTheGuard(iw):
    source = open(iw.screen.__file__, encoding="utf-8").read()
    assert source.count("subprocess.run(") == 1
    tool = source.split("    def tool(self, args, what")[1].split("\n    def ")[0]
    assert tool.index("check_dt_email(") < tool.index("subprocess.run(")
