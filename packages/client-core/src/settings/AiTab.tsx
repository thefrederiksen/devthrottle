import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  type AiModel,
  type AiProviderSnapshot,
  getAiModels,
  getAiProvider,
  setTtsModel,
  setTtsVoice,
  setWingmanFastModel,
  setWingmanModel,
  testChat,
  ttsSample,
} from "../api/ai";
import { ACCOUNT_SCOPE, CardHead, ensureIds, ensureStrings, errText } from "./settingsShared";
import "./settings.css";

// ---- "AI" tab: what the fleet thinks and speaks with -----------------------------------------------
//
// The hosted provider, the two thinking models with their live Tests, the speech model, and the voice.
// Shared by the Cockpit and the phone.
//
// What is NOT here: the Car Mode model (it went to the Car Mode tab, with the rest of Car Mode), the
// microphone and transcription checks (they went to the Transcription tab), and the spoken language -
// that feature was removed from the product entirely (pull request 2181: the multilingual speech engine
// could not reliably say narrations of the length this product writes). The product speaks English, so
// there is one sample sentence.
//
// The phone used to carry the Car Mode model on this screen and the desktop did not - the kind of split
// that is exactly why the two surfaces now render one component.
const SAMPLE_TEXT = "Hi, I'm your DevThrottle wingman. This is how I'll sound.";

export interface AiTabProps {
  /** Where "Manage account" points, when the surface has an account page. The phone has none, so it
   *  passes nothing and the line is simply not rendered - never a link to a route that does not exist. */
  accountHref?: string;
}

export function AiTab({ accountHref }: AiTabProps) {
  const [snap, setSnap] = useState<AiProviderSnapshot | null>(null);
  const [chatModels, setChatModels] = useState<AiModel[]>([]);
  const [speechModels, setSpeechModels] = useState<AiModel[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");
  const [testMsg, setTestMsg] = useState("");
  const [fastTestMsg, setFastTestMsg] = useState("");
  const [sampleMsg, setSampleMsg] = useState("");

  const audioRef = useRef<HTMLAudioElement | null>(null);

  const loadModels = useCallback(async () => {
    setChatModels(await getAiModels("chat"));
    setSpeechModels(await getAiModels("speech"));
  }, []);

  const load = useCallback(async () => {
    try {
      setError(null);
      const s = await getAiProvider();
      setSnap(s);
      // The live catalog + Test spend the shared provider credential and stay denied on the hosted Gateway
      // (issue #2022); the Gateway says so via catalogAvailable. Skip the catalog fetch there so the tab
      // renders clean with the account's saved selections instead of painting a load error.
      if (s.catalogAvailable !== false) await loadModels();
    } catch (e) {
      setError(errText(e, "load the AI settings"));
    }
  }, [loadModels]);

  useEffect(() => {
    void load();
  }, [load]);

  if (error !== null) {
    return <div className="settings-error">Could not load AI settings: {error}</div>;
  }
  if (snap === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  // Gateway-owned (issue #2022): whether the live catalog + Test are available. False on hosted, where model
  // browsing and testing are disabled with a concise explanation rather than offered as controls that fail.
  const catalogAvailable = snap.catalogAvailable !== false;
  const currentSpeech = speechModels.find((m) => m.id === snap.ttsModel);
  // An expressive model with no preset voices is not a model with a missing voice list - there is
  // nothing to choose. Showing an empty picker reads as broken, so the control is hidden instead.
  const modelHasVoices = !currentSpeech || currentSpeech.voices.length > 0;
  const voiceOptions = currentSpeech && currentSpeech.voices.length ? currentSpeech.voices : snap.voices;

  const chooseWingman = async (model: string) => {
    setBusy(true);
    setMsg("Saving...");
    setTestMsg("");
    try {
      await setWingmanModel(model);
      setSnap({ ...snap, wingmanModel: model });
      setMsg("Thinking model set. Test it to confirm.");
    } catch (e) {
      setMsg(errText(e, "set the thinking model"));
    } finally {
      setBusy(false);
    }
  };

  const runTest = async () => {
    setBusy(true);
    setTestMsg("Testing " + snap.wingmanModel + "...");
    const r = await testChat(snap.wingmanModel);
    setTestMsg(r.ok ? `OK - replied "${r.reply}" in ${r.seconds}s.` : "Failed: " + r.error);
    setBusy(false);
  };

  const chooseFastWingman = async (model: string) => {
    setBusy(true);
    setMsg("Saving...");
    setFastTestMsg("");
    try {
      await setWingmanFastModel(model);
      setSnap({ ...snap, wingmanFastModel: model });
      setMsg("Fast model set. Test it to confirm.");
    } catch (e) {
      setMsg(errText(e, "set the fast model"));
    } finally {
      setBusy(false);
    }
  };

  const runFastTest = async () => {
    setBusy(true);
    setFastTestMsg("Testing " + snap.wingmanFastModel + "...");
    const r = await testChat(snap.wingmanFastModel);
    setFastTestMsg(r.ok ? `OK - replied "${r.reply}" in ${r.seconds}s.` : "Failed: " + r.error);
    setBusy(false);
  };

  const chooseSpeech = async (model: string) => {
    setBusy(true);
    setMsg("Saving...");
    try {
      await setTtsModel(model);
      const sm = speechModels.find((m) => m.id === model);
      const voices = sm && sm.voices.length ? sm.voices : snap.voices;
      let voice = snap.ttsVoice;
      if (voices.indexOf(voice) < 0) {
        voice = sm && sm.defaultVoice && voices.indexOf(sm.defaultVoice) >= 0 ? sm.defaultVoice : voices[0] ?? voice;
        if (voice) await setTtsVoice(voice);
      }
      setSnap({ ...snap, ttsModel: model, ttsVoice: voice });
      setMsg("Speech model set.");
    } catch (e) {
      setMsg(errText(e, "set the speech model"));
    } finally {
      setBusy(false);
    }
  };

  const chooseVoice = async (voice: string) => {
    setBusy(true);
    setMsg("Saving...");
    try {
      await setTtsVoice(voice);
      setSnap({ ...snap, ttsVoice: voice });
      setMsg("Voice set to " + voice + ".");
    } catch (e) {
      setMsg(errText(e, "set the voice"));
    } finally {
      setBusy(false);
    }
  };

  const playSample = async () => {
    setBusy(true);
    setSampleMsg("Synthesizing...");
    try {
      // Audition the model and voice this page is SHOWING, so the sample matches what you just picked.
      const blob = await ttsSample(SAMPLE_TEXT, snap.ttsModel, snap.ttsVoice);
      if (audioRef.current === null) audioRef.current = new Audio();
      audioRef.current.src = URL.createObjectURL(blob);
      audioRef.current.onended = () => setSampleMsg("");
      setSampleMsg("Playing " + snap.ttsVoice + "...");
      await audioRef.current.play();
    } catch (e) {
      setSampleMsg(errText(e, "play the voice sample"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="settings-card">
      <CardHead title="AI" scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">
        DevThrottle hosts AI for this fleet: transcription, the wingman, and spoken voice all run on your
        DevThrottle account.
      </p>

      <div className="settings-provider-cards">
        <div className="settings-provider-card selected" aria-label="DevThrottle hosted AI">
          <span className="settings-provider-title">
            <span className="settings-provider-radio on" aria-hidden="true" />
            DevThrottle
            <span className="settings-provider-badge">Hosted</span>
          </span>
          <span className="settings-provider-desc">
            Hosted models on your DevThrottle account. Included with your account - transcription,
            the wingman, and voice.
          </span>
        </div>
      </div>

      {!catalogAvailable && (
        <p className="settings-hint settings-hint-inline">
          Model browsing and testing aren&apos;t available on the hosted Gateway yet. Your saved models are
          shown below; per-account model selection is coming to the hosted Gateway.
        </p>
      )}

      <div className="settings-field">
        <label htmlFor="settings-ai-model">Thinking model</label>
        <select
          id="settings-ai-model"
          className="settings-select"
          value={snap.wingmanModel}
          disabled={busy || !catalogAvailable}
          onChange={(e) => void chooseWingman(e.target.value)}
        >
          {ensureIds(snap.wingmanModel, chatModels).map((id) => (
            <option key={id} value={id}>
              {id}
            </option>
          ))}
        </select>
        <div className="settings-actions">
          <button
            type="button"
            className="settings-btn"
            disabled={busy || !catalogAvailable}
            onClick={() => void runTest()}
          >
            Test
          </button>
          <span className="settings-inline-msg">
            {testMsg || "Used for talk-to-the-wingman and product questions."}
          </span>
        </div>
      </div>

      <div className="settings-field">
        <label htmlFor="settings-ai-fast-model">Fast model</label>
        <select
          id="settings-ai-fast-model"
          className="settings-select"
          value={snap.wingmanFastModel}
          disabled={busy || !catalogAvailable}
          onChange={(e) => void chooseFastWingman(e.target.value)}
        >
          {ensureIds(snap.wingmanFastModel, chatModels).map((id) => (
            <option key={id} value={id}>
              {id}
            </option>
          ))}
        </select>
        <div className="settings-actions">
          <button
            type="button"
            className="settings-btn"
            disabled={busy || !catalogAvailable}
            onClick={() => void runFastTest()}
          >
            Test
          </button>
          <span className="settings-inline-msg">
            {fastTestMsg || "Used for spoken turn summaries, menus, and choice mapping."}
          </span>
        </div>
      </div>

      <div className="settings-field">
        <label htmlFor="settings-ai-ttsmodel">Speech model</label>
        <select
          id="settings-ai-ttsmodel"
          className="settings-select"
          value={snap.ttsModel}
          disabled={busy || !catalogAvailable}
          onChange={(e) => void chooseSpeech(e.target.value)}
        >
          {ensureIds(snap.ttsModel, speechModels).map((id) => (
            <option key={id} value={id}>
              {id}
            </option>
          ))}
        </select>
      </div>

      <div className="settings-field">
        {modelHasVoices ? (
          <>
            <label htmlFor="settings-ai-voice">Voice</label>
            <select
              id="settings-ai-voice"
              className="settings-select"
              value={snap.ttsVoice}
              disabled={busy}
              onChange={(e) => void chooseVoice(e.target.value)}
            >
              {ensureStrings(snap.ttsVoice, voiceOptions).map((v) => (
                <option key={v} value={v}>
                  {v}
                </option>
              ))}
            </select>
          </>
        ) : (
          <p className="settings-hint">
            This speech model has one expressive voice - there is nothing to choose.
          </p>
        )}
        <div className="settings-actions">
          <button type="button" className="settings-btn" disabled={busy} onClick={() => void playSample()}>
            Play sample
          </button>
          <span className="settings-inline-msg">{sampleMsg}</span>
        </div>
      </div>

      {accountHref !== undefined && (
        <p className="settings-hint settings-hint-inline">
          Hosted AI runs on your DevThrottle account. <Link to={accountHref}>Manage account</Link>.
        </p>
      )}

      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}
