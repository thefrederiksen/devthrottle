// Microphone capture for the mobile dictation dialog (issue #817). Whole-clip BATCH: the mic
// records a segment, and on stop the captured audio is handed back as one Blob to be transcoded
// and transcribed. No text appears while talking (the canonical contract,
// docs/architecture/dictation/DICTATION_UX_SPEC.md).
//
// The recorder also exposes a live input level (0..1) sampled from an AnalyserNode on the live
// stream, which the dialog draws as the equalizer. This is display-only; it never touches the
// captured audio.

// Pick a MediaRecorder container the browser actually supports, preferring Opus-in-WebM (what
// every Chromium/Firefox phone produces). The captured blob is transcoded to WAV before upload,
// so the exact container here only needs to be decodable by the browser's own decodeAudioData.
function pickMimeType(): string {
  const candidates = ["audio/webm;codecs=opus", "audio/webm", "audio/mp4", "audio/ogg;codecs=opus"];
  for (const c of candidates) {
    if (typeof MediaRecorder !== "undefined" && MediaRecorder.isTypeSupported(c)) return c;
  }
  return "";
}

export class MicRecorder {
  private stream: MediaStream | null = null;
  private recorder: MediaRecorder | null = null;
  private chunks: Blob[] = [];
  private mimeType = "";
  private audioCtx: AudioContext | null = null;
  private analyser: AnalyserNode | null = null;
  private levelData: Uint8Array | null = null;

  /** True while a segment is actively capturing. */
  get isRecording(): boolean {
    return this.recorder !== null && this.recorder.state === "recording";
  }

  /**
   * Open the microphone and start capturing a fresh segment. Throws if permission is denied or
   * no audio device is available - the caller surfaces the reason (no silent fallback).
   */
  async start(): Promise<void> {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      throw new Error("This browser does not support microphone capture (getUserMedia).");
    }
    this.stream = await navigator.mediaDevices.getUserMedia({
      audio: { echoCancellation: true, noiseSuppression: true, channelCount: 1 },
    });

    // Live level meter on the captured stream (display only).
    const AudioCtor = window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
    this.audioCtx = new AudioCtor();
    const source = this.audioCtx.createMediaStreamSource(this.stream);
    this.analyser = this.audioCtx.createAnalyser();
    this.analyser.fftSize = 64;
    this.analyser.smoothingTimeConstant = 0.5;
    source.connect(this.analyser);
    this.levelData = new Uint8Array(this.analyser.frequencyBinCount);

    this.mimeType = pickMimeType();
    this.recorder = this.mimeType
      ? new MediaRecorder(this.stream, { mimeType: this.mimeType })
      : new MediaRecorder(this.stream);
    this.chunks = [];
    this.recorder.ondataavailable = (e) => {
      if (e.data && e.data.size > 0) this.chunks.push(e.data);
    };
    this.recorder.start();
  }

  /** Current input level in 0..1, sampled live. Returns 0 when not recording. */
  level(): number {
    if (!this.analyser || !this.levelData) return 0;
    this.analyser.getByteFrequencyData(this.levelData);
    let sum = 0;
    for (let i = 0; i < this.levelData.length; i++) sum += this.levelData[i];
    const avg = sum / this.levelData.length; // 0..255
    return Math.min(1, avg / 180);
  }

  /**
   * Stop the current segment and return the captured audio as one Blob. The microphone is
   * released here, so the next segment calls start() again (a fresh Resume segment).
   */
  async stop(): Promise<Blob> {
    const rec = this.recorder;
    if (rec === null) throw new Error("Recorder was not started.");
    const mime = this.mimeType || "audio/webm";
    const captured = await new Promise<Blob>((resolve) => {
      rec.onstop = () => resolve(new Blob(this.chunks, { type: mime }));
      rec.stop();
    });
    this.releaseStream();
    return captured;
  }

  /** Release the microphone and audio graph without producing a clip (Cancel / teardown). */
  dispose(): void {
    try {
      if (this.recorder !== null && this.recorder.state !== "inactive") this.recorder.stop();
    } catch {
      // already stopped; releasing the stream below is what matters
    }
    this.releaseStream();
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
  }
}
