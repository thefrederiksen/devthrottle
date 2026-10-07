// The Team page (screen S1, devthrottle_internal#2303): the typed, same-origin client the Cockpit's Team page reads.
//
// CRITICAL RULE 7 - THE CLIENT IS DUMB. Whether the role shows as a dropdown, which roles it offers, whether a member
// can be removed and the sentence the remove confirmation shows, the seat each member takes and the line that counts
// them - all of it is decided on the Gateway and arrives here finished. The page renders these fields verbatim; it
// never decides from a role what the caller may do.
import { call } from "./invitationsClient";

/** One member row. `memberId` is opaque: it names the member in the change routes and reveals nothing else. */
export interface TeamPageMember {
  memberId: string;
  name: string;
  email: string | null;
  role: string;
  /** Paid or Free. */
  seat: string;
  isYou: boolean;
  joinedAtUtc: string;
  /** Whether the role shows as a dropdown for the caller. */
  canChangeRole: boolean;
  /** The roles the dropdown offers; empty when it is not a dropdown. */
  roleChoices: string[];
  /** Whether the row offers Remove. */
  canRemove: boolean;
  /** The sentence the remove confirmation shows; null when the row offers no Remove. */
  removeWarning: string | null;
}

/** One waiting invitation on the Team page. */
export interface TeamPageInvitation {
  id: string;
  email: string;
  role: string;
  /** sent, or expired. */
  state: string;
  /** "Paid when accepted" or Free. */
  seat: string;
  invitedBy: string;
  sentAtUtc: string;
  expiresAtUtc: string;
  canResend: boolean;
  canCancel: boolean;
}

/** The confirmation shown before the team plan starts or renews: seats, price, total, and no charge. */
export interface TeamBillCheckout {
  title: string;
  seatsLine: string;
  priceLine: string;
  totalLine: string;
  chargeLine: string;
  periodLine: string;
  confirmLabel: string;
}

/** One line of the team's billing history, finished for display. */
export interface TeamBillHistoryLine {
  id: string;
  /** "7 Oct 2026 to 7 Nov 2026". */
  period: string;
  seats: number;
  /** "US$49.00 x 3 paid seats = US$147.00". */
  amount: string;
  /** "Charged: US$0.00". */
  charged: string;
  reason: string;
}

/**
 * The Billing section (Teams v1, the team bill without Stripe): for the Owner, who may change it, and a Manager, who sees
 * it read-only. Every sentence and every flag is the Gateway's; the page renders them.
 */
export interface TeamBill {
  /** not-started, active, ending or ended. */
  state: string;
  statusLabel: string;
  statusLine: string;
  seats: number;
  seatsLine: string;
  priceLine: string;
  amountLine: string;
  /** "No charge". */
  chargeLine: string;
  periodEndUtc: string | null;
  /** The period's last day as the Gateway words it ("7 Nov 2026"); render it, never format periodEndUtc. */
  periodEnd: string | null;
  autoRenew: boolean;
  /** Whether the caller may change the bill at all (the Owner). */
  canChange: boolean;
  canStart: boolean;
  canRenew: boolean;
  canSetAutoRenew: boolean;
  canCancel: boolean;
  /** The confirmation before starting or renewing; null when neither is offered. */
  checkout: TeamBillCheckout | null;
  /** A sentence under the bill for a caller who may not change it (a Manager); null for the Owner. */
  note: string | null;
  /** What the cancel confirmation says; null when Cancel is not offered. */
  cancelWarning: string | null;
  history: TeamBillHistoryLine[];
}

/** What the Team page shows (GET /teams/{teamId}/page). */
export interface TeamPage {
  teamId: string;
  teamName: string;
  yourRole: string;
  /** "3 paid seats, 2 Collaborators (no charge), 1 invitation waiting". */
  summary: string;
  /** Whether the page offers "Invite someone". */
  canInvite: boolean;
  members: TeamPageMember[];
  invitations: TeamPageInvitation[];
  /** The Billing section; null for a role that may not see the team's bill (a Developer). */
  bill: TeamBill | null;
}

const team = (teamId: string) => `/teams/${encodeURIComponent(teamId)}`;

/** GET /teams/{teamId}/page - the Team page's model for the caller. */
export function getTeamPage(teamId: string, signal?: AbortSignal): Promise<TeamPage> {
  return call<TeamPage>("GET", `${team(teamId)}/page`, "load the Team page", undefined, signal);
}

/** PUT /teams/{teamId}/members/{memberId}/role - change a member's role. */
export async function changeMemberRole(teamId: string, memberId: string, role: string, signal?: AbortSignal): Promise<void> {
  await call<{ done: boolean }>("PUT", `${team(teamId)}/members/${encodeURIComponent(memberId)}/role`, "change the role", { role }, signal);
}

/** POST /teams/{teamId}/bill/start - the Owner starts the team plan. Nothing is charged. */
export async function startTeamPlan(teamId: string, signal?: AbortSignal): Promise<void> {
  await call<{ done: boolean }>("POST", `${team(teamId)}/bill/start`, "start the team plan", undefined, signal);
}

/** POST /teams/{teamId}/bill/renew - the Owner renews an ending or ended plan for a new month. */
export async function renewTeamPlan(teamId: string, signal?: AbortSignal): Promise<void> {
  await call<{ done: boolean }>("POST", `${team(teamId)}/bill/renew`, "renew the team plan", undefined, signal);
}

/** PUT /teams/{teamId}/bill/auto-renew - the Owner switches auto-renew on or off. */
export async function setTeamPlanAutoRenew(teamId: string, on: boolean, signal?: AbortSignal): Promise<void> {
  await call<{ done: boolean }>("PUT", `${team(teamId)}/bill/auto-renew`, on ? "switch auto-renew on" : "switch auto-renew off", { on }, signal);
}

/** POST /teams/{teamId}/bill/cancel - the Owner cancels; the plan runs to the end of its period, then ends. */
export async function cancelTeamPlan(teamId: string, signal?: AbortSignal): Promise<void> {
  await call<{ done: boolean }>("POST", `${team(teamId)}/bill/cancel`, "cancel the team plan", undefined, signal);
}

/** DELETE /teams/{teamId}/members/{memberId} - remove a member from the team. */
export async function removeMember(teamId: string, memberId: string, signal?: AbortSignal): Promise<void> {
  await call<{ done: boolean }>("DELETE", `${team(teamId)}/members/${encodeURIComponent(memberId)}`, "remove the member", undefined, signal);
}
