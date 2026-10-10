import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { type AiProviderSnapshot, getAiProvider } from "../api/ai";
import { MicTestPanel } from "../dictation/MicTestPanel";
import { TranscriptionTestPanel } from "../dictation/TranscriptionTestPanel";
import { MicrophoneQualityPanel } from "../transcription/MicrophoneQualityPanel";
import { ACCOUNT_SCOPE, CardHead, Row, errText } from "./settingsShared";
import "./settings.css";

// ---- "Transcription" tab: everything about DevThrottle hearing you --------------------------------
//
// Its own tab because "dictation keeps coming out wrong" is its own problem, and the things that
// diagnose it were scattered: the transcription model was a one-line footnote on the AI screen, the two
// checks were a separate page on the desktop and two separate screens on the phone, and the background
// microphone measurements existed on the desktop only. A user with a bad headset had to already know
// all four places existed.
//
// The order is the order you should use them in: what is doing the transcribing, then what your
// microphones look like across all your real dictating (needs nothing from you), then the two on-demand
// checks (needs you to speak).
//
// The checks below are the SAME components the Cockpit's Transcription Health page mounts, so a phone
// and a desktop can never disagree about whether a headset is any good.

export interface TranscriptionTabProps {
  /** Where the full dictation health report lives, when the surface has one. The Cockpit passes its
   *  Transcription page; the phone has no such page and passes nothing, so no dead link is rendered. */
  healthHref?: string;
}

export function TranscriptionTab({ healthHref }: TranscriptionTabProps) {
  const [snap, setSnap] = useState<AiProviderSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setError(null);
      setSnap(await getAiProvider());
    } catch (e) {
      setError(errText(e, "load the transcription settings"));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <>
      <section className="settings-card">
        <CardHead title="Transcription" scope={ACCOUNT_SCOPE} />
        <p className="settings-hint">
          How DevThrottle turns your speech into text. Dictation understands every language automatically,
          whichever one you speak; DevThrottle speaks back to you in English.
        </p>

        {error !== null ? (
          <div className="settings-error">Could not load the transcription model: {error}</div>
        ) : snap === null ? (
          <p className="settings-loading">Loading...</p>
        ) : (
          <Row label="Model" value={snap.transcriptionModel} />
        )}

        {healthHref !== undefined && (
          <p className="settings-hint settings-hint-inline">
            For speed, failures and your most-corrected words over time, see{" "}
            <Link to={healthHref}>Transcription Health</Link>.
          </p>
        )}
      </section>

      {/* The background half first: it answers "is anything wrong" without the user doing anything,
          which is the question they arrived with. The on-demand checks below are what they reach for
          once they know something IS wrong. */}
      <section className="settings-card">
        <MicrophoneQualityPanel />
      </section>

      <section className="settings-card">
        <MicTestPanel />
      </section>

      <section className="settings-card">
        <TranscriptionTestPanel />
      </section>
    </>
  );
}
