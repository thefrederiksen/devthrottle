import {
  setTurnVerdictColourEnabled,
  setTurnVerdictJudgeEnabled,
} from "./settingsClient";
import { ACCOUNT_SCOPE, CardHead, useGatewaySettings } from "./settingsShared";
import "./settings.css";

// ---- "Turn verdicts": whether the Wingman judges what each stop MEANS, and whether it says so ------
//
// Today a session goes red whenever it has been quiet for ten seconds, whatever it stopped for. On the
// owner's own fleet, measured, three in four of those reds did not need him. The Wingman judging each stop
// is what tells "I have finished" apart from "I need you", and this card is where an account asks for that.
//
// TWO SWITCHES, NOT ONE, and the card shows them as two because they are two decisions with different
// costs. Judging spends a model call at every stop and fills the record. Colouring is what actually moves
// what you see. Turning judging on and colouring off is a real and useful state - the fleet is judged, every
// verdict is kept, and nothing on your screens has changed - which is how the judging earns trust before it
// is allowed to make a session look calm. So the second box is deliberately reachable only once the first
// is on: a colour switch with nothing judging it would be a control that does nothing.
//
// WHY THIS CARD IS ON THE ASSISTANT TAB and not the AI tab the mission's plan named: the AI tab is HIDDEN
// on both surfaces (see tabs.ts - it showed hosting model identities to customers and most of it is inert
// on the hosted Gateway). A settings card on a tab nobody can open is a setting nobody can change, and the
// mission needs the owner to be able to turn these on without a database edit. The Assistant tab is where
// the model the fleet brain thinks with already lives, which is the same machinery this judging runs on.
//
// Shared by both surfaces, like every other settings card - the desktop and the phone mount this one file.

export function TurnVerdictCard() {
  const { settings, setSettings, error, busy, msg, runSave } = useGatewaySettings();

  if (error !== null) {
    return <div className="settings-error">Could not load the turn verdict settings: {error}</div>;
  }
  if (settings === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  const toggleJudge = (enabled: boolean) => {
    if (busy || enabled === settings.turnVerdictJudgeEnabled) return;
    void runSave(async () => {
      const applied = await setTurnVerdictJudgeEnabled(enabled);
      // Turning judging OFF leaves the colour switch on in the account's stored settings, and the card says
      // so rather than silently writing a second change nobody asked for. Nothing is coloured while nothing
      // is judged, so the state is harmless - but a card that quietly changed a second setting would be a
      // card whose effects you cannot predict from what you clicked.
      setSettings({ ...settings, turnVerdictJudgeEnabled: applied });
      return applied
        ? "On. From the next turn end, each stop on this account is judged and the answer is recorded."
        : "Off. No stop is judged and nothing is recorded. Your sessions go red exactly as they do now.";
    });
  };

  const toggleColour = (enabled: boolean) => {
    if (busy || enabled === settings.turnVerdictColourEnabled) return;
    void runSave(async () => {
      const applied = await setTurnVerdictColourEnabled(enabled);
      setSettings({ ...settings, turnVerdictColourEnabled: applied });
      return applied
        ? "On. A stop that was a report now shows its calm colour and its one-line summary."
        : "Off. Verdicts are still recorded, and every session keeps the colour it has today.";
    });
  };

  return (
    <section className="settings-card">
      <CardHead title="Turn verdicts" scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">
        A session goes red whenever it stops, whether it stopped to ask you something or because it had
        finished. Let the Wingman read each stop and say which it was: a stop that needed you stays red with
        the question in its label, and a stop that was a report goes calm and stays out of your
        &quot;needs you&quot; count.
      </p>

      <div className="settings-field">
        <label className="settings-check">
          <input
            type="checkbox"
            checked={settings.turnVerdictJudgeEnabled}
            disabled={busy}
            onChange={(e) => toggleJudge(e.target.checked)}
          />
          Judge what each stop means
        </label>
      </div>
      <p className="settings-hint">
        One model call per stop, on your account&apos;s included thinking model. The answer is kept for seven
        days with the session&apos;s own words as the receipt, so you can always see what it read.
      </p>

      <div className="settings-field">
        <label className="settings-check">
          <input
            type="checkbox"
            checked={settings.turnVerdictColourEnabled}
            disabled={busy || !settings.turnVerdictJudgeEnabled}
            onChange={(e) => toggleColour(e.target.checked)}
          />
          Show the verdicts on my sessions
        </label>
      </div>
      <p className="settings-hint">
        {settings.turnVerdictJudgeEnabled
          ? "Leave this off to watch it work first: every stop is judged and recorded, and nothing on your screens changes until you turn it on."
          : "Turn judging on first - there is nothing to show until stops are being judged."}
      </p>

      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}
