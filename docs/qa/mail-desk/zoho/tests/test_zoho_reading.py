"""Reading a Zoho mailbox: the first run's look-back, the cursor and its ties, reading back page by page from Zoho's
newest-first list, known senders, and that every command runs under cc-secrets with the key never passing through
the check."""

import json

from zoho_rig import zw, world  # noqa: F401 - the fixtures
from zoho_rig import (BOX, ENTRIES, INBOX, KEY_VALUES, NEWSLETTER, NOW, OTHER, SENT, cursor, mail, section, zoho)


def test_FirstRun_TheLast30DaysOldestFirst_TenAtATime_TheRestNextRun(zw):
    box = zw.tools.account()
    box.inbox(mail("2026-09-10T08:00:00Z", subject="Too old"))
    ids = box.inbox(*[mail(f"2026-10-{d:02d}T08:00:00Z", subject=f"Day {d}") for d in range(1, 13)])
    zw.memory(zoho())

    code, output, context, _ = zw.run()

    assert code == 0
    mine = section(context)
    assert [m["subject"] for m in mine["messages"]] == [f"Day {d}" for d in range(1, 11)]
    assert mine["more"] is True and output["count"] == 10
    assert zw.read_memory()["channels"][0]["cursor"] == cursor("2026-10-10T08:00:00Z", ids[9])

    zw.screen.utc_now = lambda: NOW.replace(minute=5)
    _, _, context, _ = zw.run()
    assert [m["subject"] for m in section(context)["messages"]] == ["Day 11", "Day 12"]
    assert section(context)["more"] is False


def test_FirstRun_AnEmptyLookBack_ReportsNothingAndKeepsNoCursor(zw):
    zw.tools.account().inbox(mail("2026-08-01T08:00:00Z"))
    zw.memory(zoho())
    code, output, context, _ = zw.run()
    assert code == 0 and section(context)["messages"] == [] and output["count"] == 0
    assert "failure" not in section(context) and zw.tools.inbox_lists()
    assert "cursor" not in zw.read_memory()["channels"][0]


def test_Cursor_OnlyMailAfterIt_WithTheFieldsTheContextCarries_InUtc(zw):
    """dt-zoho prints arrival times in the account's own zone (five hours behind UTC here); the context carries UTC."""
    box = zw.tools.account()
    _, last = box.inbox(mail("2026-10-18T08:00:00Z"), mail("2026-10-18T09:00:00Z"))
    [new] = box.inbox(mail("2026-10-18T10:00:00Z", sender='"Ann Example" <Ann@Example.org>', subject="Invoice",
                           summary="Please   find\nit attached.", attachments=True))
    zw.memory(zoho(cursor=cursor("2026-10-18T09:00:00Z", last)))

    _, _, context, _ = zw.run()

    [entry] = section(context)["messages"]
    assert entry == {"messageKey": zw.key(BOX, new), "receivedUtc": "2026-10-18T10:00:00Z",
                     "sender": "ann@example.org", "senderName": "Ann Example", "subject": "Invoice",
                     "hasAttachments": True, "knownSender": False, "preview": "Please find it attached."}
    assert zw.read_memory()["channels"][0]["cursor"] == cursor("2026-10-18T10:00:00Z", new)
    assert zw.read_memory()["channels"][0]["keys"][zw.key(BOX, new)]["id"] == new


def test_Cursor_MailInTheSameSecond_NeverRepeatedAndNeverMissed(zw):
    """Zoho's times are to the second. Two messages share 09:00:00; one was reported. The other is reported next run,
    and the first never again."""
    box = zw.tools.account()
    first, second = box.inbox(mail("2026-10-18T09:00:00Z", subject="First"), mail("2026-10-18T09:00:00Z", subject="Second"))
    zw.memory(zoho(cursor=cursor("2026-10-18T09:00:00Z", first)))

    _, _, context, _ = zw.run()

    assert [m["subject"] for m in section(context)["messages"]] == ["Second"]
    assert zw.read_memory()["channels"][0]["cursor"] == cursor("2026-10-18T09:00:00Z", first, second)
    zw.screen.utc_now = lambda: NOW.replace(minute=5)
    _, _, context, _ = zw.run()
    assert section(context)["messages"] == []


def test_Paging_MoreThanAPageSinceTheCursor_ReadsBackUntilItReachesIt_AndNothingIsLost(zw):
    """Zoho lists newest first, 200 at a time. 450 messages came after the cursor: the Inbox is read back three pages,
    to the one that reaches before the cursor, and the oldest new ones are reported first."""
    box = zw.tools.account()
    [last] = box.inbox(mail("2026-10-17T07:00:00Z", subject="Reported last"))
    box.inbox(*[mail(f"2026-10-17T{8 + n // 60:02d}:{n % 60:02d}:00Z", subject=f"New {n}") for n in range(450)])
    zw.memory(zoho(cursor=cursor("2026-10-17T07:00:00Z", last)))

    _, _, context, _ = zw.run()

    assert [m["subject"] for m in section(context)["messages"]] == [f"New {n}" for n in range(10)]
    assert section(context)["more"] is True
    assert [a[a.index("--start") + 1] for a in zw.tools.inbox_lists()] == ["1", "181", "361"]   # pages overlap by 20


def test_Paging_TheCursorOnTheFirstPage_ReadsOnePage(zw):
    box = zw.tools.account()
    box.inbox(*[mail(f"2026-10-18T08:{n:02d}:00Z") for n in range(30)])
    zw.memory(zoho(cursor=cursor("2026-10-18T08:25:00Z")))
    zw.run()
    assert len(zw.tools.inbox_lists()) == 1


def test_Paging_AMessageReadOnTwoPages_IsReportedOnce(zw, monkeypatch):
    """Mail arriving while the Inbox is read moves every message down a page, so the last of one page is read again as
    the first of the next. Here that is the oldest new message, the first one reported; it is still reported once."""
    box = zw.tools.account()
    [last] = box.inbox(mail("2026-10-17T07:00:00Z", subject="Reported last"))
    box.inbox(*[mail(f"2026-10-17T{8 + n // 60:02d}:{n % 60:02d}:00Z", subject=f"New {n}") for n in range(200)])
    real, arrived = zw.tools.dt_zoho, []

    def arriving(args):
        answer = real(args)
        if args[0] == "list" and INBOX in args and args[args.index("--start") + 1] == "1" and not arrived:
            arrived.append(box.inbox(mail("2026-10-18T11:00:00Z", subject="Arrived while reading")))
        return answer
    monkeypatch.setattr(zw.tools, "dt_zoho", arriving)
    zw.memory(zoho(cursor=cursor("2026-10-17T07:00:00Z", last)))

    reported = []
    for minute in range(40):
        zw.screen.utc_now = lambda m=minute: NOW.replace(minute=m)
        _, _, context, _ = zw.run()
        reported += [m["subject"] for m in section(context)["messages"]]
        if not section(context)["more"]:
            break
    assert reported == [f"New {n}" for n in range(200)] + ["Arrived while reading"]


def test_Paging_MessagesLeavingTheInboxBetweenPages_NothingFallsBetweenThem(zw, monkeypatch):
    """Review finding 1: a message that leaves the Inbox after page 1 is read moves every later one UP a place, so the
    message at the next page's first place would be read on neither page; here that is the oldest new one, which the
    cursor would then pass for good. Pages overlap, so it is read."""
    box = zw.tools.account()
    [last] = box.inbox(mail("2026-10-17T07:00:00Z", subject="Reported last"))
    ids = box.inbox(*[mail(f"2026-10-17T{8 + n // 60:02d}:{n % 60:02d}:00Z", subject=f"New {n}") for n in range(205)])
    real, gone = zw.tools.dt_zoho, []

    def owner_files_some(args):
        answer = real(args)
        if args[0] == "list" and INBOX in args and args[args.index("--start") + 1] == "1" and not gone:
            for mid in ids[-5:]:      # the owner archives the five newest after page 1 was read
                box.messages[mid]["folder"] = "9000000000003011"
            gone.append(True)
        return answer
    monkeypatch.setattr(zw.tools, "dt_zoho", owner_files_some)
    zw.memory(zoho(cursor=cursor("2026-10-17T07:00:00Z", last)))

    reported = []
    for minute in range(40):
        zw.screen.utc_now = lambda m=minute: NOW.replace(minute=m)
        _, _, context, _ = zw.run()
        reported += [m["subject"] for m in section(context)["messages"]]
        if not section(context)["more"]:
            break
    assert reported == [f"New {n}" for n in range(200)]   # the five archived are rightly never reported


def test_ATypeInboxFolderTheOwnerMade_IsNotTakenForTheInbox(zw):
    """Zoho gives a folder the owner makes the type Inbox too; /Newsletter is listed before /Inbox here."""
    box = zw.tools.account()
    box.add(NEWSLETTER, mail("2026-10-18T08:00:00Z", subject="Filed by a rule"))
    box.inbox(mail("2026-10-18T09:00:00Z", subject="In the Inbox"))
    zw.memory(zoho())
    _, _, context, _ = zw.run()
    assert [m["subject"] for m in section(context)["messages"]] == ["In the Inbox"]
    assert all(a[a.index("--folder") + 1] != NEWSLETTER for a in zw.tools.log if a[0] == "list")


def test_KnownSender_FromToAndCcOfTheSentFolder_AndTheCacheHoldsNoAddress(zw):
    box = zw.tools.account()
    box.add(SENT, mail("2026-10-10T08:00:00Z", sender=f"Desk <{BOX}>", to="Customer <Customer@Example.org>",
                       cc="partner@example.net"))
    [before] = box.inbox(mail("2026-10-18T07:00:00Z"))   # reported before
    box.inbox(mail("2026-10-18T08:00:00Z", sender="customer@example.org"),
              mail("2026-10-18T08:01:00Z", sender="partner@example.net"),
              mail("2026-10-18T08:02:00Z", sender="stranger@example.com"))
    zw.memory(zoho(cursor=cursor("2026-10-18T07:00:00Z", before)))

    _, _, context, _ = zw.run()

    assert [m["knownSender"] for m in section(context)["messages"]] == [True, True, False]
    cache = zw.read_memory()["channels"][0]["sentCache"]
    assert cache["refreshedUtc"] == "2026-10-18T12:00:00Z" and "example" not in json.dumps(cache)
    assert [a for a in zw.tools.log if a[0] == "list" and SENT in a] != []


def test_EveryCommand_RunsUnderCcSecretsWithTheThreeEntries_NamingTheMailbox(zw):
    zw.tools.account().inbox(mail("2026-10-18T08:00:00Z"), mail("2026-10-18T08:05:00Z"))
    zw.memory(zoho(cursor=cursor("2026-10-18T08:00:00Z")))

    zw.run()

    assert len(zw.tools.wrapped) == len(zw.tools.log) >= 4
    for argv in zw.tools.wrapped:
        assert argv[1:6] == ["run", ENTRIES[0], "--with", ENTRIES[1], "--with"] and argv[6] == ENTRIES[2]
    assert zw.tools.commands()[:2] == ["accounts", "folders"]
    assert all(a[-3:] == ["--account", BOX, "--json"] for a in zw.tools.log[1:])


def test_TheKey_NeverPassesThroughTheCheck(zw):
    """cc-secrets hands the key to dt-zoho alone. Nothing about it is in the memory, the output, standard error, the
    command lines or the environment the check starts a process with."""
    box = zw.tools.account()
    box.inbox(mail("2026-10-18T08:00:00Z"))
    zw.memory(zoho())

    code, output, context, err = zw.run()

    assert code == 0 and zw.tools.wrapped
    seen = json.dumps([zw.read_memory(), output, err, zw.tools.wrapped,
                       [{k: v for k, v in e.items() if k.startswith(("ZOHO", "CC_"))} for e in zw.tools.envs]])
    for value in KEY_VALUES.values():
        assert value not in seen
    assert all(not any(k.startswith("ZOHO_API") for k in e) for e in zw.tools.envs)


def test_TwoMailboxesOnOneKey_EachReadOnItsOwn(zw):
    zw.tools.account().inbox(mail("2026-10-18T08:00:00Z", subject="Desk mail"))
    zw.tools.account(OTHER).inbox(mail("2026-10-18T08:30:00Z", subject="Second desk mail"))
    zw.memory(zoho(), zoho(OTHER))
    _, _, context, _ = zw.run()
    assert [m["subject"] for m in section(context)["messages"]] == ["Desk mail"]
    assert [m["subject"] for m in section(context, OTHER)["messages"]] == ["Second desk mail"]
