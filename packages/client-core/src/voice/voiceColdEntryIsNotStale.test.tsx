// @vitest-environment jsdom
// OPENING A SESSION MUST NOT HAND YOU LAST TURN'S VOICE.
//
// The Voice screen seeds itself from this phone's own cache so it can paint instantly (issue #1015): `voice` comes
// from getVoiceMeta(sid) and the clip is warmed from Cache Storage. On a cold entry both sides therefore agree about
// a stamp that may be minutes old, and nothing can contradict them yet - agentWorking is
// `session !== null && isWorking(session)`, and a session that has not loaded is treated as not-working.
//
// So the player appeared, holding the PREVIOUS turn's audio, for a session that had since gone blue. Auto-play was
// already guarded (it waits for pollDone and a resolved session); the manual play control was not, and a tap in that
// window played the stale clip. That is one of the ways the owner heard an old voice.
//
// "I do not know yet" is not "there is nothing to play". It is a reason to offer nothing until the first poll answers.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render } from "@testing-library/react";

vi.mock("../api/client", () => ({
  GatewayError: class GatewayError extends Error {
    status: number;
    constructor(status: number, message: string) { super(message); this.status = status; }
  },
  getWaitingScreen: vi.fn(),
  sendVoicePrompt: vi.fn(),
  getWingmanVoice: vi.fn(),
  fetchWingmanVoiceAudio: vi.fn(async () => new ArrayBuffer(0)),
  listSessions: vi.fn(async () => []),
  markVoiceAndExplain: vi.fn(async () => ({})),
  setVoiceMode: vi.fn(async () => {}),
  stopWingmanVoice: vi.fn(async () => {}),
}));
vi.mock("../dictation/backgroundSend", () => ({ backgroundTranscribeAndSend: vi.fn() }));

// The phone's own caches, pre-loaded exactly as a previous visit would have left them: last turn's narration
// metadata, and last turn's clip bytes ready to play under the SAME stamp.
const STALE_STAMP = "2026-09-18T08:00:00Z";
vi.mock("./clips", () => ({
  ensureClip: vi.fn(),
  getClipState: () => ({ generatedAt: STALE_STAMP, phase: "ready", url: "blob:stale" }),
  getVoiceMeta: () => ({ ready: true, generatedAt: STALE_STAMP, spoken: "last turn's narration", reply: "x" }),
  saveVoiceMeta: vi.fn(),
  stopPlayback: vi.fn(),
  useVoiceClips: () => undefined,
}));

const api = await import("../api/client");
const { useVoiceMode } = await import("./useVoiceMode");

const listSessionsMock = api.listSessions as unknown as ReturnType<typeof vi.fn>;
const getVoiceMock = api.getWingmanVoice as unknown as ReturnType<typeof vi.fn>;

const SID = "6f0b8f52-0000-4000-8000-000000000003";
type View = ReturnType<typeof useVoiceMode>;

function mount(): { view: () => View } {
  let latest: View | null = null;
  function Probe() {
    latest = useVoiceMode(SID, { seededVoiceOn: true });
    return null;
  }
  render(<Probe />);
  return { view: () => latest as unknown as View };
}

const workingRow = {
  sessionId: SID,
  name: "an architect",
  voiceMode: true,
  activityState: "Working",
  effectiveColor: "blue",
  statusColor: "blue",
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
  getVoiceMock.mockReset();
  getVoiceMock.mockResolvedValue({ ready: false, generatedAt: "", spoken: "", reply: "" });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("entering a session whose agent has started working again", () => {
  it("offers no player before the first poll has answered", async () => {
    // REVERT PROOF: drop `pollDone && session !== null` from `speaking` and this goes red - the screen offers the
    // player on the very first paint, holding a clip made from a turn that is already over.
    vi.useFakeTimers();
    try {
      let resolvePoll: ((v: unknown) => void) | null = null;
      listSessionsMock.mockImplementation(() => new Promise((r) => { resolvePoll = r; }));

      const { view } = mount();
      // The first poll is in flight. The phone holds a matching stamp and playable bytes, and the roster seeded
      // voice-on - every input the old rule needed to draw a player.
      expect(view().speaking).toBe(false);

      // The poll answers: the session is blue.
      await act(async () => {
        resolvePoll?.([workingRow]);
        await vi.advanceTimersByTimeAsync(10);
      });
      expect(view().speaking).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it("still offers the player once the poll confirms a parked session holding this turn's clip", async () => {
    // THE NEGATIVE CONTROL. Without it the change above could be suppressing the player permanently and the first
    // test would still pass. A resolved, not-working session whose Gateway stamp matches the phone's bytes plays.
    vi.useFakeTimers();
    try {
      listSessionsMock.mockResolvedValue([{ ...workingRow, activityState: "WaitingForInput", effectiveColor: "red", statusColor: "red" }]);
      getVoiceMock.mockResolvedValue({ ready: true, generatedAt: STALE_STAMP, spoken: "this turn's narration", reply: "x" });

      const { view } = mount();
      await act(async () => { await vi.advanceTimersByTimeAsync(20); });

      expect(view().pollDone).toBe(true);
      expect(view().speaking).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });
});
