// A request to restart a Director - issue #2725 (restart epic, Phase 6). A session ASKS, the Gateway
// scrutinises, and the owner ACCEPTS once on a device he holds. These are the typed calls both shells make to
// answer one; the request itself reaches a screen as an alert (the Alerts mission, client-core/src/alerts).
//
// EVERY SENTENCE ON A REQUEST IS WRITTEN ON THE GATEWAY. The card renders `title`, `askedBySentence`,
// `liveSessionsSentence`, the capability's own `reason` and `guardedRestartReason`, `stateReason` and
// `progress` verbatim, and offers the two actions only when `canAccept` is true. Nothing here re-derives
// what a state means - a client that did would, the first time it met a state it did not expect, render
// something plausible instead of something true.
//
// The types are hand-written rather than taken from the generated schema for the reason recorded on
// `ModelDisplay` in api/client.ts: the committed schema predates this route, and regenerating it is not a
// clean addition. Delete these when the schema is next regenerated properly.
import { authHeaders, GatewayError, gatewayFetch } from "../api/client";

/** Where a request stands. Serialised as its name by the Gateway. */
export type RestartRequestState = "Pending" | "Accepted" | "Declined" | "Expired" | "Abandoned" | "Completed";

/** What the machine said when it was scrutinised. Only the fields the card reads. */
export interface RestartCapability {
  verdict: string;
  reason: string;
  guardedRestart: string;
  guardedRestartReason: string;
  launcherVersion?: string | null;
}

/** One request, as the Gateway serialises `DirectorRestartRequestDto`. */
export interface DirectorRestartRequest {
  id: string;
  machine: string;
  directorId: string;
  directorName: string;
  requestedBySessionId: string;
  requestedBySessionName: string;
  reason: string;
  requestedAtUtc: string;
  expiresAtUtc: string;
  acceptedAtUtc?: string | null;
  closedAtUtc?: string | null;
  state: RestartRequestState;
  stateReason: string;
  progress: string;
  progressAtUtc?: string | null;
  workspaceId?: string | null;
  liveSessionCount: number;
  liveSessionsSentence: string;
  capability?: RestartCapability | null;
  title: string;
  askedBySentence: string;
  /** What accepting does, written on the Gateway. The card shows it beside its confirmation. */
  acceptSentence?: string;
  canAccept: boolean;
}

/** POST .../accept - the owner's one accept. The Gateway re-checks the machine and hands the cycle to the
 *  Director; a refusal comes back as the Gateway's own sentence. */
export async function acceptRestartRequest(machine: string, id: string, signal?: AbortSignal): Promise<DirectorRestartRequest> {
  const res = await gatewayFetch(`/machines/${encodeURIComponent(machine)}/director/restart-requests/${encodeURIComponent(id)}/accept`, {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "accept the restart");
  return (await res.json()) as DirectorRestartRequest;
}

/** POST .../decline - the owner's no, with an optional reason. */
export async function declineRestartRequest(machine: string, id: string, reason?: string, signal?: AbortSignal): Promise<DirectorRestartRequest> {
  const res = await gatewayFetch(`/machines/${encodeURIComponent(machine)}/director/restart-requests/${encodeURIComponent(id)}/decline`, {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ reason: reason ?? "" }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "decline the restart");
  return (await res.json()) as DirectorRestartRequest;
}
