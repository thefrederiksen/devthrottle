import { useEffect, useState } from "react";
import { ACCOUNT_SCOPE, CardHead, errText } from "./settingsShared";
import { getWebsiteInboxCard, mintWebsiteInboxKey, revokeWebsiteInboxKey, type WebsiteInboxCard as Card } from "./websiteInboxClient";
import "./settings.css";

// WEBSITE MESSAGES (brief E, 8 November 2026): the owner makes, replaces or revokes the key the DevThrottle website uses
// to write to his Your inbox instead of emailing him. One shared card, so the Cockpit and the phone show the same thing
// (CLAUDE.md rule 8). Every word on it comes from the Gateway as it is (rule 7); the card only lays it out.
export function WebsiteInboxCard() {
  const [card, setCard] = useState<Card | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");

  useEffect(() => {
    const ctrl = new AbortController();
    getWebsiteInboxCard(ctrl.signal).then(setCard, (e: unknown) => {
      if (!ctrl.signal.aborted) setError(errText(e, "load the website key"));
    });
    return () => ctrl.abort();
  }, []);

  if (error !== null) return <div className="settings-error">Could not load website messages: {error}</div>;
  if (card === null) return <p className="settings-loading">Loading...</p>;

  const act = async (working: string, run: () => Promise<Card>) => {
    if (busy) return;
    setBusy(true);
    setMsg(working);
    try {
      setCard(await run());
      setMsg("");
    } catch (e) {
      setMsg(errText(e, "change the website key"));
    } finally {
      setBusy(false);
    }
  };

  const copy = async (key: string) => {
    try {
      await navigator.clipboard.writeText(key);
      setMsg("Copied.");
    } catch {
      setMsg("Could not copy. Select the key and copy it by hand.");
    }
  };

  return (
    <section className="settings-card" data-testid="website-inbox-card">
      <CardHead title={card.title} scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">{card.explanation}</p>
      <p className="settings-hint" data-testid="website-inbox-status">{card.statusLine}</p>
      {card.key !== null && (
        <div className="settings-field" data-testid="website-inbox-new-key">
          <input className="settings-input" readOnly value={card.key} aria-label="The new website key" onFocus={(e) => e.target.select()} />
          <button type="button" className="settings-btn" onClick={() => void copy(card.key!)}>Copy</button>
          {card.keyLine !== null && <p className="settings-hint">{card.keyLine}</p>}
        </div>
      )}
      <div className="settings-field">
        {card.canMint && (
          <button
            type="button"
            className="settings-btn"
            data-testid="website-inbox-mint"
            disabled={busy}
            onClick={() => void act("Making a key...", mintWebsiteInboxKey)}
          >
            {card.mintLabel}
          </button>
        )}
        {card.canRevoke && (
          <button
            type="button"
            className="settings-btn"
            data-testid="website-inbox-revoke"
            disabled={busy}
            onClick={() => void act("Revoking...", revokeWebsiteInboxKey)}
          >
            {card.revokeLabel}
          </button>
        )}
      </div>
      {card.canMint && card.mintNote !== null && <p className="settings-hint">{card.mintNote}</p>}
      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}
