import { Link } from "react-router-dom";
import { useNetStatus } from "@devthrottle/client-core/net/useNetStatus";
import "./statusPill.css";

// The Cockpit's network status pill in the left rail, shown ONLY when the Gateway rules the connection
// slow (its red level - relaying through a distant server instead of a direct path). It used to be
// always-visible, mirroring the phone's pill: a green "Connected" every second of every day, which is
// not information. Green is the normal case, and amber and grey are the transient warming-up and
// checking states, which resolve on their own within a poll or two and must never nag. The phone's pill
// was deleted outright on 2026-07-26 for the same reason - there it was also holding a corner that
// controls needed (see apps/mobile ConnectionBanner). This rail has room to keep a compact one for the
// state actually worth seeing. Network Diagnostics is the Network tab of Settings (owner, 8 Oct 2026), so reaching it
// never depended on this. Colour and words come from the authoritative Gateway verdict, verbatim.
export function CockpitStatusPill() {
  const status = useNetStatus();
  if (status.level !== "red") return null;
  return (
    <Link
      to="/settings?tab=network"
      className={`net-pill net-pill-${status.level}`}
      title={status.detail}
      aria-label={`Network: ${status.label}. ${status.detail}`}
    >
      <span className="net-pill-dot" aria-hidden="true" />
      <span className="net-pill-label">Network: {status.label}</span>
    </Link>
  );
}
