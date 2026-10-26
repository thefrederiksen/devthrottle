// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, act } from "@testing-library/react";

// The live capture alarm, at the JOIN between the recorder's liveness clocks and the dialog that has
// to say something about them. The clocks themselves are unit-tested in recorder.test.ts; what is
// pinned HERE is the wiring, because the wiring is what was missing for the whole life of this
// defect: the recorder could already read a flat-zero meter and the dialog drew it as nine short bars
// and said nothing, so a dead meter and a quiet user were the same picture, and a recording that had
// stopped hearing anything looked exactly like a recording that was going fine.
//
// The alarm must:
//   - stay silent while capture and the meter are both alive,
//   - say the recording has STALLED when no audio has been delivered (words are being lost NOW),
//   - say we are hearing NOTHING when the meter has been pinned at zero (dead meter, or a muted mic),
//   - prefer the stall message over the silence message, since a stalled recorder usually goes silent
//     too and the losing-your-words one is the message that matters,
//   - and CLEAR itself the moment the microphone recovers - a warning that outlives its cause trains
//     the user to ignore the one that does not.
//
// Revert-proof: delete either threshold branch in DictationDialog's animation loop and the matching
// assertion below reddens; make the alarm sticky instead of self-clearing and the recovery test fails.

// The controllable microphone. The test moves these two clocks; nothing else about it matters.
const clocks = vi.hoisted(() => ({ sinceAudio: 0, sinceMeter: 0, level: 0.5 }));

vi.mock("./recorder", () => {
  class MicRecorder {
    onCaptureLive: (() => void) | null = null;
    lastRecordedMs = 1000;
    deviceLabel = "Fake Microphone";
    deviceId = "fake-mic";
    async start() {
      this.onCaptureLive?.();
    }
    async stop() {
      return new Blob(["clip"], { type: "audio/webm" });
    }
    level() {
      return clocks.level;
    }
    // The bars read the band spectrum; the alarm reads level(). This fake answers both so the alarm
    // is exercised through the real animation loop.
    bands() {
      return new Array(9).fill(clocks.level);
    }
    msSinceLastAudio() {
      return clocks.sinceAudio;
    }
    msSinceMeterMoved() {
      return clocks.sinceMeter;
    }
    dispose() {}
  }
  return { MicRecorder, rmsLevel: () => 0, computeBands: () => new Array(9).fill(0) };
});

vi.mock("../api/client", () => ({
  transcribeUtterance: vi.fn(async () => "the dictated words"),
}));
vi.mock("./readyCue", () => ({ playReadyCue: () => {} }));
vi.mock("./qualityReport", () => ({ reportDictationQuality: () => {} }));
vi.mock("./wav", () => ({
  blobToWav16kMono: async () => ({
    wav: new Blob(["wav"]),
    decodedSeconds: 1,
    sourceBytes: 1000,
    nativeSamples: new Float32Array(0),
    nativeSampleRate: 16000,
  }),
}));

import { DictationDialog } from "./DictationDialog";

// Drive requestAnimationFrame by hand. jsdom's own RAF is a timer we would have to race; capturing
// the callback makes each frame an explicit step, so these tests read as "one frame passes" and can
// never flake on scheduling.
let frame: FrameRequestCallback | null = null;

beforeEach(() => {
  clocks.sinceAudio = 0;
  clocks.sinceMeter = 0;
  clocks.level = 0.5;
  frame = null;
  vi.stubGlobal("requestAnimationFrame", (cb: FrameRequestCallback) => {
    frame = cb;
    return 1;
  });
  vi.stubGlobal("cancelAnimationFrame", () => {
    frame = null;
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

/** Let one animation frame run. */
async function tick(): Promise<void> {
  await act(async () => {
    frame?.(performance.now());
  });
}

/** Mount the dialog and get it into RECORDING (the fake recorder fires capture-live on start). */
async function openRecording(): Promise<void> {
  render(<DictationDialog onSend={() => {}} onClose={() => {}} surface="cockpit" />);
  await act(async () => {}); // let the mount effect's start() settle
  expect(screen.getByText("RECORDING")).toBeTruthy();
}

const STALLED = /stopped sending audio/i;
const SILENT = /Nothing is coming from your microphone/i;

describe("DictationDialog live capture alarm", () => {
  it("says nothing while capture and the meter are both alive", async () => {
    await openRecording();
    await tick();

    expect(screen.queryByText(STALLED)).toBeNull();
    expect(screen.queryByText(SILENT)).toBeNull();
  });

  it("reports a stalled recording once no audio has been delivered", async () => {
    await openRecording();
    clocks.sinceAudio = 4000; // past CAPTURE_STALL_MS
    await tick();

    // The wording must tell the user their words are NOT being recorded, not merely that something
    // is odd - this is the moment the audio is actually going missing.
    expect(screen.getByText(STALLED)).toBeTruthy();
  });

  it("reports hearing nothing when the meter is pinned at zero but capture is alive", async () => {
    await openRecording();
    clocks.sinceMeter = 6000; // past MIC_SILENT_MS
    clocks.level = 0;
    await tick();

    expect(screen.getByText(SILENT)).toBeTruthy();
    expect(screen.queryByText(STALLED)).toBeNull();
  });

  it("prefers the stalled message when both clocks are past their thresholds", async () => {
    // A stalled recorder usually goes silent too. Showing the silence message then would bury the one
    // that says words are being lost under the one that merely follows from it.
    await openRecording();
    clocks.sinceAudio = 4000;
    clocks.sinceMeter = 6000;
    clocks.level = 0;
    await tick();

    expect(screen.getByText(STALLED)).toBeTruthy();
    expect(screen.queryByText(SILENT)).toBeNull();
  });

  it("clears the alarm as soon as the microphone recovers", async () => {
    await openRecording();
    clocks.sinceAudio = 4000;
    await tick();
    expect(screen.getByText(STALLED)).toBeTruthy();

    clocks.sinceAudio = 0; // audio is flowing again
    await tick();

    expect(screen.queryByText(STALLED)).toBeNull();
  });
});
