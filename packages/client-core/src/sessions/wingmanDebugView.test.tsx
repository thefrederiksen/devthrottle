// THE DEBUG VIEW SHOWS BOTH CALLS, AND TELLS APART THE THREE THINGS THAT LOOK ALIKE.
//
// A reading takes two model calls. For each one the view has to distinguish:
//   - we asked, and here is what came back,
//   - we never asked,
//   - we asked and it went wrong, and here is why.
//
// Collapsing any two of those is the defect this view exists to stop. "Nothing here" and "we could not" read the
// same on a screen and mean opposite things, and the whole point of building it was to check a contract change
// against what the model was actually given and actually said.
//
// It also has to keep "you may not read this" apart from "there is nothing to read", because the route is staff
// only and a refusal that renders as an empty view says the feature is broken.
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { WingmanDebugView, WINGMAN_DEBUG_EMPTY } from "./WingmanDebugView";

const SID = "9a1c4d20-0000-4000-8000-000000000050";

/** One stop as the Gateway folds it, with only the fields a case cares about overridden. */
function stop(over: Record<string, unknown> = {}) {
  return {
    traceId: "t-1",
    observedAtUtc: "2026-09-18T11:00:00Z",
    recordedAtUtc: "2026-09-18T11:00:04Z",
    trigger: "turn-end",
    outcome: "judged",
    verdictId: "v-1",
    model: "devthrottle/wingman-fast",
    contractVersion: "v3",
    state: "finished-report",
    label: "Pushed the branch and opened the pull request",
    agentRecommends: null,
    narration: "The branch is pushed and the pull request is open.",
    failed: false,
    failureReason: null,
    fed: '{\n  "latestReply": "I have pushed the branch."\n}',
    fedAbsentText: null,
    judgePrompt: "You are the WINGMAN...",
    judgeRawReply: '{"state":"finished-report"}',
    judgePromptCutText: null,
    judgeRawReplyCutText: null,
    judgeSeconds: 4.2,
    judgeNotAskedText: null,
    narrationPrompt: "Output ONLY the spoken version...",
    narrationRawReply: "<<<ANSWER>>>The branch is pushed.<<<END>>>",
    narrationPromptCutText: null,
    narrationRawReplyCutText: null,
    narrationSeconds: 2.1,
    narrationNotMadeText: null,
    narrationFailureDetail: null,
    rowColour: "cyan",
    rowLabel: "Telling you",
    ...over,
  };
}

function gateway(status: number, body: unknown) {
  vi.stubGlobal(
    "fetch",
    vi.fn(
      async () =>
        new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } }),
    ),
  );
}

function answer(stops: unknown[]) {
  return {
    sessionId: SID,
    judgeCallTitle: "Call one - what happened",
    narrationCallTitle: "Call two - the words",
    stops,
  };
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the Wingman debug view", () => {
  it("shows both calls of a reading - each one's prompt and its raw answer", async () => {
    gateway(200, answer([stop()]));
    render(<WingmanDebugView sessionId={SID} />);

    const judge = await screen.findByRole("article", { name: "Call one - what happened" });
    expect(judge.textContent).toContain("You are the WINGMAN...");
    expect(judge.textContent).toContain('{"state":"finished-report"}');

    const narration = screen.getByRole("article", { name: "Call two - the words" });
    expect(narration.textContent).toContain("Output ONLY the spoken version...");
    expect(narration.textContent).toContain("The branch is pushed.");

    // And what was fed in, once, because both calls are given the same package.
    const fed = screen.getByRole("article", { name: "What both calls were fed" });
    expect(fed.textContent).toContain("I have pushed the branch.");
  });

  it("says a call was never made, rather than showing it as empty", async () => {
    const notMade = "The narration call was not made for this reading.";
    gateway(
      200,
      answer([
        stop({
          narrationPrompt: null,
          narrationRawReply: null,
          narrationSeconds: null,
          narrationNotMadeText: notMade,
          narration: null,
        }),
      ]),
    );
    render(<WingmanDebugView sessionId={SID} />);

    const narration = await screen.findByRole("article", { name: "Call two - the words" });
    expect(narration.textContent).toContain(notMade);
    expect(narration.textContent).not.toContain("The exact prompt");
    expect(narration.textContent).not.toContain("The raw answer");
  });

  it("keeps a call that was MADE and FAILED apart from one that was never made", async () => {
    const why = "the narration call answered with no words";
    gateway(
      200,
      answer([
        stop({
          narrationRawReply: "",
          narrationFailureDetail: why,
          narrationNotMadeText: null,
          narration: null,
        }),
      ]),
    );
    render(<WingmanDebugView sessionId={SID} />);

    const narration = await screen.findByRole("article", { name: "Call two - the words" });
    expect(narration.textContent).toContain(why);
    // It was asked, so its prompt is still shown - that is the whole difference from the case above.
    expect(narration.textContent).toContain("The exact prompt");
  });

  it("shows a refused reading's reason, and that it kept no narration", async () => {
    gateway(
      200,
      answer([
        stop({
          outcome: "refused",
          state: "",
          label: null,
          narration: null,
          failed: true,
          failureReason: "state: 'everything-is-fine' is not one of the seven words",
          narrationNotMadeText: "The narration call was not made for this reading.",
          narrationPrompt: null,
          narrationRawReply: null,
          narrationSeconds: null,
        }),
      ]),
    );
    render(<WingmanDebugView sessionId={SID} />);

    // It appears twice on purpose: on the row in the list, and as the refusal line on the reading itself.
    await waitFor(() => expect(screen.getAllByText(/everything-is-fine/).length).toBeGreaterThan(0));
    expect(screen.getByText("This reading has no narration.")).toBeTruthy();
    // The raw answer that failed is still handed over - that is the case the record exists for.
    const judge = screen.getByRole("article", { name: "Call one - what happened" });
    expect(judge.textContent).toContain("The raw answer");
  });

  it("shows the Gateway's own refusal, never an empty view, when the account may not read this", async () => {
    const refusal =
      "the Wingman's debug view shows every prompt and every model answer behind a reading, and is open to staff accounts only";
    gateway(403, { error: refusal });
    render(<WingmanDebugView sessionId={SID} />);

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain(refusal);
    expect(screen.queryByText(WINGMAN_DEBUG_EMPTY)).toBeNull();
  });

  it("says there is nothing to read when the account may read it and there is nothing", async () => {
    gateway(200, answer([]));
    render(<WingmanDebugView sessionId={SID} />);

    await waitFor(() => expect(screen.getByText(WINGMAN_DEBUG_EMPTY)).toBeTruthy());
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
