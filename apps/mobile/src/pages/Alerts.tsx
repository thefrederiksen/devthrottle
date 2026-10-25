import { useEffect, useRef } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { AlertsList } from "@devthrottle/client-core/alerts/AlertsList";
import { returnFocusToBell, type AlertsScreenState } from "../components/AppBarAlerts";

// THE ALERTS SCREEN (the Alerts mission, phase 5). The owner tried the bottom sheet on his phone: "Why doesn't it just
// bring up a clear window, show me what the hell I need to answer, and then I can answer it." So on the phone, Alerts
// is a full, opaque screen of its own - an app bar with a back arrow and the title, and the shared AlertsList below it:
// what needs him as cards with their buttons on them, and Earlier, collapsed, underneath. No shade, no sheet.
//
// The back arrow, the phone's back gesture and the browser's back all return to where he was. A deep link
// (?alert={id}) lands here with that alert brought into view.
//
// This screen has no bell in its app bar: it IS Alerts.

export function Alerts() {
  const location = useLocation();
  const navigate = useNavigate();
  const heading = useRef<HTMLHeadingElement>(null);
  const focusAlertId = new URLSearchParams(location.search).get("alert");
  const from = location.state as AlertsScreenState | null;

  // Focus starts on the screen's title, as a page's does - unless a deep link is about to put it on its alert. Leaving
  // the screen by any route hands focus back to the bell it was opened from.
  useEffect(() => {
    if (!focusAlertId) heading.current?.focus();
    return () => returnFocusToBell();
  }, []); // Only on arriving: a later change of the address is not a new arrival.

  const back = () => {
    if (from?.pushed) navigate(-1);
    else navigate(from?.back ?? "/", { replace: true, state: from?.backState });
  };

  return (
    <div className="screen">
      <header className="app-bar">
        <button type="button" className="back-link" aria-label="Back" onClick={back}>
          <BackIcon />
        </button>
        <h1 ref={heading} tabIndex={-1}>
          Alerts
        </h1>
        <div className="app-bar-spacer" />
      </header>
      <main className="alerts-page">
        <AlertsList where="phone" focusAlertId={focusAlertId} />
      </main>
    </div>
  );
}

function BackIcon() {
  return (
    <svg viewBox="0 0 16 16" width="18" height="18" aria-hidden="true" focusable="false">
      <path d="M10 3L5 8l5 5" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" fill="none" />
    </svg>
  );
}
