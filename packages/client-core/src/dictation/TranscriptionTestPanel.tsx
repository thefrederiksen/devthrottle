import { useCallback, useEffect, useRef, useState } from "react";
import { MicRecorder, startRecorderWithTimeout } from "./recorder";
import { blobToWav16kMono } from "./wav";
import { judgeAccuracy, scoreTranscription, type AccuracyVerdict, type DiffStep } from "./transcriptionAccuracy";
import { preferredLanguage, TEST_LANGUAGES, type TestLanguage } from "./languages";
import { uploadVoiceTestClip } from "./voiceTestClient";
import { reportShownError } from "../errors/reportClientError";
import "./mictest.css";

// The "Test transcription" check, shared verbatim by the Cockpit dictation health page and the mobile
// app.
//
// The microphone check answers "is my audio any good". This answers the question that actually sends
// people looking: "how much of what I say does it get right?" It puts a passage on screen, records the
// user reading it, transcribes it through the real Gateway path, and scores the result against the
// passage - so the answer is a percentage and a list of the exact words that were missed, rather than
// an impression.
//
// It runs in the languages DevThrottle officially supports - and only those - because the answer is
// not the same in all of them and we cannot know where it is weak without measuring. Testing a
// language we do not ship would invite someone to read a poor score as a defect. The clip, the
// passage and the transcript are stored on the Gateway, per tenant, so those comparisons can be made
// later across headsets, languages and releases.

const BAR_COUNT = 9;
const MAX_SECONDS = 90;

// The passages take 15-25 seconds to read, so a recording this short cannot contain one. Scoring it
// anyway would report "none of the passage came back correctly" and send the reader off to check
// their headset, when all they did was stop early - the confident wrong diagnosis this whole feature
// exists to avoid. Ask again instead.
const MIN_USEFUL_MS = 3000;

type Stage = "idle" | "recording" | "transcribing" | "done" | "error";

export interface TranscriptionTestPanelProps {
  className?: string;
}

export function TranscriptionTestPanel({ className }: TranscriptionTestPanelProps) {
  const [language, setLanguage] = useState<TestLanguage>(() =>
    preferredLanguage(typeof navigator === "undefined" ? [] : navigator.languages ?? [navigator.language]),
  );
  const [stage, setStage] = useState<Stage>("idle");
  const [level, setLevel] = useState(0);
  const [elapsed, setElapsed] = useState(0);
  const [verdict, setVerdict] = useState<AccuracyVerdict | null>(null);
  const [transcript, setTranscript] = useState("");
  const [playbackUrl, setPlaybackUrl] = useState<string | null>(null);
  const [errorText, setErrorText] = useState("");
  const [tooShort, setTooShort] = useState(false);

  const recorderRef = useRef<MicRecorder | null>(null);
  const frameRef = useRef<number | null>(null);
  const capRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const startedAtRef = useRef(0);
  const playbackUrlRef = useRef<string | null>(null);
  // The language at the moment recording started. Read in finish() so switching the picker mid-record
  // cannot score a Danish reading against the Spanish passage.
  const recordingLanguageRef = useRef(language);

  const stopMeter = useCallback(() => {
    if (frameRef.current !== null) {
      cancelAnimationFrame(frameRef.current);
      frameRef.current = null;
    }
  }, []);

  const clearCap = useCallback(() => {
    if (capRef.current !== null) {
      clearTimeout(capRef.current);
      capRef.current = null;
    }
  }, []);

  const releasePlayback = useCallback(() => {
    if (playbackUrlRef.current !== null) {
      URL.revokeObjectURL(playbackUrlRef.current);
      playbackUrlRef.current = null;
    }
    setPlaybackUrl(null);
  }, []);

  useEffect(() => {
    return () => {
      stopMeter();
      if (capRef.current !== null) clearTimeout(capRef.current);
      recorderRef.current?.dispose();
      recorderRef.current = null;
      if (playbackUrlRef.current !== null) URL.revokeObjectURL(playbackUrlRef.current);
    };
  }, [stopMeter]);

  const finish = useCallback(async () => {
    const recorder = recorderRef.current;
    if (recorder === null) return;
    stopMeter();
    clearCap();
    setStage("transcribing");
    setLevel(0);

    const spoken = recordingLanguageRef.current;
    try {
      const captured = await recorder.stop();
      recorderRef.current = null;

      if (recorder.lastRecordedMs > 0 && recorder.lastRecordedMs < MIN_USEFUL_MS) {
        setTooShort(true);
        setStage("idle");
        return;
      }

      const { wav } = await blobToWav16kMono(captured);

      // Play back the same WAV that was sent, so "what it heard" is not a claim.
      releasePlayback();
      const url = URL.createObjectURL(wav);
      playbackUrlRef.current = url;
      setPlaybackUrl(url);

      const result = await uploadVoiceTestClip({
        kind: "transcription",
        audio: wav,
        language: spoken.code,
        expected: spoken.passage,
      });

      setTranscript(result.transcript);
      setVerdict(judgeAccuracy(scoreTranscription(spoken.passage, result.transcript, spoken.tokenMode), spoken.name));
      setStage("done");
    } catch (err) {
      setErrorText(reportShownError("transcription-test", "test the transcription", err instanceof Error ? err.message : String(err), undefined, err));
      setStage("error");
    }
  }, [clearCap, releasePlayback, stopMeter]);

  const finishRef = useRef(finish);
  finishRef.current = finish;

  const start = useCallback(async () => {
    setErrorText("");
    setTooShort(false);
    setVerdict(null);
    setTranscript("");
    releasePlayback();
    stopMeter();
    clearCap();
    recordingLanguageRef.current = language;

    const recorder = new MicRecorder();
    recorderRef.current = recorder;
    try {
      await startRecorderWithTimeout(recorder);
    } catch (err) {
      recorderRef.current = null;
      const reason = err instanceof Error ? err.message : String(err);
      setErrorText(
        reportShownError(
          "transcription-test",
          "open the microphone",
          `The microphone could not be opened: ${reason} Check that a microphone is connected, that it ` +
            "is not muted, and that this site is allowed to use it.",
          undefined,
          err,
        ),
      );
      setStage("error");
      return;
    }

    startedAtRef.current = performance.now();
    setElapsed(0);
    setStage("recording");
    // On a timer, not an animation frame: a hidden tab suspends frames entirely and the cap would
    // never fire, leaving the microphone open.
    capRef.current = setTimeout(() => void finishRef.current(), MAX_SECONDS * 1000);

    const tick = () => {
      const active = recorderRef.current;
      if (active === null) return;
      setLevel(active.level());
      setElapsed((performance.now() - startedAtRef.current) / 1000);
      frameRef.current = requestAnimationFrame(tick);
    };
    frameRef.current = requestAnimationFrame(tick);
  }, [clearCap, language, releasePlayback, stopMeter]);

  const cancel = useCallback(() => {
    stopMeter();
    clearCap();
    recorderRef.current?.dispose();
    recorderRef.current = null;
    setLevel(0);
    setStage("idle");
  }, [clearCap, stopMeter]);

  const busy = stage === "recording" || stage === "transcribing";

  return (
    <div className={className ? `mictest ${className}` : "mictest"}>
      <div className="mictest-head">
        <h2>Test transcription</h2>
      </div>
      <p className="mictest-lede">
        Read a passage out loud and see how much of it comes back correctly. This runs through the same
        transcription your dictation uses, so the score is the real one. The recording and the result
        are kept on your Gateway so transcription can be improved.
      </p>

      <div className="mictest-langrow">
        <label className="mictest-langlabel" htmlFor="mictest-language">
          Language
        </label>
        <select
          id="mictest-language"
          className="mictest-langselect"
          value={language.code}
          disabled={busy}
          onChange={(e) => setLanguage(TEST_LANGUAGES.find((l) => l.code === e.target.value) ?? TEST_LANGUAGES[0])}
        >
          {TEST_LANGUAGES.map((l) => (
            <option key={l.code} value={l.code}>
              {l.nativeName} ({l.name})
            </option>
          ))}
        </select>
      </div>

      {(stage === "idle" || stage === "recording") && (
        <>
          <p className="mictest-prompt-label">
            {stage === "idle" ? "When you start, read this out loud at your normal pace:" : "Read this out loud:"}
          </p>
          <p className="mictest-prompt mictest-passage" lang={language.code}>
            {language.passage}
          </p>
        </>
      )}

      {stage === "idle" && (
        <>
          {tooShort && (
            <p className="mictest-warn">
              That was too short to score. Record again and read the whole passage out loud.
            </p>
          )}
          <button type="button" className="mictest-primary" onClick={() => void start()}>
            Start recording
          </button>
        </>
      )}

      {stage === "recording" && (
        <>
          <div className="mictest-meter" role="img" aria-label="Microphone input level">
            {Array.from({ length: BAR_COUNT }, (_, i) => (
              <span key={i} className={level * BAR_COUNT > i ? "mictest-bar mictest-bar-on" : "mictest-bar"} />
            ))}
          </div>
          <div className="mictest-timer">{elapsed.toFixed(1)}s</div>
          <div className="mictest-actions">
            <button type="button" className="mictest-primary" onClick={() => void finish()}>
              I&apos;m finished
            </button>
            <button type="button" className="mictest-secondary" onClick={cancel}>
              Cancel
            </button>
          </div>
        </>
      )}

      {stage === "transcribing" && <div className="mictest-loading">Transcribing what you said...</div>}

      {stage === "error" && (
        <div className="mictest-error">
          <p>{errorText}</p>
          <button type="button" className="mictest-primary" onClick={() => void start()}>
            Try again
          </button>
        </div>
      )}

      {stage === "done" && verdict !== null && (
        <div className="mictest-result">
          <div className={`mictest-banner mictest-acc-${verdict.rating}`}>{verdict.headline}</div>
          <p className="mictest-lede">{verdict.detail}</p>

          {playbackUrl !== null && (
            <div className="mictest-playback">
              <div className="mictest-playback-label">What the transcriber heard:</div>
              <audio className="mictest-audio" src={playbackUrl} controls preload="auto" />
            </div>
          )}

          <div className="mictest-diffblock">
            <div className="mictest-playback-label">
              What came back, with the differences marked:
            </div>
            <DiffView diff={verdict.result.diff} lang={language.code} />
            <dl className="mictest-measurements">
              <div>
                <dt>Correct</dt>
                <dd>{verdict.result.correct}</dd>
              </div>
              <div>
                <dt>Misheard</dt>
                <dd>{verdict.result.substitutions}</dd>
              </div>
              <div>
                <dt>Missed</dt>
                <dd>{verdict.result.deletions}</dd>
              </div>
              <div>
                <dt>Added</dt>
                <dd>{verdict.result.insertions}</dd>
              </div>
            </dl>
          </div>

          <details className="mictest-details">
            <summary>The raw transcript</summary>
            <p className="mictest-rawtranscript" lang={language.code}>
              {transcript === "" ? "(nothing came back)" : transcript}
            </p>
          </details>

          <div className="mictest-actions">
            <button type="button" className="mictest-primary" onClick={() => void start()}>
              Record again
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * The aligned passage, with each token marked by what happened to it. This is the part people actually
 * act on: a percentage says how bad it is, but only the specific misheard words tell you whether the
 * problem is your microphone, your accent, or a term that belongs in your dictionary.
 */
function DiffView({ diff, lang }: { diff: DiffStep[]; lang: string }) {
  if (diff.length === 0) return <p className="mictest-diff">(nothing to compare)</p>;
  return (
    <p className="mictest-diff" lang={lang}>
      {diff.map((step, i) => {
        if (step.op === "equal") {
          return (
            <span key={i} className="mictest-tok mictest-tok-ok">
              {step.expected}
            </span>
          );
        }
        if (step.op === "substitute") {
          return (
            <span key={i} className="mictest-tok mictest-tok-sub" title={`You said "${step.expected}"`}>
              {step.actual}
            </span>
          );
        }
        if (step.op === "delete") {
          return (
            <span key={i} className="mictest-tok mictest-tok-del" title="This word was missed">
              {step.expected}
            </span>
          );
        }
        return (
          <span key={i} className="mictest-tok mictest-tok-ins" title="This word was not in the passage">
            {step.actual}
          </span>
        );
      })}
    </p>
  );
}
