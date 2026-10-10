import { useCallback, useEffect, useRef, useState } from "react";
import {
  getSpokenLanguage,
  setSpokenLanguage,
  setSpokenVoice,
  type SpokenLanguageSnapshot,
} from "../api/language";
import { ttsSample } from "../api/ai";
import { ACCOUNT_SCOPE, CardHead, errText } from "./settingsShared";
import "./settings.css";

// ---- "Language" tab: the language DevThrottle talks back to you in (issue #1010) -------------------
//
// It takes the place of the AI tab in the strip. Shared by the Cockpit and the phone, like every other
// settings card, so the two surfaces cannot offer different languages or different voices.
//
// THIS COMPONENT DECIDES NOTHING. One GET returns the whole screen already folded - which voices each
// language offers, what each voice is called, the word under each choice, the sample sentence in that
// language, and the voice the account will ACTUALLY be spoken with - and every write returns the same
// document, which simply replaces the state. There is no filtering here, no label assembly, no "which
// voice should I switch to now", and no restore step.
//
// That is not tidiness; it is the fix for two of the four defects that got the last attempt reverted
// (devthrottle_internal#547). The reverted build decided the consequences of a language IN THE BROWSER
// from the model catalog, which the hosted Gateway refuses to serve, so the client held an empty list and
// the guard that was supposed to fire never fired. And its sample played what the PAGE believed rather
// than what the ACCOUNT was set to, which is indistinguishable from the setting doing nothing.
//
// THE VOICE CONTROL STAYS VISIBLE IN EVERY LANGUAGE, including French, where the whole list is one entry
// (Kokoro ships exactly one French voice). A control that vanishes between languages reads as a glitch.
//
// AND NOTHING HERE NAMES A SPEECH MODEL. Choosing a language switched the speech engine last time, and
// that engine could not say the lengths this product writes: French returned silence at 155 characters,
// Spanish blew a sixty-second deadline at 208, and the wingman is tuned to write about 500. French and
// Spanish are voices inside the one engine that already serves English. The sample below deliberately
// sends an EMPTY model so the Gateway uses the account's own engine - the audition cannot be of anything
// but the real thing.

export function LanguageTab() {
  const [snap, setSnap] = useState<SpokenLanguageSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");
  const [sampleMsg, setSampleMsg] = useState("");

  const audioRef = useRef<HTMLAudioElement | null>(null);
  const busyRef = useRef(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      setError(null);
      setSnap(await getSpokenLanguage(signal));
    } catch (e) {
      if (signal?.aborted) return;
      setError(errText(e, "load the spoken language"));
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  // One save at a time, and the whole document is replaced by what the Gateway returns. Two overlapping
  // writes would otherwise let the slower one report a state the faster one had already superseded -
  // and here the second write is a VOICE, so the loser would be a screen showing a voice the account is
  // not being spoken with.
  const runSave = async (apply: () => Promise<{ snapshot: SpokenLanguageSnapshot; said: string }>) => {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy(true);
    setMsg("Saving...");
    setSampleMsg("");
    try {
      const { snapshot, said } = await apply();
      setSnap(snapshot);
      setMsg(said);
    } catch (e) {
      setMsg(errText(e, "save the spoken language"));
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  };

  if (error !== null) {
    return <div className="settings-error">Could not load the language setting: {error}</div>;
  }
  if (snap === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  // The selected language's own row. The Gateway puts the chosen code on the document and lists every
  // language, so this is a lookup rather than a decision. A code with no row would mean the Gateway sent
  // a language it does not itself offer, which is a Gateway bug and must not be papered over with a
  // guessed row - so the screen says so instead.
  const chosen = snap.languages.find((l) => l.code === snap.language);
  if (chosen === undefined) {
    return (
      <div className="settings-error">
        The Gateway reported language &quot;{snap.language}&quot;, which is not one of the languages it
        offers. Nothing has been changed. Please report this.
      </div>
    );
  }

  const chooseLanguage = (code: string) => {
    if (busy || code === snap.language) return;
    void runSave(async () => {
      const snapshot = await setSpokenLanguage(code);
      const now = snapshot.languages.find((l) => l.code === snapshot.language);
      return {
        snapshot,
        said: `DevThrottle now speaks ${now === undefined ? snapshot.language : now.label} to you.`,
      };
    });
  };

  const chooseVoice = (voice: string) => {
    if (busy || voice === snap.voice) return;
    void runSave(async () => {
      // The language is sent with it, so the choice is recorded against the language it was made for.
      const snapshot = await setSpokenVoice(snap.language, voice);
      const option = snapshot.languages
        .find((l) => l.code === snapshot.language)
        ?.voices.find((v) => v.id === snapshot.voice);
      return { snapshot, said: `Voice set to ${option === undefined ? snapshot.voice : option.label}.` };
    });
  };

  const playSample = async () => {
    setBusy(true);
    setSampleMsg("Synthesizing...");
    try {
      // An EMPTY model on purpose: the Gateway then uses the account's own speech engine, so this
      // auditions what the sessions will really sound like. The voice is the account's effective voice,
      // as the Gateway resolved it - not one this page picked.
      const blob = await ttsSample(chosen.sample, "", snap.voice);
      if (audioRef.current === null) audioRef.current = new Audio();
      audioRef.current.src = URL.createObjectURL(blob);
      audioRef.current.onended = () => setSampleMsg("");
      setSampleMsg("Playing...");
      await audioRef.current.play();
    } catch (e) {
      setSampleMsg(errText(e, "play the language sample"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="settings-card">
      <CardHead title="Language" scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">
        DevThrottle talks back to you out loud - in voice mode on your phone, and in the Cockpit. Choose
        the language it speaks.
      </p>

      <div className="settings-field">
        <div className="settings-langs" role="radiogroup" aria-label="Spoken language">
          {snap.languages.map((l) => (
            <label className="settings-lang" key={l.code}>
              <input
                type="radio"
                name="spoken-language"
                value={l.code}
                checked={snap.language === l.code}
                disabled={busy}
                onChange={() => chooseLanguage(l.code)}
              />
              <span className="settings-lang-text">
                <span className="settings-lang-name">{l.label}</span>
                <span className="settings-lang-note">{l.note}</span>
              </span>
            </label>
          ))}
        </div>
      </div>

      <div className="settings-field">
        <label htmlFor="settings-language-voice">Voice</label>
        <select
          id="settings-language-voice"
          className="settings-select"
          value={snap.voice}
          disabled={busy}
          onChange={(e) => chooseVoice(e.target.value)}
        >
          {chosen.voices.map((v) => (
            <option key={v.id} value={v.id}>
              {v.label}
            </option>
          ))}
        </select>
        <div className="settings-actions">
          <button type="button" className="settings-btn" disabled={busy} onClick={() => void playSample()}>
            Play sample
          </button>
          <span className="settings-sample">&quot;{chosen.sample}&quot;</span>
        </div>
        {sampleMsg !== "" && <span className="settings-inline-msg">{sampleMsg}</span>}
      </div>

      <hr className="settings-sep" />
      {/* This line exists to stop somebody hunting for a setting that should not exist. Transcription sends
          no language field and the provider detects the language itself, so French and Spanish dictation
          already work and always did (devthrottle_internal#547, section 4). */}
      <p className="settings-hint settings-hint-inline">
        Typing and dictation are unaffected. Dictation already understands all three languages on its own -
        there is nothing to set.
      </p>

      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}
