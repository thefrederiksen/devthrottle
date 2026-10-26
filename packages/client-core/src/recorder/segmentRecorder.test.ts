import { describe, expect, it } from "vitest";

// The pure pieces of the rotating capture engine (issue #958). The MediaRecorder rotation itself
// needs a real microphone and is proven in the live walk; what is pinned here is the codec label
// contract with the server - CodecToExt maps these labels by substring, so a wrong label would make
// the server store segments under the wrong extension and content type.

import { codecLabelFor, SEGMENT_MS, SegmentRecorder } from "./segmentRecorder";
import { peakBandLevel } from "../dictation/recorder";

describe("codecLabelFor", () => {
  it("maps every MediaRecorder container to a label the server's CodecToExt resolves correctly", () => {
    expect(codecLabelFor("audio/webm;codecs=opus")).toBe("webm-opus");
    expect(codecLabelFor("audio/webm")).toBe("webm-opus");
    expect(codecLabelFor("audio/mp4")).toBe("aac-m4a");
    expect(codecLabelFor("audio/ogg;codecs=opus")).toBe("ogg-opus");
  });

  it("defaults to webm-opus when the container is unknown", () => {
    expect(codecLabelFor("")).toBe("webm-opus");
  });
});

describe("capture constants", () => {
  it("rotates one-minute segments (the Android recorder's interval)", () => {
    expect(SEGMENT_MS).toBe(60_000);
  });
});

// The on-screen level bar for the long-form recorder page. It used to read a raw loudness on a
// linear scale while the dictation dialog had moved to the band calibration, so the same voice
// filled the two meters by visibly different amounts - two screens disagreeing about one
// microphone. These pin that this meter is now read through the shared calibration.
describe("SegmentRecorder.displayLevel", () => {
  const SR = 48000;

  // A speech-shaped signal at a known level, the same generator the calibration tests use.
  function voice(dbfs: number, n = 2048): Float32Array {
    const x = new Float64Array(n);
    for (let i = 0; i < n; i++) {
      const t = i / SR;
      let s = 0;
      for (let h = 1; h * 120 <= 5000; h++) s += Math.sin(2 * Math.PI * h * 120 * t + h) / h;
      x[i] = s;
    }
    let sum = 0;
    for (const v of x) sum += v * v;
    const gain = Math.pow(10, dbfs / 20) / Math.sqrt(sum / x.length);
    const out = new Float32Array(n);
    for (let i = 0; i < n; i++) out[i] = Math.round(Math.max(-1, Math.min(1, x[i] * gain)) * 32767) / 32767;
    return out;
  }

  // The constructor's options are irrelevant to the meter - it reads the analyser, nothing else -
  // but they are required, so these are the inert minimum.
  function newRecorder(): SegmentRecorder {
    return new SegmentRecorder({
      onSegment: async () => {},
      onError: () => {},
    } as unknown as ConstructorParameters<typeof SegmentRecorder>[0]);
  }

  function wire(samples: Float32Array, paused = false): SegmentRecorder {
    const rec = newRecorder();
    const anyRec = rec as unknown as {
      analyser: { getFloatTimeDomainData(buf: Float32Array): void };
      levelData: Float32Array;
      audioCtx: { sampleRate: number };
      paused: boolean;
    };
    anyRec.analyser = {
      getFloatTimeDomainData(buf: Float32Array) {
        buf.set(samples.subarray(0, buf.length));
      },
    };
    anyRec.levelData = new Float32Array(samples.length);
    anyRec.audioCtx = { sampleRate: SR };
    anyRec.paused = paused;
    return rec;
  }

  it("reads the same loudness the dictation dialog's tallest bar would show", () => {
    for (const db of [-42, -32, -26, -20]) {
      const w = voice(db);
      expect(wire(w).displayLevel()).toBeCloseTo(peakBandLevel(w, SR), 10);
    }
  });

  it("rises with the voice across the range people actually speak at", () => {
    const quiet = wire(voice(-42)).displayLevel();
    const normal = wire(voice(-26)).displayLevel();
    const loud = wire(voice(-20)).displayLevel();
    expect(quiet).toBeGreaterThan(0);
    expect(normal).toBeGreaterThan(quiet);
    expect(loud).toBeGreaterThan(normal);
    // The old linear scale put a good speaking level under a quarter of the bar; this one does not.
    expect(normal).toBeGreaterThan(0.5);
  });

  it("reads zero while paused, however loud the room is", () => {
    expect(wire(voice(-14), true).displayLevel()).toBe(0);
  });

  it("reads zero before the microphone is open", () => {
    expect(newRecorder().displayLevel()).toBe(0);
  });
});
