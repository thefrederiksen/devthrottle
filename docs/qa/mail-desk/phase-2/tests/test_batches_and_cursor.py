"""Tests 4, 5 and 10: batches that fit, a cursor that never passes what was reported, and UTF-8 counted in bytes.

The Director keeps at most 8 KB of context and 16 KB of output, and that cannot be raised without a desktop release.
Phase 0 measured one message as the check reports it at 446 bytes typical and 680 bytes at worst, and set the batch:
fill the context to 7168 bytes and stop at 10 messages, across all channels. The cursor moves only past messages
actually reported, so whatever did not fit is reported by the next run - nothing skipped, nothing twice.
"""

import json

from conftest import MAILBOX, NOW, all_messages, channel, msg

BUDGET = 7168


def at(minute, second=0):
    return f"2026-10-18T{8 + minute // 60:02}:{minute % 60:02}:{second:02}Z"


def run_until_quiet(world, limit=50):
    """Runs the check until it reports nothing new. Returns every reported message, in order, and the contexts."""
    reported, contexts = [], []
    for _ in range(limit):
        code, output, context, _ = world.run()
        assert code == 0
        assert len(output["context"].encode("utf-8")) <= BUDGET
        assert len(json.dumps(output, ensure_ascii=False).encode("utf-8")) < 16384
        contexts.append(context)
        batch = all_messages(context)
        if not batch:
            return reported, contexts
        reported += batch
    raise AssertionError("the check never caught up")


def start(world, messages, **fields):
    world.plant()
    world.graph.box(MAILBOX)["inbox"] += messages
    world.memory(channel(cursor={"newest": "2026-10-18T07:00:00Z", "idsAtNewest": []}, **fields))


def keys_of(world, ids):
    return [world.screen.short_key(MAILBOX, i) for i in ids]


def sized_message(world, mid, received, target_bytes):
    """A message whose reported entry is target_bytes of UTF-8, padded in its preview (cut to 200 characters) and
    then its subject."""
    base = msg(mid, received, sender="sender.name@example.com", name="Sender Name", subject="", preview="")
    entry = lambda m: world.screen.compact({"messageKey": "0123456789abcdef", "receivedUtc": received,
                                            "sender": "sender.name@example.com", "senderName": "Sender Name", "subject": m["subject"], "hasAttachments": False,
                                            "knownSender": False, "preview": m["bodyPreview"][:200]})
    base["bodyPreview"] = "p" * min(200, max(0, target_bytes - len(entry(base).encode("utf-8"))))
    base["subject"] = "s" * max(0, target_bytes - len(entry(base).encode("utf-8")))
    assert len(entry(base).encode("utf-8")) == target_bytes
    return base


# ---------------------------------------------------------------------------------------------------- test 4


def test_Batch_TwentyFiveSmallMessages_TenPerRunAndAllOfThemOnce(world):
    ids = [f"id-{n:02}" for n in range(25)]
    start(world, [msg(i, at(n)) for n, i in enumerate(ids)])
    reported, contexts = run_until_quiet(world)
    assert [len(all_messages(c)) for c in contexts] == [10, 10, 5, 0]
    assert [m["messageKey"] for m in reported] == keys_of(world, ids)          # oldest first, nothing skipped, nothing twice
    assert [c["channels"][0]["more"] for c in contexts] == [True, True, False, False]


def test_Batch_TypicalAndWorstMeasuredSizes_TenPerRun(world):
    """Phase 0's sizes: 446 bytes typical, 680 at worst. Both give 10 messages a run: the 10-message stop binds, not
    the 7 KB budget."""
    typical = [sized_message(world, f"t-{n:02}", at(n), 446) for n in range(12)]
    worst = [sized_message(world, f"w-{n:02}", at(30 + n), 680) for n in range(12)]
    start(world, typical + worst)
    _, contexts = run_until_quiet(world)
    counts = [len(all_messages(c)) for c in contexts]
    assert counts[:3] == [10, 10, 4]
    sizes = [len(world.screen.compact(m).encode("utf-8")) for m in all_messages(contexts[0])]
    assert sizes == [446] * 10
    sizes = [len(world.screen.compact(m).encode("utf-8")) for m in all_messages(contexts[1])]
    assert sizes == [446] * 2 + [680] * 8


def test_Batch_LargeMessages_TheSevenKilobyteBudgetStopsTheBatch(world):
    ids = [f"big-{n:02}" for n in range(20)]
    start(world, [sized_message(world, i, at(n), 900) for n, i in enumerate(ids)])
    reported, contexts = run_until_quiet(world)
    counts = [len(all_messages(c)) for c in contexts if all_messages(c)]
    assert counts[0] == 7                                              # 7 x 900 bytes fits in 7168; 8 does not
    assert all(n <= 7 for n in counts)
    assert [m["messageKey"] for m in reported] == keys_of(world, ids)


def test_Batch_ASingleMessageLargerThanTheBudget_IsReportedTrimmedAndTheCheckMovesOn(world):
    huge = msg("huge", at(1), subject="S" * 20000, name="N" * 5000, preview="P" * 200)
    start(world, [huge, msg("after", at(2))])
    reported, contexts = run_until_quiet(world)
    first = all_messages(contexts[0])
    assert len(first) == 1 and first[0]["trimmed"] is True
    assert first[0]["sender"] == "someone@example.com" and first[0]["messageKey"] == world.screen.short_key(MAILBOX, "huge")
    assert first[0]["preview"] == ""
    assert [m["messageKey"] for m in reported] == keys_of(world, ["huge", "after"])
    assert "trimmed" not in reported[1]


def test_Batch_AnOversizedMessageAfterOthers_WaitsToBeFirstNextRun(world):
    start(world, [msg("small", at(1)), msg("huge", at(2), subject="S" * 20000), msg("after", at(3))])
    _, contexts = run_until_quiet(world)
    assert [[m["messageKey"] for m in all_messages(c)] for c in contexts[:3]] == [
        keys_of(world, ["small"]), keys_of(world, ["huge"]), keys_of(world, ["after"])]


# ---------------------------------------------------------------------------------------------------- test 5


def test_Cursor_MessagesSharingOneReceiveTimeAcrossTheCut_AreReportedOnceEach(world):
    """Fifteen messages received in the same second: the cut falls inside them. The cursor keeps the ids reported at
    that time, so the next run reports exactly the other five."""
    ids = [f"same-{n:02}" for n in range(15)]
    start(world, [msg(i, at(5)) for i in ids])
    _, contexts = run_until_quiet(world)
    batches = [[m["messageKey"] for m in all_messages(c)] for c in contexts]
    assert [len(b) for b in batches] == [10, 5, 0]
    assert sorted(batches[0] + batches[1]) == sorted(keys_of(world, ids))
    cursor = world.read_memory()["channels"][0]["cursor"]
    assert cursor == {"newest": at(5), "idsAtNewest": sorted(ids)}


def test_Cursor_AfterARun_PointsAtTheLastReportedMessageNotTheLastRead(world):
    ids = [f"id-{n:02}" for n in range(14)]
    start(world, [msg(i, at(n)) for n, i in enumerate(ids)])
    world.run()
    assert world.read_memory()["channels"][0]["cursor"] == {"newest": at(9), "idsAtNewest": ["id-09"]}


def test_Cursor_TheCutBetweenTwoMessagesOfOneSecond_KeepsBothIdsOnceTheSecondIsReported(world):
    ids = [f"id-{n:02}" for n in range(9)] + ["tie-a", "tie-b"]
    times = [at(n) for n in range(9)] + [at(20), at(20)]
    start(world, [msg(i, t) for i, t in zip(ids, times)])
    world.run()
    assert world.read_memory()["channels"][0]["cursor"] == {"newest": at(20), "idsAtNewest": ["tie-a"]}
    _, _, context, _ = world.run()
    assert [m["messageKey"] for m in all_messages(context)] == keys_of(world, ["tie-b"])
    assert world.read_memory()["channels"][0]["cursor"] == {"newest": at(20), "idsAtNewest": ["tie-a", "tie-b"]}


def test_Cursor_NewMailArrivingBetweenRuns_IsPickedUpAfterTheBacklog(world):
    ids = [f"id-{n:02}" for n in range(12)]
    start(world, [msg(i, at(n)) for n, i in enumerate(ids)])
    world.run()
    world.graph.box(MAILBOX)["inbox"].append(msg("late", at(100)))
    reported, _ = run_until_quiet(world)
    assert [m["messageKey"] for m in reported] == keys_of(world, ids[10:] + ["late"])


# ---------------------------------------------------------------------------------------------------- first run


def test_FirstRun_MoreThanTwoHundredMessages_ReadsOnlyTheMostRecentTwoHundred(world):
    world.plant()
    ids = [f"id-{n:03}" for n in range(250)]
    world.graph.box(MAILBOX)["inbox"] += [msg(i, f"2026-10-1{5 + n // 100}T{(n % 100) // 5:02}:{(n % 5) * 10:02}:00Z")
                                          for n, i in enumerate(ids)]
    world.memory(channel())
    reported, _ = run_until_quiet(world)
    assert [m["messageKey"] for m in reported] == keys_of(world, ids[50:])


def test_FirstRun_OlderThanThirtyDays_IsNotRead(world):
    world.plant()
    world.graph.box(MAILBOX)["inbox"] += [msg("old", "2026-09-17T11:00:00Z"), msg("recent", "2026-09-19T11:00:00Z")]
    world.memory(channel())
    reported, _ = run_until_quiet(world)
    assert [m["messageKey"] for m in reported] == keys_of(world, ["recent"])


def test_FirstRun_EmptyInbox_CountsZeroAndStaysAFirstRun(world):
    world.plant()
    world.memory(channel())
    code, output, context, _ = world.run()
    assert code == 0 and output["count"] == 0
    assert context["channels"][0]["messages"] == []
    assert "cursor" not in world.read_memory()["channels"][0]


# ---------------------------------------------------------------------------------------------------- test 10


def test_Utf8_NonLatinSubjectsAndNames_SurviveTheOutput(world):
    subjects = ["Привет, мир", "こんにちは世界", "مرحبا بالعالم", "Γειά σου Κόσμε", "Ünïcödé — “quotes” 🎉"]
    start(world, [msg(f"u-{n}", at(n), subject=s, name=s[::-1], preview=s * 3) for n, s in enumerate(subjects)])
    _, _, context, _ = world.run()
    reported = all_messages(context)
    assert [m["subject"] for m in reported] == subjects
    assert [m["senderName"] for m in reported] == [s[::-1] for s in subjects]


def test_Utf8_TheBudgetCountsBytesNotCharacters(world):
    """Each message is about 615 characters but about 1,515 bytes of UTF-8 (three bytes for each Japanese character).
    Counting characters, 10 would fit (about 6,200 characters); counting bytes, only 4 do (5 would be about 7,600)."""
    ids = [f"jp-{n:02}" for n in range(12)]
    start(world, [msg(i, at(n), subject="件" * 250, preview="名" * 200) for n, i in enumerate(ids)])
    code, output, context, _ = world.run()
    reported = all_messages(context)
    one = world.screen.compact(reported[0])
    assert 10 * len(one) < BUDGET < 5 * len(one.encode("utf-8"))      # 10 fit in characters; 5 do not fit in bytes
    assert len(output["context"].encode("utf-8")) <= BUDGET
    assert len(reported) == 4
    reported_all, _ = run_until_quiet(world)
    assert [m["messageKey"] for m in reported + reported_all] == keys_of(world, ids)
