import { useAlerts } from "@devthrottle/client-core/alerts/AlertsProvider";
import type { AlertDto } from "@devthrottle/client-core/alerts/alertsClient";
import { AskChip } from "./AskChip";

// A session asking to talk to another (issues #3548, #3631). It used to be a full-width card above the session detail,
// which pushed every session down and sideways. Now a waiting request lives ON THE SESSION THAT ASKED: an amber phone
// badge and a "wants to talk to ..." line on its roster card, and a chip in its own header that opens the request as
// the Alerts tray shows it. The requests are read from the shared Alerts list (the Alerts mission); nothing here polls.
// Amber, never red: the red dot already means something waits in the queue.
//
// The session names how much talking it needs when it asks; the owner only approves or denies that. Approving sets up an
// ordinary message link and the Gateway tells the session; denying tells it no. Nothing here sends a message.

/** The name the Gateway gives the session a request asks to talk to - number and name. The Gateway names both sessions
 *  on every alert it lists; a record that somehow carries no name shows the session's whole id, never words of ours. */
function targetOf(alert: AlertDto): string {
  if (alert.kind !== "message-request") return "";
  return alert.detail.targetLabel || alert.detail.targetSessionId;
}

function PhoneIcon() {
  return (
    <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M22 16.9v3a2 2 0 0 1-2.2 2 19.8 19.8 0 0 1-8.6-3.1 19.5 19.5 0 0 1-6-6A19.8 19.8 0 0 1 2.1 4.2 2 2 0 0 1 4.1 2h3a2 2 0 0 1 2 1.7c.1 1 .4 1.9.7 2.8a2 2 0 0 1-.5 2.1L8 9.9a16 16 0 0 0 6 6l1.3-1.3a2 2 0 0 1 2.1-.4c.9.3 1.8.6 2.8.7a2 2 0 0 1 1.7 2z" />
    </svg>
  );
}

/** The amber phone on the asking session's roster card, with how many requests wait. Nothing when none do. */
export function RosterLinkBadge({ sessionId }: { sessionId: string }) {
  const mine = useAlerts().waitingFrom(sessionId, "message-request");
  if (mine.length === 0) return null;
  const title = mine.length === 1 ? "Wants to talk to another session" : `Wants to talk to ${mine.length} other sessions`;
  return (
    <span className="link-ask-badge" title={title} aria-label={title} data-testid="roster-link-badge">
      <PhoneIcon />
      {mine.length}
    </span>
  );
}

/** The roster card's line: "wants to talk to 125 Release - Release Manager". Nothing when no request waits. */
export function RosterLinkLine({ sessionId }: { sessionId: string }) {
  const mine = useAlerts().waitingFrom(sessionId, "message-request");
  if (mine.length === 0) return null;
  return <span className="roster-link-ask">wants to talk to {mine.map(targetOf).join(", ")}</span>;
}

/** The chip in the open session's header and its popover: what the session asks for, Approve or Deny. */
export function LinkRequestChip({ sessionId }: { sessionId: string }) {
  const mine = useAlerts().waitingFrom(sessionId, "message-request");
  const label = mine.length === 1 ? `Wants to talk to ${targetOf(mine[0])}` : `Wants to talk to ${mine.length} sessions`;
  return (
    <AskChip
      alerts={mine}
      label={label}
      icon={<PhoneIcon />}
      dialogLabel="Requests to talk to another session"
      testId="link-ask-chip"
    />
  );
}
