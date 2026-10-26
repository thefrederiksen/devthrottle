// @vitest-environment jsdom
// The equalizer strip, as the user sees it (the "Cockpit bars hardly move" defect).
//
// These drive the REAL animation loop in DictationDialog through hand-stepped animation frames and
// read the heights off the DOM, because the defect lived precisely in the gap between what the
// recorder measured and what the bars ended up showing. The two properties that matter:
//
//   - the nine bars are driven by nine SEPARATE band readings. The old meter fanned one loudness
//     number through a fixed triangle, so the bars could only ever rise and fall as one hill, and no
//     amount of fixing the scale would have made it read as a spectrum.
//   - a bar EASES toward its target, fast up and slow down, instead of snapping to it.

import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, act } from "@testing-library/react";

// The fake recorder hands back a fixed, deliberately uneven spectrum so "did each bar get its own
// number?" is answerable from the rendered heights.
const SPECTRUM = [0.9, 0.1, 0.8, 0.2, 0.7, 0.3, 0.6, 0.4, 0.5];
const state = { bands: SPECTRUM.slice(), level: 0.5 };

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
      return state.level;
    }
    bands() {
      return state.bands.slice();
    }
    msSinceLastAudio() {
      return 0;
    }
    msSinceMeterMoved() {
      return 0;
    }
    dispose() {}
  }
  return { MicRecorder, rmsLevel: () => 0, computeBands: () => state.bands.slice() };
});
vi.mock("../api/client", () => ({ transcribeUtterance: vi.fn(async () => "the dictated words") }));
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

let frame: FrameRequestCallback | null = null;
let clock = 0;

beforeEach(() => {
  state.bands = SPECTRUM.slice();
  state.level = 0.5;
  frame = null;
  clock = performance.now();
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

/** Let one animation frame pass, advancing the clock by a 60 Hz frame. */
async function tick(ms = 16.7): Promise<void> {
  clock += ms;
  await act(async () => {
    frame?.(clock);
  });
}

async function openRecording(): Promise<void> {
  render(<DictationDialog onSend={() => {}} onClose={() => {}} surface="cockpit" />);
  await act(async () => {});
  expect(screen.getByText("RECORDING")).toBeTruthy();
}

function barHeights(): number[] {
  const bars = document.querySelectorAll<HTMLElement>(".dictate-eq-bar");
  return Array.from(bars, (b) => parseFloat(b.style.height));
}

describe("the dictation equalizer", () => {
  it("draws nine bars", async () => {
    await openRecording();
    expect(barHeights()).toHaveLength(9);
  });

  it("gives every bar its own height from its own band, not one hill scaled nine ways", async () => {
    await openRecording();
    // Several frames so the easing has settled close to the targets.
    for (let i = 0; i < 40; i++) await tick();
    const heights = barHeights();

    // Nine distinct values. The old fixed triangle produced a centre-peaked shape with mirrored
    // pairs, so it could not have passed this.
    expect(new Set(heights.map((h) => Math.round(h))).size).toBe(9);

    // And the ORDER follows the spectrum it was given, bar for bar.
    const order = (xs: number[]) => xs.map((_, i) => i).sort((a, b) => xs[a] - xs[b]);
    expect(order(heights)).toEqual(order(SPECTRUM));
  });

  it("eases toward a new peak instead of snapping to it, and falls more slowly than it rises", async () => {
    await openRecording();
    state.bands = new Array(9).fill(0);
    for (let i = 0; i < 40; i++) await tick();
    const atRest = barHeights()[0];

    // A sudden loud band: the first frame must move a long way but NOT all the way.
    state.bands = new Array(9).fill(1);
    await tick();
    const afterOneRise = barHeights()[0];
    expect(afterOneRise).toBeGreaterThan(atRest);
    expect(afterOneRise).toBeLessThan(92);

    // Let it reach the top, then cut to silence: one frame of decay must travel a SMALLER fraction
    // of the remaining distance than one frame of attack did.
    for (let i = 0; i < 40; i++) await tick();
    const atTop = barHeights()[0];
    state.bands = new Array(9).fill(0);
    await tick();
    const afterOneFall = barHeights()[0];

    const roseFraction = (afterOneRise - atRest) / (92 - atRest);
    const fellFraction = (atTop - afterOneFall) / (atTop - 8);
    expect(fellFraction).toBeLessThan(roseFraction);
  });

  it("eases at the same rate per millisecond whatever the display's frame rate", async () => {
    await openRecording();
    state.bands = new Array(9).fill(0);
    for (let i = 0; i < 40; i++) await tick();
    state.bands = new Array(9).fill(1);
    // One 60 Hz frame.
    await tick(16.7);
    const at60 = barHeights()[0];

    cleanup();
    clock = performance.now();
    await openRecording();
    state.bands = new Array(9).fill(0);
    for (let i = 0; i < 40; i++) await tick(8.3);
    state.bands = new Array(9).fill(1);
    // Two 120 Hz frames cover the same elapsed time.
    await tick(8.3);
    await tick(8.3);
    const at120 = barHeights()[0];

    expect(Math.abs(at60 - at120)).toBeLessThan(1);
  });

  // The dialog still re-renders once a second for the timer, and the bar heights are written outside
  // React. If a re-render reset them - by re-running the inline ref callback, or by React re-applying
  // the JSX style - the strip would twitch back to the floor once a second, which is a worse artefact
  // than the one being fixed. This pins that it does not.
  it("keeps the bar heights across the once-a-second timer re-render", async () => {
    await openRecording();
    for (let i = 0; i < 40; i++) await tick();
    const before = barHeights();
    expect(Math.max(...before)).toBeGreaterThan(50);

    // Cross a second boundary, which is the only thing that still re-renders the dialog.
    await tick(1100);
    const after = barHeights();

    // The heights moved only by one frame of easing, not back to the floor.
    expect(Math.min(...after)).toBeGreaterThan(9);
    for (let i = 0; i < 9; i++) expect(Math.abs(after[i] - before[i])).toBeLessThan(2);
  });

  it("parks the bars on the floor when the microphone is not capturing", async () => {
    await openRecording();
    for (let i = 0; i < 40; i++) await tick();
    expect(Math.max(...barHeights())).toBeGreaterThan(50);

    // Pause stops capture; the meter must stop dancing rather than freeze mid-bounce.
    // Selected by class rather than by accessible name: while recording the Pause control renders as
    // a bare glyph with no text and no aria-label, so it has no accessible name to query by. That is
    // a real gap, but it belongs to the buttons and not to the meter, so it is not fixed here.
    const pause = document.querySelector<HTMLButtonElement>(".dictate-pause");
    expect(pause).not.toBeNull();
    pause?.click();
    await act(async () => {});
    await tick();
    expect(barHeights().every((h) => h === 8)).toBe(true);
  });
});
