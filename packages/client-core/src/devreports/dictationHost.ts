// DICTATION INTO A DEV REPORT'S BOXES - the host's half (Report Answers by Voice).
//
// The report runs in a frame with scripts and no same-origin access, so it cannot open the microphone. Its
// microphone button asks the host over the private port (`dictation-start`, then `dictation-stop`), and this class
// does the recording and the transcription and tells the page where it stands with `dictation` updates.
//
// Framework-free, so the whole loop is tested with a fake recorder and a fake transcriber. The real ones are the
// session prompt's own: the shared MicRecorder, and the same Gateway transcription upload the Speak dialog uses
// (`transcribeForDevReport` in DevReportViewer.tsx). Nothing sits between the speech model and the box: the text
// the Gateway returns is handed to the page unchanged, and the page puts it in the box for the owner to read and
// correct. Nothing is sent by talking.
//
// One box records at a time. A second start while one is live is answered "failed" for the second box, and the
// live one carries on.

import { reportShownError } from "../errors/reportClientError";
import type { DevReportDictationUpdate } from "./protocol";

const SURFACE = "dev-report-viewer";

/** How often the page hears the seconds and the level while recording. */
export const DICTATION_TICK_MS = 200;

/**
 * How long an opened microphone may go without delivering its first audio before the box says so. The same backstop,
 * and the same words, as the Speak dialog's GETTING READY state: a muted or dead input opens without error and then
 * delivers nothing, and without this the box would say "Starting" for ever with the microphone held open.
 */
export const DICTATION_READY_TIMEOUT_MS = 6000;

/** Words for a microphone that opened but never delivered audio - the Speak dialog's sentence. */
export const MICROPHONE_NOT_CAPTURING =
  "The microphone did not start capturing. Check that it is connected, not muted, and that this site is allowed to use it, then try again.";

/** The parts of the shared MicRecorder this uses. */
export interface DictationRecorder {
  /** Fired once, when the first real audio arrives - the honest "recording now" moment. */
  onCaptureLive: (() => void) | null;
  /** Opens the microphone. Rejects with a sentence for the owner when it cannot. */
  start(): Promise<void>;
  /** Stops and returns the captured audio. */
  stop(): Promise<Blob>;
  /** The current level, 0 to 1, for the meter. */
  displayLevel(): number;
  /** How long the microphone was actually capturing, for the capture-health check. */
  readonly lastRecordedMs: number;
  dispose(): void;
}

/** What a finished transcription hands back: the words, and a warning when audio was dropped, or null. */
export interface DictationTranscript {
  text: string;
  warning: string | null;
}

export interface DevReportDictationOptions {
  /** A fresh recorder for each recording. */
  createRecorder: () => DictationRecorder;
  /** Opens the recorder, failing loud when the microphone does not open in time. */
  startRecorder: (recorder: DictationRecorder) => Promise<void>;
  /** Turns the captured audio into words through the Gateway. */
  transcribe: (captured: Blob, recordedMs: number) => Promise<DictationTranscript>;
  /** Where every update goes - the frame host's pushDictation. */
  push: (update: DevReportDictationUpdate) => void;
  now?: () => number;
  setInterval?: (fn: () => void, ms: number) => unknown;
  clearInterval?: (handle: unknown) => void;
  setTimeout?: (fn: () => void, ms: number) => unknown;
  clearTimeout?: (handle: unknown) => void;
}

interface Active {
  fieldId: string;
  recorder: DictationRecorder;
  /** When capture went live, or null while the microphone is still opening. */
  liveAt: number | null;
  ticker: unknown;
  /** The not-capturing backstop, armed once the microphone opens and cleared by the first audio. */
  readyTimer: unknown;
  stopping: boolean;
}

/** Words the owner reads when the microphone heard nothing the transcriber could turn into words. */
export const NOTHING_HEARD = "Nothing was heard, so nothing was added. Tap the microphone and try again.";
/** Words for a second box asked to record while one already is. */
export const ANOTHER_BOX_RECORDING = "Another box is recording. Stop that one first.";

export class DevReportDictation {
  private readonly options: DevReportDictationOptions;
  private readonly now: () => number;
  private readonly setTicker: (fn: () => void, ms: number) => unknown;
  private readonly clearTicker: (handle: unknown) => void;
  private readonly setTimer: (fn: () => void, ms: number) => unknown;
  private readonly clearTimer: (handle: unknown) => void;
  private active: Active | null = null;
  /** Bumped by cancel, so an answer that arrives for a recording that was cancelled is dropped. */
  private generation = 0;

  constructor(options: DevReportDictationOptions) {
    this.options = options;
    this.now = options.now ?? (() => performance.now());
    this.setTicker = options.setInterval ?? ((fn, ms) => globalThis.setInterval(fn, ms));
    this.clearTicker = options.clearInterval ?? ((handle) => globalThis.clearInterval(handle as ReturnType<typeof setInterval>));
    this.setTimer = options.setTimeout ?? ((fn, ms) => globalThis.setTimeout(fn, ms));
    this.clearTimer = options.clearTimeout ?? ((handle) => globalThis.clearTimeout(handle as ReturnType<typeof setTimeout>));
  }

  /** The box recording or transcribing now, or null. */
  get activeFieldId(): string | null {
    return this.active?.fieldId ?? null;
  }

  /** The page's microphone button was tapped on an idle box. */
  async start(fieldId: string): Promise<void> {
    console.log(`[DevReportDictation] start: field=${fieldId}`);
    if (this.active) {
      console.log(`[DevReportDictation] start: field=${fieldId} refused, ${this.active.fieldId} is recording`);
      this.options.push({ fieldId, phase: "failed", reason: ANOTHER_BOX_RECORDING });
      return;
    }
    const generation = this.generation;
    const recorder = this.options.createRecorder();
    const active: Active = { fieldId, recorder, liveAt: null, ticker: null, readyTimer: null, stopping: false };
    this.active = active;
    recorder.onCaptureLive = () => {
      if (this.active !== active || active.stopping) return;
      this.clearReadyTimer(active);
      active.liveAt = this.now();
      this.pushRecording(active);
      active.ticker = this.setTicker(() => this.pushRecording(active), DICTATION_TICK_MS);
    };
    this.options.push({ fieldId, phase: "starting" });
    try {
      await this.options.startRecorder(recorder);
    } catch (err) {
      if (this.generation !== generation || this.active !== active) return;
      this.release(active);
      const reason = reportShownError(
        SURFACE,
        "start the microphone",
        err instanceof Error && err.message ? err.message : "The microphone could not be started.",
        undefined,
        err,
      );
      console.log(`[DevReportDictation] start FAILED: field=${fieldId} ${reason}`);
      this.options.push({ fieldId, phase: "failed", reason });
      return;
    }
    if (this.generation !== generation || this.active !== active) {
      // Cancelled while the microphone was opening: the cancel's dispose ran before the stream existed, so the stream
      // the open just assigned would stay live with no owner. Dispose again now that there is a stream to release.
      console.log(`[DevReportDictation] start: field=${fieldId} opened after a cancel, releasing the microphone`);
      recorder.onCaptureLive = null;
      recorder.dispose();
      return;
    }
    if (active.liveAt === null) {
      active.readyTimer = this.setTimer(() => this.onNotCapturing(active), DICTATION_READY_TIMEOUT_MS);
    }
  }

  /** The microphone opened and then delivered no audio in time: say so, and let the microphone go. */
  private onNotCapturing(active: Active): void {
    active.readyTimer = null;
    if (this.active !== active || active.liveAt !== null || active.stopping) return;
    const reason = reportShownError(SURFACE, "start the microphone", MICROPHONE_NOT_CAPTURING);
    console.log(`[DevReportDictation] start FAILED: field=${active.fieldId} ${reason}`);
    this.release(active);
    this.options.push({ fieldId: active.fieldId, phase: "failed", reason });
  }

  private clearReadyTimer(active: Active): void {
    if (active.readyTimer !== null) this.clearTimer(active.readyTimer);
    active.readyTimer = null;
  }

  /** The page's stop square was tapped: stop, transcribe, and hand the words back. */
  async stop(fieldId: string): Promise<void> {
    console.log(`[DevReportDictation] stop: field=${fieldId}`);
    const active = this.active;
    if (!active || active.fieldId !== fieldId || active.stopping) return;
    active.stopping = true;
    this.clearReadyTimer(active);
    if (active.ticker !== null) this.clearTicker(active.ticker);
    active.ticker = null;
    const generation = this.generation;
    this.options.push({ fieldId, phase: "transcribing" });

    let result: DictationTranscript;
    try {
      const captured = await active.recorder.stop();
      result = await this.options.transcribe(captured, active.recorder.lastRecordedMs);
    } catch (err) {
      if (this.generation !== generation) return;
      this.release(active);
      const reason = reportShownError(
        SURFACE,
        "transcribe a dictation",
        err instanceof Error && err.message ? err.message : "The recording could not be transcribed.",
        undefined,
        err,
      );
      console.log(`[DevReportDictation] stop FAILED: field=${fieldId} ${reason}`);
      this.options.push({ fieldId, phase: "failed", reason });
      return;
    }
    if (this.generation !== generation) return;
    this.release(active);
    if (result.text.trim() === "") {
      console.log(`[DevReportDictation] stop: field=${fieldId} nothing heard`);
      this.options.push({ fieldId, phase: "failed", reason: NOTHING_HEARD });
      return;
    }
    console.log(`[DevReportDictation] stop: field=${fieldId} done, ${result.text.length} characters`);
    const done: DevReportDictationUpdate = { fieldId, phase: "done", text: result.text };
    if (result.warning) done.reason = result.warning;
    this.options.push(done);
  }

  /**
   * The page went away - a new version loaded, the frame was navigated, the viewer closed. The microphone is let go
   * at once and anything still in flight is dropped: there is no box left to put it in.
   */
  cancel(): void {
    this.generation++;
    const active = this.active;
    if (!active) return;
    console.log(`[DevReportDictation] cancel: field=${active.fieldId}`);
    this.release(active);
  }

  private pushRecording(active: Active): void {
    if (this.active !== active || active.liveAt === null) return;
    const seconds = Math.floor((this.now() - active.liveAt) / 1000);
    const level = Math.max(0, Math.min(1, active.recorder.displayLevel()));
    this.options.push({ fieldId: active.fieldId, phase: "recording", seconds, level });
  }

  private release(active: Active): void {
    this.clearReadyTimer(active);
    if (active.ticker !== null) this.clearTicker(active.ticker);
    active.ticker = null;
    active.recorder.onCaptureLive = null;
    active.recorder.dispose();
    if (this.active === active) this.active = null;
  }
}
