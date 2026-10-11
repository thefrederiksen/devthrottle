// Secret Handoff (issue #2943, phase 5): a secret moves between two of the owner's machines only with the owner's one
// approval, given wherever they are asked - the phone, the Cockpit card, or the badge on the session that asked. The
// Gateway writes every sentence on a transfer (summary, statusText, replaceNote, askedBy) and decides whether it can
// still be answered (canAnswer); this file only carries the request and the words. No route here takes or returns a
// secret: a transfer is names, machines, a reason and an answer.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/** Where an answer is given, as the Gateway names it. The phone answers "phone"; the Cockpit "cockpit" or "badge". */
export type SecretTransferPlace = "phone" | "cockpit" | "badge";

/** One transfer, as GET /gateway/secrets/transfers lists it (SecretTransferDto). */
export interface SecretTransfer {
  transferId: string;
  entry: string;
  targetName: string;
  fromMachine: string;
  toMachine: string;
  replace: boolean;
  askedBySessionId?: string | null;
  /** Who asked, in the Gateway's words: 'Session 042 "Name"', "You, on the phone", and so on. */
  askedBy: string;
  /** Why it is needed, in the asker's words. */
  reason: string;
  /** waiting, approved, denied, expired, delivered or failed. */
  state: string;
  createdAtUtc: string;
  expiresAtUtc: string;
  answeredWhere?: string | null;
  answeredBy?: string | null;
  answeredAtUtc?: string | null;
  outcome?: string | null;
  finishedAtUtc?: string | null;
  /** "devlinux from SOREN_NORTH to devthrottle-mac-mini", folded on the Gateway. */
  summary: string;
  /** "Replaces the entry already on MACHINE." when it replaces one; otherwise null. */
  replaceNote?: string | null;
  /** Where it stands, in one sentence. */
  statusText: string;
  /** True only while it waits and has not expired - the only time Approve and Deny are offered. */
  canAnswer: boolean;
}

export interface SecretTransferList {
  transfers: SecretTransfer[];
  /** How far back finished transfers are listed, in hours. */
  finishedWithinHours: number;
}

/** GET /gateway/secrets/transfers: every waiting transfer, and the ones that finished lately. */
export async function listSecretTransfers(signal?: AbortSignal): Promise<SecretTransferList> {
  const res = await gatewayFetch("/gateway/secrets/transfers", {
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "load the secret transfers");
  return (await res.json()) as SecretTransferList;
}

/** POST /gateway/secrets/transfers/{id}/answer with exactly one of approve or deny, and where it was answered. A refusal
 *  - "already approved in the cc-secrets window", say - throws a GatewayError whose message is the Gateway's sentence. */
export async function answerSecretTransfer(
  transferId: string,
  approve: boolean,
  where: SecretTransferPlace,
): Promise<{ transfer: SecretTransfer; note: string }> {
  const res = await gatewayFetch(`/gateway/secrets/transfers/${encodeURIComponent(transferId)}/answer`, {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify(approve ? { approve: true, where } : { deny: true, where }),
  });
  if (!res.ok) throw await GatewayError.from(res, "answer the secret transfer");
  return (await res.json()) as { transfer: SecretTransfer; note: string };
}

/** How long a waiting transfer has left, from the Gateway's expiry time: "14 minutes left", "1 minute left",
 *  "under a minute left". Formatting a time is the client's job; whether it can still be answered is canAnswer's. */
export function timeLeftWords(expiresAtUtc: string, nowMs: number): string {
  const ms = Date.parse(expiresAtUtc) - nowMs;
  if (Number.isNaN(ms)) return `Expires at ${expiresAtUtc}`;
  const minutes = Math.floor(ms / 60_000);
  if (minutes < 1) return "under a minute left";
  return minutes === 1 ? "1 minute left" : `${minutes} minutes left`;
}
