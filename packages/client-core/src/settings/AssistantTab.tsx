import { useCallback, useEffect, useState } from "react";
import {
  type AiModel,
  type AiProviderSnapshot,
  getAiModels,
  getAiProvider,
  setCarModeModel,
} from "../api/ai";
import { ACCOUNT_SCOPE, CardHead, ensureIds, errText } from "./settingsShared";
import "./settings.css";

// ---- "Assistant" tab: the model the fleet brain thinks with ---------------------------------------
//
// The Assistant is the surface that talks to the whole fleet in conversation - fleet tools, a server-side
// conversation per device, one reply per turn. It runs on its OWN model, separate from the wingman's, because
// the job is different: it has to call tools reliably, and it has to be fast enough that a spoken question does
// not feel like a wait.
//
// This tab was called "Car Mode" and held two more things: the phrase that ended a hands-free turn, and a live
// tester for whether that phrase was heard. Car Mode was removed from the product and both went with it - they
// were about hands-free turn-taking and nothing else uses them. The MODEL stays, because it was never Car
// Mode's alone: the same setting has always driven the Assistant, which is exactly why it had to be settable
// from the desktop as well as from the phone.
//
// Shared by both surfaces, like every other settings card.

// The turn verdict switches that used to sit here moved to the Fleet Manager tab (the Fleet Manager mission,
// step 5): they are about the Wingman, which the Fleet Manager is built on, and this tab is hidden.
export function AssistantTab() {
  return <AssistantModelCard />;
}

function AssistantModelCard() {
  const [snap, setSnap] = useState<AiProviderSnapshot | null>(null);
  const [chatModels, setChatModels] = useState<AiModel[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");

  const load = useCallback(async () => {
    try {
      setError(null);
      const s = await getAiProvider();
      setSnap(s);
      // The model catalog stays denied on hosted (issue #2022); skip it there so the tab renders clean.
      if (s.catalogAvailable !== false) setChatModels(await getAiModels("chat"));
    } catch (e) {
      setError(errText(e));
    }
  }, []);
  useEffect(() => {
    void load();
  }, [load]);

  if (error !== null) {
    return <div className="settings-error">Could not load the Assistant settings: {error}</div>;
  }
  if (snap === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  // Gateway-owned (issue #2022): false on hosted, where model browsing is disabled with a concise note.
  const catalogAvailable = snap.catalogAvailable !== false;

  const chooseModel = async (model: string) => {
    setBusy(true);
    setMsg("Saving...");
    try {
      await setCarModeModel(model);
      setSnap({ ...snap, carModeModel: model });
      setMsg("Assistant model set. It applies to the next turn, on every device.");
    } catch (e) {
      setMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="settings-card">
      <CardHead title="Assistant" scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">
        The Assistant talks to your whole fleet in conversation - it answers questions about your sessions and
        acts on them. Choose the model it thinks with. One setting for the account, so the desk and your phone
        use the same one.
      </p>

      <div className="settings-field">
        <label htmlFor="settings-assistant-model">Model</label>
        <select
          id="settings-assistant-model"
          className="settings-select"
          value={snap.carModeModel}
          disabled={busy || !catalogAvailable}
          onChange={(e) => void chooseModel(e.target.value)}
        >
          {ensureIds(snap.carModeModel, chatModels).map((id) => (
            <option key={id} value={id}>
              {id}
            </option>
          ))}
        </select>
        <div className="settings-actions">
          <span className="settings-inline-msg">
            {catalogAvailable
              ? "A fast model is recommended - it must also call tools reliably. GLM-5.2 is slower but a strong tool-caller."
              : "Model browsing isn't available on the hosted Gateway yet; your saved model is shown."}
          </span>
        </div>
      </div>

      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}
