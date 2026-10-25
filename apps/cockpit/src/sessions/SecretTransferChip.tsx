import { useAlerts } from "@devthrottle/client-core/alerts/AlertsProvider";
import { AskChip } from "./AskChip";

// A session that asked for a secret transfer (Secret Handoff, issue #2943, phase 5) carries it ON ITSELF, the way a
// session asking to talk to another does (LinkRequests.tsx): an amber key on its roster card, and a chip in its own
// header that opens the transfer as the Alerts tray shows it. The transfers are read from the shared Alerts list (the
// Alerts mission); this file is only the Cockpit's badge and chip around them.

function KeyIcon() {
  return (
    <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <circle cx="7.5" cy="15.5" r="4.5" />
      <path d="M10.7 12.3 21 2M16 7l3 3M18 5l2 2" />
    </svg>
  );
}

/** The amber key on the asking session's roster card, with how many transfers wait. Nothing when none do. */
export function RosterSecretBadge({ sessionId }: { sessionId: string }) {
  const mine = useAlerts().waitingFrom(sessionId, "secret-transfer");
  if (mine.length === 0) return null;
  const title = mine.length === 1 ? "Asks for a secret from another machine" : `Asks for ${mine.length} secrets from other machines`;
  return (
    <span className="link-ask-badge" title={title} aria-label={title} data-testid="roster-secret-badge">
      <KeyIcon />
      {mine.length}
    </span>
  );
}

/** The chip in the open session's header and its popover: each transfer the session asked for, ready to answer. */
export function SecretTransferChip({ sessionId }: { sessionId: string }) {
  const mine = useAlerts().waitingFrom(sessionId, "secret-transfer");
  const first = mine[0];
  const label =
    mine.length === 1 && first.kind === "secret-transfer" ? `Asks for ${first.detail.entry}` : `Asks for ${mine.length} secrets`;
  return (
    <AskChip
      alerts={mine}
      label={label}
      icon={<KeyIcon />}
      dialogLabel="Secret transfers this session asked for"
      testId="secret-transfer-chip"
    />
  );
}
