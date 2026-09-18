// A FAILED VOICE ACTION STAYS ON THE SCREEN.
//
// The owner's report was "half the time it doesn't work" - and one of the two things producing that was a
// button that visibly did nothing at all. Pressing "Switch to voice mode" on a session whose computer was
// unreachable sent POST /sessions/{sid}/voice-mode, got a 503, and set an error. The Voice screen polls every
// three seconds, and both of the poll's good paths called setError(null) unconditionally - so the message was
// wiped before it could be read. Captured live on 2026-09-17: the POST answered 503 and the screen's text was
// byte-for-byte identical before and after the click.
//
// The rule these pin: a POLL may only clear what a POLL set. An error a person's action produced survives
// until that person tries again.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render } from "@testing-library/react";

vi.mock("../api/client", () => ({
  GatewayError: class GatewayError extends Error {
    status: number;
    constructor(status: number, message: string) { super(message); this.status = status; }
  },
  getWaitingScreen: vi.fn(),
  sendVoicePrompt: vi.fn(),
  getWingmanVoice: vi.fn(async () => ({ ready: false, generatedAt: "", spoken: "", reply: "" })),
  fetchWingmanVoiceAudio: vi.fn(async () => new ArrayBuffer(0)),
  listSessions: vi.fn(async () => []),
  markVoiceAndExplain: vi.fn(async () => ({})),
  setVoiceMode: vi.fn(async () => {}),
  stopWingmanVoice: vi.fn(async () => {}),
}));

vi.mock("../dictation/backgroundSend", () => ({ backgroundTranscribeAndSend: vi.fn() }));

const api = await import("../api/client");
const { useVoiceMode } = await import("./useVoiceMode");

const listSessionsMock = api.listSessions as unknown as ReturnType<typeof vi.fn>;
const setVoiceModeMock = api.setVoiceMode as unknown as ReturnType<typeof vi.fn>;
const explainMock = api.markVoiceAndExplain as unknown as ReturnType<typeof vi.fn>;

const SID = "6f0b8f52-0000-4000-8000-000000000002";

type View = ReturnType<typeof useVoiceMode>;

function Probe({ onReady }: { onReady: (v: View) => void }) {
  const v = useVoiceMode(SID);
  onReady(v);
  return <span data-testid="err">{v.error ?? ""}</span>;
}

function mount(): { view: () => View } {
  let latest: View | null = null;
  render(<Probe onReady={(v) => { latest = v; }} />);
  return { view: () => latest as unknown as View };
}

/** The session as the roster reports it while voice is OFF - the poll branch that used to clear the error. */
const voiceOffRow = {
  sessionId: SID,
  name: "a worker",
  voiceMode: false,
  activityState: "WaitingForInput",
  // The Gateway folds the colour and the client refuses a row without it (ordering.requireGatewayField).
  effectiveColor: "red",
  statusColor: "red",
};

beforeEach(() => {
  const store = new Map<string, string>();
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
    clear: () => store.clear(),
  });
  listSessionsMock.mockReset();
  setVoiceModeMock.mockReset();
  explainMock.mockReset();
  listSessionsMock.mockResolvedValue([voiceOffRow]);
  explainMock.mockResolvedValue({});
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("a voice action that fails", () => {
  it("keeps its message on screen after the poll has run again", async () => {
    // REVERT PROOF: put an unconditional setError(null) back on the poll's !on branch and this goes red with
    // error === null. The poll is DRIVEN here rather than merely awaited - an earlier version of this test
    // used bare microtask flushes, the 3-second interval never fired, and it passed against the defect.
    vi.useFakeTimers();
    try {
      setVoiceModeMock.mockRejectedValue(new Error("Gateway returned 503"));

      const { view } = mount();
      await act(async () => { await vi.advanceTimersByTimeAsync(10); });
      await act(async () => { await view().onSwitchOn(); });
      expect(view().error).toBe("Gateway returned 503");

      // Two poll ticks: the session IS reported, still not in voice mode. That is the exact branch that used
      // to wipe the message within three seconds of the person pressing the button.
      await act(async () => { await vi.advanceTimersByTimeAsync(3100); });
      await act(async () => { await vi.advanceTimersByTimeAsync(3100); });
      expect(view().error).toBe("Gateway returned 503");
    } finally {
      vi.useRealTimers();
    }
  });

  it("is cleared when the person tries the action again", async () => {
    // The error is the person's to dismiss, and trying again is how they dismiss it. A message that outlived a
    // successful retry would be its own kind of lie.
    vi.useFakeTimers();
    try {
      setVoiceModeMock.mockRejectedValueOnce(new Error("Gateway returned 503"));

      const { view } = mount();
      await act(async () => { await vi.advanceTimersByTimeAsync(10); });
      await act(async () => { await view().onSwitchOn(); });
      expect(view().error).toBe("Gateway returned 503");

      setVoiceModeMock.mockResolvedValue(undefined);
      await act(async () => { await view().onSwitchOn(); });
      expect(view().error).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it("does not make the poll's own transient note sticky", async () => {
    // THE NEGATIVE CONTROL. Without it this change could be making EVERY error permanent, including the soft
    // "Reconnecting..." note the poll raises when a session is briefly missing from the roster - which must
    // still clear by itself on the next good poll.
    vi.useFakeTimers();
    try {
      listSessionsMock.mockResolvedValue([]);            // the session is momentarily absent
      const { view } = mount();
      await act(async () => { await vi.advanceTimersByTimeAsync(10); });
      expect(view().error).toMatch(/Reconnecting/);

      listSessionsMock.mockResolvedValue([voiceOffRow]); // it is back
      await act(async () => { await vi.advanceTimersByTimeAsync(3100); });
      expect(view().error).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });
});
