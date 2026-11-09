import { useEffect, useRef, useState } from "react";
import { ACCOUNT_SCOPE, CardHead, errText } from "./settingsShared";
import {
  getContributorNoticeCard,
  setContributorNoticeChoice,
  type ContributorNoticeCard as Card,
} from "./contributorNoticeClient";
import "./settings.css";

// HOW A CONTRIBUTOR IS TOLD THAT SOMETHING IS WAITING (Team implementation piece 4.4; mockup.html, "Notifications"):
// right away, once a day at an hour they choose, or never. One shared card, so the Cockpit and the phone show the same
// thing (CLAUDE.md rule 8), and the contributor's welcome and own Settings page can mount it too. Every word on it, and
// whether it is shown at all, comes from the Gateway as it is (rule 7); the card only lays it out.
export function ContributorNoticeCard() {
  const [card, setCard] = useState<Card | null | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");
  const busyRef = useRef(false);

  useEffect(() => {
    const ctrl = new AbortController();
    getContributorNoticeCard(ctrl.signal).then(setCard, (e: unknown) => {
      if (!ctrl.signal.aborted) setError(errText(e, "load how you are told about new things"));
    });
    return () => ctrl.abort();
  }, []);

  if (error !== null) return <div className="settings-error">Could not load your email choice: {error}</div>;
  if (card === undefined) return <p className="settings-loading">Loading...</p>;
  // No such setting on this Gateway, or the person is a contributor on no team: the Gateway's verdict is "not shown".
  if (card === null || !card.applies) return null;

  const save = async (mode: string, dailyHour: number) => {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy(true);
    setMsg("Saving...");
    try {
      const next = await setContributorNoticeChoice(mode, dailyHour);
      setCard(next);
      setMsg(next.savedLine ?? "");
    } catch (e) {
      setMsg(errText(e, "save how you are told about new things"));
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  };

  return (
    <section className="settings-card" data-testid="contributor-notice-card">
      <CardHead title={card.title} scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">{card.explanation}</p>
      <div className="settings-field">
        {card.options.map((o) => (
          <label className="settings-check" key={o.value}>
            <input
              type="radio"
              name="contributor-notice-mode"
              value={o.value}
              checked={o.selected}
              disabled={busy}
              onChange={() => {
                if (!o.selected) void save(o.value, card.dailyHour);
              }}
            />
            {o.label}
          </label>
        ))}
      </div>
      {card.showHour && (
        <div className="settings-field" data-testid="contributor-notice-hour">
          <label htmlFor="contributor-notice-hour-select">{card.hourLabel}</label>
          <select
            id="contributor-notice-hour-select"
            className="settings-select"
            value={card.dailyHour}
            disabled={busy}
            onChange={(e) => void save(card.mode, Number(e.target.value))}
          >
            {card.hours.map((h) => (
              <option key={h.value} value={h.value}>
                {h.label}
              </option>
            ))}
          </select>
        </div>
      )}
      {card.showHour && <p className="settings-hint">{card.timeZoneLine}</p>}
      <p className="settings-hint" data-testid="contributor-notice-note">{card.note}</p>
      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}
