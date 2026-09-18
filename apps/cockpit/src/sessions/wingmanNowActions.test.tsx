// @vitest-environment jsdom
// What the Cockpit's Now actions do with what the Gateway answers (the Wingman tab, version 3, item 3).
//
// THE DEFECT THESE EXIST FOR. The first wiring was fire-and-forget: `void sendPrompt(...)` and an option answer whose
// result was thrown away. A refused answer showed the owner NOTHING - the buttons stayed put, he could click again,
// and the route's own sentence, which the settled design says he sees unedited, never left this file. A failed send
// was an unhandled rejection, and the reply box had already emptied itself, so his typed words were gone too.
//
// So every test below drives a REAL Gateway answer through the real client and asserts on the outcome the view is
// handed: accepted or not, and the sentence. An action that swallowed its answer, or that rejected instead of
// answering, goes red here.
import { afterEach, describe, expect, it, vi } from "vitest";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { wingmanNowActions } from "./wingmanNowActions";

const SID = "6e4a7c30-0000-4000-8000-000000000070";
const OPTION = { index: 1, key: "Allow the merge", note: null, recommended: true };

const GO = { openTerminal: () => {}, goToSession: () => {}, openSettings: () => {}, openStop: () => {} };

/** The roster row as the Gateway stamps it. Only `onHold` is read here; the rest is what a row carries. */
const row = (onHold: boolean) => ({ sessionId: SID, onHold }) as unknown as SessionDto;

/** The actions for a live, un-snoozed session in a state the tests do not care about. */
const actions = (nowState = "needs-you") => wingmanNowActions(SID, row(false), nowState, GO);

let calls: string[] = [];

/** Answers each route with its own status and body, so one call can be refused while another succeeds. */
function fakeGateway(routes: Record<string, [number, unknown]>) {
  calls = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string) => {
      calls.push(url);
      const hit = Object.keys(routes).find((k) => url.includes(k));
      const [status, body] = hit ? routes[hit] : [500, { error: "no route in this test" }];
      return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    }),
  );
}

afterEach(() => vi.unstubAllGlobals());

describe("what Now can do, and what it says about it", () => {
  it("hands back the answer route's own refusal sentence, unedited, instead of discarding it", async () => {
    fakeGateway({
      "turn-verdict/answer": [409, { error: "The screen has moved on since that question was asked." }],
    });

    const outcome = await actions().onAnswerOption!(OPTION, "v-3002");

    expect(outcome.accepted).toBe(false);
    expect(outcome.message).toContain("The screen has moved on since that question was asked.");
  });

  it("hands back what the answer route said when it accepted the answer", async () => {
    fakeGateway({
      "turn-verdict/answer": [200, { accepted: true, code: "written", reason: "Sent option 1 to the session." }],
    });

    const outcome = await actions().onAnswerOption!(OPTION, "v-3002");

    expect(outcome).toEqual({ accepted: true, message: "Sent option 1 to the session." });
    expect(calls[0]).toBe(`/sessions/${SID}/turn-verdict/answer`);
  });

  it("reports a 200 the route did NOT accept as not accepted, in the route's own words", async () => {
    fakeGateway({
      "turn-verdict/answer": [200, { accepted: false, code: "stale", reason: "That stop is no longer on screen." }],
    });

    const outcome = await actions().onAnswerOption!(OPTION, "v-3002");

    expect(outcome).toEqual({ accepted: false, message: "That stop is no longer on screen." });
  });

  it("sends a verdict identifier the Gateway can refuse rather than doing nothing at all", async () => {
    fakeGateway({ "turn-verdict/answer": [400, { error: "No stop was named." }] });

    const outcome = await actions().onAnswerOption!(OPTION, null);

    // The call was MADE. A shell that quietly skipped it would leave buttons that look enabled and do nothing.
    expect(calls[0]).toBe(`/sessions/${SID}/turn-verdict/answer`);
    expect(outcome.accepted).toBe(false);
    expect(outcome.message).toContain("No stop was named.");
  });

  it("says what snoozing did, including a snooze that arms when the work ends", async () => {
    fakeGateway({ "/hold": [200, { onHold: false, pending: true }] });
    const deferred = await actions().onSnooze!();
    expect(deferred).toEqual({ accepted: true, message: "Snoozing when it finishes" });

    fakeGateway({ "/hold": [200, { onHold: true, pending: false }] });
    const armed = await actions().onSnooze!();
    expect(armed).toEqual({ accepted: true, message: "Snoozed" });

    fakeGateway({ "/hold": [403, { error: "This session belongs to another account." }] });
    const refused = await actions().onSnooze!();
    expect(refused.accepted).toBe(false);
    expect(refused.message).toContain("This session belongs to another account.");
  });

  it("turns voice on with both calls the product makes, and says what the Gateway had to add", async () => {
    fakeGateway({
      "voice-mode": [200, {}],
      "wingman/explain": [200, { reply: "", spoken: "There is nothing to read out yet.", replySeconds: 0, nothingYet: true }],
    });

    const outcome = await actions().onTurnOnVoice!();

    expect(calls).toEqual([`/sessions/${SID}/voice-mode`, `/sessions/${SID}/wingman/explain`]);
    expect(outcome).toEqual({ accepted: true, message: "There is nothing to read out yet." });
  });

  it("adds nothing of its own when the Gateway narrated the turn, leaving the Gateway's sentence to the view", async () => {
    fakeGateway({
      "voice-mode": [200, {}],
      "wingman/explain": [200, { reply: "r", spoken: "s", replySeconds: 2, nothingYet: false }],
    });

    expect(await actions().onTurnOnVoice!()).toEqual({ accepted: true, message: "" });
  });

  it("says in the Gateway's words when voice could not be turned on", async () => {
    fakeGateway({ "voice-mode": [502, { error: "That computer could not be reached." }] });

    const outcome = await actions().onTurnOnVoice!();

    expect(outcome.accepted).toBe(false);
    expect(outcome.message).toContain("That computer could not be reached.");
  });

  it("says so when the narration could not be played here, rather than leaving a silent button", async () => {
    // The Gateway has a narration; this machine has no way to hold or play its audio, which is a local fact.
    fakeGateway({ "wingman/voice": [200, { ready: false, generatedAt: "", spoken: "", reply: "" }] });

    const outcome = await actions().onPlayVoice!();

    expect(outcome).toEqual({ accepted: false, message: "The narration could not be played on this computer." });
  });

  it("offers a snooze and no wake while the session is running, and the other way round once it is snoozed", () => {
    // WHICH ONE IS OFFERED IS THE GATEWAY'S ANSWER, not a guess: `onHold` is the roster row's own field. The screen
    // that shipped offered "Snooze this session" on a session that was already snoozed, and nothing on it said what
    // a second snooze would do.
    const running = wingmanNowActions(SID, row(false), "needs-you", GO);
    expect(running.onSnooze).toBeDefined();
    expect(running.onUnsnooze).toBeUndefined();

    const snoozed = wingmanNowActions(SID, row(true), "other", GO);
    expect(snoozed.onSnooze).toBeUndefined();
    expect(snoozed.onUnsnooze).toBeDefined();
  });

  it("offers neither until the roster row has arrived, rather than a button that might be either", () => {
    const cold = wingmanNowActions(SID, undefined, "needs-you", GO);
    expect(cold.onSnooze).toBeUndefined();
    expect(cold.onUnsnooze).toBeUndefined();
    expect(cold.onClose).toBeUndefined();
  });

  it("says the session is still snoozed when the unsnooze did not take, instead of reporting success", async () => {
    fakeGateway({ "/hold": [200, { onHold: false, pending: false }] });
    const woke = await wingmanNowActions(SID, row(true), "other", GO).onUnsnooze!();
    expect(woke).toEqual({ accepted: true, message: "Unsnoozed." });

    fakeGateway({ "/hold": [200, { onHold: true, pending: false }] });
    const stuck = await wingmanNowActions(SID, row(true), "other", GO).onUnsnooze!();
    expect(stuck).toEqual({ accepted: false, message: "The session is still snoozed." });

    fakeGateway({ "/hold": [403, { error: "This session belongs to another account." }] });
    const refused = await wingmanNowActions(SID, row(true), "other", GO).onUnsnooze!();
    expect(refused.accepted).toBe(false);
    expect(refused.message).toContain("This session belongs to another account.");
  });

  it("offers a close on finished work only, and it opens the shell's own stop question", () => {
    const openStop = vi.fn();
    const done = wingmanNowActions(SID, row(false), "done", { ...GO, openStop });
    expect(done.onClose).toBeDefined();
    done.onClose!();
    expect(openStop).toHaveBeenCalled();

    // A report is telling him something and the work is not finished, so closing it is not the next step.
    expect(wingmanNowActions(SID, row(false), "report", GO).onClose).toBeUndefined();
    expect(wingmanNowActions(SID, row(false), "working", GO).onClose).toBeUndefined();
    expect(wingmanNowActions(SID, row(false), "needs-you", GO).onClose).toBeUndefined();
  });

  it("says in the Gateway's words when the narration could not even be read", async () => {
    fakeGateway({ "wingman/voice": [403, { error: "This session belongs to another account." }] });

    const outcome = await actions().onPlayVoice!();

    expect(outcome.accepted).toBe(false);
    expect(outcome.message).toContain("This session belongs to another account.");
  });
});
