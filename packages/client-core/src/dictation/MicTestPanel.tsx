import { useCallback, useEffect, useRef, useState } from "react";
import { MicRecorder, startRecorderWithTimeout } from "./recorder";
import { blobToWav16kMono, decodeToMono } from "./wav";
import { checkMicQuality, formatDb, type MicQualityVerdict } from "./micQuality";
import { uploadVoiceTestClip } from "./voiceTestClient";
import { reportShownError } from "../errors/reportClientError";
import "./mictest.css";

// The "Test microphone" check, shared verbatim by the Cockpit dictation health page and the mobile
// app so both surfaces give identical readings and identical advice (one fold, two renderers).
//
// It answers the question a user actually has when dictation keeps coming out as nonsense: "is it me,
// the software, or my headset?" It records a short clip through THE SAME capture path dictation uses,
// plays it straight back so the user can hear what the transcriber hears, and measures the three
// defects that break transcription (micQuality.ts).
//
// The measurement and the verdict are computed entirely in the browser, so the check works and reports
// even when the Gateway cannot be reached. The clip and its measurements are then sent to the Gateway
// and kept per tenant, so microphone problems can be studied across real hardware rather than one
// person's anecdote - see VoiceTestClipStore for what is kept and for how long. That send happens
// after the verdict is already on screen and never blocks it.

// Bars in the live level meter, matching the dictation dialog's equalizer so the two feel like the
// same microphone.
const BAR_COUNT = 9;

// Below this there is not enough speech to measure honestly - too few frames for a stable noise floor
// or a stable spectrum. We ask for more rather than reporting a shaky verdict.
const MIN_USEFUL_SECONDS = 1.5;

// Hard stop, so a forgotten test cannot record forever.
const MAX_SECONDS = 30;

// The sentence we ask people to read. Not decorative: it is loaded with /s/ and /sh/ sounds, whose
// energy sits in the 4.5-8 kHz band the narrowband check reads. A sentence without them would leave
// that band empty for an honest reason and make a perfectly good microphone look band-limited.
const PROMPT_SENTENCE = "The six sisters shared fresh fish and crisp chips at sunset.";

type Stage = "idle" | "recording" | "analyzing" | "done" | "error";

export interface MicTestPanelProps {
  /** Extra class on the root, so a host page can scope its own layout around the panel. */
  className?: string;
}

export function MicTestPanel({ className }: MicTestPanelProps) {
  const [stage, setStage] = useState<Stage>("idle");
  const [level, setLevel] = useState(0);
  const [elapsed, setElapsed] = useState(0);
  const [verdict, setVerdict] = useState<MicQualityVerdict | null>(null);
  const [playbackUrl, setPlaybackUrl] = useState<string | null>(null);
  const [errorText, setErrorText] = useState("");
  const [tooShort, setTooShort] = useState(false);

  const recorderRef = useRef<MicRecorder | null>(null);
  const frameRef = useRef<number | null>(null);
  const capRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const startedAtRef = useRef(0);
  // Held in a ref as well as state so cleanup can revoke the URL without re-running on every render.
  const playbackUrlRef = useRef<string | null>(null);

  const releasePlayback = useCallback(() => {
    if (playbackUrlRef.current !== null) {
      URL.revokeObjectURL(playbackUrlRef.current);
      playbackUrlRef.current = null;
    }
    setPlaybackUrl(null);
  }, []);

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

  // Release the microphone and the playback URL if the panel goes away mid-test - a page change must
  // never leave the microphone light on.
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
    setStage("analyzing");
    setLevel(0);

    try {
      const captured = await recorder.stop();
      recorderRef.current = null;

      // Measure at the microphone's native rate (the resampled 16 kHz clip cannot answer the
      // bandwidth question), but play back the 16 kHz WAV, because that is literally the audio the
      // transcription model receives. Hearing the real thing is the point of the playback.
      const clip = await decodeToMono(captured);
      if (clip.samples.length / clip.sampleRate < MIN_USEFUL_SECONDS) {
        setTooShort(true);
        setStage("idle");
        return;
      }

      const result = checkMicQuality(clip.samples, clip.sampleRate);
      const { wav } = await blobToWav16kMono(captured);

      releasePlayback();
      const url = URL.createObjectURL(wav);
      playbackUrlRef.current = url;
      setPlaybackUrl(url);
      setVerdict(result);
      setStage("done");

      // Keep the clip and its measurements on the Gateway so microphone problems can be studied
      // across real hardware. Deliberately AFTER the verdict is on screen and deliberately not
      // awaited into the result: this is our analysis, not the user's answer, and a Gateway that is
      // unreachable must not turn a perfectly good microphone check into an error.
      void uploadVoiceTestClip({ kind: "microphone", audio: wav, quality: result.report }).catch((err: unknown) => {
        console.warn(`[MicTestPanel] could not store the test clip: ${err instanceof Error ? err.message : String(err)}`);
      });
    } catch (err) {
      setErrorText(reportShownError("mic-test", "test the microphone", err instanceof Error ? err.message : String(err), undefined, err));
      setStage("error");
    }
  }, [clearCap, releasePlayback, stopMeter]);

  // finish() is recreated on each render; the meter loop needs the latest one without restarting.
  const finishRef = useRef(finish);
  finishRef.current = finish;

  const start = useCallback(async () => {
    setTooShort(false);
    setErrorText("");
    setVerdict(null);
    releasePlayback();
    // Never leave a previous run's cap armed - it would cut this recording short.
    stopMeter();
    clearCap();

    const recorder = new MicRecorder();
    recorderRef.current = recorder;
    try {
      await startRecorderWithTimeout(recorder);
    } catch (err) {
      recorderRef.current = null;
      // No silent fallback: say exactly what failed and what to check.
      const reason = err instanceof Error ? err.message : String(err);
      setErrorText(
        reportShownError(
          "mic-test",
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

    // The hard stop runs on a TIMER, not on the animation frame below. A browser suspends animation
    // frames entirely in a hidden tab - which is exactly what happens when someone starts the test on
    // their phone and switches app - and a cap that rode on them would simply never fire, leaving the
    // microphone open indefinitely. Timers are throttled in the background but they still arrive.
    capRef.current = setTimeout(() => void finishRef.current(), MAX_SECONDS * 1000);

    // The meter and timer are display only, so animation frames are the right vehicle: they stop when
    // nobody is looking and resume when the user comes back.
    const tick = () => {
      const active = recorderRef.current;
      if (active === null) return;
      setLevel(active.level());
      setElapsed((performance.now() - startedAtRef.current) / 1000);
      frameRef.current = requestAnimationFrame(tick);
    };
    frameRef.current = requestAnimationFrame(tick);
  }, [clearCap, releasePlayback, stopMeter]);

  const cancel = useCallback(() => {
    stopMeter();
    clearCap();
    recorderRef.current?.dispose();
    recorderRef.current = null;
    setLevel(0);
    setStage("idle");
  }, [clearCap, stopMeter]);

  return (
    <div className={className ? `mictest ${className}` : "mictest"}>
      <div className="mictest-head">
        <h2>Test your microphone</h2>
      </div>
      <p className="mictest-lede">
        Poor dictation is usually a poor microphone, not a poor model. Record yourself for a few
        seconds, listen back to exactly what the transcriber hears, and see what we measure. The
        recording is kept on your Gateway so microphone problems can be improved.
      </p>

      {stage === "idle" && (
        <div className="mictest-idle">
          <p className="mictest-prompt-label">When you start, read this out loud at your normal volume:</p>
          <p className="mictest-prompt">&ldquo;{PROMPT_SENTENCE}&rdquo;</p>
          {tooShort && (
            <p className="mictest-warn">
              That was too short to measure. Record for at least a couple of seconds and read the whole
              sentence.
            </p>
          )}
          <button type="button" className="mictest-primary" onClick={() => void start()}>
            Start recording
          </button>
        </div>
      )}

      {stage === "recording" && (
        <div className="mictest-recording">
          <p className="mictest-prompt">&ldquo;{PROMPT_SENTENCE}&rdquo;</p>
          <div className="mictest-meter" role="img" aria-label="Microphone input level">
            {Array.from({ length: BAR_COUNT }, (_, i) => (
              <span
                key={i}
                className={level * BAR_COUNT > i ? "mictest-bar mictest-bar-on" : "mictest-bar"}
              />
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
        </div>
      )}

      {stage === "analyzing" && <div className="mictest-loading">Listening to your recording...</div>}

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
          <div className={`mictest-banner mictest-${verdict.rating}`}>{verdict.headline}</div>

          {playbackUrl !== null && (
            <div className="mictest-playback">
              <div className="mictest-playback-label">This is exactly what the transcriber hears:</div>
              <audio className="mictest-audio" src={playbackUrl} controls preload="auto" />
            </div>
          )}

          {verdict.issues.length > 0 && (
            <ul className="mictest-issues">
              {verdict.issues.map((issue) => (
                <li key={issue.id} className={`mictest-issue mictest-issue-${issue.severity}`}>
                  <div className="mictest-issue-title">{issue.title}</div>
                  <div className="mictest-issue-advice">{issue.advice}</div>
                </li>
              ))}
            </ul>
          )}

          <MicMeasurements verdict={verdict} />

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

/** The raw numbers behind the verdict. Shown so the reading can be checked rather than trusted. */
function MicMeasurements({ verdict }: { verdict: MicQualityVerdict }) {
  const r = verdict.report;
  if (!r.heardSpeech) return null;

  return (
    <details className="mictest-details">
      <summary>What we measured</summary>
      <dl className="mictest-measurements">
        <div>
          <dt>Your voice</dt>
          <dd>{formatDb(r.speechLevelDb)}</dd>
        </div>
        <div>
          <dt>Background noise</dt>
          <dd>{formatDb(r.noiseFloorDb)}</dd>
        </div>
        <div>
          <dt>Voice above noise</dt>
          <dd>{r.signalToNoiseDb.toFixed(0)} dB</dd>
        </div>
        <div>
          <dt>Distorted audio</dt>
          <dd>{(r.clippedFraction * 100).toFixed(2)}%</dd>
        </div>
        <div>
          <dt>Audio bandwidth</dt>
          <dd>{r.narrowband ? "Telephone quality" : "Full bandwidth"}</dd>
        </div>
        <div>
          <dt>Sample rate</dt>
          <dd>{(r.sampleRate / 1000).toFixed(1)} kHz</dd>
        </div>
      </dl>
    </details>
  );
}
