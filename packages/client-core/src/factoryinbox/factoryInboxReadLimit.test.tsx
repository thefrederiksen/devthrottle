import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { FACTORY_INBOX_READ_TIMEOUT_MS } from "./factoryInboxClient";
import { oddList, oddThread } from "./factoryInboxFixture.testkit";

// A READ THAT NEVER ENDS (the review of pull request 4703, finding 1). The list and thread screens read one at a time, so
// a read that hangs half-open on a flaky phone network - fetch neither answers nor fails - would hold them for ever. The
// real client and the real transport are used here; only fetch is stood in for. A hung read must end at the read's time
// limit with the transport's timeout error, the screen must say so, and the next read must be made.

vi.mock("../yourinbox/yourInboxClient", () => ({
  getYourInboxCount: vi.fn(async () => ({ unread: 0, label: "" })),
}));
vi.mock("./factoryInboxVoice", () => ({
  listFactoryRecordings: vi.fn(async () => []),
  saveFactoryRecording: vi.fn(async () => {}),
  deleteFactoryRecording: vi.fn(async () => {}),
  discardFactoryRecording: vi.fn(async () => {}),
  sendFactoryRecording: vi.fn(),
}));

import { FactoryInboxListView } from "./FactoryInboxListView";
import { FactoryInboxThreadView } from "./FactoryInboxThreadView";

type Answer = "hang" | unknown;

/** fetch for one route: each call takes the next answer; "hang" never settles unless its signal is aborted. */
function stubFetch(answers: Answer[]): { calls: () => number } {
  let n = 0;
  vi.stubGlobal(
    "fetch",
    vi.fn((_input: unknown, init?: RequestInit) => {
      const answer = answers[Math.min(n, answers.length - 1)];
      n += 1;
      if (answer !== "hang") {
        return Promise.resolve(new Response(JSON.stringify(answer), { status: 200, headers: { "Content-Type": "application/json" } }));
      }
      return new Promise<Response>((_resolve, reject) => {
        init?.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")), { once: true });
      });
    }),
  );
  return { calls: () => n };
}

// A timed-out read ends through several promise steps (the transport, the client, the screen, the loop).
const settle = async () => {
  for (let i = 0; i < 5; i++) await vi.advanceTimersByTimeAsync(0);
};
// The screen's sentence for a read whose time limit ran out.
const TIMED_OUT = "the Gateway took too long to answer";

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: false });
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("a hung read ends at the read's time limit and the loop goes on", () => {
  it("FactoryInboxReadTimeout_IsWellAboveASlowButHonestRead", () => {
    // The proof's thread reads took 70 to 90 seconds on the live Gateway; a limit below that brings the pile-up back.
    expect(FACTORY_INBOX_READ_TIMEOUT_MS).toBeGreaterThanOrEqual(120_000);
  });

  it("FactoryInboxThreadView_HungRead_TimesOutShowsItAndReadsAgain", async () => {
    const gateway = stubFetch(["hang", oddThread()]);
    render(<FactoryInboxThreadView factory="zeta-works" surface="mobile" />);
    await settle();
    expect(gateway.calls()).toBe(1);

    await vi.advanceTimersByTimeAsync(FACTORY_INBOX_READ_TIMEOUT_MS - 1);
    expect(gateway.calls()).toBe(1);
    await vi.advanceTimersByTimeAsync(1);
    await settle();
    expect(screen.getByRole("alert").textContent).toContain(TIMED_OUT);

    await vi.advanceTimersByTimeAsync(15_000);
    expect(gateway.calls()).toBe(2);
    await settle();
    expect(screen.getAllByTestId("fi-message")).toHaveLength(2);
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("FactoryInboxListView_HungRead_TimesOutShowsItAndReadsAgain", async () => {
    const gateway = stubFetch(["hang", oddList()]);
    render(<FactoryInboxListView surface="mobile" onOpen={() => {}} />);
    await settle();
    expect(gateway.calls()).toBe(1);

    await vi.advanceTimersByTimeAsync(FACTORY_INBOX_READ_TIMEOUT_MS);
    await settle();
    expect(screen.getByRole("alert").textContent).toContain(TIMED_OUT);

    await vi.advanceTimersByTimeAsync(15_000);
    expect(gateway.calls()).toBe(2);
    await settle();
    expect(screen.getAllByTestId("fi-row")).toHaveLength(2);
  });
});
