import { describe, expect, it } from "vitest";
import { applyItems, applyReset, emptyConversation, LiveItemError } from "./liveConversation";
import { receiptItem, replyDelta, replyFinal, toolCard, turnEndedItem } from "./liveStream.testkit";

// The client applies the Gateway's items exactly as each says (append or replace, by item id) and rules nothing.

describe("applyItems", () => {
  it("applyItems_DeltasForOneReply_AppendInOrderToOneEntry", () => {
    // Arrange
    const items = [replyDelta("m1", "t1", "Hel", 4), replyDelta("m1", "t1", "lo", 5), replyDelta("m1", "t1", " there", 6)];

    // Act
    const state = applyItems(emptyConversation(), items);

    // Assert
    expect(state.entries).toHaveLength(1);
    expect(state.entries[0]).toMatchObject({ itemId: "reply:m1", kind: "replyText", text: "Hello there" });
  });

  it("applyItems_FinalAfterDeltas_ReplacesEverythingStreamed", () => {
    const streamed = applyItems(emptyConversation(), [replyDelta("m1", "t1", "PON", 4)]);

    const state = applyItems(streamed, [replyFinal("m1", "t1", "PONG", 5)]);

    expect(state.entries.map((e) => e.text)).toEqual(["PONG"]);
  });

  it("applyItems_ToolCardRunningThenDone_IsOneCardInItsFirstPlace", () => {
    const running = applyItems(emptyConversation(), [
      receiptItem("p1", "Delivered", "green", 1),
      toolCard("c1", "t1", "bash", "{\"command\":\"ls\"}", "running", "neutral", 3),
      replyDelta("m1", "t1", "Listing.", 4),
    ]);

    const done = applyItems(running, [toolCard("c1", "t1", "bash", "{\"command\":\"ls\"}", "done", "green", 5)]);

    expect(done.entries.map((e) => e.itemId)).toEqual(["receipt:p1", "tool:c1", "reply:m1"]);
    expect(done.entries[1]).toMatchObject({ status: "done", tone: "green", detail: "{\"command\":\"ls\"}" });
  });

  it("applyItems_ReceiptReplacedByFailedTurnWords_KeepsItsPlaceAndTakesTheNewWords", () => {
    const delivered = applyItems(emptyConversation(), [
      receiptItem("p1", "Delivered", "green", 1),
      replyDelta("m1", "t1", "Work", 3),
    ]);

    const failed = applyItems(delivered, [
      turnEndedItem("t1", "Turn failed: Pi exited during the turn (exit code 137)", "red", 4),
      receiptItem("p1", "Delivered - the turn failed: Pi exited during the turn (exit code 137)", "red", 4, "t1"),
    ]);

    expect(failed.entries.map((e) => e.itemId)).toEqual(["receipt:p1", "reply:m1", "turn:t1"]);
    expect(failed.entries[0]).toMatchObject({ text: "Delivered - the turn failed: Pi exited during the turn (exit code 137)", tone: "red" });
  });

  it("applyItems_TheSameItemReplayedTwice_DoesNotDoubleIt", () => {
    const once = applyItems(emptyConversation(), [receiptItem("p1", "Delivered", "green", 1)]);

    const twice = applyItems(once, [receiptItem("p1", "Delivered", "green", 1)]);

    expect(twice.entries).toHaveLength(1);
  });

  it("applyItems_ArrivalOrder_IsKeptEvenWhenSequencesGoBackwards", () => {
    // After a reset the stored receipts arrive with sequence 0, after items with higher ones.
    const state = applyItems(emptyConversation(), [replyFinal("m9", "t9", "later", 40), receiptItem("p1", "Delivered", "green", 0)]);

    expect(state.entries.map((e) => e.itemId)).toEqual(["reply:m9", "receipt:p1"]);
  });

  it("applyItems_UnknownMode_ThrowsAndChangesNothing", () => {
    const before = applyItems(emptyConversation(), [receiptItem("p1", "Delivered", "green", 1)]);

    expect(() => applyItems(before, [{ ...receiptItem("p2", "Delivered", "green", 2), mode: "merge" }])).toThrow(LiveItemError);
    expect(before.entries).toHaveLength(1);
  });

  it("applyItems_NoItems_ReturnsTheSameState", () => {
    const state = emptyConversation();

    expect(applyItems(state, [])).toBe(state);
  });
});

describe("applyReset", () => {
  it("applyReset_AfterItems_LetsEverythingGoAndKeepsTheGatewaySentence", () => {
    const held = applyItems(emptyConversation(), [receiptItem("p1", "Delivered", "green", 1), replyFinal("m1", "t1", "Hi", 2)]);
    const reset = { code: "new_generation", message: "The Director restarted.", rebuildFrom: ["/sessions/s/receipts"] };

    const state = applyReset(held, reset);

    expect(state.entries).toEqual([]);
    expect(state.reset).toEqual(reset);
  });

  it("applyItems_AfterAReset_BuildsTheViewAgainAndKeepsTheResetSentence", () => {
    const reset = { code: "missed_events", message: "Some events were missed.", rebuildFrom: [] };
    const cleared = applyReset(emptyConversation(), reset);

    const state = applyItems(cleared, [receiptItem("p1", "Delivered", "green", 0)]);

    expect(state.entries).toHaveLength(1);
    expect(state.reset).toEqual(reset);
  });
});
