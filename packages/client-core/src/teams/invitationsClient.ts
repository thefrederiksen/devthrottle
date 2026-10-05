// Team invitations by email that expire (devthrottle_internal#2301): the typed, same-origin client the Cockpit's invite
// form (screen S2) and accept page (screen S3) read.
//
// CRITICAL RULE 7 - THE CLIENT IS DUMB. Which roles may be chosen, the sentence beside each, whether the form can be
// sent, whether the invitation can be answered and why not - all of it is decided on the Gateway and arrives here
// finished. A page renders these fields verbatim; it never decides who may invite whom or whether an invitation has
// expired.
//
// THE LINK'S SECRET IS SENT IN A REQUEST BODY, never in an API address: the Gateway's access log records every API
// path and query. The accept page reads it from its own address once and hands it over here.
import { authHeaders, GatewayError } from "../api/client";

/** One role the invite form offers. `allowed` false means it is shown greyed with `hint` saying why. */
export interface InviteRoleOption {
  role: "Manager" | "Developer" | "Collaborator";
  allowed: boolean;
  hint: string;
}

/** What the invite form shows (GET /teams/{teamId}/invitations/options). */
export interface InviteOptions {
  teamId: string;
  teamName: string;
  yourRole: string;
  roles: InviteRoleOption[];
  /** Why nothing can be sent now (the caller's role, or the team's bill), or null when the form can be sent. */
  blocked: string | null;
  expiryNote: string;
}

/** One invitation as the Gateway describes it. The link's secret is never part of it. */
export interface TeamInvitation {
  id: string;
  teamId: string;
  teamName: string;
  email: string;
  role: string;
  /** sent, accepted, declined, cancelled or expired - what it is NOW. */
  state: string;
  invitedBy: string;
  /** Who accepted it, as named on screen - which need not be the address it was sent to - or null. */
  acceptedBy: string | null;
  /** Who pays for the seat, or null for a free Collaborator seat. */
  paidBy: string | null;
  sentAtUtc: string;
  expiresAtUtc: string;
  /** Accept page only: the signed-in account, as it is named on screen. */
  signedInAs: string | null;
  /** Accept page only: whether this account can accept or decline it now. */
  canRespond: boolean;
  /** Accept page only: why it cannot, in plain words. */
  refusal: string | null;
}

/** Whether the invitation's email went out, and the sentence that says so. */
export interface InvitationEmail {
  sent: boolean;
  message: string;
}

export interface InvitationSent {
  invitation: TeamInvitation;
  email: InvitationEmail;
}

// `what` names the action in the reader's words; the Gateway's own sentence ("Only the Owner can invite a Manager.")
// wins over it whenever the Gateway sent one (GatewayError.from keeps it as the error's serverReason).
// Shared by the Team page client (teamPageClient.ts), so every team call reads a refusal and a dark Gateway one way.
export async function call<T>(method: "GET" | "POST" | "PUT" | "DELETE", path: string, what: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, {
    method,
    headers: {
      Accept: "application/json",
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      ...authHeaders(),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, what);
  const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  // The Gateway falls unknown page paths back to the app's own HTML shell with a 200 - which is exactly what a
  // Gateway with Teams not released answers. Never read that as data.
  if (type !== "application/json") {
    throw new GatewayError(404, "Teams are not available on this DevThrottle Gateway yet.", {
      reason: "Teams are not available on this DevThrottle Gateway yet.",
    });
  }
  return (await res.json()) as T;
}

const team = (teamId: string) => `/teams/${encodeURIComponent(teamId)}/invitations`;

/** GET /teams/{teamId}/invitations/options - what the invite form offers the caller. */
export function getInviteOptions(teamId: string, signal?: AbortSignal): Promise<InviteOptions> {
  return call<InviteOptions>("GET", `${team(teamId)}/options`, "load the invite form", undefined, signal);
}

/** GET /teams/{teamId}/invitations - the team's invitations, for its Owner and Managers. */
export async function listInvitations(teamId: string, signal?: AbortSignal): Promise<TeamInvitation[]> {
  const body = await call<{ invitations: TeamInvitation[] }>("GET", team(teamId), "load the invitations", undefined, signal);
  return body.invitations;
}

/** POST /teams/{teamId}/invitations - invite an email address to a role. */
export function sendInvitation(teamId: string, email: string, role: string, signal?: AbortSignal): Promise<InvitationSent> {
  return call<InvitationSent>("POST", team(teamId), "send the invitation", { email, role }, signal);
}

/** POST /teams/{teamId}/invitations/{id}/resend - send again; a new 7 days and a new link. */
export function resendInvitation(teamId: string, invitationId: string, signal?: AbortSignal): Promise<InvitationSent> {
  return call<InvitationSent>("POST", `${team(teamId)}/${encodeURIComponent(invitationId)}/resend`, "resend the invitation", {}, signal);
}

/** POST /teams/{teamId}/invitations/{id}/cancel - cancel a waiting invitation. */
export async function cancelInvitation(teamId: string, invitationId: string, signal?: AbortSignal): Promise<TeamInvitation> {
  const body = await call<{ invitation: TeamInvitation }>("POST", `${team(teamId)}/${encodeURIComponent(invitationId)}/cancel`, "cancel the invitation", {}, signal);
  return body.invitation;
}

/** POST /team-invitations/open - what the accept page shows the signed-in person holding the link. */
export async function openInvitation(token: string, signal?: AbortSignal): Promise<TeamInvitation> {
  const body = await call<{ invitation: TeamInvitation }>("POST", "/team-invitations/open", "open the invitation", { token }, signal);
  return body.invitation;
}

/** POST /team-invitations/accept - join the team in the invited role. */
export async function acceptInvitation(token: string, signal?: AbortSignal): Promise<TeamInvitation> {
  const body = await call<{ invitation: TeamInvitation }>("POST", "/team-invitations/accept", "join the team", { token }, signal);
  return body.invitation;
}

/** POST /team-invitations/decline - say no to the invitation. */
export async function declineInvitation(token: string, signal?: AbortSignal): Promise<TeamInvitation> {
  const body = await call<{ invitation: TeamInvitation }>("POST", "/team-invitations/decline", "decline the invitation", { token }, signal);
  return body.invitation;
}
