import { useEffect, useRef, useState } from "react";
import { SecretTransferCard, useSecretTransfers } from "@devthrottle/client-core/secrets/SecretTransfers";

// A session that asked for a secret transfer (Secret Handoff, issue #2943, phase 5) carries it ON ITSELF, the way a
// session asking to talk to another does (LinkRequests.tsx): an amber key on its roster card, and a chip in its own
// header that opens the shared approval card. An answer given from here is recorded as "from the badge". The card
// and its words are the shared client-core component; this file is only the Cockpit's badge and popover around it.

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
  const mine = useSecretTransfers().waitingFrom(sessionId);
  if (mine.length === 0) return null;
  const title = mine.length === 1 ? "Asks for a secret from another machine" : `Asks for ${mine.length} secrets from other machines`;
  return (
    <span className="link-ask-badge" title={title} aria-label={title} data-testid="roster-secret-badge">
      <KeyIcon />
      {mine.length}
    </span>
  );
}

/** The chip in the open session's header and its popover: the approval card for each transfer the session asked for. */
export function SecretTransferChip({ sessionId }: { sessionId: string }) {
  const mine = useSecretTransfers().waitingFrom(sessionId);
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  if (mine.length === 0) return null;
  const label = mine.length === 1 ? `Asks for ${mine[0].entry}` : `Asks for ${mine.length} secrets`;
  return (
    <span className="link-ask-wrap" ref={wrapRef}>
      <button
        type="button"
        className="link-ask-chip"
        aria-expanded={open}
        aria-haspopup="dialog"
        onClick={() => setOpen((o) => !o)}
        data-testid="secret-transfer-chip"
      >
        <KeyIcon />
        {label}
      </button>
      {open && (
        <div className="link-ask-pop secret-transfer-pop" role="dialog" aria-label="Secret transfers this session asked for">
          {mine.map((t) => (
            <SecretTransferCard key={t.transferId} transfer={t} where="badge" />
          ))}
        </div>
      )}
    </span>
  );
}
