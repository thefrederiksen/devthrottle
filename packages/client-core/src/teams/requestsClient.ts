// Requests to a team's Owner and Managers (devthrottle_internal#2308): the typed, same-origin client the Cockpit's
// Requests pages (screen S9) read - the sender's page and the Owner and Managers' list.
//
// CRITICAL RULE 7 - THE CLIENT IS DUMB. Every state's name, every line of a request's trail, who is named on it, and
// which of Accept, Not doing this and Done the person may click arrive here finished from the Gateway. A page
// renders these fields verbatim; it never works out what a person may do from their role or a request's state.
//
// A request is written and read by people only: the Gateway refuses these routes to any session or Director key.
import { authHeaders, GatewayError } from "../api/client";

/** One line of a request's trail: sent, or a change of state. */
export interface TeamRequestStep {
  /** sent, accepted, declined or done. */
  state: string;
  /** The state as the screens name it: Sent, Accepted, Not doing this, Done. */
  label: string;
  /** Who made this step, as named on screen ("You" for the reader). */
  by: string;
  atUtc: string;
  /** Why, on "Not doing this" only. */
  reason: string | null;
  /** The finished line, for example "Accepted by priya@acme.com". */
  sentence: string;
}

/** One request as the Gateway describes it to the reader. */
export interface TeamRequest {
  id: string;
  text: string;
  /** sent, accepted, declined or done - what it is NOW. */
  state: string;
  stateLabel: string;
  /** Who sent it, as named on screen ("You" for the sender). */
  sentBy: string;
  isYours: boolean;
  sentAtUtc: string;
  updatedAtUtc: string;
  trail: TeamRequestStep[];
  /** Whether the reader may accept it now. */
  canAccept: boolean;
  /** Whether the reader may mark it Not doing this now. */
  canDecline: boolean;
  /** Whether the reader may mark it Done now. */
  canMarkDone: boolean;
}

// `what` names the action in the reader's words; the Gateway's own sentence wins over it whenever the Gateway sent one.
async function call<T>(method: "GET" | "POST", path: string, what: string, body?: unknown, signal?: AbortSignal): Promise<T> {
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
  // A Gateway with Teams not released answers an unknown page path with the app's own HTML shell and a 200. Never
  // read that as data.
  if (type !== "application/json") {
    throw new GatewayError(404, "Teams are not available on this DevThrottle Gateway yet.", {
      reason: "Teams are not available on this DevThrottle Gateway yet.",
    });
  }
  return (await res.json()) as T;
}

const root = (teamId: string) => `/teams/${encodeURIComponent(teamId)}/requests`;
const one = (teamId: string, requestId: string) => `${root(teamId)}/${encodeURIComponent(requestId)}`;

/** GET /teams/{teamId}/requests/mine - the reader's own requests, newest first. */
export async function listMyRequests(teamId: string, signal?: AbortSignal): Promise<TeamRequest[]> {
  const body = await call<{ requests: TeamRequest[] }>("GET", `${root(teamId)}/mine`, "load your requests", undefined, signal);
  return body.requests;
}

/** GET /teams/{teamId}/requests - the team's whole Requests list, for its Owner and Managers. */
export async function listTeamRequests(teamId: string, signal?: AbortSignal): Promise<TeamRequest[]> {
  const body = await call<{ requests: TeamRequest[] }>("GET", root(teamId), "load the team's requests", undefined, signal);
  return body.requests;
}

/** POST /teams/{teamId}/requests - send a request in the person's own words. */
export async function sendRequest(teamId: string, text: string, signal?: AbortSignal): Promise<TeamRequest> {
  const body = await call<{ request: TeamRequest }>("POST", root(teamId), "send the request", { text }, signal);
  return body.request;
}

/** POST /teams/{teamId}/requests/{id}/accept. */
export async function acceptRequest(teamId: string, requestId: string, signal?: AbortSignal): Promise<TeamRequest> {
  const body = await call<{ request: TeamRequest }>("POST", `${one(teamId, requestId)}/accept`, "accept the request", {}, signal);
  return body.request;
}

/** POST /teams/{teamId}/requests/{id}/decline - Not doing this; the Gateway requires the reason. */
export async function declineRequest(teamId: string, requestId: string, reason: string, signal?: AbortSignal): Promise<TeamRequest> {
  const body = await call<{ request: TeamRequest }>("POST", `${one(teamId, requestId)}/decline`, "mark the request Not doing this", { reason }, signal);
  return body.request;
}

/** POST /teams/{teamId}/requests/{id}/done. */
export async function markRequestDone(teamId: string, requestId: string, signal?: AbortSignal): Promise<TeamRequest> {
  const body = await call<{ request: TeamRequest }>("POST", `${one(teamId, requestId)}/done`, "mark the request Done", {}, signal);
  return body.request;
}
