// The rotating capture engine for long-form voice recording (issue #958) - the PWA port of the
// Android recorder's segment rotation (phone/CcRecorder/Platforms/Android/AndroidAudioRecorder.cs).
//
// Capture rotates a FINALIZED segment on a fixed one-minute interval: the active MediaRecorder is
// stopped (which flushes its container so the blob is a complete, independently decodable audio
// file) and a fresh MediaRecorder is started on the SAME live MediaStream, so the microphone never
// closes between segments and the gap is a few milliseconds. Each finalized segment is handed to the
// caller the moment it exists, to be persisted durably (IndexedDB) right away - so a crash, an app
// close, or a phone reboot loses at most the currently-open segment, never the recording.
//
// A plain MediaRecorder timeslice cannot do this: timeslice chunks after the first carry no container
// header and are not independently decodable, and the server transcribes each stored segment file on
// its own. Rotation of whole MediaRecorder instances is the browser equivalent of the Android
// recorder's finalized one-minute files.
//
// Pause finalizes the open segment and RELEASES the microphone (the browser's recording indicator
// goes off - the honest signal that nothing is being captured); Resume reopens it and starts the next
// segment. The elapsed clock excludes paused time, like the Android recorder's.
//
// The live level meter reuses the dictation recorder's RMS math (display-only, never touches the
// captured audio).

import { rmsLevel } from "../dictation/recorder";

/** Fixed segment length, matching the Android recorder's one-minute rotation. */
export const SEGMENT_MS = 60_000;

/** How long to wait for the microphone to open before failing loudly (see dictation/recorder.ts). */
const MIC_OPEN_TIMEOUT_MS = 8000;

// The equalizer time window (see dictation/recorder.ts LEVEL_FFT_SIZE).
const LEVEL_FFT_SIZE = 512;

// Pick a MediaRecorder container the browser actually supports, preferring Opus-in-WebM (what every
// Chromium/Firefox phone produces). Exported for tests.
export function pickMimeType(): string {
  const candidates = ["audio/webm;codecs=opus", "audio/webm", "audio/mp4", "audio/ogg;codecs=opus"];
  for (const c of candidates) {
    if (typeof MediaRecorder !== "undefined" && MediaRecorder.isTypeSupported(c)) return c;
  }
  return "";
}

/** The codec label the server's CodecToExt maps to the right file extension and content type. */
export function codecLabelFor(mimeType: string): string {
  const m = mimeType.toLowerCase();
  if (m.includes("webm")) return "webm-opus";
  if (m.includes("mp4")) return "aac-m4a";
  if (m.includes("ogg")) return "ogg-opus";
  return "webm-opus";
}

/** A finalized segment handed to the caller the moment the rotation produced it. */
export interface FinalizedSegment {
  index: number;
  blob: Blob;
  /** Millisecond offset of this segment from the start of the recording (excluding paused time). */
  startMs: number;
  durationMs: number;
}

export interface SegmentRecorderOptions {
  /** Called for EVERY finalized segment (rotation, pause, and stop). The caller persists it durably
   *  before anything else happens to it. Errors thrown here are reported via onCaptureLost. */
  onSegment: (segment: FinalizedSegment) => Promise<void>;
  /** A segment could not be persisted or the recorder failed mid-capture. The capture is stopped. */
  onCaptureLost: (message: string) => void;
  /** Total capture reached the opt-in cap and the recorder stopped itself. */
  onAutoStop?: () => void;
  /** OPT-IN auto-stop cap on total captured (non-paused) time. There is deliberately NO default:
   *  recording is unlimited (recorder-unlimited-capture mission - a 30-minute default cap silently
   *  truncated a conference talk). A caller that wants a bound passes one explicitly. */
  maxDurationMs?: number;
  /** Rotation interval override for tests. Default SEGMENT_MS. */
  segmentMs?: number;
}

export class SegmentRecorder {
  private readonly opts: SegmentRecorderOptions;
  private stream: MediaStream | null = null;
  private recorder: MediaRecorder | null = null;
  private chunks: Blob[] = [];
  private mimeType = "";
  private audioCtx: AudioContext | null = null;
  private analyser: AnalyserNode | null = null;
  private levelData: Uint8Array | null = null;
  private rotateTimer: ReturnType<typeof setTimeout> | null = null;

  private nextIndex = 0;
  /** Captured (non-paused) milliseconds accumulated across finished segments. */
  private elapsedBeforeSegment = 0;
  /** performance.now() when the active segment started; 0 when idle/paused. */
  private segmentStartedAt = 0;
  private paused = false;
  private stopped = false;
  private failed = false;

  // Serializes every lifecycle transition (rotate, pause, resume, stop) so a rotation firing while
  // Stop is pressed cannot interleave: each operation chains on the previous one's completion.
  private op: Promise<void> = Promise.resolve();

  constructor(opts: SegmentRecorderOptions) {
    this.opts = opts;
  }

  get isPaused(): boolean {
    return this.paused;
  }

  get segmentCount(): number {
    return this.nextIndex;
  }

  /** Captured milliseconds so far, excluding paused time. */
  get elapsedMs(): number {
    const live = this.segmentStartedAt > 0 ? performance.now() - this.segmentStartedAt : 0;
    return this.elapsedBeforeSegment + live;
  }

  /** The codec label ("webm-opus" etc.) for the manifest. Valid after start(). */
  get codecLabel(): string {
    return codecLabelFor(this.mimeType || "audio/webm");
  }

  /** The capturing sample rate, for the manifest. 48000 when unknown. */
  get sampleRateHz(): number {
    return this.audioCtx?.sampleRate ?? 48000;
  }

  /** Current input level in 0..1, sampled live from the waveform. 0 when idle or paused. */
  level(): number {
    if (this.analyser === null || this.levelData === null || this.paused) return 0;
    this.analyser.getByteTimeDomainData(this.levelData);
    return rmsLevel(this.levelData);
  }

  /** Open the microphone and begin capturing. Fails loudly if the mic does not open in time. */
  async start(): Promise<void> {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      throw new Error("This browser does not support microphone capture (getUserMedia).");
    }
    await this.openMicWithTimeout();
    this.startSegment();
  }

  /** Pause capture: finalize the open segment (persisted via onSegment) and release the microphone. */
  pause(): Promise<void> {
    return this.enqueue(async () => {
      if (this.paused || this.stopped || this.recorder === null) return;
      this.paused = true;
      this.clearRotateTimer();
      await this.finalizeSegment();
      this.releaseStream();
    });
  }

  /** Resume a paused capture: reopen the microphone and start the next segment. */
  resume(): Promise<void> {
    return this.enqueue(async () => {
      if (!this.paused || this.stopped) return;
      await this.openMicWithTimeout();
      this.paused = false;
      this.startSegment();
    });
  }

  /** Stop and finalize: the open segment is persisted and the microphone released. Idempotent. */
  stop(): Promise<void> {
    return this.enqueue(async () => {
      if (this.stopped) return;
      this.stopped = true;
      this.clearRotateTimer();
      if (!this.paused) await this.finalizeSegment();
      this.releaseStream();
    });
  }

  /** Release everything without emitting a segment (only for teardown after a failure). */
  dispose(): void {
    this.stopped = true;
    this.clearRotateTimer();
    try {
      if (this.recorder !== null && this.recorder.state !== "inactive") this.recorder.stop();
    } catch {
      /* already stopped; releasing the stream is what matters */
    }
    this.releaseStream();
  }

  // ===== internals ======================================================================

  private enqueue(work: () => Promise<void>): Promise<void> {
    const next = this.op.then(work);
    // The chain must survive a failed operation (the failure is surfaced to the caller of THIS op).
    this.op = next.catch(() => undefined);
    return next;
  }

  private async openMicWithTimeout(): Promise<void> {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const timeout = new Promise<never>((_, reject) => {
      timer = setTimeout(
        () =>
          reject(
            new Error(
              `The microphone did not open within ${Math.round(MIC_OPEN_TIMEOUT_MS / 1000)} seconds. ` +
                "It may be in use by another application, or disconnected.",
            ),
          ),
        MIC_OPEN_TIMEOUT_MS,
      );
    });
    try {
      const stream = await Promise.race([
        navigator.mediaDevices.getUserMedia({
          audio: { echoCancellation: true, noiseSuppression: true, channelCount: 1 },
        }),
        timeout,
      ]);
      this.stream = stream;
    } finally {
      if (timer !== undefined) clearTimeout(timer);
    }

    // The system can close the microphone under us - screen lock, another app claiming it, the
    // browser suspending the page. A track's "ended" event fires ONLY for such external causes
    // (never for our own track.stop()), so this is the honest signal that capture died. Salvage
    // what the recorder flushed and stop loudly: a capture that dies must never look complete.
    for (const track of this.stream.getTracks()) {
      track.addEventListener("ended", () => this.onTrackEnded());
    }

    // Live level meter on the captured stream (display only).
    const AudioCtor =
      window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
    this.audioCtx = new AudioCtor();
    const source = this.audioCtx.createMediaStreamSource(this.stream);
    this.analyser = this.audioCtx.createAnalyser();
    this.analyser.fftSize = LEVEL_FFT_SIZE;
    source.connect(this.analyser);
    this.levelData = new Uint8Array(this.analyser.fftSize);

    if (this.mimeType === "") this.mimeType = pickMimeType();
  }

  private startSegment(): void {
    if (this.stream === null) throw new Error("Recorder has no live microphone stream.");
    this.recorder = this.mimeType
      ? new MediaRecorder(this.stream, { mimeType: this.mimeType })
      : new MediaRecorder(this.stream);
    this.chunks = [];
    this.recorder.ondataavailable = (e) => {
      if (e.data && e.data.size > 0) this.chunks.push(e.data);
    };
    this.recorder.start();
    this.segmentStartedAt = performance.now();
    this.armRotateTimer();
  }

  private armRotateTimer(): void {
    this.clearRotateTimer();
    const segmentMs = this.opts.segmentMs ?? SEGMENT_MS;
    this.rotateTimer = setTimeout(() => {
      void this.enqueue(async () => {
        if (this.stopped || this.paused || this.failed) return;
        await this.finalizeSegment();
        // finalizeSegment stops the capture itself when the segment could not be persisted.
        if (this.stopped || this.failed) return;
        const cap = this.opts.maxDurationMs;
        if (cap !== undefined && this.elapsedBeforeSegment >= cap) {
          // The cap is enforced here, at a segment boundary, so the capped recording is made of
          // exactly the finalized segments already persisted - nothing is truncated or lost.
          this.stopped = true;
          this.releaseStream();
          this.opts.onAutoStop?.();
          return;
        }
        this.startSegment();
      });
    }, segmentMs);
  }

  private onTrackEnded(): void {
    void this.enqueue(async () => {
      // A pause released the stream on purpose; a stop already finished. Only a LIVE capture whose
      // track died externally is a loss to report.
      if (this.stopped || this.paused || this.failed) return;
      this.failed = true;
      this.stopped = true;
      this.clearRotateTimer();
      await this.finalizeSegment();
      this.releaseStream();
      this.opts.onCaptureLost(
        "the phone suspended the microphone (screen lock, another app took it, or the browser " +
          "paused the page). Everything captured up to that point is saved.",
      );
    });
  }

  private clearRotateTimer(): void {
    if (this.rotateTimer !== null) {
      clearTimeout(this.rotateTimer);
      this.rotateTimer = null;
    }
  }

  /** Stop the active MediaRecorder, wait for its final flush, and hand the finalized segment out. */
  private async finalizeSegment(): Promise<void> {
    const rec = this.recorder;
    if (rec === null) return;
    this.recorder = null;
    const mime = this.mimeType || "audio/webm";
    const blob = await new Promise<Blob>((resolve) => {
      rec.onstop = () => resolve(new Blob(this.chunks, { type: mime }));
      try {
        rec.stop();
      } catch {
        // Already inactive - whatever chunks were delivered are the segment.
        resolve(new Blob(this.chunks, { type: mime }));
      }
    });
    const durationMs = this.segmentStartedAt > 0 ? Math.round(performance.now() - this.segmentStartedAt) : 0;
    const startMs = Math.round(this.elapsedBeforeSegment);
    this.segmentStartedAt = 0;
    this.elapsedBeforeSegment += durationMs;
    this.chunks = [];

    // A rotation that captured nothing (e.g. a wedged device) produces an empty blob; skip it rather
    // than storing a segment the server's completeness gate would verify but Whisper cannot decode.
    if (blob.size === 0) return;

    const index = this.nextIndex;
    this.nextIndex += 1;
    try {
      await this.opts.onSegment({ index, blob, startMs, durationMs });
    } catch (err) {
      // The segment could not be persisted durably - the never-lose-audio contract is broken, so the
      // capture must not carry on pretending. Stop loudly.
      this.failed = true;
      this.stopped = true;
      this.clearRotateTimer();
      this.releaseStream();
      this.opts.onCaptureLost(err instanceof Error ? err.message : String(err));
    }
  }

  private releaseStream(): void {
    if (this.stream !== null) {
      for (const track of this.stream.getTracks()) track.stop();
    }
    if (this.audioCtx !== null) {
      void this.audioCtx.close();
    }
    this.stream = null;
    this.recorder = null;
    this.audioCtx = null;
    this.analyser = null;
    this.levelData = null;
    this.segmentStartedAt = 0;
  }
}
