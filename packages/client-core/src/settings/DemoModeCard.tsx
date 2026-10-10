import { useEffect, useState } from "react";
import { getDemoMode, setDemoMode } from "../demo/demoMode";
import { ACCOUNT_SCOPE, CardHead, errText } from "./settingsShared";
import "./settings.css";

// DEMO MODE (owner, 8 Oct 2026): the one switch that blurs what this account's factories and sessions do, on
// every screen on the account. A checkbox, because the question has two answers. Shared so the phone can mount
// the same card when its Settings gains the Account tab.
export function DemoModeCard() {
  const [on, setOn] = useState<boolean | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");

  useEffect(() => {
    const ctrl = new AbortController();
    getDemoMode(ctrl.signal).then(setOn, (e: unknown) => {
      if (!ctrl.signal.aborted) setError(errText(e, "load demo mode"));
    });
    return () => ctrl.abort();
  }, []);

  if (error !== null) return <div className="settings-error">Could not load demo mode: {error}</div>;
  if (on === null) return <p className="settings-loading">Loading...</p>;

  const toggle = async (enabled: boolean) => {
    if (busy || enabled === on) return;
    setBusy(true);
    setMsg("Saving...");
    try {
      const applied = await setDemoMode(enabled);
      setOn(applied);
      setMsg(applied ? "On. Private text is blurred on every screen on this account." : "Off. Everything shows again.");
    } catch (e) {
      setMsg(`Could not save: ${errText(e, "save demo mode")}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="settings-card" data-testid="demo-mode-card">
      <CardHead title="Demo mode" scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">
        Blurs what your factories and sessions do, for screen shares and demos. Applies to everyone on this account.
      </p>
      <div className="settings-field">
        <label className="settings-check">
          <input
            type="checkbox"
            data-testid="demo-mode-toggle"
            checked={on}
            disabled={busy}
            onChange={(e) => void toggle(e.target.checked)}
          />
          Demo mode
        </label>
      </div>
      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}
