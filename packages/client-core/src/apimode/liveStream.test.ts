import { describe, expect, it } from "vitest";
import { LIVE_CONNECTING_MESSAGE, LIVE_ENDED_MESSAGE, openLiveStream, SseParser, type LiveStreamState } from "./liveStream";
import type { ApiModeLiveItem, ApiModeLiveReset } from "./liveTypes";
import { fakeLiveNetwork, receiptItem, replyDelta, replyFinal, settle } from "./liveStream.testkit";

// The reader of GET /sessions/{sid}/live, driven by a fake network and a fake timer: no real port, sleep or clock.

function recorder() {
  const items: ApiModeLiveItem[][] = [];
  const openings: boolean[] = [];
  const resets: ApiModeLiveReset[] = [];
  const states: LiveStreamState[] = [];
  const errors: string[] = [];
  return {
    items,
    openings,
    resets,
    states,
    errors,
    handlers: {
      onItems: (i: ApiModeLiveItem[], opening: boolean) => {
        items.push(i);
        openings.push(opening);
      },
      onReset: (r: ApiModeLiveReset) => resets.push(r),
      onState: (s: LiveStreamState) => states.push(s),
      onProblem: (e: string) => errors.push(e),
    },
  };
}

describe("SseParser", () => {
  it("push_FrameSplitAcrossChunks_IsReturnedOnceWhole", () => {
    const parser = new SseParser();

    const first = parser.push("id: g1:3\nevent: it");
    const second = parser.push("ems\ndata: [1]\n\n");

    expect(first).toEqual([]);
    expect(second).toEqual([{ id: "g1:3", event: "items", data: "[1]" }]);
  });

  it("push_KeepAliveComment_IsIgnored", () => {
    expect(new SseParser().push(": keep-alive\n\n")).toEqual([]);
  });

  it("push_CarriageReturnLineFeeds_AreLineEnds", () => {
    expect(new SseParser().push("event: items\r\ndata: [2]\r\n\r\n")).toEqual([{ id: undefined, event: "items", data: "[2]" }]);
  });

  it("push_SeveralDataLines_JoinWithALineFeed", () => {
    expect(new SseParser().push("data: a\ndata: b\n\n")).toEqual([{ id: undefined, event: "message", data: "a\nb" }]);
  });

  it("push_EmptyId_IsKeptAsEmpty", () => {
    expect(new SseParser().push("id: \nevent: reset\ndata: {}\n\n")).toEqual([{ id: "", event: "reset", data: "{}" }]);
  });
});

describe("openLiveStream", () => {
  it("openLiveStream_FirstOpen_SendsNoCursorAndHandsOnEachFrameInOrder", async () => {
    // Arrange
    const net = fakeLiveNetwork();
    const rec = recorder();

    // Act
    const close = openLiveStream("s1", rec.handlers, net.deps);
    await settle();
    net.connections[0].items("g1:1", [receiptItem("p1", "Delivered", "green", 1)]);
    net.connections[0].items("g1:2", [replyDelta("m1", "t1", "Hi", 2)]);
    await settle();
    close();

    // Assert
    expect(net.connections[0].url).toBe("/sessions/s1/live");
    expect(net.connections[0].headers["Last-Event-ID"]).toBeUndefined();
    expect(net.connections[0].url).not.toContain("after=");
    expect(rec.items.map((f) => f[0].itemId)).toEqual(["receipt:p1", "reply:m1"]);
    expect(rec.states[0]).toEqual({ open: false, message: LIVE_CONNECTING_MESSAGE });
    expect(rec.states.at(-1)).toEqual({ open: true, message: null });
  });

  it("openLiveStream_StreamEnds_ReconnectsWithTheLastFrameCursor", async () => {
    const net = fakeLiveNetwork();
    const rec = recorder();
    const close = openLiveStream("s1", rec.handlers, net.deps);
    await settle();
    net.connections[0].items("g1:7", [receiptItem("p1", "Delivered", "green", 7)]);
    await settle();

    net.connections[0].end();
    await settle();
    expect(rec.states.at(-1)?.message).toContain(LIVE_ENDED_MESSAGE);
    expect(net.pendingTimers()).toBe(1);
    net.runTimers();
    await settle();
    close();

    expect(net.connections).toHaveLength(2);
    expect(net.connections[1].headers["Last-Event-ID"]).toBe("g1:7");
  });

  it("openLiveStream_Reset_IsHandedOnAndClearsTheCursorForTheNextConnect", async () => {
    const net = fakeLiveNetwork();
    const rec = recorder();
    const close = openLiveStream("s1", rec.handlers, net.deps);
    await settle();
    net.connections[0].items("g1:7", [receiptItem("p1", "Delivered", "green", 7)]);
    const reset = { code: "new_generation", message: "The Director restarted.", rebuildFrom: [] };
    net.connections[0].reset(reset);
    await settle();

    net.connections[0].end();
    await settle();
    net.runTimers();
    await settle();
    close();

    expect(rec.resets).toEqual([reset]);
    expect(net.connections[1].headers["Last-Event-ID"]).toBeUndefined();
  });

  it("openLiveStream_NoCursor_MarksTheFirstFrameAsTheOpeningAndNoOtherFrame", async () => {
    // Arrange
    const net = fakeLiveNetwork();
    const rec = recorder();

    // Act
    const close = openLiveStream("s1", rec.handlers, net.deps);
    await settle();
    net.connections[0].items("g1:4", [receiptItem("p1", "Delivered", "green", 0), replyFinal("m1", "t1", "Stored", 3)]);
    net.connections[0].items("g1:5", [replyDelta("m2", "t2", "Live", 5)]);
    await settle();
    close();

    // Assert
    expect(rec.openings).toEqual([true, false]);
  });

  it("openLiveStream_ReconnectWithACursor_TheGatewayContinuesSoNoFrameIsAnOpening", async () => {
    // Arrange
    const net = fakeLiveNetwork();
    const rec = recorder();
    const close = openLiveStream("s1", rec.handlers, net.deps);
    await settle();
    net.connections[0].items("g1:4", [replyFinal("m1", "t1", "Stored", 3)]);
    net.connections[0].end();
    await settle();

    // Act
    net.runTimers();
    await settle();
    net.connections[1].items("g1:5", [replyDelta("m2", "t2", "Live", 5)]);
    await settle();
    close();

    // Assert
    expect(net.connections[1].headers["Last-Event-ID"]).toBe("g1:4");
    expect(rec.openings).toEqual([true, false]);
  });

  it("openLiveStream_OpeningWithNoIdThenTheStreamEnds_TheReconnectHasNoCursorAndIsOpenedAgain", async () => {
    // Arrange: the Gateway holds no event of the session, so its opening carries no id.
    const net = fakeLiveNetwork();
    const rec = recorder();
    const close = openLiveStream("s1", rec.handlers, net.deps);
    await settle();
    net.connections[0].write(`event: items\ndata: ${JSON.stringify([replyFinal("m1", "t1", "Stored", 0)])}\n\n`);
    net.connections[0].end();
    await settle();

    // Act
    net.runTimers();
    await settle();
    net.connections[1].items("g2:1", [replyFinal("m1", "t1", "Stored", 0)]);
    await settle();
    close();

    // Assert
    expect(net.connections[1].headers["Last-Event-ID"]).toBeUndefined();
    expect(rec.openings).toEqual([true, true]);
  });

  it("openLiveStream_FrameAfterAReset_IsTheOpening", async () => {
    // Arrange
    const net = fakeLiveNetwork();
    const rec = recorder();
    const close = openLiveStream("s1", rec.handlers, net.deps);
    await settle();
    net.connections[0].items("g1:4", [replyFinal("m1", "t1", "Before", 3)]);

    // Act: the Director restarted; the Gateway resets, and its stored conversation follows as the opening.
    net.connections[0].reset({ code: "new_generation", message: "The Director restarted.", rebuildFrom: ["/sessions/s1/live"] });
    net.connections[0].items("g2:1", [replyFinal("m1", "t1", "Before", 3)]);
    net.connections[0].items("g2:2", [replyDelta("m2", "t2", "After", 2)]);
    await settle();
    close();

    // Assert
    expect(rec.openings).toEqual([true, true, false]);
  });

  it("openLiveStream_Refused_ShowsTheGatewaySentenceAndTriesAgain", async () => {
    const net = fakeLiveNetwork();
    net.answers.push({ status: 503, error: "This session's Director is away, and the Gateway holds no events for it." });
    const rec = recorder();

    const close = openLiveStream("s1", rec.handlers, net.deps);
    await settle();
    const refusedState = rec.states.at(-1);
    net.runTimers();
    await settle();
    close();

    expect(refusedState?.open).toBe(false);
    expect(refusedState?.message).toContain("This session's Director is away, and the Gateway holds no events for it.");
    expect(refusedState?.message).toContain("Trying again in 3 seconds.");
    expect(net.connections).toHaveLength(1);
    expect(rec.states.at(-1)).toEqual({ open: true, message: null });
  });

  it("openLiveStream_FrameThatIsNotJson_IsReportedAndDoesNotMoveTheCursor", async () => {
    const net = fakeLiveNetwork();
    const rec = recorder();
    const close = openLiveStream("s1", rec.handlers, net.deps);
    await settle();
    net.connections[0].items("g1:1", [receiptItem("p1", "Delivered", "green", 1)]);
    net.connections[0].write("id: g1:2\nevent: items\ndata: {not json\n\n");
    await settle();
    net.connections[0].end();
    await settle();
    net.runTimers();
    await settle();
    close();

    expect(rec.errors).toHaveLength(1);
    expect(rec.items).toHaveLength(1);
    expect(net.connections[1].headers["Last-Event-ID"]).toBe("g1:1");
  });

  it("openLiveStream_UnknownFrameKind_IsReported", async () => {
    const net = fakeLiveNetwork();
    const rec = recorder();
    const close = openLiveStream("s1", rec.handlers, net.deps);
    await settle();

    net.connections[0].write("id: g1:1\nevent: surprise\ndata: {}\n\n");
    await settle();
    close();

    expect(rec.errors).toHaveLength(1);
    expect(String(rec.errors[0])).toContain("surprise");
  });

  it("openLiveStream_Closed_AbortsTheStreamAndSchedulesNothing", async () => {
    const net = fakeLiveNetwork();
    const rec = recorder();
    const close = openLiveStream("s1", rec.handlers, net.deps);
    await settle();

    close();
    await settle();

    expect(net.connections[0].signal?.aborted).toBe(true);
    expect(net.pendingTimers()).toBe(0);
  });
});
