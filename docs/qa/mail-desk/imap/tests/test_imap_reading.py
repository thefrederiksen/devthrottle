"""Reading an IMAP mailbox: the first run's look-back, the cursor, the order of new mail, a renumbered Inbox, known
senders, and that every command with the password runs under cc-secrets."""

import json

from imap_rig import iw, world  # noqa: F401 - the fixtures
from imap_rig import BOX, NOW, OTHER, PASSWORD, cursor, imap, mail, section


def test_FirstRun_TheLast30DaysOldestFirst_TenAtATime_TheRestNextRun(iw):
    box = iw.tools.account()
    box.inbox(mail("2026-09-10T08:00:00Z", subject="Too old"))
    uids = box.inbox(*[mail(f"2026-10-{d:02d}T08:00:00Z", subject=f"Day {d}") for d in range(1, 13)])
    iw.memory(imap())

    code, output, context, _ = iw.run()

    assert code == 0
    mine = section(context)
    assert [m["subject"] for m in mine["messages"]] == [f"Day {d}" for d in range(1, 11)]
    assert mine["more"] is True and output["count"] == 10
    assert iw.read_memory()["channels"][0]["cursor"] == cursor(uids[9], "2026-10-10T08:00:00Z")

    iw.screen.utc_now = lambda: NOW.replace(minute=5)
    _, _, context, _ = iw.run()
    assert [m["subject"] for m in section(context)["messages"]] == ["Day 11", "Day 12"]
    assert section(context)["more"] is False


def test_FirstRun_AnEmptyLookBack_ReportsNothingAndKeepsNoCursor(iw):
    iw.tools.account().inbox(mail("2026-08-01T08:00:00Z"))
    iw.memory(imap())
    code, output, context, _ = iw.run()
    assert code == 0 and section(context)["messages"] == [] and output["count"] == 0
    assert "cursor" not in iw.read_memory()["channels"][0]


def test_Cursor_OnlyUidsAboveIt_WithTheFieldsTheContextCarries(iw):
    box = iw.tools.account()
    box.inbox(mail("2026-10-18T08:00:00Z"), mail("2026-10-18T09:00:00Z"))
    box.inbox(mail("2026-10-18T10:00:00Z", sender='"Ann Example" <Ann@Example.org>', subject="Invoice",
                   text="Please   find\nit attached.", attachments=1))
    iw.memory(imap(cursor=cursor(2)))

    _, _, context, _ = iw.run()

    [entry] = section(context)["messages"]
    assert entry == {"messageKey": iw.key(BOX, 1, 3), "receivedUtc": "2026-10-18T10:00:00Z",
                     "sender": "ann@example.org", "senderName": "Ann Example", "subject": "Invoice",
                     "hasAttachments": True, "knownSender": False, "preview": "Please find it attached."}
    assert iw.read_memory()["channels"][0]["cursor"] == cursor(3, "2026-10-18T10:00:00Z")
    assert iw.read_memory()["channels"][0]["keys"][iw.key(BOX, 1, 3)]["id"] == "1.3"


def test_MailMovedIntoTheInboxLate_IsReportedInUidOrder_SoTheCursorNeverJumpsOverOne(iw):
    """Ten messages in one mailbox, the lowest UID with the LATEST arrival time (moved into the Inbox from another
    folder), and one older message in a second mailbox. Ordered by arrival time alone, the run would report UIDs 2-10
    and the other mailbox's, and move the cursor to 10 past UID 1, which would then never be reported."""
    box = iw.tools.account()
    box.inbox(mail("2026-10-18T09:00:00Z", subject="Moved in"),
              *[mail(f"2026-10-18T08:0{n}:00Z", subject=f"Plain {n}") for n in range(9)])
    iw.tools.account(OTHER, name="second").inbox(mail("2026-10-18T08:30:00Z", subject="Elsewhere"))
    iw.memory(imap(), imap(OTHER))   # both from the 30-day look-back

    _, _, context, _ = iw.run()

    mine = section(context)
    reported = [m["subject"] for m in mine["messages"]]
    assert reported[0] == "Moved in"
    assert mine["messages"][0]["receivedUtc"] == "2026-10-18T09:00:00Z"   # the real arrival time, never the order
    saved = iw.read_memory()["channels"][0]["cursor"]
    assert saved["lastUid"] == len(reported)          # every UID up to the cursor was reported
    iw.screen.utc_now = lambda: NOW.replace(minute=5)
    _, _, context, _ = iw.run()
    assert reported + [m["subject"] for m in section(context)["messages"]] == \
        ["Moved in"] + [f"Plain {n}" for n in range(9)]


def test_Cursor_ANewerUidWithAnEarlierArrival_KeepsTheLatestArrivalEverReported(iw):
    """A migrated mailbox: UID 3 arrived before UID 2 was reported. The cursor's newest never goes back."""
    box = iw.tools.account()
    box.inbox(mail("2026-10-18T08:00:00Z"), mail("2026-10-18T10:00:00Z"), mail("2026-10-17T09:00:00Z"))
    iw.memory(imap(cursor=cursor(2, "2026-10-18T10:00:00Z")))
    _, _, context, _ = iw.run()
    assert len(section(context)["messages"]) == 1
    assert iw.read_memory()["channels"][0]["cursor"] == cursor(3, "2026-10-18T10:00:00Z")


def test_Renumbered_NewMailIsFoundAgainByArrivalTime_TheLastReportedOnceMore(iw):
    box = iw.tools.account()
    box.inbox(mail("2026-10-17T08:00:00Z", subject="Old"), mail("2026-10-18T09:00:00Z", subject="Reported last"),
              mail("2026-10-18T10:00:00Z", subject="New"))
    box.renumber()
    iw.memory(imap(cursor=cursor(2, "2026-10-18T09:00:00Z", validity=1)))

    _, _, context, err = iw.run()

    assert [m["subject"] for m in section(context)["messages"]] == ["Reported last", "New"]
    assert iw.read_memory()["channels"][0]["cursor"] == cursor(3, "2026-10-18T10:00:00Z", validity=2)
    assert "renumbered the Inbox" in err


def test_KnownSender_FromToAndCcOfTheSentFolder_AndTheCacheHoldsNoAddress(iw):
    box = iw.tools.account()
    box.add(box.sent, mail("2026-10-10T08:00:00Z", sender=f"Desk <{BOX}>", to="Customer <Customer@Example.org>",
                           cc="partner@example.net"))
    box.inbox(mail("2026-10-18T07:00:00Z"),   # UID 1, reported before
              mail("2026-10-18T08:00:00Z", sender="customer@example.org"),
              mail("2026-10-18T08:01:00Z", sender="partner@example.net"),
              mail("2026-10-18T08:02:00Z", sender="stranger@example.com"))
    iw.memory(imap(cursor=cursor(1, "2026-10-18T07:00:00Z")))

    _, _, context, _ = iw.run()

    assert [m["knownSender"] for m in section(context)["messages"]] == [True, True, False]
    cache = iw.read_memory()["channels"][0]["sentCache"]
    assert cache["refreshedUtc"] == "2026-10-18T12:00:00Z" and "example" not in json.dumps(cache)
    assert [a for a in iw.tools.log if a[0] == "list" and "Sent Items" in a] != []


def test_EveryCommandButTheAccountList_RunsUnderCcSecrets_InTheAccountsOwnVariable(iw):
    iw.tools.account(entry="zoho-desk", variable="ZOHO_DESK_PASSWORD").inbox(mail("2026-10-18T08:00:00Z"),
                                                                          mail("2026-10-18T08:05:00Z"))
    iw.memory(imap(cursor=cursor(1)))

    _, output, _, err = iw.run()

    assert len(iw.tools.wrapped) >= 2
    for argv in iw.tools.wrapped:
        assert argv[1:8] == ["run", "zoho-desk", "--via", "env", "--env-name", "ZOHO_DESK_PASSWORD", "--timeout"]
    assert iw.tools.commands().count("account list") == 1
    assert len(iw.tools.log) == len(iw.tools.wrapped) + 1
    seen = json.dumps(output) + err + iw.state.read_text(encoding="utf-8")
    assert PASSWORD not in seen and "zoho-desk" not in seen


def test_Preview_IsAskedFor_OnlyForTheMessagesReported(iw):
    iw.tools.account().inbox(*[mail(f"2026-10-18T08:{n:02d}:00Z") for n in range(15)])
    iw.memory(imap(cursor=cursor(1)))
    iw.run()
    previews = [a for a in iw.tools.log if "--preview" in a]
    assert len(previews) == 1 and previews[0][previews[0].index("--count") + 1] == "10"
