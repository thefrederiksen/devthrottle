// The Wingman tab, rendered against a fake Gateway (the Wingman inspector, phase 3).
//
// What these prove: every state is visible and never blank (loading, the Gateway's refusal sentence, the empty
// sentence); the chips carry the Gateway's labels and counts and filter by the group key the Gateway stamped; and a
// selected stop shows the fold's strings verbatim in the summary strip and the four blocks - so a view that composed
// its own words, or dropped one, fails here.
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { formatLocalInstant, WINGMAN_TAB_EMPTY, WingmanStopsView } from "./WingmanTab";
import type { WingmanStop, WingmanStopsResponse } from "./wingmanStops";

const SID = "5b0c2e7a-0000-4000-8000-000000000020";

function stop(overrides: Partial<WingmanStop> = {}): WingmanStop {
  return {
    traceId: "t-judged",
    observedAtUtc: "2026-09-16T14:38:12Z",
    recordedAtUtc: "2026-09-16T14:38:16Z",
    trigger: "turn-end",
    triggerText: "The turn ended",
    outcome: "judged",
    outcomeText: "Judged",
    group: "needs-you",
    rowRecorded: true,
    rowColour: "red",
    rowColourHex: "#F14C4C",
    rowLabel: "Apply the migration now?",
    verdictWord: "needed-you",
    confidence: "high",
    verdictText: "needed-you, high confidence",
    strip: { label: "Apply the migration now?", replyText: "Answered in 3.8 seconds", outcomeText: "Judged" },
    saw: {
      kept: true,
      notKeptText: null,
      screenRows: ["  Migration written.", "Apply the migration to the local database now?"],
      sourceHeading: "Latest reply",
      sourceText: "The migration is written. Apply it now?",
      recentTurns: "user: add the column\nassistant: written",
      facts: [{ name: "Agent", value: "Claude Code" }],
    },
    asked: { prompt: "You judge ONE stop of a coding agent's session.", cut: false, cutText: null, notAskedText: null },
    answered: {
      rawReply: '{ "verdict": "needed-you", "confidence": "high" }',
      cut: false,
      cutText: null,
      replySeconds: 3.8,
      noAnswerText: null,
    },
    did: {
      decision: "Accepted",
      accepted: true,
      reason: null,
      verdictLabel: "Apply the migration now?",
      verdictSummary: "The session is asking before it changes the database.",
      causeText: null,
      replacedText: null,
      replacedTraceId: null,
      clock: null,
    },
    ...overrides,
  };
}

const REFUSED = stop({
  traceId: "t-refused",
  observedAtUtc: "2026-09-16T14:31:02Z",
  recordedAtUtc: "2026-09-16T14:31:07Z",
  outcome: "refused",
  outcomeText: "Refused",
  group: "failed",
  rowColour: "red",
  rowLabel: "Waiting for you",
  verdictWord: null,
  confidence: null,
  verdictText: "No verdict",
  strip: { label: "Refused", replyText: "Answered in 4.1 seconds", outcomeText: "Refused" },
  answered: {
    rawReply: '{ "verdict": "continues-alone", "evidence": "I will deploy the Gateway before tagging" }',
    cut: true,
    cutText: "Cut at 64,000 characters; the rest was not kept.",
    replySeconds: 4.1,
    noAnswerText: null,
  },
  did: {
    decision: "Refused",
    accepted: false,
    reason: "the evidence \"I will deploy the Gateway before tagging\" is not on the screen or in the reply",
    verdictLabel: null,
    verdictSummary: null,
    causeText: null,
    replacedText: null,
    replacedTraceId: null,
    clock: null,
  },
});

const CARRYING_ON = stop({
  traceId: "t-carrying",
  observedAtUtc: "2026-09-16T15:19:40Z",
  outcome: "judged",
  outcomeText: "Judged",
  group: "calm",
  rowColour: "purple",
  rowColourHex: "#A855F7",
  rowLabel: "Monitor and push tag when green",
  verdictText: "continues-alone, high confidence",
  strip: { label: "Monitor and push tag when green", replyText: "Answered in 2.2 seconds", outcomeText: "Judged" },
  did: {
    decision: "Accepted",
    accepted: true,
    reason: null,
    verdictLabel: "Monitor and push tag when green",
    verdictSummary: "Watching a test run.",
    causeText: null,
    replacedText: null,
    replacedTraceId: null,
    clock: {
      setToRunOutAtUtc: "2026-09-16T15:29:40Z",
      ranOutAtUtc: "2026-09-16T15:31:00Z",
      text: "The clock was set to run out, and moves later while the sessions it waits on keep working. It ran out.",
      ranOutTraceId: "t-expired",
    },
  },
});

const SKIPPED = stop({
  traceId: "t-expired",
  observedAtUtc: "2026-09-16T15:31:00Z",
  outcome: "skipped",
  outcomeText: "Skipped",
  group: "not-asked",
  rowRecorded: false,
  rowColour: null,
  rowColourHex: null,
  rowLabel: "Not recorded",
  verdictWord: null,
  confidence: null,
  verdictText: "No verdict",
  strip: { label: "Skipped", replyText: "Not asked", outcomeText: "Skipped" },
  saw: {
    kept: false,
    notKeptText: "Not kept - over the size ceiling",
    screenRows: [],
    sourceHeading: null,
    sourceText: null,
    recentTurns: null,
    facts: [],
  },
  asked: { prompt: null, cut: false, cutText: null, notAskedText: "The judge was not asked." },
  answered: { rawReply: null, cut: false, cutText: null, replySeconds: null, noAnswerText: "Nobody was asked, so nothing answered." },
  did: {
    decision: "Not asked",
    accepted: false,
    reason: null,
    verdictLabel: null,
    verdictSummary: null,
    causeText: "The session was held, so nothing asked the judge.",
    replacedText: "Replaced the carrying-on verdict from 15:19.",
    replacedTraceId: "t-carrying",
    clock: null,
  },
});

function answer(stops: WingmanStop[]): WingmanStopsResponse {
  return {
    sessionId: SID,
    stops,
    groups: [
      { key: "all", label: "All", count: stops.length },
      { key: "needs-you", label: "Needs you", count: stops.filter((s) => s.group === "needs-you").length },
      { key: "calm", label: "Calm", count: stops.filter((s) => s.group === "calm").length },
      { key: "failed", label: "Failed", count: stops.filter((s) => s.group === "failed").length },
      { key: "not-asked", label: "Not asked", count: stops.filter((s) => s.group === "not-asked").length },
    ],
  };
}

let urls: string[] = [];

function fakeGateway(status: number, body: unknown) {
  urls = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string) => {
      urls.push(url);
      return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    }),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the Wingman tab - the version 1 stops list, now its History view", () => {
  it("shows a loading line while the read is in flight, never a blank tab", async () => {
    let release: (r: Response) => void = () => {};
    vi.stubGlobal("fetch", vi.fn(() => new Promise<Response>((resolve) => (release = resolve))));

    render(<WingmanStopsView sessionId={SID} />);

    expect(screen.getByRole("status").textContent).toBe("Loading the Wingman's stops...");
    release(new Response(JSON.stringify(answer([])), { status: 200 }));
    await waitFor(() => expect(screen.getByRole("status").textContent).toBe(WINGMAN_TAB_EMPTY));
  });

  it("shows the empty sentence when the Wingman has judged nothing", async () => {
    fakeGateway(200, answer([]));
    render(<WingmanStopsView sessionId={SID} />);
    await waitFor(() => expect(screen.getByRole("status").textContent).toBe(WINGMAN_TAB_EMPTY));
    expect(urls).toEqual([`/sessions/${SID}/wingman-stops`]);
  });

  it("shows the Gateway's refusal sentence verbatim, not an empty tab", async () => {
    const sentence = "the Wingman's stops are not available on this gateway";
    fakeGateway(404, { error: sentence });
    render(<WingmanStopsView sessionId={SID} />);
    await waitFor(() => expect(screen.getByRole("alert").textContent).toBe(sentence));
    expect(screen.queryByText(WINGMAN_TAB_EMPTY)).toBeNull();
  });

  it("lists every stop with the chips' labels and counts from the Gateway, and opens the newest", async () => {
    fakeGateway(200, answer([CARRYING_ON, stop(), REFUSED, SKIPPED]));
    render(<WingmanStopsView sessionId={SID} />);

    const list = await screen.findByRole("list", { name: "Stops" });
    expect(within(list).getAllByRole("button")).toHaveLength(4);
    const chips = within(screen.getByRole("toolbar")).getAllByRole("button").map((b) => b.textContent);
    expect(chips).toEqual(["All4", "Needs you1", "Calm1", "Failed1", "Not asked1"]);
    expect(within(list).getByText("needed-you, high confidence")).toBeTruthy();
    expect(within(list).getByText(formatLocalInstant(REFUSED.observedAtUtc))).toBeTruthy();

    const detail = screen.getByRole("region", { name: "The selected stop" });
    expect(within(detail).getByText("Monitor and push tag when green", { selector: ".wingman-strip-label" })).toBeTruthy();
  });

  it("filters by the group the Gateway stamped on each stop", async () => {
    fakeGateway(200, answer([CARRYING_ON, stop(), REFUSED, SKIPPED]));
    render(<WingmanStopsView sessionId={SID} />);
    await screen.findByRole("list", { name: "Stops" });

    const failedChip = within(screen.getByRole("toolbar"))
      .getAllByRole("button")
      .find((b) => b.textContent === "Failed1");
    fireEvent.click(failedChip!);

    const items = within(screen.getByRole("list", { name: "Stops" })).getAllByRole("button");
    expect(items).toHaveLength(1);
    expect(items[0].textContent).toContain("Refused");
    // The selection follows the filter, so the detail never shows a stop the list has hidden.
    const detail = screen.getByRole("region", { name: "The selected stop" });
    expect(within(detail).getByText(REFUSED.did.reason!)).toBeTruthy();
  });

  it("shows a judged stop's strip and four blocks verbatim, with times in local time", async () => {
    const judged = stop();
    fakeGateway(200, answer([judged]));
    render(<WingmanStopsView sessionId={SID} />);
    const detail = await screen.findByRole("region", { name: "The selected stop" });

    expect(within(detail).getByText(judged.strip.label, { selector: ".wingman-strip-label" })).toBeTruthy();
    expect(within(detail).getAllByText(judged.strip.replyText).length).toBeGreaterThan(0);
    expect(within(detail).getByText("red - Apply the migration now?")).toBeTruthy();
    expect(within(detail).getByText(formatLocalInstant(judged.observedAtUtc))).toBeTruthy();
    expect(within(detail).getByText(formatLocalInstant(judged.recordedAtUtc))).toBeTruthy();

    const saw = within(detail).getByRole("article", { name: "What it saw" });
    expect(saw.querySelector(".wingman-screen")?.textContent).toBe(judged.saw.screenRows.join("\n"));
    expect(within(saw).getByText("Latest reply")).toBeTruthy();
    expect(within(saw).getByText(judged.saw.sourceText!)).toBeTruthy();
    expect(within(saw).getByText("Claude Code")).toBeTruthy();

    const asked = within(detail).getByRole("article", { name: "What it was asked" });
    expect(asked.querySelector("pre")?.textContent).toBe(judged.asked.prompt);

    const answered = within(detail).getByRole("article", { name: "What it answered" });
    expect(answered.querySelector("pre")?.textContent).toBe(judged.answered.rawReply);

    const did = within(detail).getByRole("article", { name: "What the product did" });
    expect(within(did).getByText("Accepted")).toBeTruthy();
    expect(within(did).getByText(judged.did.verdictSummary!)).toBeTruthy();
  });

  it("shows a refused stop's reason beside the raw reply that failed it, and says the reply was cut", async () => {
    fakeGateway(200, answer([REFUSED]));
    render(<WingmanStopsView sessionId={SID} />);
    const detail = await screen.findByRole("region", { name: "The selected stop" });

    const answered = within(detail).getByRole("article", { name: "What it answered" });
    expect(answered.querySelector("pre")?.textContent).toBe(REFUSED.answered.rawReply);
    expect(within(answered).getByText(REFUSED.answered.cutText!)).toBeTruthy();

    const did = within(detail).getByRole("article", { name: "What the product did" });
    expect(within(did).getByText("Refused")).toBeTruthy();
    expect(did.querySelector(".wingman-reason")?.textContent).toBe(REFUSED.did.reason);
  });

  it("shows why nothing was kept, asked or answered for a stop that asked nothing, and follows what it replaced", async () => {
    fakeGateway(200, answer([SKIPPED, CARRYING_ON]));
    render(<WingmanStopsView sessionId={SID} />);
    const detail = await screen.findByRole("region", { name: "The selected stop" });

    expect(within(detail).getByText("Not kept - over the size ceiling")).toBeTruthy();
    expect(within(detail).getByText("The judge was not asked.")).toBeTruthy();
    expect(within(detail).getByText("Nobody was asked, so nothing answered.")).toBeTruthy();
    expect(within(detail).getByText(SKIPPED.did.causeText!)).toBeTruthy();
    expect(within(detail).getByText("Not recorded")).toBeTruthy();

    fireEvent.click(within(detail).getByRole("button", { name: "Show that stop" }));

    const next = screen.getByRole("region", { name: "The selected stop" });
    const did = within(next).getByRole("article", { name: "What the product did" });
    expect(within(did).getByText(CARRYING_ON.did.clock!.text)).toBeTruthy();
    expect(within(did).getByText(formatLocalInstant(CARRYING_ON.did.clock!.setToRunOutAtUtc!))).toBeTruthy();
    expect(within(did).getByText(formatLocalInstant(CARRYING_ON.did.clock!.ranOutAtUtc!))).toBeTruthy();

    fireEvent.click(within(did).getByRole("button", { name: "Show the stop that ended it" }));
    expect(within(screen.getByRole("region", { name: "The selected stop" })).getByText(SKIPPED.did.causeText!)).toBeTruthy();
  });

  it("formats a UTC instant into the reader's local time", () => {
    const utc = "2026-09-16T14:38:12Z";
    expect(formatLocalInstant(utc)).toBe(
      new Date(Date.UTC(2026, 8, 16, 14, 38, 12)).toLocaleString(undefined, {
        year: "numeric",
        month: "short",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
      }),
    );
  });
});
