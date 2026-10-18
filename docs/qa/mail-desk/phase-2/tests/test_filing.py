"""Tests 2 and 3: filing the queue the Gateway wrote.

Observe mode (the only mode in production in this mission, D17) must make NO move even when a filing queue is
present, and leave the queue exactly as it was. Live mode moves each queued message to Archive or Junk Email and
reports what happened to each.
"""

from conftest import MAILBOX, channel, msg

CURSOR = {"newest": "2026-10-18T09:00:00Z", "idsAtNewest": ["id-old"]}


def keyed(world, *ids):
    return {world.screen.short_key(MAILBOX, i): {"id": i, "reportedUtc": "2026-10-18T09:05:00Z"} for i in ids}


def test_ObserveMode_AFilingQueue_MakesNoMoveAndKeepsTheQueue(world):
    world.plant()
    inbox = world.graph.box(MAILBOX)["inbox"]
    inbox += [msg("id-1", "2026-10-18T08:00:00Z"), msg("id-2", "2026-10-18T08:30:00Z")]
    k1, k2 = world.screen.short_key(MAILBOX, "id-1"), world.screen.short_key(MAILBOX, "id-2")
    queue = [{"messageKey": k1, "decision": "archive"}, {"messageKey": k2, "decision": "junk"}]
    world.memory(channel(mode="observe", cursor=CURSOR, keys=keyed(world, "id-1", "id-2"), filingQueue=queue))

    code, output, context, err = world.run()

    assert code == 0
    assert world.graph.posts() == []                                   # no move request at all
    assert all(method == "GET" for method, _, _, _ in world.graph.log) and world.graph.log  # the log is live
    assert [m["id"] for m in world.graph.box(MAILBOX)["inbox"]] == ["id-1", "id-2"]
    kept = world.read_memory()["channels"][0]
    assert kept["filingQueue"] == queue                                # the queue survives untouched
    assert set(keyed(world, "id-1", "id-2")) <= set(kept["keys"])      # and so do the keys it names
    section = context["channels"][0]
    assert section["filing"] == []
    assert section["filingNotCarriedOut"] == {"count": 2, "reason": "the channel is in observe mode"}
    assert output["count"] == 0                                        # nothing new, nothing filed
    assert "NOT carried out" in err


def test_LiveMode_ArchiveAndJunk_AreMovedAndReported(world):
    world.plant()
    box = world.graph.box(MAILBOX)
    box["inbox"] += [msg("id-1", "2026-10-18T08:00:00Z"), msg("id-2", "2026-10-18T08:30:00Z")]
    k1, k2 = world.screen.short_key(MAILBOX, "id-1"), world.screen.short_key(MAILBOX, "id-2")
    world.memory(channel(mode="live", cursor=CURSOR, keys=keyed(world, "id-1", "id-2"),
                         filingQueue=[{"messageKey": k1, "decision": "archive"}, {"messageKey": k2, "decision": "junk"}]))

    code, output, context, _ = world.run()

    assert code == 0
    assert [(m, u.rsplit("/v1.0", 1)[1], b) for m, u, b, _ in world.graph.posts()] == [
        ("POST", "/me/messages/id-1/move", '{"destinationId": "archive"}'),
        ("POST", "/me/messages/id-2/move", '{"destinationId": "junkemail"}')]
    assert [m["id"] for m in box["archive"]] == ["moved-id-1"]
    assert [m["id"] for m in box["junkemail"]] == ["moved-id-2"]
    assert box["inbox"] == []
    assert context["channels"][0]["filing"] == [{"messageKey": k1, "decision": "archive", "result": "moved"},
                                                {"messageKey": k2, "decision": "junk", "result": "moved"}]
    assert output["count"] == 2                                        # something was filed
    assert world.read_memory()["channels"][0]["filingQueue"] == []


def test_LiveMode_AVanishedMessage_IsAlreadyGone(world):
    world.plant()
    key = world.screen.short_key(MAILBOX, "id-gone")
    world.memory(channel(mode="live", cursor=CURSOR, keys=keyed(world, "id-gone"),
                         filingQueue=[{"messageKey": key, "decision": "archive"}]))
    code, output, context, _ = world.run()
    assert code == 0
    assert context["channels"][0]["filing"] == [{"messageKey": key, "decision": "archive", "result": "already-gone"}]
    assert world.read_memory()["channels"][0]["filingQueue"] == []


def test_LiveMode_AKeyNotInTheTable_IsRefusedWithNoRequest(world):
    world.plant()
    world.memory(channel(mode="live", cursor=CURSOR, keys={}, filingQueue=[{"messageKey": "ffffffffffffffff", "decision": "archive"}]))
    code, _, context, _ = world.run()
    assert code == 0
    assert world.graph.posts() == []
    assert context["channels"][0]["filing"][0]["result"] == "refused"


def test_LiveMode_ALongQueue_FilesWhatFitsAndLeavesTheRestQueued(world):
    """The results share the 7 KB context with new mail, so at most half of it goes to filing; the items not
    attempted stay in the queue, in order, for the next run - none is lost and none is done twice."""
    world.plant()
    ids = [f"id-{n:03}" for n in range(120)]
    world.graph.box(MAILBOX)["inbox"] += [msg(i, "2026-10-18T08:00:00Z") for i in ids]
    queue = [{"messageKey": world.screen.short_key(MAILBOX, i), "decision": "archive"} for i in ids]
    world.memory(channel(mode="live", cursor=CURSOR, keys=keyed(world, *ids), filingQueue=queue))

    _, first, context, _ = world.run()
    done_first = len(context["channels"][0]["filing"])
    left = world.read_memory()["channels"][0]["filingQueue"]
    assert 0 < done_first < 120
    assert left == queue[done_first:]
    assert len(first["context"].encode("utf-8")) <= 7168

    done = done_first
    while world.read_memory()["channels"][0]["filingQueue"]:
        _, _, context, _ = world.run()
        done += len(context["channels"][0]["filing"])
    assert done == 120
    assert len(world.graph.posts()) == 120
    assert len(world.graph.box(MAILBOX)["archive"]) == 120


def test_LiveMode_FilingHappensBeforeNewMailIsRead(world):
    world.plant()
    world.graph.box(MAILBOX)["inbox"] += [msg("id-1", "2026-10-18T08:00:00Z")]
    key = world.screen.short_key(MAILBOX, "id-1")
    world.memory(channel(mode="live", cursor=CURSOR, keys=keyed(world, "id-1"),
                         filingQueue=[{"messageKey": key, "decision": "archive"}],
                         sentCache={"refreshedUtc": "2026-10-18T11:00:00Z", "hashes": []}))
    world.run()
    methods = [m for m, _, _, _ in world.graph.log]
    assert methods[0] == "POST" and set(methods[1:]) == {"GET"}
