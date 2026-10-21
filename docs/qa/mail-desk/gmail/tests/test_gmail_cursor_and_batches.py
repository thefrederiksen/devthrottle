"""R4: the cursor (internalDate in milliseconds and the ids at exactly it), the first run (30 days or 200), and the
batches - 10 messages and 7 KB across every channel, Outlook and Gmail together, oldest first."""

import json

from gmail_rig import gw, world  # noqa: F401 - the fixtures
from gmail_rig import (GMAIL_BOX, GMAIL_TWO, MACHINE, NOW, OUTLOOK_BOX, all_messages, channel, cursor, gmsg, ms,
                       phase2, section)


def gmail_channel(address=GMAIL_BOX, **fields):
    return channel(address, provider="gmail", **fields)


def reported_ids(gw, context, address=GMAIL_BOX):
    keys = gw.read_memory()["channels"][[c["address"] for c in gw.read_memory()["channels"]].index(address)]["keys"]
    return [keys[m["messageKey"]]["id"] for m in section(context, address)["messages"]]


def test_FirstRun_TheLast30DaysAtMost200_OldestFirst(gw):
    gw.plant_gmail()
    box = gw.gmail.box(GMAIL_BOX)
    box.append(gmsg("old1", "2026-09-17T11:59:59Z"))           # 30 days and a second ago: outside the look-back
    box.extend(gmsg(f"m{i:03d}", ms("2026-10-01T00:00:00Z") + i * 60_000) for i in range(205))
    gw.memory(gmail_channel())
    code, _, context, _ = gw.run()
    assert code == 0
    # The 200 newest of the 205 are the look-back, so the first reported is m005, and the 10 oldest of them come first.
    assert reported_ids(gw, context) == [f"m{i:03d}" for i in range(5, 15)]
    assert section(context, GMAIL_BOX)["more"] is True
    lists = [u for m, u, _, _ in gw.gmail.log if m == "GET" and "/messages?" in u and "labelIds=INBOX" in u
             and "has%3Aattachment" not in u]
    since = int(NOW.timestamp()) - 30 * 86400
    assert f"q=after%3A{since}" in lists[0] and "labelIds=INBOX" in lists[0]
    assert sum("maxResults" in u for u in lists) == 1 and "maxResults=200" in lists[0]


def test_FirstRun_FewerThan200_TakesAllOfThe30Days(gw):
    gw.plant_gmail()
    gw.gmail.box(GMAIL_BOX).extend([gmsg("old1", "2026-09-01T00:00:00Z"), gmsg("new1", "2026-10-18T08:00:00Z")])
    gw.memory(gmail_channel())
    _, _, context, _ = gw.run()
    assert reported_ids(gw, context) == ["new1"]
    assert gw.read_memory()["channels"][0]["cursor"] == cursor("2026-10-18T08:00:00Z", "new1")


def test_Cursor_KeepsMillisecondsAndTheOutputDoesNot(gw):
    gw.plant_gmail()
    gw.gmail.box(GMAIL_BOX).append(gmsg("a1", "2026-10-18T08:00:00.789Z"))
    gw.memory(gmail_channel())
    _, _, context, _ = gw.run()
    assert all_messages(context)[0]["receivedUtc"] == "2026-10-18T08:00:00Z"
    assert gw.read_memory()["channels"][0]["cursor"] == {"provider": "gmail", "newestMs": ms("2026-10-18T08:00:00.789Z"),
                                                         "idsAtNewest": ["a1"]}


def test_Cursor_ACutInsideOneMillisecond_NothingSkippedNothingTwice(gw):
    """12 messages, the last 5 at exactly the same millisecond: the 10-message stop cuts between them. The next run
    reports the other 2 and nothing else - and the cursor's ids at that millisecond are kept and added to."""
    gw.plant_gmail()
    box = gw.gmail.box(GMAIL_BOX)
    box.extend(gmsg(f"a{i:02d}", f"2026-10-18T08:00:0{i}.100Z") for i in range(7))
    box.extend(gmsg(f"b{i:02d}", "2026-10-18T08:00:07.500Z") for i in range(5))
    gw.memory(gmail_channel(cursor=cursor("2026-10-18T07:00:00Z")))
    _, _, first, _ = gw.run_later(0)
    seen = reported_ids(gw, first)
    assert len(seen) == 10 and section(first, GMAIL_BOX)["more"] is True
    held = gw.read_memory()["channels"][0]["cursor"]
    assert held["newestMs"] == ms("2026-10-18T08:00:07.500Z") and len(held["idsAtNewest"]) == 3
    _, _, second, _ = gw.run_later(1)
    seen += reported_ids(gw, second)
    assert sorted(seen) == sorted(m["id"] for m in box) and len(seen) == len(set(seen)) == 12
    assert section(second, GMAIL_BOX)["more"] is False
    assert len(gw.read_memory()["channels"][0]["cursor"]["idsAtNewest"]) == 5
    _, output, third, _ = gw.run_later(2)
    assert all_messages(third) == [] and output["count"] == 0


def test_Cursor_TheQueryIsOneSecondBeforeAndTheSecondBeforeIsFilteredHere(gw):
    """Gmail's after: counts whole seconds, so the query starts one second early; mail in that second that was not
    after the cursor's millisecond is left out in code."""
    gw.plant_gmail()
    box = gw.gmail.box(GMAIL_BOX)
    box.extend([gmsg("early", "2026-10-18T08:00:04.900Z"), gmsg("same", "2026-10-18T08:00:05.200Z"),
                gmsg("held", "2026-10-18T08:00:05.300Z"), gmsg("late", "2026-10-18T08:00:05.301Z")])
    gw.memory(gmail_channel(cursor=cursor("2026-10-18T08:00:05.300Z", "held")))
    _, _, context, _ = gw.run()
    assert reported_ids(gw, context) == ["late"]
    lists = [u for m, u, _, _ in gw.gmail.log if "/messages?" in u and "INBOX" in u]
    assert f"q=after%3A{ms('2026-10-18T08:00:05Z') // 1000 - 1}" in lists[0]


def test_Cursor_CatchingUpOverManyRuns_NothingSkippedNothingTwice(gw):
    gw.plant_gmail()
    box = gw.gmail.box(GMAIL_BOX)
    box.extend(gmsg(f"c{i:03d}", ms("2026-10-18T06:00:00Z") + (i // 3) * 1000 + (i % 3)) for i in range(47))
    gw.memory(gmail_channel(cursor=cursor("2026-10-18T05:00:00Z")))
    seen = []
    for n in range(6):
        _, _, context, _ = gw.run_later(n)
        seen += reported_ids(gw, context)
    assert seen == [f"c{i:03d}" for i in range(47)]


def test_Cursor_ABurstInsideTwoSeconds_NeverStalls(gw):
    """100 messages inside two seconds: the query of each later run takes in every one already reported, and the key
    table lets them be skipped unread, so every run moves on."""
    gw.plant_gmail()
    box = gw.gmail.box(GMAIL_BOX)
    box.extend(gmsg(f"d{i:03d}", ms("2026-10-18T06:00:00Z") + i * 15) for i in range(100))
    gw.memory(gmail_channel(cursor=cursor("2026-10-18T05:00:00Z")))
    seen = []
    for n in range(10):
        _, _, context, _ = gw.run_later(n)
        seen += reported_ids(gw, context)
    assert seen == [f"d{i:03d}" for i in range(100)]


def test_Cursor_AnOutlookCursorOnAGmailChannel_FailsThatChannel(gw):
    """The two cursors cannot be taken for each other: a channel whose provider changed fails, naming the cursor."""
    gw.plant_gmail()
    gw.memory(gmail_channel(cursor={"newest": "2026-10-18T07:00:00Z", "idsAtNewest": []}))
    sentence = phase2.channel_failed(gw, GMAIL_BOX).splitlines()[0]
    assert sentence == f"the channel {GMAIL_BOX} has a 'cursor' this check did not write"
    assert gw.gmail.log == []


def test_Cursor_AGmailCursorOnAnOutlookChannel_FailsThatChannel(gw):
    gw.plant(OUTLOOK_BOX)
    gw.memory(channel(OUTLOOK_BOX, cursor=cursor("2026-10-18T07:00:00Z")))
    sentence = phase2.channel_failed(gw, OUTLOOK_BOX).splitlines()[0]
    assert sentence == f"the channel {OUTLOOK_BOX} has a 'cursor' this check did not write"


def test_Cursor_AnyOtherShape_FailsThatChannel(gw):
    gw.plant_gmail()
    for bad in ({"provider": "gmail", "newestMs": "1", "idsAtNewest": []},
                {"provider": "gmail", "newestMs": 1, "idsAtNewest": [1]},
                {"provider": "gmail", "newestMs": True, "idsAtNewest": []},
                {"provider": "outlook", "newestMs": 1, "idsAtNewest": []},
                {"provider": "gmail", "newestMs": 1, "idsAtNewest": [], "historyId": "9"}):
        gw.memory(gmail_channel(cursor=bad))
        assert "has a 'cursor' this check did not write" in phase2.channel_failed(gw, GMAIL_BOX)


def test_Batches_GmailAndOutlookShareTheTenMessageStop_OldestFirstAcrossBoth(gw):
    gw.plant(OUTLOOK_BOX)
    gw.plant_gmail()
    outlook = gw.graph.box(OUTLOOK_BOX)["inbox"]
    outlook.extend(phase2.msg(f"o{i}", f"2026-10-18T08:0{i}:30Z") for i in range(8))
    gw.gmail.box(GMAIL_BOX).extend(gmsg(f"g{i}", f"2026-10-18T08:0{i}:00Z") for i in range(8))
    gw.memory(channel(OUTLOOK_BOX, cursor={"newest": "2026-10-18T07:00:00Z", "idsAtNewest": []}),
              gmail_channel(cursor=cursor("2026-10-18T07:00:00Z")))
    code, output, context, _ = gw.run()
    assert code == 0 and output["count"] == 10
    times = [m["receivedUtc"] for m in all_messages(context)]
    assert len(times) == 10
    assert sorted(m["receivedUtc"] for s in context["channels"] for m in s["messages"])[-1] == "2026-10-18T08:04:30Z"
    assert reported_ids(gw, context) == ["g0", "g1", "g2", "g3", "g4"]
    assert section(context, OUTLOOK_BOX)["more"] is True and section(context, GMAIL_BOX)["more"] is True
    # Both channels' keys are 16 hexadecimal digits of SHA-256 of "<address>\n<message id>".
    assert all_messages(context)[-1]["messageKey"] == gw.screen.short_key(GMAIL_BOX, "g4")
    assert len({m["messageKey"] for m in all_messages(context)}) == 10


def test_Batches_TheSevenKilobyteBudgetCountsGmailMessagesToo(gw):
    gw.plant_gmail()
    gw.gmail.box(GMAIL_BOX).extend(gmsg(f"g{i}", f"2026-10-18T08:0{i}:00Z", subject="S" * 900, snippet="p" * 199)
                                   for i in range(8))
    gw.memory(gmail_channel(cursor=cursor("2026-10-18T07:00:00Z")))
    _, output, context, _ = gw.run()
    assert len(output["context"].encode("utf-8")) <= 7168
    assert 0 < len(all_messages(context)) < 8 and section(context, GMAIL_BOX)["more"] is True


def test_Batches_TwoGmailChannels_EachSignedInAlone_AndOneFailingLeavesTheOtherRead(gw):
    gw.plant_gmail()
    gw.gmail.box(GMAIL_BOX).append(gmsg("g1", "2026-10-18T08:00:00Z"))
    gw.memory(gmail_channel(cursor=cursor("2026-10-18T07:00:00Z")),
              gmail_channel(GMAIL_TWO, cursor=cursor("2026-10-18T07:00:00Z")))
    code, output, context, _ = gw.run()
    assert code == 0 and output["count"] == 1
    assert section(context, GMAIL_TWO)["failure"]["sentence"].startswith(f"{GMAIL_TWO} is not signed in on {MACHINE}")
    assert reported_ids(gw, context) == ["g1"]


def test_Context_AGmailChannelsSectionHasTheSameShapeAsOutlooks(gw):
    gw.plant(OUTLOOK_BOX)
    gw.plant_gmail()
    gw.graph.box(OUTLOOK_BOX)["inbox"].append(phase2.msg("o1", "2026-10-18T08:00:00Z"))
    gw.gmail.box(GMAIL_BOX).append(gmsg("g1", "2026-10-18T08:01:00Z"))
    gw.memory(channel(OUTLOOK_BOX, id="c-1", cursor={"newest": "2026-10-18T07:00:00Z", "idsAtNewest": []}),
              gmail_channel(id="c-2", cursor=cursor("2026-10-18T07:00:00Z")))
    _, _, context, _ = gw.run()
    outlook, gmail = context["channels"]
    assert list(outlook) == list(gmail) and gmail["provider"] == "gmail"
    assert list(outlook["messages"][0]) == list(gmail["messages"][0])
    assert context["version"] == 2
