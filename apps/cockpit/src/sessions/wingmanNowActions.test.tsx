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
import { wingmanNowActions } from "./wingmanNowActions";

const SID = "6e4a7c30-0000-4000-8000-000000000070";
const OPTION = { index: 1, key: "Allow the merge", note: null, recommended: true };

const GO = { openTerminal: () => {}, goToSession: () => {}, openSettings: () => {} };

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

    const outcome = await wingmanNowActions(SID, GO).onAnswerOption!(OPTION, "v-3002");

    expect(outcome.accepted).toBe(false);
    expect(outcome.message).toContain("The screen has moved on since that question was asked.");
  });

  it("hands back what the answer route said when it accepted the answer", async () => {
    fakeGateway({
      "turn-verdict/answer": [200, { accepted: true, code: "written", reason: "Sent option 1 to the session." }],
    });

    const outcome = await wingmanNowActions(SID, GO).onAnswerOption!(OPTION, "v-3002");

    expect(outcome).toEqual({ accepted: true, message: "Sent option 1 to the session." });
    expect(calls[0]).toBe(`/sessions/${SID}/turn-verdict/answer`);
  });

  it("reports a 200 the route did NOT accept as not accepted, in the route's own words", async () => {
    fakeGateway({
      "turn-verdict/answer": [200, { accepted: false, code: "stale", reason: "That stop is no longer on screen." }],
    });

    const outcome = await wingmanNowActions(SID, GO).onAnswerOption!(OPTION, "v-3002");

    expect(outcome).toEqual({ accepted: false, message: "That stop is no longer on screen." });
  });

  it("sends a verdict identifier the Gateway can refuse rather than doing nothing at all", async () => {
    fakeGateway({ "turn-verdict/answer": [400, { error: "No stop was named." }] });

    const outcome = await wingmanNowActions(SID, GO).onAnswerOption!(OPTION, null);

    // The call was MADE. A shell that quietly skipped it would leave buttons that look enabled and do nothing.
    expect(calls[0]).toBe(`/sessions/${SID}/turn-verdict/answer`);
    expect(outcome.accepted).toBe(false);
    expect(outcome.message).toContain("No stop was named.");
  });

  it("says a reply reached the session, and says in the Gateway's words when it did not", async () => {
    fakeGateway({ "/prompt": [200, {}] });
    const sent = await wingmanNowActions(SID, GO).onSendReply!("allow the merge");
    expect(sent).toEqual({ accepted: true, message: "Sent to the session." });

    fakeGateway({ "/prompt": [502, { error: "The machine running this session could not be reached." }] });
    const failed = await wingmanNowActions(SID, GO).onSendReply!("allow the merge");
    // Not accepted is what keeps the owner's words in the box.
    expect(failed.accepted).toBe(false);
    expect(failed.message).toContain("The machine running this session could not be reached.");
  });

  it("says what snoozing did, including a snooze that arms when the work ends", async () => {
    fakeGateway({ "/hold": [200, { onHold: false, pending: true }] });
    const deferred = await wingmanNowActions(SID, GO).onSnooze!();
    expect(deferred).toEqual({ accepted: true, message: "Snoozing when it finishes" });

    fakeGateway({ "/hold": [200, { onHold: true, pending: false }] });
    const armed = await wingmanNowActions(SID, GO).onSnooze!();
    expect(armed).toEqual({ accepted: true, message: "Snoozed" });

    fakeGateway({ "/hold": [403, { error: "This session belongs to another account." }] });
    const refused = await wingmanNowActions(SID, GO).onSnooze!();
    expect(refused.accepted).toBe(false);
    expect(refused.message).toContain("This session belongs to another account.");
  });

  it("turns voice on with both calls the product makes, and says what the Gateway had to add", async () => {
    fakeGateway({
      "voice-mode": [200, {}],
      "wingman/explain": [200, { reply: "", spoken: "There is nothing to read out yet.", replySeconds: 0, nothingYet: true }],
    });

    const outcome = await wingmanNowActions(SID, GO).onTurnOnVoice!();

    expect(calls).toEqual([`/sessions/${SID}/voice-mode`, `/sessions/${SID}/wingman/explain`]);
    expect(outcome).toEqual({ accepted: true, message: "There is nothing to read out yet." });
  });

  it("adds nothing of its own when the Gateway narrated the turn, leaving the Gateway's sentence to the view", async () => {
    fakeGateway({
      "voice-mode": [200, {}],
      "wingman/explain": [200, { reply: "r", spoken: "s", replySeconds: 2, nothingYet: false }],
    });

    expect(await wingmanNowActions(SID, GO).onTurnOnVoice!()).toEqual({ accepted: true, message: "" });
  });

  it("says in the Gateway's words when voice could not be turned on", async () => {
    fakeGateway({ "voice-mode": [502, { error: "That computer could not be reached." }] });

    const outcome = await wingmanNowActions(SID, GO).onTurnOnVoice!();

    expect(outcome.accepted).toBe(false);
    expect(outcome.message).toContain("That computer could not be reached.");
  });

  it("says so when the narration could not be played here, rather than leaving a silent button", async () => {
    // The Gateway has a narration; this machine has no way to hold or play its audio, which is a local fact.
    fakeGateway({ "wingman/voice": [200, { ready: false, generatedAt: "", spoken: "", reply: "" }] });

    const outcome = await wingmanNowActions(SID, GO).onPlayVoice!();

    expect(outcome).toEqual({ accepted: false, message: "The narration could not be played on this computer." });
  });

  it("says in the Gateway's words when the narration could not even be read", async () => {
    fakeGateway({ "wingman/voice": [403, { error: "This session belongs to another account." }] });

    const outcome = await wingmanNowActions(SID, GO).onPlayVoice!();

    expect(outcome.accepted).toBe(false);
    expect(outcome.message).toContain("This session belongs to another account.");
  });
});
