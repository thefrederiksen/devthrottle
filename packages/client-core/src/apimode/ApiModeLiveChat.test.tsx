import { act, cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { ApiModeLiveChat } from "./ApiModeLiveChat";
import {
  connectionItem, fakeLiveNetwork, receiptItem, replyDelta, replyFinal, settle, toolCard, turnEndedItem, workingDisplay,
} from "./liveStream.testkit";

// The shared live Chat, fed by a fake stream. Every word asserted here is a word the fake Gateway sent: the view's
// only job is to put each one in the right place.

afterEach(cleanup);

async function mount(display = workingDisplay(2)) {
  const net = fakeLiveNetwork();
  const view = render(<ApiModeLiveChat sessionId="s1" display={display} streamDeps={net.deps} />);
  await act(settle);
  return { net, view, conn: () => net.connections[net.connections.length - 1] };
}

async function send(fn: () => void) {
  await act(async () => {
    fn();
    await settle();
  });
}

describe("ApiModeLiveChat", () => {
  it("ApiModeLiveChat_MidTurn_ShowsThePromptItsReceiptTheStreamingReplyAndARunningToolCard", async () => {
    // Arrange
    const { conn } = await mount();

    // Act
    await send(() => {
      conn().items("g1:1", [receiptItem("p1", "Delivered", "green", 1, undefined, "List the files, then say PONG")]);
      conn().items("g1:3", [toolCard("c1", "t1", "bash", "{\"command\":\"ls\"}", "running", "neutral", 3)]);
      conn().items("g1:4", [replyDelta("m1", "t1", "Here are ", 4)]);
      conn().items("g1:5", [replyDelta("m1", "t1", "the files", 5)]);
    });

    // Assert
    const log = screen.getByRole("log", { name: "Live conversation" });
    expect(within(log).getByText("List the files, then say PONG")).toBeTruthy();
    expect(within(log).getByText("Delivered").className).toContain("live-tone-green");
    expect(within(log).getByRole("article", { name: "bash: running" })).toBeTruthy();
    expect(within(log).getByText("Here are the files")).toBeTruthy();
  });

  it("ApiModeLiveChat_ToolFinishes_TheSameCardGoesFromRunningToDone", async () => {
    const { conn } = await mount();
    await send(() => conn().items("g1:3", [toolCard("c1", "t1", "bash", "{}", "running", "neutral", 3)]));

    await send(() => conn().items("g1:6", [toolCard("c1", "t1", "bash", "{}", "done", "green", 6)]));

    expect(screen.queryByRole("article", { name: "bash: running" })).toBeNull();
    const card = screen.getByRole("article", { name: "bash: done" });
    expect(card.className).toContain("live-tone-green");
    expect(screen.getAllByRole("article", { name: /^bash:/ })).toHaveLength(1);
  });

  it("ApiModeLiveChat_StatusLine_ShowsTheGatewayVerdictWordsVerbatim", async () => {
    await mount({
      ...workingDisplay(3),
      queuedCount: 2,
      queueLabel: "2 prompts waiting: a turn is running",
    });

    const status = screen.getByRole("status", { name: "Session status" });
    expect(within(status).getByText("API mode")).toBeTruthy();
    expect(within(status).getByText("Working - turn 3")).toBeTruthy();
    expect(within(status).getByText("2 prompts waiting: a turn is running")).toBeTruthy();
    expect(within(status).getByText("Agent connected (gpt-5.1-codex-mini)")).toBeTruthy();
  });

  it("ApiModeLiveChat_Idle_ShowsIdleWithTheTurnNumberAndTheLastStoppedTurn", async () => {
    await mount({
      ...workingDisplay(4),
      kind: "idle",
      tone: "neutral",
      working: false,
      activityLabel: "Idle after turn 4",
      lastTurnEnding: "The last turn was stopped: stopped by the user",
    });

    expect(screen.getByText("Idle after turn 4")).toBeTruthy();
    expect(screen.getByText("The last turn was stopped: stopped by the user")).toBeTruthy();
  });

  it("ApiModeLiveChat_RefusedPrompt_ShowsTheRefusalWithItsReason", async () => {
    const { conn } = await mount();

    await send(() => conn().items("g1:2", [receiptItem(
      "p2", "Refused: Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.", "red", 2,
      undefined, "A second prompt")]));

    const refusal = screen.getByText(/^Refused: Agent is already processing/);
    expect(refusal.className).toContain("live-tone-red");
    expect(screen.getByText("A second prompt")).toBeTruthy();
  });

  it("ApiModeLiveChat_QueuedPrompt_ShowsTheQueuedWordsThenDeliveredInTheSamePlace", async () => {
    const { conn } = await mount();
    await send(() => conn().items("g1:1", [receiptItem("p3", "Queued: a turn is running", "neutral", 1, undefined, "Then this")]));
    expect(screen.getByText("Queued: a turn is running")).toBeTruthy();

    await send(() => conn().items("g1:9", [receiptItem("p3", "Delivered", "green", 9, undefined, "Then this")]));

    expect(screen.queryByText("Queued: a turn is running")).toBeNull();
    expect(screen.getByText("Delivered")).toBeTruthy();
    expect(screen.getAllByText("Then this")).toHaveLength(1);
  });

  it("ApiModeLiveChat_FailedTurn_ShowsTheFailureBesideDeliveredAndTheTurnLine", async () => {
    const { conn } = await mount();
    await send(() => conn().items("g1:1", [receiptItem("p1", "Delivered", "green", 1, undefined, "Count to ten")]));

    await send(() => conn().items("g1:8", [
      turnEndedItem("t1", "Turn failed: Pi exited during the turn (exit code 137)", "red", 8),
      receiptItem("p1", "Delivered - the turn failed: Pi exited during the turn (exit code 137)", "red", 8, "t1", "Count to ten"),
    ]));

    expect(screen.queryByText("Delivered")).toBeNull();
    expect(screen.getByText("Delivered - the turn failed: Pi exited during the turn (exit code 137)").className).toContain("live-tone-red");
    expect(screen.getByText("Turn failed: Pi exited during the turn (exit code 137)")).toBeTruthy();
  });

  it("ApiModeLiveChat_StoppedTurn_ShowsDeliveredStopped", async () => {
    const { conn } = await mount();
    await send(() => conn().items("g1:1", [receiptItem("p1", "Delivered", "green", 1, undefined, "Write an essay")]));

    await send(() => conn().items("g1:5", [
      turnEndedItem("t1", "Turn stopped: stopped by the user", "yellow", 5),
      receiptItem("p1", "Delivered - stopped", "yellow", 5, "t1", "Write an essay"),
    ]));

    expect(screen.getByText("Delivered - stopped").className).toContain("live-tone-yellow");
    expect(screen.getByText("Turn stopped: stopped by the user")).toBeTruthy();
  });

  it("ApiModeLiveChat_ReplyFinal_ReplacesTheStreamedTextAndRendersMarkdown", async () => {
    const { conn, view } = await mount();
    await send(() => conn().items("g1:4", [replyDelta("m1", "t1", "**PO", 4)]));

    await send(() => conn().items("g1:5", [replyFinal("m1", "t1", "**PONG**", 5)]));

    const strong = view.container.querySelector(".live-chat-reply strong");
    expect(strong?.textContent).toBe("PONG");
  });

  it("ApiModeLiveChat_ConnectionGone_ShowsTheLineAndItsDetail", async () => {
    const { conn } = await mount();

    await send(() => conn().items("g1:9", [connectionItem(9, "Agent disconnected: Pi exited", "red", "exit code 137")]));

    expect(screen.getByText("Agent disconnected: Pi exited")).toBeTruthy();
    expect(screen.getByText("exit code 137")).toBeTruthy();
  });

  it("ApiModeLiveChat_Reset_ClearsTheThreadAndShowsTheGatewaySentence", async () => {
    const { conn } = await mount();
    await send(() => conn().items("g1:4", [replyFinal("m1", "t1", "Old reply", 4)]));

    await send(() => {
      conn().reset({ code: "new_generation", message: "The machine running this session restarted.", rebuildFrom: [] });
      conn().items("g2:1", [receiptItem("p1", "Delivered", "green", 0, undefined, "Hello")]);
    });

    expect(screen.queryByText("Old reply")).toBeNull();
    expect(screen.getByText("The machine running this session restarted.")).toBeTruthy();
    expect(screen.getByText("Hello")).toBeTruthy();
  });

  it("ApiModeLiveChat_StreamRefused_ShowsTheGatewaySentence", async () => {
    const net = fakeLiveNetwork();
    net.answers.push({ status: 503, error: "This session's Director is away, and the Gateway holds no events for it." });

    render(<ApiModeLiveChat sessionId="s1" display={workingDisplay(1)} streamDeps={net.deps} />);
    await act(settle);

    expect(screen.getByText(/This session's Director is away, and the Gateway holds no events for it\./)).toBeTruthy();
  });

  it("ApiModeLiveChat_ItemWithAnUnknownMode_IsReportedAndTheThreadStays", async () => {
    const { conn } = await mount();
    await send(() => conn().items("g1:1", [receiptItem("p1", "Delivered", "green", 1, undefined, "Hi")]));

    await send(() => conn().items("g1:2", [{ ...replyFinal("m1", "t1", "x", 2), mode: "merge" }]));

    expect(screen.getByRole("alert").textContent).toContain("merge");
    expect(screen.getByText("Hi")).toBeTruthy();
  });

  it("ApiModeLiveChat_NoVerdictYet_ShowsNoStatusLine", async () => {
    await mount(null as never);

    expect(screen.queryByRole("status", { name: "Session status" })).toBeNull();
  });
});
