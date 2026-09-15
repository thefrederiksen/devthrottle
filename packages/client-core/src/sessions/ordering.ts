// Client-side ordering helpers for the roster. Presentation state is Gateway-owned: /sessions must
// provide effectiveColor and triageBucket. The browser shell is deliberately dumb and fails loudly
// if that contract is broken instead of reconstructing the Gateway state machine in TypeScript.
import type { SessionDto } from "../api/client";

export type TriageBucket = "needsYou" | "active" | "onHold";
type GatewayStampedSession = SessionDto & {
  effectiveColor?: string | null;
  stateLabel?: string | null;
  triageBucket?: TriageBucket | string | null;
  // Snooze Length mission: display-only marker that this session just returned from an EXPIRED snooze
  // (its Gateway-owned timer elapsed and the fold put it back into "needs you" on its own). The Gateway
  // stamps it; clients render a distinct "Snooze ended" badge. Optional (absent/false for most sessions).
  snoozeExpired?: boolean | null;
  // The armed-snooze deadline (Gateway-owned snooze clock), so a client can render the hold time
  // ("wakes in 3h 48m"). Null when there is no running clock: not snoozed, or a deferred snooze that has
  // not landed yet. The generated schema does not carry it, so it is augmented here like the fields above.
  snoozeUntil?: string | null;
  // The Gateway-owned hold tri-state ("None" | "Held" | "DeferredHold"). A DeferredHold is a snooze asked
  // for while the agent was working: accepted, but its clock only starts when the work ends, so onHold is
  // still false. Clients read this to tell a deferred snooze apart from "not snoozed" (onHold cannot). The
  // generated schema does not carry it, so it is augmented here like the fields above.
  holdState?: string | null;
  // The session is flagged for deletion and awaiting the reaper - drives the "winding down" badge. A BADGE,
  // NEVER A COLOUR: the fold does not read it, so the dot keeps telling the truth about the work while this
  // rides beside it (mirrors the desktop rail's IsPendingDeletion). Optional; false for most sessions.
  pendingDeletion?: boolean | null;
  deletionReason?: string | null;
  // Where the Wingman is with this row's last stop: "none" | "reading" | "judged" | "failed", stamped by the
  // Gateway on every fold (the Wingman-on-every-turn mission). The calm band below SELECTS on it and nothing
  // interprets it. The generated schema does not carry it yet, so it is augmented here like the fields above.
  verdictState?: string | null;
};

// The stable "desktop order": honor the owning Director's SortOrder (the user-controlled,
// drag-to-reorder, persisted order), then CreatedAt as a deterministic tie-break.
export function inDesktopOrder(sessions: SessionDto[]): SessionDto[] {
  return [...sessions].sort((a, b) => {
    // sortOrder is typed number|string in the generated schema (the serializer may emit a
    // numeric string); coerce so the comparison is always numeric.
    const so = Number(a.sortOrder ?? 0) - Number(b.sortOrder ?? 0);
    if (so !== 0) return so;
    return String(a.createdAt ?? "").localeCompare(String(b.createdAt ?? ""));
  });
}

// One cc-director's group in the roster's "My order" view: the sessions of a single Director, under a
// "computer:port" header. sortOrder is the OWNING Director's per-Director drag order, so it only orders
// sessions WITHIN a Director - which is exactly why the roster groups by Director and sorts each group
// by inDesktopOrder, rather than trying to interleave sortOrders that mean different things on different
// machines.
export interface DirectorGroup {
  /** The owning Director's id - the group key, unique per running cc-director. */
  directorId: string;
  /** The machine the Director runs on (from the sessions' machineName), for the header. */
  machineName: string;
  /** The Director's Control API port, or "" when unknown - the header's disambiguator. */
  port: string;
  /** This Director's sessions, in desktop (drag) order. */
  sessions: SessionDto[];
}

// Group sessions into their owning cc-director, ordered for the roster's "My order" view: each group's
// sessions are in desktop order, and the groups themselves are ordered by machine name then port so the
// list is stable (sortOrder cannot order ACROSS Directors, since it is per-Director). `portByDirector`
// maps a directorId to its Control API port (from GET /directors); a missing entry yields an empty port
// and the header degrades to the bare machine name.
export function groupByDirector(
  sessions: SessionDto[],
  portByDirector: Map<string, string>,
): DirectorGroup[] {
  const byDirector = new Map<string, DirectorGroup>();
  for (const s of sessions) {
    const directorId = (s.directorId ?? "").trim();
    // A session with no directorId cannot be attributed to a cc-director; bucket it under a stable empty
    // key so it still shows (rather than vanishing) instead of guessing an owner.
    const group = byDirector.get(directorId);
    if (group) {
      group.sessions.push(s);
    } else {
      byDirector.set(directorId, {
        directorId,
        machineName: (s.machineName ?? "").trim(),
        port: portByDirector.get(directorId) ?? "",
        sessions: [s],
      });
    }
  }
  const groups = [...byDirector.values()];
  for (const g of groups) g.sessions = inDesktopOrder(g.sessions);
  groups.sort((a, b) => {
    const byMachine = a.machineName.localeCompare(b.machineName);
    if (byMachine !== 0) return byMachine;
    return a.port.localeCompare(b.port, undefined, { numeric: true });
  });
  return groups;
}

// The ONE rule for reading a Gateway-stamped presentation field: it is there, or we fail loudly. A
// client that cannot get a stamped answer must never guess one - a guessed colour is indistinguishable
// from a real one on screen, so it does not degrade gracefully, it lies quietly.
//
// Exported because not every stamped surface projects a SessionDto: /exes/list has its own narrower
// row type carrying the same stamped fields, and it must fail by the same rule with the same message
// rather than growing a second, drifting copy of this check.
export function requireGatewayField(value: string | null | undefined, field: string, sid: string | undefined): string {
  const text = value?.trim();
  if (!text) {
    throw new Error(`Gateway /sessions missing ${field} for session ${sid ?? "(unknown)"}. Redeploy Gateway and mobile together.`);
  }
  return text;
}

// The ONE effective status color every client renders and triages on. It is stamped by the Gateway
// after folding on-hold, transcribing, explaining, briefing, and voice-generation state.
export function effectiveColor(s: SessionDto): string {
  return requireGatewayField((s as GatewayStampedSession).effectiveColor, "effectiveColor", s.sessionId);
}

// The ONE human-readable state label every client renders, stamped by the Gateway from the same fold
// as effectiveColor (so the dot color and its label never disagree). Clients render this instead of
// re-deriving a label from the raw color or activity state.
export function stateLabel(s: SessionDto): string {
  return requireGatewayField((s as GatewayStampedSession).stateLabel, "stateLabel", s.sessionId);
}

// Snooze Length mission: true when this session just RETURNED from an expired snooze (its Gateway-owned
// timer fired and put it back into "needs you" on its own). Clients render a distinct "Snooze ended"
// badge so the owner knows it is a "go investigate why it went quiet" item, not a fresh turn-end.
// Non-throwing (optional field): a session without the marker is simply not returned-from-snooze.
export function snoozeExpired(s: SessionDto): boolean {
  return Boolean((s as GatewayStampedSession).snoozeExpired);
}

// True when this session carries a DEFERRED snooze: one asked for while the agent was working, so its
// clock has not started (it arms when the work ends - owner ruling). The raw onHold flag CANNOT see this
// (a deferred hold reads onHold=false), so a snooze surface reads this to tell "accepted, will snooze
// when it finishes" apart from "not snoozed at all". Non-throwing (optional Gateway field).
export function isDeferredHold(s: SessionDto): boolean {
  return (s as GatewayStampedSession).holdState === "DeferredHold";
}

// "wakes in 3h 48m" - how long until an armed snooze returns this session to needs-you, read from the
// Gateway-owned snooze clock (snoozeUntil). Returns null when there is no running clock: not snoozed, or a
// deferred snooze that has not landed (no deadline yet). The Director never owns the clock; the client only
// renders the countdown. The wording matches the desktop rail's HoldTimeLabel exactly, so the hold time
// reads identically on the rail, the Cockpit and the phone. `nowMs` is injectable for deterministic tests.
export function snoozeCountdown(s: SessionDto, nowMs: number = Date.now()): string | null {
  const raw = (s as GatewayStampedSession).snoozeUntil?.trim();
  if (!raw) return null;
  const until = Date.parse(raw);
  if (Number.isNaN(until)) return null;
  const remainingMs = until - nowMs;
  if (remainingMs <= 0) return "waking up";
  const totalMin = Math.floor(remainingMs / 60000);
  if (totalMin < 1) return "wakes in <1m";
  if (totalMin < 60) return `wakes in ${totalMin}m`;
  const h = Math.floor(totalMin / 60);
  const m = totalMin % 60;
  return m > 0 ? `wakes in ${h}h ${m}m` : `wakes in ${h}h`;
}

// True when this session is flagged for deletion and awaiting the reaper - drives the "winding down"
// badge. A BADGE, NEVER A COLOUR (owner's ruling): pending deletion says nothing about what the agent is
// DOING - a flagged session may still be working - so the dot keeps telling the truth and this rides
// beside it. Non-throwing (optional field); mirrors the desktop rail's IsPendingDeletion.
export function pendingDeletion(s: SessionDto): boolean {
  return Boolean((s as GatewayStampedSession).pendingDeletion);
}

// The human reason captured when a session was flagged for deletion (e.g. "jobs-auto: nothing to report"),
// or null when none was given - surfaced as the winding-down badge's tooltip.
export function deletionReason(s: SessionDto): string | null {
  const r = (s as GatewayStampedSession).deletionReason?.trim();
  return r ? r : null;
}

// True while the agent is actively running a turn - the "working" state. Blue is the authoritative
// working color (blue = agent working / a turn is in progress). Used to retire a now-stale Wingman
// voice cue: the roster play-triangle is shown only while a session is red / parked and is removed the
// instant it starts working again (you no longer want to hear the finished-turn narration).
//
// THE LAW (2026-07-14): a working session is BLUE, always - so blue IS working, and nothing else gets
// a vote. This used to open with `if (s.onHold) return false`, a client-side override that made a
// snoozed session report NOT working even while the Gateway said blue. That is the client re-deriving
// state, which is exactly what this module exists to prevent: the Gateway owns the fold, clients render
// it. The Gateway now applies the working check at the top of its own ladder, so a snoozed session that
// starts working arrives here already stamped blue and must be reported as working.
export function isWorking(s: SessionDto): boolean {
  return effectiveColor(s).toLowerCase() === "blue";
}

// Classify a session for triage. The Gateway owns this fold; the client consumes the stamped bucket.
export function classify(s: SessionDto): TriageBucket {
  const stamped = requireGatewayField((s as GatewayStampedSession).triageBucket, "triageBucket", s.sessionId);
  if (stamped === "needsYou" || stamped === "active" || stamped === "onHold") return stamped;
  throw new Error(`Gateway /sessions returned invalid triageBucket '${stamped}' for session ${s.sessionId ?? "(unknown)"}.`);
}

export function inBucket(sessions: SessionDto[], bucket: TriageBucket): SessionDto[] {
  return inDesktopOrder(sessions.filter((s) => classify(s) === bucket));
}

// MAY THIS SESSION NAG THE HUMAN? The Gateway's own answer, read verbatim (SessionDto.MachineReachable).
//
// The roster now serves the sessions of a machine nobody can reach - dimmed and dated instead of deleted
// - and the moment it does, "needs you" splits into two questions that used to be one: SHOW it (yes, the
// work is real and the owner should see it) and NAG about it (no, there is nothing anybody can do about
// a laptop that is asleep). A badge lit all night over three red sessions on a machine nobody can act on
// is not information, it is noise the owner cannot switch off.
//
// ONLY AN EXPLICIT false SUPPRESSES. Undefined and null are NOT unreachable: an older Gateway does not
// stamp the field at all, and a Director-local response leaves it null because the question is
// meaningless there (the answering Director IS the machine). Treating "I was not told" as "unreachable"
// would silently switch the badge off for everyone on a mixed-version deploy, which is a worse failure
// than a badge that counts one session too many - a missing nag is invisible, and the owner would never
// learn the phone had stopped telling them.
//
// It is NEVER re-derived. The same fact also rides on the roster envelope's per-machine reachability
// list, and a client could join the two itself - one of them already did, for the voice queue - but a
// join is a rule, and a rule computed in two clients is two rules that drift.
export function machineCanBeActedOn(s: SessionDto): boolean {
  return s.machineReachable !== false;
}

// The count behind the app-icon "needs you" dot, on every surface. Needs-you sessions whose owning
// machine is unreachable are counted OUT: they stay visible on the roster (the "Needs you" group still
// lists them, dimmed and dated) but they do not light the badge. The badge and the group are two
// different questions, and this function answers only the badge's.
export function needsYouBadgeCount(sessions: SessionDto[]): number {
  return sessions.filter((s) => classify(s) === "needsYou" && machineCanBeActedOn(s)).length;
}

// The "waiting line" order for the needs-you group (the mobile roster's top group). The session that
// has been asking for you the LONGEST sits at the top; a session that only just started needing you
// drops in at the BOTTOM. This keeps the list from reshuffling under you as new work arrives and
// makes it a natural first-in, first-handled queue when you work from the top down. Ordered by
// needsYouSince ascending - the earliest stamp is the oldest wait. A session with no parseable stamp
// sorts to the bottom, and createdAt then sessionId break ties so equal waits never jitter between
// polls. This intentionally ignores the drag-to-reorder desktop SortOrder: the needs-you group is a
// queue by wait time, not the user's manual arrangement.
//
// THE CALM BAND (the Wingman-on-every-turn mission, slice D). After every needs-you row come the rows the
// Wingman judged a REPORT: the Gateway stamped them cyan ("Done") or purple ("Carrying on") with verdictState
// "judged". They keep a place in the attention view, below the reds, and they are not counted - the needs-you
// heading and needsYouBadgeCount read the stamped bucket, which is "active" for them. Ordered by the same
// waiting-line rule, so the band does not reshuffle between polls either.
export function inWaitingOrder(sessions: SessionDto[]): SessionDto[] {
  const needsYou = sessions.filter((s) => classify(s) === "needsYou").sort(byWaitingLine);
  const calm = sessions.filter(isInCalmBand).sort(byWaitingLine);
  return [...needsYou, ...calm];
}

// Is this row in the calm band? Selected by the Gateway's stamped strings and nothing else: a calm colour AND
// an accepted verdict. A brand-new session is green, which is not a calm colour, so it is not in the band. A
// snoozed row is grey, so a snooze keeps a row out of the band by the Gateway's own ladder. The C# port is
// SessionOrdering.IsInCalmBand, and tree-agreement.json holds the two to the same answers.
export function isInCalmBand(s: SessionDto): boolean {
  const color = effectiveColor(s);
  return (color === "cyan" || color === "purple") && (s as GatewayStampedSession).verdictState === "judged";
}

function byWaitingLine(a: SessionDto, b: SessionDto): number {
  const wa = waitingSinceMs(a);
  const wb = waitingSinceMs(b);
  if (wa !== wb) return wa - wb;
  const created = String(a.createdAt ?? "").localeCompare(String(b.createdAt ?? ""));
  if (created !== 0) return created;
  return String(a.sessionId ?? "").localeCompare(String(b.sessionId ?? ""));
}

// The needsYouSince stamp parsed to epoch milliseconds for the waiting-line sort. A missing or
// unparseable stamp returns positive infinity so that session sorts to the bottom of the queue
// (we cannot place it in the line, so it never jumps ahead of a session with a real wait time).
function waitingSinceMs(s: SessionDto): number {
  const raw = String(s.needsYouSince ?? "").trim();
  if (raw.length === 0) return Number.POSITIVE_INFINITY;
  const parsed = Date.parse(raw);
  return Number.isNaN(parsed) ? Number.POSITIVE_INFINITY : parsed;
}

// Map a Gateway colour NAME to its dot hex, for the LEGEND / AGGREGATE swatches that have no session
// behind them (the fleet-map legend, the lane aggregate dots, the missions-board priority key). A real
// SESSION dot no longer reads this table - it renders the Gateway-stamped hex via dotHex() below, so the
// name->hex step is done ONCE on the Gateway and every device paints the identical pixel. This table
// survives only for the decorative swatches, which have no stamped session to read.
//
// THE DRIFT GUARD: these values must equal the canonical Gateway map (SessionColorPalette.HexFor in
// CcDirector.Gateway.Contracts). Comparing fold answers ("red" === "red") cannot see a table that paints
// "red" a different hex, so the StateAgreementCheck asserts canonical == this table == the desktop
// StatusPalette on every run. Change a value here and that check goes red until the canonical map and the
// desktop agree - which is how a legend swatch is kept from drifting from the session dot beside it.
//
// Every name below is Tailwind-500 for its own ramp, except `error`, which is deliberately red-700 so a
// crashed session reads darker than a needs-you red.
const COLORS: Record<string, string> = {
  red: "#EF4444", // needs you
  yellow: "#EAB308", // wingman narrating / preparing voice
  orange: "#F97316", // dictation in flight, or a deep dive running
  green: "#22C55E", // ready - brand-new with nothing needed. Never a finished row (issue #2892)
  cyan: "#06B6D4", // the Wingman judged the stop finished - done, or a report that asks nothing
  blue: "#3B82F6", // working - always
  purple: "#A855F7", // the Wingman judged that the session is carrying on by itself
  supporting: "#64748B", // issue #815: controlled sub-agent, recessive slate
  error: "#B91C1C", // issue #959: the agent process crashed - deep red, distinct from needs-you red
  // Parked: on hold or exited. ONE grey, on purpose. The Gateway folds both to "grey" and draws no
  // distinction between them, so no client may invent one: the snoozed/exited difference is lifecycle,
  // and lifecycle travels on the stamped label ("Snoozed" / "Exited") and a badge, never on the dot.
  // The desktop used to split this one name into two hexes by re-reading the raw OnHold flag - a
  // distinction the Gateway never made - and that re-derive is gone.
  grey: "#6B7280",
  unknown: "#6B7280", // indeterminate activity state (e.g. an unrecognized state) - rendered gray like grey
};

// Every colour name the palette knows, for the colour legend's coverage check (colourMeanings.test.ts).
export function paletteNames(): string[] {
  return Object.keys(COLORS);
}

export function dotColor(color: string): string {
  const value = COLORS[color];
  if (!value) throw new Error(`Unknown Gateway effectiveColor '${color}'.`);
  return value;
}

// The magenta protocol-error sentinel - matches SessionColorPalette.Broken and the desktop rail's
// StatusPalette.Broken. NOT a state: it is what a session dot paints when the Gateway did not stamp a
// usable hex, and it is deliberately unmissable and impossible to mistake for a real colour (grey MEANS
// snoozed/exited, so a fallback to grey would be an affirmative lie that the session is parked).
export const BROKEN_HEX = "#FF00FF";
// A #RGB or #RRGGBB hex, the only shapes the canonical palette emits.
const HEX_RE = /^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/;

// THE session dot's colour: the Gateway-stamped pixel hex (effectiveColorHex), resolved once on the
// Gateway through the canonical name->hex map and painted here verbatim. A session dot renders THIS, not
// the COLORS table above, so the phone, the Cockpit and the desktop rail all paint the identical pixel
// and no client carries a name->hex table that can drift from the others.
//
// FAIL LOUD, NEVER GUESS. A missing or unparseable stamp - an old Gateway that sends the colour NAME but
// not the hex, i.e. a mixed-version deploy - renders the magenta sentinel and logs it. It does NOT fall
// back to dotColor(effectiveColor(s)): a guessed colour is indistinguishable from a real one on screen,
// so it would not degrade gracefully, it would lie quietly. Magenta plus a console error is the honest
// answer - loud on the dot, diagnosable in the log.
export function dotHex(s: SessionDto): string {
  // Read the generated-contract property directly (schema.ts carries effectiveColorHex). Typed as
  // string | null, but a mixed-version or malformed wire can still deliver anything, so guard the runtime
  // type below rather than trusting the annotation.
  const value: unknown = s.effectiveColorHex;
  // Type-guard BEFORE trim. Optional chaining only guards null/undefined; a malformed but possible JSON
  // value (a number, object, or array) would make .trim non-callable and throw a TypeError from the React
  // render path - one bad row taking down the whole roster. A non-string stamp is a protocol error exactly
  // like a missing one: log and paint the magenta sentinel, never crash and never guess a colour.
  const raw = typeof value === "string" ? value.trim() : "";
  if (raw && HEX_RE.test(raw)) return raw;
  console.error(
    `Gateway /sessions missing or unparseable effectiveColorHex (${JSON.stringify(value)}) for session ` +
      `${s.sessionId ?? "(unknown)"}. Rendering the magenta protocol-error sentinel. Redeploy Gateway and clients together.`,
  );
  return BROKEN_HEX;
}

// One short context line per row: the Gateway's stamped label, else the latest status reason.
// Never empty so every row reads cleanly.
//
// THE LAW (2026-07-14): a working session is BLUE and reads "Working". This used to open with
// `if (s.onHold) return "Snoozed"` followed by its own dictation/transcribing ladder - a THIRD fold
// of the same question, in a third place, in a different order from the Gateway's. That is how a row
// ended up with a blue dot and the word "Snoozed" beside it. The Gateway already stamps stateLabel
// from the same inputs as the dot, so the row simply renders it: one fold, one answer, every screen.
// It FAILS LOUDLY when the Gateway did not stamp a label, exactly like stateLabel() - it does not fall
// back to raw fields. An earlier version fell back to lastStatusReason / assessedState / activityState /
// status when stateLabel was missing, which reintroduced the whole problem in miniature: against a
// mixed-version Gateway the dot and bucket came from the Gateway while the WORDS came from a local guess,
// which is the "blue dot labelled Snoozed" class of bug this module exists to prevent. Both callers
// (the Cockpit roster and the mobile home list) render Gateway /sessions rows, so a missing stamp is a
// real defect and must be seen, not painted over.
export function contextLine(s: SessionDto): string {
  return stateLabel(s);
}

// The leaf repo name for a row's secondary label.
export function repoLeaf(s: SessionDto): string {
  const path = (s.repoPath ?? "").trim();
  if (!path) return "";
  const parts = path.split(/[\\/]/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : path;
}
