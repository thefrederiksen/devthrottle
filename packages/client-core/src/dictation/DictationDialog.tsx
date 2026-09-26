import { useCallback, useEffect, useRef, useState } from "react";
import { transcribeUtterance } from "../api/client";
import type { CapturedUtterance } from "./backgroundSend";
import { captureLossWarning, logCaptureHealth } from "./captureHealth";
import { playReadyCue } from "./readyCue";
import { MicRecorder } from "./recorder";
import { joinText } from "./transcript";
import { blobToWav16kMono } from "./wav";
import { reportDictationQuality } from "./qualityReport";
// The dialog carries its own styles (issue #1288): every shell that mounts this component gets the
// dictate-* rules from one copy, so the Cockpit renders it as a centered overlay just like the phone
// instead of unstyled elements at the page bottom. The rules formerly lived only in the mobile
// stylesheet (apps/mobile/src/styles.css); they now live beside this component.
import "./dictation.css";

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

// "connecting" = the GETTING READY warm-up: the mic is opening but has not delivered audio yet, so
// the dialog is deliberately NOT in the red RECORDING look and plays no cue. It flips to "recording"
// (and plays the ready bloop) only when the first real audio chunk arrives, so neither the red state
// nor the sound ever precedes real audio - the desktop SpeakDialog does the same.
type Stage = "connecting" | "recording" | "transcribing" | "paused" | "error";

// Backstop for the GETTING READY state: if the mic never delivers a first chunk, fail loud with a
// "check your microphone" message rather than stranding the user on GETTING READY forever.
const READY_TIMEOUT_MS = 6000;

// ---- live capture alarms (the "half of what I said went missing" defect) -------------------------
// Until now the ONLY loss check ran after the clip was finished (recorded wall-clock versus decoded
// duration), which can tell the user their words are gone but never in time to save them. These two
// thresholds watch the same failures WHILE the microphone is open, so a recording that has stopped
// hearing the user says so on screen instead of quietly producing half a sentence.
//
// They watch two independent paths deliberately. MediaRecorder (what is captured) and the analyser
// (what the bars draw) are separate audio graphs, so the common failure is exactly one of them dying:
//   - no captured audio for CAPTURE_STALL_MS -> the recording itself has stalled; words are being lost
//     right now. MediaRecorder delivers every 100ms while healthy, so this is thirty missed deliveries.
//     Deliberately not tighter: dataavailable is delivered on the main thread, so a busy session view
//     that blocks it for a second or two makes chunks arrive LATE without any audio being lost (the
//     encoder keeps buffering), and crying loss over ordinary jank would teach the user to ignore the
//     one alarm that matters. Still fast enough to stop and say it again.
//   - a meter pinned at exactly zero for MIC_SILENT_MS -> we are hearing nothing at all. A live
//     microphone in a quiet room never reads exact zero, so this is a dead meter (the Cockpit
//     flat-bars defect) or a muted/absent microphone. Longer than the stall window because it is the
//     less urgent of the two and must never fire on a genuine pause for thought.
const CAPTURE_STALL_MS = 3000;
const MIC_SILENT_MS = 5000;

const CAPTURE_STALLED_MESSAGE =
  "The microphone has stopped sending audio - what you are saying now is NOT being recorded. Stop and check your microphone, then say it again.";
const MIC_SILENT_MESSAGE =
  "Nothing is coming from your microphone. Check that it is not muted and that it is the device this page is using - your words may not be recorded.";

export interface DictationDialogProps {
  /** Commit the transcript WITHOUT submitting (drop into the view's text box for editing). Required
   *  only when Insert is shown; ignored when showInsert is false. */
  /** Insert receives the utterance id alongside the text (the commit path has always passed it; the type
   *  hid it), so a composer can record WHICH characters it just inserted came from which transcript -
   *  source logging, 2026-09-05. A host that does not care takes the text alone. */
  onInsert?: (text: string, spokenDeliveryId?: string) => void;
  /** Commit the transcript AND submit it (the view's Send path). Used for the instant PAUSED-stage
   *  Send (the text is already transcribed) and as the fallback Send when onSendAudio is not wired.
   *
   *  `spokenDeliveryId` is present ONLY when the committed text is exactly one unedited transcription,
   *  and it is what lets the host record the turn as SPOKEN rather than typed - the Director reads
   *  modality off the send source, so a transcript submitted as an ordinary prompt counts as typed
   *  without it (ruling R10, "Clean up Your Throttle", 2026-09-05). It is deliberately dropped the
   *  moment the words stop being purely a transcription: a second dictated segment joined on, or any
   *  keystroke in the box. A host that mixes in its own typed text must drop it too. Undefined means
   *  "do not claim this was spoken", which is the honest answer for a mixture and is what the page's
   *  own disclosure already tells the reader. */
  onSend: (text: string, spokenDeliveryId?: string) => void;
  /** Immediate (fire-and-forget) Send: when provided, hitting Send while still RECORDING captures the
   *  audio buffer, hands it here, and closes the dialog IMMEDIATELY - the host then transcodes,
   *  uploads, transcribes, and submits in the background (marking the session "Transcribing..."). This
   *  is what releases the screen the instant Send is pressed, since the result cannot be viewed here
   *  anyway. When omitted (e.g. the Voice-mode reply panel) Send falls back to the blocking
   *  transcribe-then-submit path via onSend. */
  onSendAudio?: (captured: CapturedUtterance) => void;
  /** Close the dialog (Cancel, or after a commit). Nothing is sent on Cancel. */
  onClose: () => void;
  /** Whether to offer the Insert button. The Voice mode "Respond" flow (issue #850, mockup F) sets
   *  this false so the reply panel is Cancel / Pause / Send only - Send goes straight into the
   *  session, there is no "drop into a box" target. Defaults true for the Terminal/Chat Speak flow. */
  showInsert?: boolean;
  /** Which shell mounted this dialog - "cockpit" or "mobile". It tags the capture-health measurements
   *  this dialog reports, and it exists because those measurements were previously labelled "mobile"
   *  from EVERY browser, so a Cockpit dictation on a desktop was filed as a phone dictation and the
   *  Cockpit's own audio loss could not be seen in the data at all (which is how it went unnoticed
   *  while issue #645 read the same log and concluded the web surfaces were healthy). Defaults to
   *  "browser" so an unlabelled mount is honestly unlabelled rather than silently counted as a phone. */
  surface?: string;
}

const BAR_COUNT = 9;

function formatElapsed(ms: number): string {
  const totalSeconds = Math.floor(ms / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${String(seconds).padStart(2, "0")}`;
}

export function DictationDialog({
  onInsert,
  onSend,
  onSendAudio,
  onClose,
  showInsert = true,
  surface = "browser",
}: DictationDialogProps) {
  const recorderRef = useRef<MicRecorder>(new MicRecorder());
  const accumulatedRef = useRef<string>(""); // committed segments; the box may be edited past this
  const busyRef = useRef<boolean>(false); // guards the transcribe window against double taps

  // Segment timing: the running total freezes during transcribing/paused and resumes on Resume.
  const segmentStartRef = useRef<number>(0);
  const elapsedBeforeRef = useRef<number>(0);

  const [stage, setStage] = useState<Stage>("connecting");
  // Mirror of the current stage for use inside timers/callbacks without stale closures.
  const stageRef = useRef<Stage>("connecting");
  useEffect(() => {
    stageRef.current = stage;
  }, [stage]);

  // Pending GETTING READY backstop timer (window.setTimeout handle), or null when disarmed.
  const readyTimeoutRef = useRef<number | null>(null);

  const [transcript, setTranscript] = useState<string>("");

  // THE SPOKEN CLAIM, AND WHAT REVOKES IT (ruling R10, "Clean up Your Throttle", 2026-09-05).
  //
  // Holds the delivery id of the ONE transcription the current text came from, so a Send can tell the
  // host these words were spoken rather than typed. It is null unless that is exactly true, and it is
  // dropped - not merely questioned - the instant the text stops being one unedited transcription: a
  // second dictated segment joined on (the id names one utterance and cannot speak for two) or any
  // keystroke in the box (the person is now typing).
  //
  // Conservative on purpose, and the direction of the error is the point. Claiming spoken wrongly puts
  // words in the person's mouth in a figure he publishes; declining to claim it understates by one
  // turn, and the page's own disclosure already tells the reader that a transcript sent alongside
  // typed text counts as typed.
  const spokenDeliveryRef = useRef<string | null>(null);
  const [hint, setHint] = useState<string>("");
  // Sticky dropped-audio warning (issue #863 made the loss measurable; this makes it VISIBLE).
  // Once a segment shows a material capture deficit the warning stays for the rest of the dialog -
  // the loss happened and the text may be missing words, so it must not be cleared by a later
  // healthy segment.
  const [captureWarning, setCaptureWarning] = useState<string>("");
  const [errorText, setErrorText] = useState<string>("");
  const [elapsed, setElapsed] = useState<number>(0);
  const [levels, setLevels] = useState<number[]>(() => new Array(BAR_COUNT).fill(0));
  // The LIVE alarm (capture stalled / hearing nothing), as opposed to captureWarning above, which is
  // the sticky post-clip measurement. This one describes the microphone right now, so it clears by
  // itself the moment audio comes back - a warning that outlived its cause would train the user to
  // ignore it. Mirrored in a ref so the per-frame loop can compare without re-subscribing.
  const [liveWarning, setLiveWarning] = useState<string>("");
  const liveWarningRef = useRef<string>("");

  const clearReadyBackstop = useCallback(() => {
    if (readyTimeoutRef.current !== null) {
      window.clearTimeout(readyTimeoutRef.current);
      readyTimeoutRef.current = null;
    }
  }, []);

  const armReadyBackstop = useCallback(() => {
    clearReadyBackstop();
    readyTimeoutRef.current = window.setTimeout(() => {
      readyTimeoutRef.current = null;
      if (stageRef.current !== "connecting") return;
      setErrorText(
        "The microphone did not start capturing. Check that it is connected, not muted, and that this site is allowed to use it, then try again.",
      );
      setStage("error");
    }, READY_TIMEOUT_MS);
  }, [clearReadyBackstop]);

  // The honest "mic is now capturing your voice" moment (first real audio chunk). Anchor the
  // segment timer here, flip to RECORDING, and play the ready cue together - so the red and the
  // sound land exactly when audio starts flowing, never before. elapsedBeforeRef is left untouched
  // so a Resume keeps its accumulated time; only a fresh open resets it (in the mount effect).
  const onCaptureLive = useCallback(() => {
    clearReadyBackstop();
    if (stageRef.current !== "connecting") return;
    segmentStartRef.current = performance.now();
    setStage("recording");
    playReadyCue();
  }, [clearReadyBackstop]);

  // ----- equalizer + timer animation, and the live capture alarm -----
  // The alarm rides this loop rather than a timer of its own because the loop already runs once per
  // frame for exactly as long as we are recording, so the two can never disagree about whether the
  // microphone is supposed to be open.
  useEffect(() => {
    // Leaving RECORDING (pause, transcribe, error, commit) retires any live alarm: it describes an
    // open microphone, and there is no longer one to describe.
    if (stage !== "recording" && liveWarningRef.current !== "") {
      liveWarningRef.current = "";
      setLiveWarning("");
    }
    let raf = 0;
    const tick = () => {
      if (stage === "recording") {
        const now = performance.now();
        setElapsed(elapsedBeforeRef.current + (now - segmentStartRef.current));
        const recorder = recorderRef.current;
        const level = recorder.level();
        const next = new Array(BAR_COUNT).fill(0).map((_, i) => {
          // Centre bars react most, so it reads as an equalizer rather than a flat bar.
          const centre = (BAR_COUNT - 1) / 2;
          const falloff = 1 - Math.abs(i - centre) / (centre + 1);
          return Math.min(1, level * (0.55 + falloff));
        });
        setLevels(next);

        // Capture stalling is reported ahead of silence: it is the one that is actively losing the
        // user's words, and a stalled recorder usually goes silent too, so reporting both at once
        // would bury the message that matters under the one that merely follows from it.
        const alarm =
          recorder.msSinceLastAudio() > CAPTURE_STALL_MS
            ? CAPTURE_STALLED_MESSAGE
            : recorder.msSinceMeterMoved() > MIC_SILENT_MS
              ? MIC_SILENT_MESSAGE
              : "";
        // Only on a CHANGE: this runs sixty times a second, and setting the same string every frame
        // would re-render the dialog for no reason.
        if (alarm !== liveWarningRef.current) {
          liveWarningRef.current = alarm;
          setLiveWarning(alarm);
        }
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
    // Flip to RECORDING + play the cue only when the first real audio chunk arrives (not merely
    // when start() returns). The same handler serves every segment on this recorder instance.
    recorder.onCaptureLive = () => {
      if (cancelled) return;
      onCaptureLive();
    };
    (async () => {
      try {
        // A fresh open starts the running total from zero; hold GETTING READY until audio flows.
        elapsedBeforeRef.current = 0;
        armReadyBackstop();
        await recorder.start();
        // Intentionally do NOT flip to RECORDING here - onCaptureLive does, once audio is real.
      } catch (err) {
        if (cancelled) return;
        clearReadyBackstop();
        setErrorText(err instanceof Error ? err.message : "Could not start the microphone.");
        setStage("error");
      }
    })();
    return () => {
      cancelled = true;
      clearReadyBackstop();
      recorder.dispose();
    };
  }, [onCaptureLive, armReadyBackstop, clearReadyBackstop]);

  // Stop the current segment, transcode to WAV, and transcribe it. Returns the segment text plus a
  // dropped-audio warning when the capture-health check found a material deficit, or null when there
  // was no audio / transcription failed (the reason is shown). The recorder is consumed here, so a
  // segment is never transcribed twice.
  const transcribeCurrentSegment = useCallback(async (): Promise<{ text: string; lossWarning: string | null; deliveryId: string } | null> => {
    let wav: Blob;
    let health;
    let lossWarning: string | null = null;
    let nativeSamples = new Float32Array(0);
    let nativeSampleRate = 0;
    const device = { label: recorderRef.current.deviceLabel, deviceId: recorderRef.current.deviceId };
    try {
      const captured = await recorderRef.current.stop();
      const transcoded = await blobToWav16kMono(captured);
      wav = transcoded.wav;
      nativeSamples = transcoded.nativeSamples;
      nativeSampleRate = transcoded.nativeSampleRate;
      // Capture-health (issue #863): compare the recording wall-clock to the decoded audio
      // duration to detect audio dropped before transcription. Logged locally AND sent to the
      // Gateway so it persists into the same dictation session log every other surface writes.
      health = {
        recordedMs: recorderRef.current.lastRecordedMs,
        decodedSeconds: transcoded.decodedSeconds,
        sourceBytes: transcoded.sourceBytes,
      };
      logCaptureHealth(surface, health);
      // Material loss is surfaced to the USER, not just the console: the transcript may be missing
      // words, and a silent commit of truncated speech is exactly the failure mode this dialog
      // exists to prevent. The caller shows the warning and parks instead of auto-committing.
      lossWarning = captureLossWarning(health);
      if (lossWarning !== null) setCaptureWarning(lossWarning);
    } catch (err) {
      setHint(err instanceof Error ? err.message : "Could not capture the recording.");
      return null;
    }
    // Measure the microphone in the background. AFTER the transcode and BEFORE awaiting the
    // transcription, so it rides the gap while the upload is being prepared and never sits between
    // the user and their words. Fire-and-forget by contract - see qualityReport.ts.
    reportDictationQuality(nativeSamples, nativeSampleRate, device, "dictation-dialog");

    try {
      // The surface rides ALONGSIDE the measurement rather than inside it: capture-health is what was
      // measured, the surface is who measured it, and only the upload needs both.
      const transcribed = await transcribeUtterance(wav, { ...health, surface }, undefined, (uploaded, total) => {
        // A short clip uploads in one chunk (no progress line needed); a long recording uploads in
        // several, so show which piece is going up before the "Transcribing..." wait.
        if (total > 1) setHint(`Uploading recording... ${uploaded} of ${total}`);
      });
      return { text: transcribed.text, lossWarning, deliveryId: transcribed.deliveryId };
    } catch (err) {
      setHint(err instanceof Error ? err.message : "The transcription service had a problem and couldn't process your recording.");
      return null;
    }
  }, [surface]);

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
        // One utterance id cannot speak for two segments, so a second one drops the claim rather than
        // naming whichever happened to be last.
        const wasTheOnlySegment = accumulatedRef.current.length === 0 && transcript.length === 0;
        accumulatedRef.current = joinText(accumulatedRef.current, segment.text);
        spokenDeliveryRef.current = wasTheOnlySegment ? segment.deliveryId : null;
        setTranscript(accumulatedRef.current);
        setHint("");
      }
      setStage("paused");
      busyRef.current = false;
    } else if (stage === "paused") {
      // Resume: re-seed from the (possibly edited) box so edits survive, start a fresh segment.
      // Hold GETTING READY until the mic delivers audio again; onCaptureLive flips to RECORDING
      // (and re-cues), keeping the accumulated elapsed time.
      accumulatedRef.current = transcript;
      setHint("");
      setStage("connecting");
      armReadyBackstop();
      try {
        await recorderRef.current.start();
      } catch (err) {
        clearReadyBackstop();
        setHint("Could not resume - your text is kept. " + (err instanceof Error ? err.message : ""));
        setStage("paused");
      }
    }
  }, [stage, transcript, transcribeCurrentSegment, armReadyBackstop, clearReadyBackstop]);

  const commit = useCallback(
    async (action: (text: string, spokenDeliveryId?: string) => void) => {
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
        // The claim survives only if this transcription IS the whole text: nothing committed before
        // it, and nothing typed into the box.
        const wasTheOnlySegment = accumulatedRef.current.length === 0 && transcript.length === 0;
        accumulatedRef.current = joinText(accumulatedRef.current, segment.text);
        spokenDeliveryRef.current = wasTheOnlySegment ? segment.deliveryId : null;
        if (segment.lossWarning !== null) {
          // The capture dropped audio, so this text may be missing words. Committing it silently
          // (worst of all straight into a session via Send) would hide the loss - park in PAUSED
          // with the warning showing so the user reviews or re-dictates, then commits by hand.
          setTranscript(accumulatedRef.current);
          setHint("");
          setStage("paused");
          busyRef.current = false;
          return;
        }
        busyRef.current = false;
        action(accumulatedRef.current.trim(), spokenDeliveryRef.current ?? undefined);
        onClose();
      } else if (stage === "paused") {
        // The PAUSED box is editable, so the claim only stands if what is in it is still exactly the
        // text the transcription produced.
        const untouched = transcript === accumulatedRef.current;
        action(transcript.trim(), untouched ? (spokenDeliveryRef.current ?? undefined) : undefined);
        onClose();
      }
    },
    [stage, transcript, transcribeCurrentSegment, onClose],
  );

  // Send. The whole point of this handler (mobile Speak should not hold the screen): when we are
  // still RECORDING and the host wired the fire-and-forget path (onSendAudio), grab the captured audio
  // buffer and hand it up, then close IMMEDIATELY - the host transcribes + submits in the background
  // and marks the session "Transcribing...". The user cannot see the transcript here anyway (Send
  // submits straight into the session), so waiting for it is pure dead time.
  //   * PAUSED: the text is already transcribed - submit it instantly, no audio round trip.
  //   * RECORDING without onSendAudio (Voice-mode reply panel): fall back to the blocking
  //     transcribe-then-submit commit so that surface is unchanged.
  const onSendClick = useCallback(async () => {
    if (busyRef.current) return;
    if (stage === "paused") {
      // Same rule as the commit path: an edited box is no longer a transcription.
      const untouched = transcript === accumulatedRef.current;
      onSend(transcript.trim(), untouched ? (spokenDeliveryRef.current ?? undefined) : undefined);
      onClose();
      return;
    }
    if (stage !== "recording") return;
    if (!onSendAudio) {
      await commit(onSend);
      return;
    }
    // The Send time (voice delivery, #3398), stamped at the press and before stopping the recorder, which
    // awaits. The Gateway measures its 5-minute age rule from this moment.
    const sentAt = Date.now();
    busyRef.current = true;
    elapsedBeforeRef.current += performance.now() - segmentStartRef.current;
    let captured: Blob;
    try {
      captured = await recorderRef.current.stop();
    } catch (err) {
      // Could not finalize the mic - keep the user's audio session alive in PAUSED rather than
      // dropping it, and surface why. (No text is lost; nothing was committed.)
      setHint(err instanceof Error ? err.message : "Could not capture the recording.");
      setStage("paused");
      busyRef.current = false;
      return;
    }
    onSendAudio({
      sentAt,
      deviceLabel: recorderRef.current.deviceLabel,
      deviceId: recorderRef.current.deviceId,
      blob: captured,
      recordedMs: recorderRef.current.lastRecordedMs,
      prefixText: accumulatedRef.current,
      surface,
    });
    onClose();
  }, [stage, transcript, onSend, onSendAudio, onClose, commit, surface]);

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
  const isConnecting = stage === "connecting";
  // Nothing is captured yet while GETTING READY (and nothing is mid-transcription), so the
  // commit/checkpoint actions are not actionable then.
  const actionsDisabled = isTranscribing || isConnecting;
  const statusLabel =
    stage === "connecting"
      ? "GETTING READY"
      : stage === "recording"
        ? "RECORDING"
        : stage === "transcribing"
          ? "TRANSCRIBING"
          : stage === "paused"
            ? "PAUSED"
            : "ERROR";

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

        {/* The LIVE alarm sits above the sticky one: it is about the microphone right now, and it is
            the only one the user can still act on. */}
        {liveWarning !== "" && !isError && (
          <div className="dictate-warning dictate-warning-live" role="alert">{liveWarning}</div>
        )}

        {captureWarning !== "" && !isError && (
          <div className="dictate-warning" role="alert">{captureWarning}</div>
        )}

        {isError ? (
          <div className="dictate-error" role="alert">{errorText}</div>
        ) : (
          <textarea
            className="dictate-transcript"
            value={isPaused ? transcript : accumulatedRef.current}
            readOnly={!isPaused}
            onChange={(e) => {
              // Typing here makes the words no longer purely a transcription, so the spoken claim
              // goes (R10). Dropped on the FIRST keystroke rather than compared at Send: an edit that
              // happens to restore the original text is still the person composing.
              spokenDeliveryRef.current = null;
              setTranscript(e.target.value);
            }}
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
              disabled={actionsDisabled}
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
                  disabled={actionsDisabled}
                >
                  Insert
                </button>
              )}
              <button
                type="button"
                className="dictate-btn dictate-send"
                onClick={onSendClick}
                disabled={actionsDisabled}
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
