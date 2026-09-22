// The fleet + machine surface of the Gateway (issue #975): the typed, same-origin client the React
// Cockpit's Fleet, Directors, and Director-detail pages read. It is the shared-library port of the
// Blazor Cockpit's GatewayClient (fleet/interrupted/rename) and DirectorClient (director settings)
// calls, so the desktop React shell and the mobile shell keep exactly one copy of each contract.
//
// Every request is root-relative to the Gateway front door (never a Director address), the same
// Gateway-only-ingress rule the rest of client-core obeys, and carries the same Bearer via
// authHeaders(). A non-2xx throws GatewayError so the caller surfaces the real reason instead of a
// silently empty list (no fallback that hides the problem).
import { authHeaders, gatewayFetch, GatewayError, POLL_TIMEOUT_MS, type SessionDto } from "../api/client";

// ===== Directors registry (GET /directors) =====

// One machine (Director) in the fleet, projected from GET /directors -> registry.ListDirectors().
// The FULL DirectorDto the registry emits (camelCase on the wire), as the Directors table and the
// Director-detail page consume it. This is the rich shape; the add-session picker's narrower
// DirectorInfo (api/client) stays separate so retargeting one never disturbs the other.
export interface FleetDirector {
  directorId: string;
  pid?: number;
  /** When the Director process started (ISO 8601 UTC). */
  startedAt?: string;
  controlEndpoint?: string;
  machineName?: string;
  user?: string;
  /** The instance's user-editable display name (devthrottle_internal#1176), e.g. "SOREN_NORTH_SLOT_2".
   * Empty/absent when unnamed or from an older Director - fall back to machineName. */
  displayName?: string;
  version?: string;
  schemaVersion?: number;
  /** When the Gateway last heard from this Director (ISO 8601), or null. */
  lastSeen?: string | null;
  tailnetEndpoint?: string | null;
  /** A flagged registration's own reason for advertising no reachable endpoint (issue #324). */
  endpointUnreachableReason?: string | null;
  /** "file" (local filesystem discovery) or "http" (push registration). */
  source?: string;
  /** When the WebSocket UPGRADE (terminal stream) path was last verified, or null. */
  streamVerifiedAt?: string | null;
  /** Set when the terminal stream leg is down while plain HTTP is reachable (cross-machine). */
  streamVerifyError?: string | null;
  /** "ok" or ENDPOINT_STATE_UNREACHABLE_BY_NAME (issue #325), or null on old Directors. */
  advertisedEndpointState?: string | null;
  advertisedEndpointCheckedAt?: string | null;
  advertisedEndpointUnreachableSince?: string | null;
  advertisedEndpointError?: string | null;
}

// The advertised-endpoint state a Director reports when it is alive (heartbeating) but the NAME it
// advertised stopped answering the Gateway's per-heartbeat probe (issue #325) - rendered distinctly
// from a full heartbeat/fan-out loss. Mirrors DirectorDto.EndpointStateUnreachableByName.
export const ENDPOINT_STATE_UNREACHABLE_BY_NAME = "unreachable-by-name";

// GET /directors - the machines registered with this Gateway, as the FULL DirectorDto. Throws
// GatewayError on non-2xx so the Directors page shows the real reason instead of an empty table.
export async function getFleetDirectors(signal?: AbortSignal): Promise<FleetDirector[]> {
  const res = await gatewayFetch("/directors", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `GET /directors failed: ${res.status}`);
  }
  // Contract is a JSON array; a non-array body must degrade to [] so the Directors table never
  // throws "x.map is not a function" (same sibling-list guard as getRecordings, issue #1050).
  const body = (await res.json()) as unknown;
  return Array.isArray(body) ? (body as FleetDirector[]) : [];
}

// ===== Roster envelope (GET /sessions?envelope=true) =====

// One machine the Gateway could not reach on the last roster read (the envelope's machineErrors), which
// the Fleet and Directors pages surface as unreachable. It is a statement about the LINK, not about the
// roster: the machine's sessions are still served (dimmed and dated), so this no longer means "its
// sessions are missing".
export interface MachineError {
  directorId?: string;
  machineName?: string;
  error?: string;
}

// The three machine states in the roster envelope (issue #1215; re-based on the tunnel by Epic #1159
// step A). A Director reads as:
//  - "online": its tunnel is up and its last push is current - this is live data.
//  - "wobbly": its tunnel is up but nothing recent has arrived - real data, going stale, machine still
//    there. Its sessions are in the roster, shown dimmed with a "last seen N seconds ago" age.
//  - "offline": its tunnel is down - real data, dated, and the machine cannot be acted on.
//
// OFFLINE NO LONGER MEANS DELETED. It used to: the Gateway dropped an offline machine's sessions from
// the envelope once a grace window expired, which is why the phone's roster blanked. The Gateway now
// serves every session it last knew about, whatever its age, so all three states carry sessions and the
// state decides only how they are RENDERED. A session leaves the roster when its Director says so or
// when the machine passes the Gateway's eviction horizon - never because a display timer ran out.
//  - "stopped": it said goodbye. The Director sent the tunnel farewell at the start of an orderly
//    shutdown, so its registration is retired and its absence is EXPECTED - not a fault, and never
//    counted as a machine the Gateway cannot reach. A Director that dies without a farewell has no such
//    stamp and still reads "offline", which is the case actually worth showing.
export const REACHABILITY_ONLINE = "online";
export const REACHABILITY_WOBBLY = "wobbly";
export const REACHABILITY_OFFLINE = "offline";
export const REACHABILITY_STOPPED = "stopped";
export type ReachabilityState =
  | typeof REACHABILITY_ONLINE
  | typeof REACHABILITY_WOBBLY
  | typeof REACHABILITY_OFFLINE
  | typeof REACHABILITY_STOPPED;

// One Director's reachability presentation in the roster envelope (issue #1215). The Cockpit joins a
// session to its Director by directorId (also stamped on SessionDto.directorId) to decide how to render
// it, so the list changes appearance IN PLACE and never reflows because of a transient miss.
export interface DirectorReachability {
  directorId: string;
  machineName?: string;
  /** The Director's user-editable display name (devthrottle_internal#1176). Empty/absent when unnamed
   * or from an older Gateway - fall back to machineName. */
  displayName?: string;
  /** "online" | "wobbly" | "offline". */
  state: ReachabilityState;
  /** When the Gateway last HEARD this machine - the arrival stamp of its newest push (ISO 8601 UTC), or
   * null if it has never pushed. */
  lastSeenUtc?: string | null;
  /**
   * Seconds since that stamp. A REAL age in every state, including online (where a healthy machine reads
   * a few seconds, not zero - the old Gateway wrote "now" here, which measured when the response was
   * assembled rather than when anything was heard). Null when the machine has never pushed.
   */
  lastSeenAgeSeconds?: number | null;
  /** The last poll's failure reason for wobbly/offline; null while online. */
  error?: string | null;
  /**
   * THE GATEWAY'S FINISHED PRESENTATION for this Director (CLAUDE.md rule 7). The four fields below are
   * rendered, never re-derived: the Cockpit used to map the state string to a badge word, a placeholder
   * sentence, whether a button appeared, and whether a card dimmed - four judgements about what a state
   * MEANS, made in a view file, in three separate places. Absent from an older Gateway, where the
   * `??` fallbacks beside each use keep the historical rendering.
   *
   * The badge word; empty while online (a healthy Director wears no badge).
   */
  stateLabel?: string;
  /** True when the rows are last-known rather than confirmed - dim the cards and show the age. */
  dataIsStale?: boolean;
  /** True when a start could actually be delivered down this Director's tunnel. */
  canStartSession?: boolean;
  /** The line to print where this Director has no sessions, saying why there are none. */
  emptySlotText?: string;
}

// The envelope shape GET /sessions returns with ?envelope=true: the live sessions, the machines that
// failed this fan-out, AND the per-Director reachability presentation (issue #1215). (Plain GET
// /sessions - api/client listSessions - returns just the array; the Fleet and Directors pages need the
// machineErrors and reachability too, so they ask for the envelope.)
export interface SessionsEnvelope {
  sessions: SessionDto[];
  machineErrors: MachineError[];
  /** Per-Director reachability for the Online / Wobbly / Offline / Not running rendering (issue #1215). */
  directors: DirectorReachability[];
  /**
   * The fleet-wide warning line, folded on the Gateway and printed VERBATIM, or null when nothing is
   * wrong. It replaces counting machineErrors in the view: those rows are per-DIRECTOR, so a view that
   * counted them and printed the word "machine" announced a dead machine whenever any one slot on it was
   * unreachable - while fifteen sessions on that machine were pushing every few seconds.
   *
   * NULL AND ABSENT ARE DIFFERENT ANSWERS and must stay distinguishable. Null is the Gateway saying
   * "nothing is wrong"; absent is an OLDER Gateway that cannot answer at all, and collapsing the second
   * into the first would silence a real outage on the very screen that reports outages. getSessionsEnvelope
   * fills an absent field from machineErrors instead - see there.
   */
  unreachableBanner: string | null;
}

/**
 * The fallback warning line for an OLDER Gateway that does not fold one - built from machineErrors, which
 * such a Gateway does still populate. Deliberately Director-shaped ("1 director could not be reached"),
 * because that is what those rows have always been; the old view called them machines and that was the
 * defect. Exported for its test.
 */
export function bannerFromMachineErrors(errors: MachineError[]): string | null {
  const named = errors
    .map((m) => (m.machineName ?? "").trim())
    .map((n) => (n.length > 0 ? n : "an unidentified machine"));
  if (named.length === 0) return null;
  const head = named.length === 1 ? "1 director could not be reached" : `${named.length} directors could not be reached`;
  return `${head} on the last sweep: ${named.join(", ")}`;
}

// GET /sessions?envelope=true - the roster plus the unreachable-machine list and per-Director
// reachability. Throws GatewayError on non-2xx so the page surfaces the failure rather than showing a
// silently empty roster.
//
// TRAFFIC OPTIMIZATION, PHASE 2: the roster is asked for WITHOUT its clock fields (clockFields=absolute), so an
// unchanged roster is byte-identical from one poll to the next and the Gateway answers 304 with no body. The
// Gateway used to recompute two fields from its own clock on every read - sessions[].idleSeconds and
// directors[].lastSeenAgeSeconds - so no two polls ever matched. Those two are rebuilt here by
// restoreRosterClockFields, from the absolute timestamps the answer carries and the X-Gateway-Time header: the
// instant the Gateway measured them against. So every reader of this envelope sees the same values it always
// did, computed by the same rule, and none of them depends on this device's clock being right.
//
// This function makes the conditional request ITSELF (its own If-None-Match, cache "no-store") instead of leaving
// it to the browser's HTTP cache, because the Gateway time must be the one on THIS answer - a 304 carries its own
// - and what a page sees of a 304's headers through the browser cache varies by browser.
export const ROSTER_PATH = "/sessions?envelope=true&clockFields=absolute";
export const GATEWAY_TIME_HEADER = "X-Gateway-Time";

let rosterHeld: { etag: string; body: Partial<SessionsEnvelope> } | null = null;

/**
 * Forget the held roster body. Used by the tests. Nothing else needs to: after an account switch the held tag is
 * sent once, and a 304 would mean the new account's roster is byte-identical to the held one - so the held body
 * is then exactly what that account would have been sent.
 */
export function resetRosterHeld(): void {
  rosterHeld = null;
}

export async function getSessionsEnvelope(signal?: AbortSignal): Promise<SessionsEnvelope> {
  // The mobile roster polls this every couple of seconds; cap it so a hung request cannot leave the
  // health signal stuck "good" during an outage (mobile-resilience mission, Phase 4).
  const held = rosterHeld;
  const headers = new Headers(authHeaders());
  headers.set("Accept", "application/json");
  if (held) headers.set("If-None-Match", held.etag);
  const res = await gatewayFetch(ROSTER_PATH, {
    method: "GET",
    headers,
    cache: "no-store",
    signal,
  }, { timeoutMs: POLL_TIMEOUT_MS });
  let raw: Partial<SessionsEnvelope>;
  if (res.status === 304 && held) {
    // "What you hold is what I would send you": the ETag is the hash of the exact bytes, so this is the answer.
    raw = held.body;
  } else {
    if (!res.ok) {
      throw new GatewayError(res.status, `GET /sessions?envelope=true failed: ${res.status}`);
    }
    raw = (await res.json()) as Partial<SessionsEnvelope>;
    const etag = res.headers.get("ETag");
    rosterHeld = etag ? { etag, body: raw } : null;
  }
  const body = restoreRosterClockFields(raw, res.headers.get(GATEWAY_TIME_HEADER));
  const machineErrors = body.machineErrors ?? [];
  return {
    sessions: body.sessions ?? [],
    machineErrors,
    directors: body.directors ?? [],
    // ABSENT means an older Gateway, and it must not read as "nothing is wrong" - that would leave the
    // Fleet Map silent during a real outage. Only a field the Gateway actually sent (including an explicit
    // null, which IS the "nothing is wrong" answer) is taken at face value; otherwise the line is rebuilt
    // from machineErrors, which every Gateway populates.
    unreachableBanner:
      body.unreachableBanner !== undefined ? body.unreachableBanner : bannerFromMachineErrors(machineErrors),
  };
}

// Seconds since the Unix epoch for an ISO 8601 timestamp as the Gateway writes it (to the tick, e.g.
// "2026-09-21T12:00:04.1234567Z"), or null when it does not parse. Parsed by hand rather than with Date.parse,
// which keeps only milliseconds and whose handling of more than three fraction digits is not specified. A
// timestamp with no zone is read as UTC: every one the Gateway sends is UTC.
export function isoToEpochSeconds(iso: string | null | undefined): number | null {
  if (typeof iso !== "string") return null;
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(\.\d+)?(Z|[+-]\d{2}:\d{2})?$/.exec(iso.trim());
  if (!m) return null;
  const whole = Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]) / 1000;
  const fraction = m[7] ? Number(m[7]) : 0;
  let offset = 0;
  if (m[8] && m[8] !== "Z") {
    const sign = m[8][0] === "-" ? -1 : 1;
    offset = sign * (Number(m[8].slice(1, 3)) * 3600 + Number(m[8].slice(4, 6)) * 60);
  }
  return whole + fraction - offset;
}

/**
 * Rebuild the two clock fields the roster was asked to leave out, exactly as the Gateway computes them
 * (PushedSessionStore.RecomputeClocks and the /sessions reachability fold), against the instant in `gatewayTime`:
 *   - a session with lastActivityAt and no idleSeconds: idleSeconds = gatewayTime - lastActivityAt, never below 0;
 *   - a director with no lastSeenAgeSeconds: gatewayTime - lastSeenUtc, never below 0, and null when lastSeenUtc is.
 * With no gatewayTime the answer is taken as it came ONLY when it still carries the old fields - a Gateway that
 * did not take the parameter. An answer that left the fields out but arrived without the time (a proxy that
 * stripped the header, a Gateway defect) cannot be rebuilt, and is an error rather than a roster with every age
 * quietly blank. The body given is never changed; held copies stay as they arrived.
 */
export function restoreRosterClockFields(
  body: Partial<SessionsEnvelope>,
  gatewayTime: string | null,
): Partial<SessionsEnvelope> {
  const now = isoToEpochSeconds(gatewayTime);
  if (now === null) {
    const missing =
      (body.sessions ?? []).some((s) => s.idleSeconds === undefined && s.lastActivityAt != null) ||
      (body.directors ?? []).some((d) => d.lastSeenAgeSeconds === undefined);
    if (!missing) return body;
    const why = gatewayTime === null ? "no" : `an unreadable (${gatewayTime})`;
    const message =
      `The roster arrived without its idle times and last-seen ages, and with ${why} ${GATEWAY_TIME_HEADER} header ` +
      "to compute them from. Something between this device and the Gateway may be removing that header.";
    console.error(`[fleetClient] ${message}`);
    throw new Error(message);
  }
  const sessions = body.sessions?.map((s) => {
    if (s.idleSeconds !== undefined) return s;
    const last = isoToEpochSeconds(s.lastActivityAt as string | null | undefined);
    if (last === null) return s;
    const idle = now - last;
    return { ...s, idleSeconds: idle > 0 ? idle : 0 };
  });
  const directors = body.directors?.map((d) => {
    if (d.lastSeenAgeSeconds !== undefined) return d;
    const seen = isoToEpochSeconds(d.lastSeenUtc);
    return { ...d, lastSeenAgeSeconds: seen === null ? null : Math.max(0, now - seen) };
  });
  return { ...body, sessions, directors };
}

// Join a session to its Director's reachability (issue #1215). Returns undefined when the envelope
// carries no reachability for that Director (an older Gateway, or a Director that is fully Online with
// no entry) - the caller then renders the session normally (Online). This is how a session's card
// changes appearance IN PLACE (dimmed while Wobbly) instead of the list reflowing on a transient miss.
export function reachabilityFor(
  directors: DirectorReachability[],
  directorId: string | null | undefined,
): DirectorReachability | undefined {
  if (!directorId) return undefined;
  return directors.find((d) => d.directorId === directorId);
}

// The "last seen N ago" age label for a Wobbly/Offline card. Empty for a missing or non-positive age.
// The age is real in every state now, so the CALLER decides when to show it: every caller renders it
// only on a wobbly/offline row, because "last seen 4s ago" beside a healthy machine is noise.
export function reachabilityLastSeen(ageSeconds: number | null | undefined): string {
  if (ageSeconds === null || ageSeconds === undefined || ageSeconds <= 0) return "";
  if (ageSeconds < 60) return `last seen ${Math.round(ageSeconds)}s ago`;
  if (ageSeconds < 3600) return `last seen ${Math.floor(ageSeconds / 60)}m ago`;
  return `last seen ${Math.floor(ageSeconds / 3600)}h ago`;
}

// PATCH /sessions/{sid} { name } - rename a session; the Gateway routes to the owning Director and
// returns the updated SessionDto (with the new name). Empties/whitespace are the caller's business;
// the Gateway trims and echoes the applied name.
export async function renameSession(sessionId: string, name: string, signal?: AbortSignal): Promise<SessionDto> {
  const sid = encodeURIComponent(sessionId);
  const res = await gatewayFetch(`/sessions/${sid}`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ name }),
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `PATCH /sessions/${sessionId} (rename) failed: ${res.status}`);
  }
  return (await res.json()) as SessionDto;
}

// ===== Interrupted sessions (issue #212 W3/W4) =====

// One session lost to an unexpected Director shutdown, from the crash journal a live Director on the
// same machine reports (GET /interrupted). Grouped in the UI by dead Director + pid.
export interface InterruptedSession {
  sessionId: string;
  name?: string | null;
  repoPath?: string;
  agent?: string;
  claudeSessionId?: string | null;
  /** ISO 8601 UTC when the session was created. */
  createdAtUtc?: string;
  deadDirectorId: string;
  deadPid: number;
  machineName?: string;
  user?: string;
  /** ISO 8601 UTC when the owning Director died. */
  diedAtUtc?: string;
  /** The live Director that reported this journal - the routing key ("via") for dismiss/restore. */
  reportedByDirectorId: string;
  /** The Gateway-enriched last wingman read for this session, if known. */
  railLine?: string | null;
  /** The Gateway-enriched "was working on" headline, if known. */
  headline?: string | null;
}

// The result of restoring an interrupted session: whether a continuation session was created and,
// if so, that new session (its sessionId is the jump link).
export interface RestoreInterruptedResult {
  restored: boolean;
  targetSession?: SessionDto | null;
  contextSent?: string | null;
  journalCleaned: boolean;
}

// GET /interrupted - every interrupted session across the fleet, newest death first. Throws
// GatewayError on non-2xx.
export async function getInterrupted(signal?: AbortSignal): Promise<InterruptedSession[]> {
  const res = await gatewayFetch("/interrupted", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `GET /interrupted failed: ${res.status}`);
  }
  // Contract is a JSON array; a non-array body must degrade to [] so the Fleet interrupted-cards
  // grouping never throws "x.map is not a function" (sibling-list guard, issue #1050).
  const body = (await res.json()) as unknown;
  return Array.isArray(body) ? (body as InterruptedSession[]) : [];
}

// DELETE /interrupted/{deadDirectorId}/{deadPid}?via={reportedBy} - dismiss a WHOLE crash journal
// (all of its sessions). Routed to the live Director that reported it via the required `via` param.
export async function dismissInterruptedJournal(
  deadDirectorId: string,
  deadPid: number,
  reportedByDirectorId: string,
  signal?: AbortSignal,
): Promise<void> {
  const dir = encodeURIComponent(deadDirectorId);
  const via = encodeURIComponent(reportedByDirectorId);
  const res = await gatewayFetch(`/interrupted/${dir}/${deadPid}?via=${via}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `DELETE interrupted journal failed: ${res.status}`);
  }
}

// DELETE /interrupted/{deadDirectorId}/{deadPid}/sessions/{sessionId}?via={reportedBy} - dismiss ONE
// session from a journal, keeping its siblings in the list.
export async function dismissInterruptedSession(
  deadDirectorId: string,
  deadPid: number,
  sessionId: string,
  reportedByDirectorId: string,
  signal?: AbortSignal,
): Promise<void> {
  const dir = encodeURIComponent(deadDirectorId);
  const sid = encodeURIComponent(sessionId);
  const via = encodeURIComponent(reportedByDirectorId);
  const res = await gatewayFetch(`/interrupted/${dir}/${deadPid}/sessions/${sid}?via=${via}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `DELETE interrupted session failed: ${res.status}`);
  }
}

// POST /interrupted/{deadDirectorId}/{deadPid}/restore { sessionId, via } - create a continuation
// session seeded with this session's surviving turn-brief context and pull the row from the journal.
// Returns the restore result whose targetSession.sessionId is the new session to jump to.
export async function restoreInterrupted(
  deadDirectorId: string,
  deadPid: number,
  sessionId: string,
  reportedByDirectorId: string,
  signal?: AbortSignal,
): Promise<RestoreInterruptedResult> {
  const dir = encodeURIComponent(deadDirectorId);
  const res = await gatewayFetch(`/interrupted/${dir}/${deadPid}/restore`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ sessionId, via: reportedByDirectorId }),
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `POST restore interrupted failed: ${res.status}`);
  }
  const body = (await res.json()) as Partial<RestoreInterruptedResult>;
  return {
    restored: Boolean(body.restored),
    targetSession: body.targetSession ?? null,
    contextSent: body.contextSent ?? null,
    journalCleaned: Boolean(body.journalCleaned),
  };
}

// ===== Director settings (GET/PUT /directors/{id}/settings) =====

// The settings body is an OPAQUE, arbitrary JSON object the Director owns - the Gateway forwards it
// verbatim (SessionWsProxyEndpoints). So it is read and written as raw JSON text, exactly like the
// Blazor DirectorClient (GetSettingsAsync/PutSettingsAsync). Routed by DIRECTOR id, not session id.

// GET /directors/{id}/settings - the Director's current settings as the raw JSON text it emits.
export async function getDirectorSettings(directorId: string, signal?: AbortSignal): Promise<string> {
  const id = encodeURIComponent(directorId);
  const res = await gatewayFetch(`/directors/${id}/settings`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `GET /directors/${directorId}/settings failed: ${res.status}`);
  }
  return res.text();
}

// PUT /directors/{id}/settings - write the Director's settings from raw JSON text; the Director
// re-applies live. The caller validates the JSON (JSON.parse) before calling, so a malformed edit
// never reaches the wire.
export async function putDirectorSettings(directorId: string, json: string, signal?: AbortSignal): Promise<void> {
  const id = encodeURIComponent(directorId);
  const res = await gatewayFetch(`/directors/${id}/settings`, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: json,
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `PUT /directors/${directorId}/settings failed: ${res.status}`);
  }
}
