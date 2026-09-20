// The "Wingman error" block, rendered against a fake Gateway (mission "Wingman error and retry", 2026-09-19).
//
// What these prove that a Gateway test cannot: the words the Gateway stamped reach the card verbatim, the
// countdown is the Gateway's absolute time and nothing else, a card with nothing booked never says an attempt is
// coming, and a press sends exactly one request and shows the Gateway's answer.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { SessionDto } from "../api/client";
import { WingmanErrorLine } from "./WingmanErrorLine";
import { waitWords, wingmanErrorOf, wingmanRetryLine, type WingmanErrorDisplay } from "./wingmanError";

const SID = "5b0c2e7a-0000-4000-8000-000000000009";
const NOW = Date.parse("2026-09-19T09:00:00Z");

function booked(overrides: Partial<WingmanErrorDisplay> = {}): WingmanErrorDisplay {
  return {
    tag: "Wingman error",
    reason: "The model did not answer in time.",
    nextRetryNumber: 2,
    retriesTotal: 8,
    retriesRemaining: 7,
    nextRetryAtUtc: "2026-09-19T09:00:40Z",
    exhausted: false,
    retryLabel: "retry 2 of 8",
    exhaustedText: "",
    askAgainLabel: "Ask again",
    ...overrides,
  };
}

const USED_UP = booked({
  nextRetryNumber: 0,
  retriesRemaining: 0,
  nextRetryAtUtc: null,
  exhausted: true,
  retryLabel: "",
  exhaustedText: "The Wingman could not read this stop. Nothing more is scheduled.",
});

function session(wingmanError: WingmanErrorDisplay | null, extra: Record<string, unknown> = {}): SessionDto {
  return { sessionId: SID, wingmanError, ...extra } as unknown as SessionDto;
}

let calls: { url: string; method: string }[] = [];

function fakeGateway(status: number, body: Record<string, unknown>) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: init?.method ?? "GET" });
      return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    }),
  );
}

beforeEach(() => {
  calls = [];
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(NOW);
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("the retry line", () => {
  it("counts down to the Gateway's booked time", () => {
    expect(wingmanRetryLine(booked(), NOW)).toBe("retry 2 of 8 in 40 seconds");
    expect(wingmanRetryLine(booked(), NOW + 39_000)).toBe("retry 2 of 8 in 1 second");
    expect(wingmanRetryLine(booked({ retryLabel: "retry 7 of 8", nextRetryAtUtc: "2026-09-19T09:30:00Z" }), NOW)).toBe(
      "retry 7 of 8 in 30 minutes",
    );
  });

  it("says the retry is due, not that it is in zero seconds, once the booked time has passed", () => {
    expect(wingmanRetryLine(booked(), NOW + 41_000)).toBe("retry 2 of 8 is due now");
  });

  it("says the Gateway's own sentence when nothing is booked, and never that an attempt is coming", () => {
    const line = wingmanRetryLine(USED_UP, NOW);
    expect(line).toBe("The Wingman could not read this stop. Nothing more is scheduled.");
    expect(line).not.toMatch(/retry|in \d/);
    // A display that lost its time but not its flag is still "nothing booked": no time, no promise.
    expect(wingmanRetryLine(booked({ nextRetryAtUtc: null, exhaustedText: "Nothing more is scheduled." }), NOW)).toBe(
      "Nothing more is scheduled.",
    );
  });

  it("rounds a wait up to whole units", () => {
    expect(waitWords(500)).toBe("1 second");
    expect(waitWords(89_000)).toBe("89 seconds");
    expect(waitWords(90_000)).toBe("2 minutes");
    expect(waitWords(5 * 60_000)).toBe("5 minutes");
  });
});

describe("the Wingman error on a card", () => {
  it("renders nothing when the Gateway stamped no error", () => {
    expect(wingmanErrorOf(session(null))).toBeNull();
    const { container } = render(<WingmanErrorLine session={session(null)} />);
    expect(container.innerHTML).toBe("");
  });

  it("shows the tag, the reason and the countdown, whether or not the session is in voice mode", () => {
    for (const voiceMode of [false, true]) {
      render(<WingmanErrorLine session={session(booked(), { voiceMode })} />);
      expect(screen.getByText("Wingman error")).toBeTruthy();
      expect(screen.getByText("The model did not answer in time.")).toBeTruthy();
      expect(screen.getByText("retry 2 of 8 in 40 seconds")).toBeTruthy();
      expect(screen.getByRole("button", { name: "Ask again" })).toBeTruthy();
      cleanup();
    }
  });

  it("offers the button on a used-up schedule too, beside the sentence that nothing more is scheduled", () => {
    render(<WingmanErrorLine session={session(USED_UP)} />);
    expect(screen.getByText("The Wingman could not read this stop. Nothing more is scheduled.")).toBeTruthy();
    expect(screen.queryByText(/retry \d of 8/)).toBeNull();
    expect(screen.getByRole("button", { name: "Ask again" })).toBeTruthy();
  });

  it("sends one request for one press, and shows the Gateway's answer", async () => {
    fakeGateway(200, { failed: true, message: "The Wingman could not read this stop just now." });
    render(<WingmanErrorLine session={session(booked())} />);

    fireEvent.click(screen.getByRole("button", { name: "Ask again" }));
    fireEvent.click(screen.getByRole("button", { name: "Asking..." }));   // a second press while asking does nothing

    await waitFor(() => expect(screen.getByText("The Wingman could not read this stop just now.")).toBeTruthy());
    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe("POST");
    expect(calls[0].url).toContain(`/sessions/${SID}/wingman/ask-again`);
    // The press changed nothing on the card's schedule: the line is still the Gateway's booked retry.
    expect(screen.getByText("retry 2 of 8 in 40 seconds")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Ask again" })).toBeTruthy();
  });

  it("says so when the press itself could not reach the Gateway", async () => {
    fakeGateway(502, { error: "the Wingman could not be asked again" });
    render(<WingmanErrorLine session={session(booked())} />);
    fireEvent.click(screen.getByRole("button", { name: "Ask again" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Ask again" })).toBeTruthy());
    expect(document.querySelector(".wingman-error-answer")?.textContent ?? "").not.toBe("");
  });
});
