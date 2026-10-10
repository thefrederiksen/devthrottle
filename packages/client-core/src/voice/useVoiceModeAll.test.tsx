// The voice-mode switch hook (owner, 2026-07-24). Voice mode is the FLEET state - every session narrates
// its turns - and it is read from the Gateway, never derived from the roster. Auto-speak is a different
// thing, a setting of this phone, and turning voice mode off must take it down with it.
//
// Two behaviours here are worth a test because both fail SILENTLY and both read, to the person holding the
// phone, as "the app will not let me leave": a poll landing after a write and repainting the old value, and
// auto-speak surviving voice mode being turned off so the queue drags you back in.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { getAutoSpeak, setAutoSpeak } from "./queueTouch";
import { __resetVoiceModeAllForTests, useVoiceModeAll, writeVoiceModeAll } from "./useVoiceModeAll";
import { VoiceAutoOffNote } from "./VoiceAutoOffNote";

vi.mock("../api/client", async (importOriginal) => {
  // The error reporter needs the real error helpers; everything else stays faked.
  const real = await importOriginal<typeof import("../api/client")>();
  return {
    GatewayError: real.GatewayError,
    gatewayErrorMessage: real.gatewayErrorMessage,
    authHeaders: real.authHeaders,
    getVoiceModeAllState: vi.fn(),
    setVoiceModeAllSessions: vi.fn(),
  };
});

const api = await import("../api/client");
const getMock = api.getVoiceModeAllState as unknown as ReturnType<typeof vi.fn>;
const state = (enabled: boolean, note: string | null = null) => ({ enabled, note });
const setMock = api.setVoiceModeAllSessions as unknown as ReturnType<typeof vi.fn>;

function Probe({ onReady }: { onReady: (v: ReturnType<typeof useVoiceModeAll>) => void }) {
  const v = useVoiceModeAll();
  onReady(v);
  return <span data-testid="state">{v.enabled === null ? "unknown" : v.enabled ? "on" : "off"}</span>;
}

beforeEach(() => {
  const store = new Map<string, string>();
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
    clear: () => store.clear(),
  });
  getMock.mockReset();
  setMock.mockReset();
  // The state is deliberately module-level (one answer for the whole app), so it must be forgotten between
  // cases or one test's answer leaks into the next and they stop meaning anything.
  __resetVoiceModeAllForTests();
});

afterEach(() => {
  cleanup();
  __resetVoiceModeAllForTests();
  vi.unstubAllGlobals();
});

describe("useVoiceModeAll", () => {
  it("renders the Gateway's answer, and starts unknown rather than guessing", async () => {
    let resolve: (v: ReturnType<typeof state>) => void = () => {};
    getMock.mockReturnValue(new Promise<ReturnType<typeof state>>((r) => { resolve = r; }));

    render(<Probe onReady={() => {}} />);
    // Before the first read lands the state is UNKNOWN. A banner that guessed "off" here would flash on
    // and off on every app open, which teaches you to ignore it.
    expect(screen.getByTestId("state").textContent).toBe("unknown");

    await act(async () => { resolve(state(true)); });
    expect(screen.getByTestId("state").textContent).toBe("on");
  });

  it("turning voice mode OFF also switches auto-speak off on this phone", async () => {
    setAutoSpeak(true);
    getMock.mockResolvedValue(state(true));
    setMock.mockResolvedValue({ enabled: false, total: 0, changed: 0, skipped: 0, sessions: [] });

    let hook!: ReturnType<typeof useVoiceModeAll>;
    render(<Probe onReady={(v) => { hook = v; }} />);
    await act(async () => {});

    await act(async () => { await hook.set(false); });

    // Auto-speak with nothing to speak is not a state worth being in, and leaving it armed is how someone
    // gets dragged back into the queue they just left.
    expect(getAutoSpeak()).toBe(false);
    expect(screen.getByTestId("state").textContent).toBe("off");
  });

  it("turning voice mode ON leaves auto-speak alone - they are two different things", async () => {
    setAutoSpeak(false);
    getMock.mockResolvedValue(state(false));
    setMock.mockResolvedValue({ enabled: true, total: 0, changed: 0, skipped: 0, sessions: [] });

    let hook!: ReturnType<typeof useVoiceModeAll>;
    render(<Probe onReady={(v) => { hook = v; }} />);
    await act(async () => {});

    await act(async () => { await hook.set(true); });

    // Voice mode on with auto-speak off is the ORDINARY case: your sessions all speak, and you choose
    // which one to listen to. Turning voice mode on must never arm hands-free playback by itself.
    expect(getAutoSpeak()).toBe(false);
    expect(screen.getByTestId("state").textContent).toBe("on");
  });

  it("a poll that started before a write does not repaint the old value AFTER the write finished", async () => {
    // The failure this prevents: you tap "Turn off", and a poll that was already in the air lands a moment
    // later carrying the pre-write answer and paints "voice mode is on" again. On screen that is
    // indistinguishable from the app refusing to let you leave.
    //
    // The stale read must land AFTER the write completes - that is the hard case, and the one an in-flight
    // flag alone does not catch, because by then the write is no longer in flight.
    vi.useFakeTimers();
    try {
      let landStalePoll: (v: ReturnType<typeof state>) => void = () => {};
      getMock
        .mockReturnValueOnce(Promise.resolve(state(true)))
        .mockReturnValueOnce(new Promise<ReturnType<typeof state>>((r) => { landStalePoll = r; }));
      setMock.mockResolvedValue({ enabled: false, total: 0, changed: 0, skipped: 0, sessions: [] });

      let hook!: ReturnType<typeof useVoiceModeAll>;
      render(<Probe onReady={(v) => { hook = v; }} />);
      await act(async () => {});
      expect(screen.getByTestId("state").textContent).toBe("on");

      // The 15-second poll fires and its request goes out, still unanswered.
      await act(async () => { await vi.advanceTimersByTimeAsync(15000); });

      // The write happens and completes while that read is still in the air.
      await act(async () => { await hook.set(false); });
      expect(screen.getByTestId("state").textContent).toBe("off");

      // NOW the old read lands, carrying the pre-write answer.
      await act(async () => { landStalePoll(state(true)); });

      expect(screen.getByTestId("state").textContent).toBe("off");
    } finally {
      vi.useRealTimers();
    }
  });

  it("two readers share ONE answer - turning it off in one place updates the other at once", async () => {
    // The banner lives in the app shell and the switch lives on the roster, so both are on screen together.
    // If each held its own copy with its own poll they would disagree for up to fifteen seconds: you tap
    // "Turn off" on the banner and the roster goes on saying voice mode is on. Two copies of one truth is
    // the same mistake as deriving the answer from the roster, just later in the stack.
    getMock.mockResolvedValue(state(true));
    setMock.mockResolvedValue({ enabled: false, total: 0, changed: 0, skipped: 0, sessions: [] });

    let banner!: ReturnType<typeof useVoiceModeAll>;
    render(
      <>
        <Probe onReady={(v) => { banner = v; }} />
        <Probe onReady={() => {}} />
      </>,
    );
    await act(async () => {});
    for (const el of screen.getAllByTestId("state")) expect(el.textContent).toBe("on");

    // One of them turns voice mode off; the OTHER must show it immediately, with no poll in between.
    await act(async () => { await banner.set(false); });
    for (const el of screen.getAllByTestId("state")) expect(el.textContent).toBe("off");

    // And one shared poll serves both readers, not one poll each.
    expect(getMock).toHaveBeenCalledTimes(1);
  });

  it("a failed write says so and does not claim the state changed", async () => {
    getMock.mockResolvedValue(state(true));
    setMock.mockRejectedValue(new Error("gateway unreachable"));

    let hook!: ReturnType<typeof useVoiceModeAll>;
    render(<Probe onReady={(v) => { hook = v; }} />);
    await act(async () => {});

    let ok = true;
    await act(async () => { ok = await hook.set(false); });

    // Silence here is the dangerous outcome: the person believes they left voice mode when they did not.
    expect(ok).toBe(false);
    expect(hook.error).not.toBeNull();
    expect(screen.getByTestId("state").textContent).toBe("on");
  });

  // Voice mode auto-off (owner ruling 2026-09-28): the Gateway writes the quiet line; the client renders it verbatim.
  it("renders the Gateway's quiet line word for word beside the switch, and nothing when there is none", async () => {
    const line = "Voice mode switched off at 14:32 - you answered five sessions without listening";
    getMock.mockResolvedValue(state(false, line));

    render(<VoiceAutoOffNote className="note" />);
    await act(async () => {});

    expect(screen.getByRole("status").textContent).toBe(line);
  });

  it("renders nothing when the Gateway has no line", async () => {
    getMock.mockResolvedValue(state(false, null));

    render(<VoiceAutoOffNote className="note" />);
    await act(async () => {});

    expect(screen.queryByRole("status")).toBeNull();
  });

  it("switching voice mode yourself retires the line at once", async () => {
    getMock.mockResolvedValue(state(false, "Voice mode switched off at 14:32 - you answered five sessions without listening"));
    setMock.mockResolvedValue({ enabled: true, total: 1, changed: 1, skipped: 0, sessions: [] });

    let hook!: ReturnType<typeof useVoiceModeAll>;
    render(<Probe onReady={(v) => { hook = v; }} />);
    await act(async () => {});
    expect(hook.note).not.toBeNull();

    await act(async () => { await hook.set(true); });

    expect(hook.note).toBeNull();
  });

  it("a write made OUTSIDE the hook - the Cockpit's roster button - also stops a stale poll bringing the line back", async () => {
    // The Cockpit throws the switch through writeVoiceModeAll, not the hook's set, because it reports what changed.
    // A poll already in the air when voice goes back on must not land afterwards and repaint the old line.
    vi.useFakeTimers();
    try {
      const line = "Voice mode switched off at 14:32 - you answered five sessions without listening";
      let landStalePoll: (v: ReturnType<typeof state>) => void = () => {};
      getMock
        .mockReturnValueOnce(Promise.resolve(state(false, line)))
        .mockReturnValueOnce(new Promise<ReturnType<typeof state>>((r) => { landStalePoll = r; }));
      setMock.mockResolvedValue({ enabled: true, total: 1, changed: 1, skipped: 0, sessions: [] });

      render(<VoiceAutoOffNote className="note" />);
      await act(async () => {});
      expect(screen.getByRole("status").textContent).toBe(line);

      await act(async () => { await vi.advanceTimersByTimeAsync(15000); });   // the poll goes out, unanswered
      await act(async () => { await writeVoiceModeAll(true); });                // voice back on, from the Cockpit
      expect(screen.queryByRole("status")).toBeNull();

      await act(async () => { landStalePoll(state(false, line)); });           // the old answer lands late

      expect(screen.queryByRole("status")).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });
});
