import { useCallback, useEffect, useRef, useState } from "react";
import { transcribeUtterance } from "../api/client";
import { MicRecorder } from "./recorder";
import { blobToWav16kMono } from "./wav";

// The ONE shared mobile dictation dialog (issue #817), used by the Terminal view (#810) and, when
// it lands, the Chat view (#811) - both mount this same component and pass their own onInsert /
// onSend, so the Speak control behaves identically everywhere from one source. It is the phone-web
// twin of the Android SpeakIntoTextboxDialog and the desktop SpeakDialog, obeying the canonical
// contract docs/architecture/dictation/DICTATION_UX_SPEC.md.
//
// Whole-clip BATCH with a Pause checkpoint. NO text appears while talking; speech becomes text only
// at a checkpoint (Pause) or on commit (Insert/Send). Pause stops the mic, transcribes the segment
// so far, appends it to the (editable) transcript, and Resume starts a fresh segment that appends.
// Insert commits the text WITHOUT submitting; Send commits AND submits (the host wires what those
// mean). The four buttons are Cancel / Pause-Resume / Insert / Send.
//
// Transcript integrity (CodingStyle.md s16): the server returns the user's words already corrected
// by the single dictionary-correction engine; this component only displays them, lets the user
// hand-edit, and joins segments with a single space. It never rewrites the words.

type Stage = "recording" | "transcribing" | "paused" | "error";

export interface DictationDialogProps {
  /** Commit the transcript WITHOUT submitting (drop into the view's text box for editing). Required
   *  only when Insert is shown; ignored when showInsert is false. */
  onInsert?: (text: string) => void;
  /** Commit the transcript AND submit it (the view's Send path). */
  onSend: (text: string) => void;
  /** Close the dialog (Cancel, or after a commit). Nothing is sent on Cancel. */
  onClose: () => void;
  /** Whether to offer the Insert button. The Voice mode "Respond" flow (issue #850, mockup F) sets
   *  this false so the reply panel is Cancel / Pause / Send only - Send goes straight into the
   *  session, there is no "drop into a box" target. Defaults true for the Terminal/Chat Speak flow. */
  showInsert?: boolean;
}

const BAR_COUNT = 9;

// Join two transcript fragments with exactly one separating space unless either side already
// supplies the boundary whitespace (mirrors the desktop/phone DictationText.Join). This is the only
// transformation allowed on the user's words on the client.
function joinText(left: string, right: string): string {
  if (!left) return right;
  if (!right) return left;
  const boundary = /\s$/.test(left) || /^\s/.test(right);
  return boundary ? left + right : left + " " + right;
}

function formatElapsed(ms: number): string {
  const totalSeconds = Math.floor(ms / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${String(seconds).padStart(2, "0")}`;
}

export function DictationDialog({ onInsert, onSend, onClose, showInsert = true }: DictationDialogProps) {
  const recorderRef = useRef<MicRecorder>(new MicRecorder());
  const accumulatedRef = useRef<string>(""); // committed segments; the box may be edited past this
  const busyRef = useRef<boolean>(false); // guards the transcribe window against double taps

  // Segment timing: the running total freezes during transcribing/paused and resumes on Resume.
  const segmentStartRef = useRef<number>(0);
  const elapsedBeforeRef = useRef<number>(0);

  const [stage, setStage] = useState<Stage>("recording");
  const [transcript, setTranscript] = useState<string>("");
  const [hint, setHint] = useState<string>("");
  const [errorText, setErrorText] = useState<string>("");
  const [elapsed, setElapsed] = useState<number>(0);
  const [levels, setLevels] = useState<number[]>(() => new Array(BAR_COUNT).fill(0));

  // ----- equalizer + timer animation (display only) -----
  useEffect(() => {
    let raf = 0;
    const tick = () => {
      if (stage === "recording") {
        const now = performance.now();
        setElapsed(elapsedBeforeRef.current + (now - segmentStartRef.current));
        const level = recorderRef.current.level();
        const next = new Array(BAR_COUNT).fill(0).map((_, i) => {
          // Centre bars react most, so it reads as an equalizer rather than a flat bar.
          const centre = (BAR_COUNT - 1) / 2;
          const falloff = 1 - Math.abs(i - centre) / (centre + 1);
          return Math.min(1, level * (0.55 + falloff));
        });
        setLevels(next);
      }
      raf = window.requestAnimationFrame(tick);
    };
    raf = window.requestAnimationFrame(tick);
    return () => window.cancelAnimationFrame(raf);
  }, [stage]);

  // ----- start the first recording segment on open; tear the mic down on unmount -----
  useEffect(() => {
    const recorder = recorderRef.current;
    let cancelled = false;
    (async () => {
      try {
        await recorder.start();
        if (cancelled) return;
        segmentStartRef.current = performance.now();
        elapsedBeforeRef.current = 0;
        setStage("recording");
      } catch (err) {
        if (cancelled) return;
        setErrorText(err instanceof Error ? err.message : "Could not start the microphone.");
        setStage("error");
      }
    })();
    return () => {
      cancelled = true;
      recorder.dispose();
    };
  }, []);

  // Stop the current segment, transcode to WAV, and transcribe it. Returns the segment text, or
  // null when there was no audio / transcription failed (the reason is shown). The recorder is
  // consumed here, so a segment is never transcribed twice.
  const transcribeCurrentSegment = useCallback(async (): Promise<string | null> => {
    let wav: Blob;
    try {
      const captured = await recorderRef.current.stop();
      wav = await blobToWav16kMono(captured);
    } catch (err) {
      setHint(err instanceof Error ? err.message : "Could not capture the recording.");
      return null;
    }
    try {
      const text = await transcribeUtterance(wav);
      return text;
    } catch (err) {
      setHint("Transcription failed: " + (err instanceof Error ? err.message : "unknown error"));
      return null;
    }
  }, []);

  const onPauseResume = useCallback(async () => {
    if (busyRef.current) return;
    if (stage === "recording") {
      // Pause: checkpoint. Transcribe the segment so far, append it, park in PAUSED (editable).
      busyRef.current = true;
      elapsedBeforeRef.current += performance.now() - segmentStartRef.current;
      setStage("transcribing");
      setHint("Transcribing what you have said so far...");
      const segment = await transcribeCurrentSegment();
      if (segment !== null) {
        accumulatedRef.current = joinText(accumulatedRef.current, segment);
        setTranscript(accumulatedRef.current);
        setHint("");
      }
      setStage("paused");
      busyRef.current = false;
    } else if (stage === "paused") {
      // Resume: re-seed from the (possibly edited) box so edits survive, start a fresh segment.
      accumulatedRef.current = transcript;
      try {
        await recorderRef.current.start();
        segmentStartRef.current = performance.now();
        setHint("");
        setStage("recording");
      } catch (err) {
        setHint("Could not resume - your text is kept. " + (err instanceof Error ? err.message : ""));
        setStage("paused");
      }
    }
  }, [stage, transcript, transcribeCurrentSegment]);

  const commit = useCallback(
    async (action: (text: string) => void) => {
      if (busyRef.current) return;
      if (stage === "recording") {
        busyRef.current = true;
        elapsedBeforeRef.current += performance.now() - segmentStartRef.current;
        setStage("transcribing");
        setHint("Transcribing...");
        const segment = await transcribeCurrentSegment();
        if (segment === null) {
          // Do not commit partial/failed text - park in PAUSED with what we have.
          setStage("paused");
          busyRef.current = false;
          return;
        }
        accumulatedRef.current = joinText(accumulatedRef.current, segment);
        busyRef.current = false;
        action(accumulatedRef.current.trim());
        onClose();
      } else if (stage === "paused") {
        action(transcript.trim());
        onClose();
      }
    },
    [stage, transcript, transcribeCurrentSegment, onClose],
  );

  const onCancel = useCallback(() => {
    recorderRef.current.dispose();
    onClose();
  }, [onClose]);

  // Keyboard: Escape cancels; Enter sends except while editing the transcript box (newline there).
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        onCancel();
      }
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onCancel]);

  const isError = stage === "error";
  const isPaused = stage === "paused";
  const isTranscribing = stage === "transcribing";
  const statusLabel =
    stage === "recording" ? "RECORDING" : stage === "transcribing" ? "TRANSCRIBING" : stage === "paused" ? "PAUSED" : "ERROR";

  return (
    <div className="dictate-overlay" role="dialog" aria-modal="true" aria-label="Dictate">
      <div className="dictate-card">
        <div className="dictate-head">
          <span className={`dictate-status dictate-status-${stage}`}>{statusLabel}</span>
          <span className="dictate-timer">{formatElapsed(elapsed)}</span>
        </div>

        {!isError && (
          <div className="dictate-eq" aria-hidden="true">
            {levels.map((v, i) => (
              <span key={i} className="dictate-eq-well">
                <span
                  className={`dictate-eq-bar ${stage !== "recording" ? "dictate-eq-bar-idle" : ""}`}
                  style={{ height: `${8 + v * 84}%` }}
                />
              </span>
            ))}
          </div>
        )}

        <div className="dictate-hint" role="status">{hint}</div>

        {isError ? (
          <div className="dictate-error" role="alert">{errorText}</div>
        ) : (
          <textarea
            className="dictate-transcript"
            value={isPaused ? transcript : accumulatedRef.current}
            readOnly={!isPaused}
            onChange={(e) => setTranscript(e.target.value)}
            placeholder="Your words appear when you pause or finish - press Pause to see them so far."
          />
        )}

        <div className="dictate-actions">
          <button type="button" className="dictate-btn dictate-cancel" onClick={onCancel}>
            {isError ? "Close" : "Cancel"}
          </button>
          {!isError && (
            <button
              type="button"
              className="dictate-btn dictate-pause"
              onClick={onPauseResume}
              disabled={isTranscribing}
            >
              {isPaused ? "Resume" : <span className="dictate-pause-glyph"><i /><i /></span>}
            </button>
          )}
          <span className="dictate-spacer" />
          {!isError && (
            <>
              {showInsert && onInsert && (
                <button
                  type="button"
                  className="dictate-btn dictate-insert"
                  onClick={() => commit(onInsert)}
                  disabled={isTranscribing}
                >
                  Insert
                </button>
              )}
              <button
                type="button"
                className="dictate-btn dictate-send"
                onClick={() => commit(onSend)}
                disabled={isTranscribing}
              >
                Send
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
