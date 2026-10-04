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

/** What the Team page shows (GET /teams/{teamId}/page). */
export interface TeamPage {
  teamId: string;
  teamName: string;
  yourRole: string;
  /** "3 paid seats, 2 Collaborators (free), 1 invitation waiting". */
  summary: string;
  /** Whether the page offers "Invite someone". */
  canInvite: boolean;
  members: TeamPageMember[];
  invitations: TeamPageInvitation[];
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

/** DELETE /teams/{teamId}/members/{memberId} - remove a member from the team. */
export async function removeMember(teamId: string, memberId: string, signal?: AbortSignal): Promise<void> {
  await call<{ done: boolean }>("DELETE", `${team(teamId)}/members/${encodeURIComponent(memberId)}`, "remove the member", undefined, signal);
}
