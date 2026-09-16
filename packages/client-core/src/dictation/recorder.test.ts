import { afterEach, describe, expect, it, vi } from "vitest";
import { MicRecorder, rmsLevel, STOP_TAIL_MS } from "./recorder";

// snapshotFlushed() is the tail-loss fix for the turn-taking paths (Car Mode "Over and out" and the
// end-phrase watch): a bare snapshot() only sees chunks MediaRecorder has already delivered, so the
// last words - up to one timeslice of audio still buffered inside the recorder - were missing from
// the clip that got transcribed. These tests drive the flush contract with a fake MediaRecorder
// (the tests run in Node, where no real recorder exists): requestData() must be asked for the tail,
// the tail must be in the returned blob, and a recorder that never delivers (or has already gone
// inactive) must resolve with what has arrived instead of hanging the turn.

type DataListener = (e: { data: Blob }) => void;

class FakeMediaRecorder {
  state: "recording" | "inactive" | "paused" = "recording";
  requestDataCalls = 0;
  /** What requestData does; the test sets this to emulate the browser delivering the tail. */
  onRequestData: (() => void) | null = null;
  private onceListeners: DataListener[] = [];

  addEventListener(type: string, listener: DataListener, options?: { once?: boolean }): void {
    if (type !== "dataavailable") throw new Error(`unexpected listener type: ${type}`);
    if (!options?.once) throw new Error("snapshotFlushed must register a once-listener");
    this.onceListeners.push(listener);
  }

  requestData(): void {
    this.requestDataCalls += 1;
    this.onRequestData?.();
  }

  /** Emulate the browser firing dataavailable to the registered once-listeners. */
  deliver(data: Blob): void {
    const listeners = this.onceListeners;
    this.onceListeners = [];
    for (const l of listeners) l({ data });
  }
}

// Reach into the private capture state the way start() would have set it up. TypeScript private is
// compile-time only; the fields are the recorder's real ones, so the method under test runs unchanged.
function wire(fake: FakeMediaRecorder, chunks: Blob[]): MicRecorder {
  const mic = new MicRecorder();
  const anyMic = mic as unknown as { recorder: FakeMediaRecorder; mimeType: string; chunks: Blob[] };
  anyMic.recorder = fake;
  anyMic.mimeType = "audio/webm";
  anyMic.chunks = chunks;
  return mic;
}

function bytes(n: number): Blob {
  return new Blob([new Uint8Array(n)]);
}

afterEach(() => {
  vi.useRealTimers();
});

describe("MicRecorder.snapshotFlushed", () => {
  it("includes the tail chunk MediaRecorder had not delivered yet", async () => {
    const fake = new FakeMediaRecorder();
    const chunks: Blob[] = [bytes(3)]; // header chunk already delivered
    const mic = wire(fake, chunks);

    // The plain snapshot documents the gap: only the delivered 3 bytes.
    expect(mic.snapshot().size).toBe(3);

    // requestData makes the browser flush the buffered tail: the production ondataavailable handler
    // (registered first) pushes it, then the once-listener resolves - emulate exactly that order.
    fake.onRequestData = () => {
      chunks.push(bytes(5));
      fake.deliver(bytes(5));
    };

    const flushed = await mic.snapshotFlushed();
    expect(fake.requestDataCalls).toBe(1);
    expect(flushed.size).toBe(8); // 3 delivered + 5 flushed tail
  });

  it("returns the plain snapshot when the recorder is not actively recording", async () => {
    const fake = new FakeMediaRecorder();
    fake.state = "inactive";
    const mic = wire(fake, [bytes(4)]);

    const flushed = await mic.snapshotFlushed();
    expect(fake.requestDataCalls).toBe(0); // nothing is buffered in an inactive recorder
    expect(flushed.size).toBe(4);
  });

  it("resolves via the backstop when the flush is never delivered, with what has arrived", async () => {
    vi.useFakeTimers();
    const fake = new FakeMediaRecorder();
    const mic = wire(fake, [bytes(7)]);
    // requestData is called but the recorder never fires dataavailable (a wedged recorder).

    const pending = mic.snapshotFlushed();
    await vi.advanceTimersByTimeAsync(500);
    const flushed = await pending;
    expect(fake.requestDataCalls).toBe(1);
    expect(flushed.size).toBe(7);
  });

  it("resolves with what has arrived when requestData throws (a concurrent stop)", async () => {
    const fake = new FakeMediaRecorder();
    fake.onRequestData = () => {
      throw new Error("The MediaRecorder is in an invalid state.");
    };
    const mic = wire(fake, [bytes(2)]);

    const flushed = await mic.snapshotFlushed();
    expect(flushed.size).toBe(2);
  });

  it("does not resolve twice when the delivery and the backstop both fire", async () => {
    vi.useFakeTimers();
    const fake = new FakeMediaRecorder();
    const chunks: Blob[] = [bytes(1)];
    const mic = wire(fake, chunks);
    fake.onRequestData = () => {
      chunks.push(bytes(1));
      fake.deliver(bytes(1));
    };

    const flushed = await mic.snapshotFlushed();
    expect(flushed.size).toBe(2);
    // The backstop timer was cleared on delivery; advancing time must not blow up on a settled promise.
    await vi.advanceTimersByTimeAsync(1000);
  });
});

// The equalizer meter (issue: the Cockpit Speak bars were sluggish/near-flat). rmsLevel reads the live
// WAVEFORM (getByteTimeDomainData: bytes centred on 128, silence = 128) as instantaneous loudness, so the
// bars track the speaker in real time. These pin the shape the equalizer relies on.
describe("rmsLevel", () => {
  // A window of pure silence is every sample sitting at the 128 centre.
  function silence(n: number): Uint8Array {
    return new Uint8Array(n).fill(128);
  }
  // A full-scale square wave: samples alternate between the two rails (0 and 255), the loudest possible
  // read.
  function fullScale(n: number): Uint8Array {
    const buf = new Uint8Array(n);
    for (let i = 0; i < n; i++) buf[i] = i % 2 === 0 ? 0 : 255;
    return buf;
  }

  it("reads silence (all samples at the centre) as zero", () => {
    expect(rmsLevel(silence(512))).toBe(0);
  });

  it("reads a full-scale waveform as the clamped maximum of 1", () => {
    expect(rmsLevel(fullScale(512))).toBe(1);
  });

  it("reads an empty window as zero rather than dividing by zero", () => {
    expect(rmsLevel(new Uint8Array(0))).toBe(0);
  });

  it("rises monotonically as the waveform gets louder", () => {
    const quiet = new Uint8Array(512);
    const loud = new Uint8Array(512);
    for (let i = 0; i < 512; i++) {
      // Small deviation from centre vs a larger one - louder audio swings further from 128.
      quiet[i] = i % 2 === 0 ? 118 : 138; // +-10
      loud[i] = i % 2 === 0 ? 88 : 168; // +-40
    }
    const q = rmsLevel(quiet);
    const l = rmsLevel(loud);
    expect(q).toBeGreaterThan(0);
    expect(l).toBeGreaterThan(q);
  });

  it("stays within 0..1 for any input", () => {
    const random = new Uint8Array(512);
    for (let i = 0; i < 512; i++) random[i] = (i * 37) % 256;
    const v = rmsLevel(random);
    expect(v).toBeGreaterThanOrEqual(0);
    expect(v).toBeLessThanOrEqual(1);
  });
});

// The suspended-context guard (the Cockpit "bars never bounce" defect): the meter's AudioContext is
// created after the async getUserMedia round trip, so the browser's autoplay policy can start it
// SUSPENDED - and a suspended analyser reads the flat centre line forever, pinning the equalizer at
// zero while MediaRecorder captures fine. level() must re-ask a suspended context to resume on every
// read (it runs once per animation frame), so the meter self-heals instead of staying dead for the
// whole dictation. These drive the REAL level() with the private capture fields wired in, the same
// way the snapshotFlushed tests wire theirs - not a copy of the logic.
describe("MicRecorder.level suspended-context guard", () => {
  // The analyser hands back a clearly loud waveform so a healed meter is distinguishable from silence.
  class FakeAnalyser {
    getByteTimeDomainData(buf: Uint8Array): void {
      for (let i = 0; i < buf.length; i++) buf[i] = i % 2 === 0 ? 88 : 168;
    }
  }

  class FakeAudioContext {
    state: "suspended" | "running";
    resumeCalls = 0;
    constructor(state: "suspended" | "running") {
      this.state = state;
    }
    resume(): Promise<void> {
      this.resumeCalls += 1;
      this.state = "running";
      return Promise.resolve();
    }
  }

  function wireLevel(ctx: FakeAudioContext): MicRecorder {
    const mic = new MicRecorder();
    const anyMic = mic as unknown as { audioCtx: FakeAudioContext; analyser: FakeAnalyser; levelData: Uint8Array };
    anyMic.audioCtx = ctx;
    anyMic.analyser = new FakeAnalyser();
    anyMic.levelData = new Uint8Array(8);
    return mic;
  }

  it("asks a suspended context to resume, once per read, until it runs", () => {
    const ctx = new FakeAudioContext("suspended");
    const mic = wireLevel(ctx);
    mic.level();
    expect(ctx.resumeCalls).toBe(1);
    // The next frame's read sees the context running and leaves it alone.
    mic.level();
    expect(ctx.resumeCalls).toBe(1);
  });

  it("never touches resume on a context that is already running", () => {
    const ctx = new FakeAudioContext("running");
    const mic = wireLevel(ctx);
    expect(mic.level()).toBeGreaterThan(0);
    expect(ctx.resumeCalls).toBe(0);
  });

  it("still returns the analyser's reading on the same call that resumes", () => {
    // The resume is fire-and-forget; the read itself must not be skipped or zeroed by the guard.
    const ctx = new FakeAudioContext("suspended");
    const mic = wireLevel(ctx);
    expect(mic.level()).toBeGreaterThan(0);
  });
});

// ===== stop(): the tail is ASKED FOR, not assumed ==================================================
// The owner's report was "it didn't finish to the end". MediaRecorder is specified to emit its
// buffered audio before it fires stop, so resolving on onstop was already collecting the tail - but
// that was a behaviour being trusted rather than an instruction being given, and the last words are
// precisely what a user notices missing. These pin that stop() now flushes explicitly, that the
// flushed bytes are in the returned clip, and that a flush which fails can never stop the stop.

class StopFake {
  state: "recording" | "inactive" | "paused" = "recording";
  requestDataCalls = 0;
  stopCalls = 0;
  onstop: (() => void) | null = null;
  /** Set by the test to emulate the browser delivering the buffered tail on requestData. */
  onRequestData: (() => void) | null = null;
  /** Set by the test to emulate audio the browser only delivers on stop itself. */
  onStopDeliver: (() => void) | null = null;

  requestData(): void {
    this.requestDataCalls += 1;
    this.onRequestData?.();
  }

  stop(): void {
    // As browsers do: stopping a recorder that is already inactive throws, and fires no stop event.
    if (this.state === "inactive") {
      throw new DOMException("The MediaRecorder's state is 'inactive'.", "InvalidStateError");
    }
    this.stopCalls += 1;
    this.onStopDeliver?.();
    this.state = "inactive";
    this.onstop?.();
  }
}

function wireStop(fake: StopFake, chunks: Blob[]): MicRecorder {
  const mic = new MicRecorder();
  const anyMic = mic as unknown as { recorder: StopFake; mimeType: string; chunks: Blob[]; startedAt: number };
  anyMic.recorder = fake;
  anyMic.mimeType = "audio/webm";
  anyMic.chunks = chunks;
  anyMic.startedAt = performance.now();
  return mic;
}

describe("MicRecorder.stop", () => {
  it("flushes the buffered tail before stopping and returns it in the clip", async () => {
    const fake = new StopFake();
    const chunks: Blob[] = [bytes(3)]; // already delivered while recording
    const mic = wireStop(fake, chunks);
    // The production ondataavailable handler pushes the flushed chunk; emulate that.
    fake.onRequestData = () => chunks.push(bytes(5));

    const clip = await mic.stop();
    expect(fake.requestDataCalls).toBe(1);
    expect(fake.stopCalls).toBe(1);
    expect(clip.size).toBe(8); // 3 delivered + 5 flushed tail - the last words are in
  });

  it("still includes audio the browser only delivers on stop itself", async () => {
    // The flush and the spec's own stop-time delivery are additive, never either/or: whatever the
    // flush did not take, stop still hands over, and both land in the clip in order.
    const fake = new StopFake();
    const chunks: Blob[] = [bytes(2)];
    const mic = wireStop(fake, chunks);
    fake.onRequestData = () => chunks.push(bytes(4));
    fake.onStopDeliver = () => chunks.push(bytes(1));

    const clip = await mic.stop();
    expect(clip.size).toBe(7);
  });

  it("stops and returns the clip even when the tail flush throws", async () => {
    // A recorder that went inactive between the state check and the flush must not strand the turn:
    // everything already delivered is still the user's audio and still ships.
    const fake = new StopFake();
    fake.onRequestData = () => {
      throw new Error("The MediaRecorder is in an invalid state.");
    };
    const mic = wireStop(fake, [bytes(6)]);

    const clip = await mic.stop();
    expect(fake.stopCalls).toBe(1);
    expect(clip.size).toBe(6);
  });

  it("does not ask for a flush when the recorder is no longer recording", async () => {
    const fake = new StopFake();
    fake.state = "inactive"; // nothing is buffered in an inactive recorder
    const mic = wireStop(fake, [bytes(9)]);

    const clip = await mic.stop();
    expect(fake.requestDataCalls).toBe(0);
    expect(fake.stopCalls).toBe(0); // stopping an inactive recorder throws in a browser
    expect(clip.size).toBe(9);
  });
});

// ===== stop(): the 250 ms tail after Send and Pause (issue #2927) ==================================
// A word said on the Send or Pause click ends AFTER the click. Stopping at once cut it off, and neither
// the flush nor the browser's stop-time delivery can return audio that was never captured.

describe("MicRecorder.stop tail", () => {
  it("keeps capturing for the tail, so audio delivered after Send is in the clip", async () => {
    vi.useFakeTimers();
    const fake = new StopFake();
    const chunks: Blob[] = [bytes(3)];
    const mic = wireStop(fake, chunks);

    const pending = mic.stop();
    // The end of the last word arrives 100 ms after Send, while the tail is running.
    await vi.advanceTimersByTimeAsync(100);
    expect(fake.stopCalls).toBe(0); // the recorder has not been stopped yet
    chunks.push(bytes(4));
    await vi.advanceTimersByTimeAsync(STOP_TAIL_MS);

    const clip = await pending;
    expect(fake.stopCalls).toBe(1);
    expect(clip.size).toBe(7); // the late 4 bytes are in
  });

  it("does not stop the recorder before the tail has elapsed", async () => {
    vi.useFakeTimers();
    const fake = new StopFake();
    const mic = wireStop(fake, [bytes(1)]);

    const pending = mic.stop();
    await vi.advanceTimersByTimeAsync(STOP_TAIL_MS - 1);
    expect(fake.stopCalls).toBe(0);
    await vi.advanceTimersByTimeAsync(1);
    await pending;
    expect(fake.stopCalls).toBe(1);
  });

  it("returns the delivered chunks when the recorder goes inactive on its own during the tail", async () => {
    // Inspection one, finding 4: the input track ends while the tail runs, the browser fires stop and
    // the recorder is inactive. Calling stop() then throws InvalidStateError and the clip was lost.
    vi.useFakeTimers();
    const fake = new StopFake();
    const chunks: Blob[] = [bytes(3)];
    const mic = wireStop(fake, chunks);

    const pending = mic.stop();
    const outcome = pending.then(
      (clip) => clip.size,
      (err: Error) => `rejected: ${err.message}`,
    );
    await vi.advanceTimersByTimeAsync(100);
    chunks.push(bytes(4)); // the browser's final delivery as the track ended
    fake.state = "inactive";
    await vi.advanceTimersByTimeAsync(STOP_TAIL_MS);

    expect(await outcome).toBe(7);
    expect(fake.stopCalls).toBe(0);
    expect(fake.requestDataCalls).toBe(0);
  });

  it("fails loudly instead of hanging when Cancel releases the microphone during the tail", async () => {
    vi.useFakeTimers();
    const fake = new StopFake();
    const mic = wireStop(fake, [bytes(1)]);

    const pending = mic.stop();
    const outcome = pending.then(
      () => "resolved",
      (err: Error) => err.message,
    );
    (mic as unknown as { recorder: StopFake | null }).recorder = null; // what dispose() leaves behind
    await vi.advanceTimersByTimeAsync(STOP_TAIL_MS);
    expect(await outcome).toMatch(/cancelled/);
  });
});

// ===== the liveness clocks ========================================================================
// These are what let the dialog say "the microphone has stopped sending audio" WHILE the user is
// still talking, instead of only measuring the loss after the clip is finished. Capture liveness and
// meter liveness are tracked separately on purpose: they are two different audio graphs, and exactly
// one of them dying is the common case (a dead meter over a healthy recording is the Cockpit
// flat-bars defect; stalled capture over a live meter is audio genuinely going missing).

function wireLive(state: "recording" | "inactive", lastChunkAgeMs: number, meterAgeMs: number): MicRecorder {
  const mic = new MicRecorder();
  const now = performance.now();
  const anyMic = mic as unknown as {
    recorder: { state: string };
    lastChunkAt: number;
    meterMovedAt: number;
  };
  anyMic.recorder = { state };
  anyMic.lastChunkAt = now - lastChunkAgeMs;
  anyMic.meterMovedAt = now - meterAgeMs;
  return mic;
}

describe("MicRecorder liveness clocks", () => {
  it("read zero when there is no live recorder, so a stopped mic never reads as stalled", () => {
    const mic = new MicRecorder();
    expect(mic.msSinceLastAudio()).toBe(0);
    expect(mic.msSinceMeterMoved()).toBe(0);
  });

  it("read zero once the recorder has gone inactive", () => {
    const mic = wireLive("inactive", 10_000, 10_000);
    expect(mic.msSinceLastAudio()).toBe(0);
    expect(mic.msSinceMeterMoved()).toBe(0);
  });

  it("report how long capture has been silent while recording", () => {
    const mic = wireLive("recording", 3_000, 0);
    expect(mic.msSinceLastAudio()).toBeGreaterThanOrEqual(3_000);
    expect(mic.msSinceMeterMoved()).toBeLessThan(1_000);
  });

  it("report how long the meter has been pinned at zero while recording", () => {
    const mic = wireLive("recording", 0, 5_000);
    expect(mic.msSinceMeterMoved()).toBeGreaterThanOrEqual(5_000);
    expect(mic.msSinceLastAudio()).toBeLessThan(1_000);
  });

  // A recorder whose analyser fills every sample with `fill`: 128 is the flat centre line a dead or
  // muted graph reads (loudness exactly 0), anything else is real sound.
  function wireMeter(fill: number, meterAgeMs: number): MicRecorder {
    const mic = new MicRecorder();
    const anyMic = mic as unknown as {
      audioCtx: { state: string };
      analyser: { getByteTimeDomainData: (b: Uint8Array) => void };
      levelData: Uint8Array;
      recorder: { state: string };
      meterMovedAt: number;
    };
    anyMic.audioCtx = { state: "running" };
    anyMic.analyser = {
      getByteTimeDomainData: (buf: Uint8Array) => {
        for (let i = 0; i < buf.length; i++) buf[i] = fill;
      },
    };
    anyMic.levelData = new Uint8Array(8);
    anyMic.recorder = { state: "recording" };
    anyMic.meterMovedAt = performance.now() - meterAgeMs;
    return mic;
  }

  it("treat a reading above zero as proof the meter is alive", () => {
    // level() resets the meter clock only when the analyser actually reads something.
    const mic = wireMeter(200, 5_000);
    expect(mic.level()).toBeGreaterThan(0);
    expect(mic.msSinceMeterMoved()).toBeLessThan(1_000);
  });

  it("leave the meter clock running when the analyser reads exact silence", () => {
    // A dead graph reads the flat centre line forever, which is exactly zero. The clock must KEEP
    // running through that - it is what turns dead bars into a reported fault rather than a drawing
    // of silence, which is how the Cockpit meter lied for so long.
    const mic = wireMeter(128, 5_000);
    expect(mic.level()).toBe(0);
    expect(mic.msSinceMeterMoved()).toBeGreaterThanOrEqual(5_000);
  });
});
