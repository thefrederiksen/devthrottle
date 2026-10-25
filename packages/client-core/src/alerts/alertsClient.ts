// Alerts (the Alerts mission, 24 October 2026): one place for everything that needs the owner - a restart request, a
// secret transfer, a message request - on the phone and in the Cockpit. The Gateway composes the alerts at read time
// from each feature's own record (section 5.1 of docs/missions/alerts-2026-10-24/mission.md); the only state it stores
// is what the owner has SEEN and CLEARED. These are the typed calls for its four routes.
//
// EVERY SENTENCE ON AN ALERT IS WRITTEN ON THE GATEWAY. `title`, `summary` and `status` are rendered verbatim, and
// `needsAnswer` alone decides the red count and which section a row sits in. Answering does not go through these
// routes: the detail view calls each feature's existing answer route, which keeps every check it has today.
//
// The types are hand-written for the reason recorded on `ModelDisplay` in api/client.ts: the committed schema predates
// these routes. Delete them when the schema is next regenerated properly.
import { authHeaders, GatewayError, gatewayFetch, POLL_TIMEOUT_MS } from "../api/client";
import type { DirectorRestartRequest } from "../restart/restartRequests";
import type { SecretTransfer } from "../secrets/secretTransfersClient";
import type { MessageLinkRequest } from "../fleet/messageLinksClient";

/** How an alert's status reads (`AlertTones` on the Gateway). */
export type AlertTone = "good" | "bad" | "neutral";

/** The class that colours a status by its tone. Layout only: the tone itself is the Gateway's. */
export function toneClass(tone: string): string {
  return tone === "good" || tone === "bad" ? `alert-tone-${tone}` : "alert-tone-neutral";
}

/** What an alert is about. A closed set: the detail view switches on it to pick the action block. */
export type AlertKind = "restart-request" | "secret-transfer" | "message-request";

interface AlertFields {
  /** Stable and globally unique: "{kind}:{sourceId}". */
  id: string;
  /** True while the owner can still act. This alone drives the red count and the "Needs you" section. */
  needsAnswer: boolean;
  /** One line, written for the owner. */
  title: string;
  /** One plain sentence: who asked and why, or the outcome. */
  summary: string;
  /** A short status word for an Earlier row, written by the source: Accepted, Declined, Expired... */
  status: string;
  /** How that status reads, ruled by the source: "good" (it happened), "bad" (it did not, and the owner should know) or
   *  "neutral". A client only colours it; an unknown tone is drawn as neutral, never guessed at. */
  tone: AlertTone;
  createdAtUtc: string;
  updatedAtUtc: string;
  /** When a needs-answer alert stops being answerable, if it does. */
  expiresAtUtc?: string | null;
  /** The owner has opened the tray since this alert last changed. */
  seen: boolean;
}

/** One alert, as the Gateway serialises `AlertDto`. `detail` is the feature's own record, so answering keeps using the
 *  feature's own, already-tested route. */
export type AlertDto =
  | (AlertFields & { kind: "restart-request"; detail: DirectorRestartRequest })
  | (AlertFields & { kind: "secret-transfer"; detail: SecretTransfer })
  | (AlertFields & { kind: "message-request"; detail: MessageLinkRequest });

export interface AlertList {
  alerts: AlertDto[];
  /** How many alerts need an answer, counted on the Gateway. The bell shows this number and no other. */
  needsAnswerCount: number;
}

/** GET /gateway/alerts - every alert in the account. */
export async function listAlerts(signal?: AbortSignal): Promise<AlertList> {
  const res = await gatewayFetch("/gateway/alerts", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  }, { timeoutMs: POLL_TIMEOUT_MS });
  if (!res.ok) throw await GatewayError.from(res, "load the alerts");
  const body = (await res.json()) as { alerts?: unknown; needsAnswerCount?: unknown };
  // A successful body that does not carry the array is NOT an empty list. Reading it as one would take a pending
  // approval off the screen while telling the owner nothing needs him - a malformed answer reaches the screen as an
  // error and the last-known list stays standing. The same holds for the count the bell shows.
  if (!Array.isArray(body.alerts)) {
    throw new GatewayError(res.status, "The Gateway answered the alert list without an 'alerts' array; showing the last-known list.");
  }
  if (typeof body.needsAnswerCount !== "number") {
    throw new GatewayError(res.status, "The Gateway answered the alert list without a 'needsAnswerCount'; showing the last-known list.");
  }
  return { alerts: body.alerts as AlertDto[], needsAnswerCount: body.needsAnswerCount };
}

/** POST /gateway/alerts/seen - mark these alerts seen at their current version. */
export async function markAlertsSeen(ids: string[], signal?: AbortSignal): Promise<void> {
  const res = await gatewayFetch("/gateway/alerts/seen", {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ ids }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "mark the alerts seen");
}

/** POST /gateway/alerts/{id}/clear. An alert that still needs an answer is refused with a 409 whose sentence is the
 *  Gateway's, and that sentence is the error's message. */
export async function clearAlert(id: string, signal?: AbortSignal): Promise<void> {
  const res = await gatewayFetch(`/gateway/alerts/${encodeURIComponent(id)}/clear`, {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "clear the alert");
}

/** POST /gateway/alerts/clear - clear every alert that does not need an answer. */
export async function clearAllAlerts(signal?: AbortSignal): Promise<void> {
  const res = await gatewayFetch("/gateway/alerts/clear", {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "clear the alerts");
}

/** The session that asked for this alert, from the feature's own record, or null when no session asked. */
export function askingSessionId(alert: AlertDto): string | null {
  switch (alert.kind) {
    case "restart-request":
      return alert.detail.requestedBySessionId || null;
    case "secret-transfer":
      return alert.detail.askedBySessionId ?? null;
    case "message-request":
      return alert.detail.requesterSessionId || null;
  }
}

/** Newest first by when the alert last changed. An alert whose time will not parse sorts last, never drops out. */
export function newestFirst(alerts: AlertDto[]): AlertDto[] {
  const at = (a: AlertDto) => {
    const ms = Date.parse(a.updatedAtUtc);
    return Number.isNaN(ms) ? Number.NEGATIVE_INFINITY : ms;
  };
  return [...alerts].sort((a, b) => at(b) - at(a));
}

/** A wall-clock time in the viewer's locale ("21:41"), or the raw value when it will not parse - never blank. */
export function alertClock(iso: string): string {
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return iso;
  return new Date(ms).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

/** The bell's badge text: the count, or "9+" above nine. Empty at zero, when no badge is drawn. */
export function badgeText(count: number): string {
  if (count <= 0) return "";
  return count > 9 ? "9+" : String(count);
}

/** The bell's accessible name: "Alerts", "Alerts, 1 needs you", "Alerts, 2 need you". */
export function bellLabel(count: number): string {
  if (count <= 0) return "Alerts";
  return count === 1 ? "Alerts, 1 needs you" : `Alerts, ${count} need you`;
}
