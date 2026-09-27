// The DevThrottle Stats "Your Throttle" surface: the typed, same-origin client both the Cockpit and the
// mobile app read to render the throttle dashboard natively.
//
// The Gateway serves the figure at GET /stats/data (see StatsPageEndpoint). EVERY COUNT OF TURNS ON IT
// COMES FROM ONE DEFINITION OVER THE SUBMISSION LEDGER (mission "Clean up Your Throttle", 2026-09-05,
// ruling R9): the Gateway computes the figure, states the window it covers and the definition it used,
// and discloses what it left out. This client is DUMB on purpose (CLAUDE.md rule 7): it normalizes the
// wire shape and renders the Gateway's FINISHED headline - the shares, their denominator, the rounded
// percentages and the empty state all arrive computed (final inspection finding F-01). It never divides a
// count, never re-totals a bucket, and never decides what a state means - a self-hosted Gateway answers with
// a sentence, and the pages render that sentence verbatim.
//
// No character volume anywhere here (ruling R16): the ledger carries none, and a number the page cannot
// vouch for is not shown with an apology attached. Read-only; every request is root-relative to the
// Gateway and carries the same Bearer via authHeaders().
import { authHeaders, GatewayError } from "../api/client";

/** How a unit of input was produced. */
export type Modality = "typed" | "voice";

/** Which surface the operator drove from. */
export type Surface = "desktop" | "cockpit" | "phone" | "unknown";

/** One (modality, surface) count of submitted turns, mirroring the Gateway ThrottleBucketDto. */
export interface ThrottleBucket {
  modality: Modality;
  surface: Surface;
  /** Submitted turns through this bucket (never synthesized from raw keystrokes). */
  turns: number;
}

/** One tracked concurrency dimension (live loaded/running, or actively working). */
export interface ConcurrencySeries {
  /** Most recently observed count. */
  current: number;
  /** Highest count ever observed. */
  allTimeMax: number;
  /** When the all-time peak was observed (ISO 8601 UTC), or null if never. */
  allTimeMaxAtUtc: string | null;
  /** Highest count in the last 7 days, derived from the hourly history. */
  weeklyMax: number;
}

/** One hour of the fleet activity log (hour key "yyyy-MM-ddTHH" UTC): the max concurrent live and
 * working counts, and how many distinct sessions, machines, and repositories ran that hour. */
export interface ConcurrencyHour {
  hour: string;
  maxLive: number;
  maxWorking: number;
  sessions: number;
  machines: number;
  repos: number;
}

/** Fleet concurrency: how many sessions are loaded/running at once (live) and how many are actively
 * working at once (working), plus the per-hour activity log. Null when the Gateway has not tracked any
 * yet, or when its statistics store has not published. */
export interface ConcurrencyStats {
  live: ConcurrencySeries;
  working: ConcurrencySeries;
  /** Per-hour activity log, oldest hour first. */
  hourly: ConcurrencyHour[];
}

/** One hour of the "working day" log: turns (total + by modality) submitted that UTC hour (hour key
 * "yyyy-MM-ddTHH"). */
export interface InputHour {
  hour: string;
  turns: number;
  voiceTurns: number;
  typedTurns: number;
  /** The spoken and typed fractions of this hour's turns, finished on the Gateway; null when the hour has
   * none. The chart draws its segments from these and divides nothing (fix-round finding F-01). */
  voiceShare: number | null;
  typedShare: number | null;
}

/** The ratios a ranked row prints, finished on the Gateway (fix-round finding F-01): the row's share of every
 * turn and of every session (with the whole-number percent it prints), and the spoken share of its own turns.
 * Null when there is nothing underneath the ratio. */
export interface RowShares {
  turnShare: number | null;
  turnPercent: number | null;
  sessionShare: number | null;
  sessionPercent: number | null;
  voiceShare: number | null;
  voicePercent: number | null;
}

/** One repository's count of submitted turns over the window, through the Gateway's session-history join.
 * Mirrors the Gateway ThrottleRepoDto. Counts only - never any message text. */
export interface RepoStat extends RowShares {
  /** Grouping key: the "owner/repo" name the session's checkout belongs to (e.g. "thefrederiksen/devthrottle"),
   * so worktrees and per-machine clones are one row; or the checkout's folder name when history holds a path
   * and no name. */
  repo: string;
  /** Display leaf of the key (e.g. "devthrottle"). */
  repoName: string;
  /** Submitted turns into this repo (voice + typed). */
  turns: number;
  voiceTurns: number;
  typedTurns: number;
  /** Distinct sessions the counted turns went into in this repo. */
  sessions: number;
  /** The checkout paths those sessions ran in, sorted. */
  checkouts: string[];
}

/** One agent CLI's count of submitted turns over the window. Mirrors the Gateway ThrottleAgentDto. */
export interface AgentStat extends RowShares {
  /** The agent token the ledger recorded ("ClaudeCode", "Codex", ...), or "" when none (the key). */
  agent: string;
  /** Display name of the agent, e.g. "Claude Code"; "(unknown)" when the session carried no agent. */
  agentName: string;
  /** Submitted turns you drove through this agent (voice + typed). */
  turns: number;
  voiceTurns: number;
  typedTurns: number;
  /** Distinct sessions the counted turns went into under this agent. */
  sessions: number;
  /** Turns OTHER sessions drove into the sessions running this agent (issue #1636) - not you. NOT part of
   *  `turns`, which stays your own driving: the two answer different questions and adding them would corrupt
   *  the voice share. */
  agentDrivenTurns: number;
}

/** How a served window came to be - the four query forms GET /stats/data accepts. */
export type ThrottleWindowKind = "default" | "days" | "week" | "explicit";

/** One length the period selector offers, with the Gateway's name for it. The list is the Gateway's:
 * the selector renders what it is handed and never keeps lengths of its own (CLAUDE.md rule 7). */
export interface ThrottleWindowChoice {
  days: number;
  label: string;
}

/** The window the figure describes, as the Gateway stated it. */
export interface ThrottleWindow {
  /** Inclusive start (ISO 8601 UTC). */
  fromUtc: string;
  /** Exclusive end (ISO 8601 UTC). */
  toUtc: string;
  /** True when no window was asked for and the Gateway answered its default (a rolling seven days). */
  isDefault: boolean;
  /** The Gateway's own plain-English name for the window ("Last 7 days", "Week 35 of 2026, Monday 24 August
   * to Sunday 30 August (America/Toronto)"). Rendered verbatim. */
  label: string;
  /** Which query form produced this window. The selector marks its choice from this. */
  kind: ThrottleWindowKind;
  /** The rolling length in days when kind is "default" or "days"; null otherwise. */
  days: number | null;
  /** The ISO week ("2026-W35") when kind is "week"; null otherwise. */
  week: string | null;
  /** The selector's options, served on every answer in order. */
  choices: ThrottleWindowChoice[];
}

/** What the submission ledger holds, so a page can say where the record begins. */
export interface ThrottleLedger {
  retentionDays: number;
  /** The oldest submission the ledger holds for this account (ISO 8601 UTC), or null when it holds none. */
  earliestUtc: string | null;
}

/** The population the definition left out, disclosed beside the share (rulings R7 and R17). */
export interface ThrottleExcluded {
  /** Every submission in the window with no input origin. */
  noInputOrigin: number;
  /** Of those, the ones another session sent into this one - the fleet driving itself. */
  agentDriven: number;
  /** Of those, text the product wrote itself (a seed prompt, a handover). Nobody's turn. */
  framework: number;
  /** The remainder: a submission of yours the product could not place on a surface. Outside every number. */
  unresolved: number;
  /** Turns that carried an origin but went into a session you did not start - another session or a schedule
   * started it, or its starter was not recorded. Outside every number (owner's ruling, 2026-09-27). */
  notStartedByYou: number;
}

/** The four starter groups the Gateway serves, in its drawing order. A kind outside these is refused. */
export const STARTER_KINDS = ["human", "agent", "schedule", "notRecorded"] as const;
export type StarterKind = (typeof STARTER_KINDS)[number];

/** One starter group: the sessions of the window that one kind of starter opened. Mirrors ThrottleStarterDto. */
export interface ThrottleStarterGroup {
  kind: StarterKind;
  /** The Gateway's display name, rendered verbatim. */
  label: string;
  sessions: number;
  /** Every submission into this group's sessions in the window, whoever drove it. */
  turns: number;
  sessionShare: number | null;
  sessionPercent: number | null;
}

/** WHO RUNS YOUR SESSIONS, finished on the Gateway: every session that received a submission in the window,
 * by who started it. Only the human group's turns are in the headline. Mirrors ThrottleStartersDto. */
export interface ThrottleStarters {
  sessions: number;
  turns: number;
  hasData: boolean;
  /** Sessions a person started, of all sessions - the ring's share. */
  humanShare: number | null;
  humanPercent: number | null;
  /** Always human, agent, schedule, notRecorded, in that order. */
  groups: ThrottleStarterGroup[];
}

/** One share of the headline denominator, as the Gateway finished it: the count, the fraction, and the
 * whole-number percentage the reader is shown (rounded half up, once, on the Gateway). Fraction and percent
 * are null when nothing was counted. Mirrors the Gateway ThrottleShareDto. */
export interface ThrottleShare {
  turns: number;
  share: number | null;
  percent: number | null;
}

/** A share drawn as a ring: the count on the ring's other side is served with it, so the page subtracts
 * nothing (fix-round finding F-01). Mirrors the Gateway ThrottleRingDto. */
export interface ThrottleRingShare extends ThrottleShare {
  /** The counted turns NOT in this share: the denominator less `turns`. */
  remainder: number;
}

/** One surface's share of the headline, with the Gateway's own display name. */
export interface ThrottleSurfaceShare extends ThrottleShare {
  surface: Surface;
  label: string;
  /** The counted turns on every other surface. */
  remainder: number;
}

/** THE HEADLINE, finished on the Gateway (final inspection finding F-01): the two rings' shares with their
 * denominator, their rounding and their empty state. The pages print these fields; nothing here divides.
 * Mirrors the Gateway ThrottleHeadlineDto. */
export interface ThrottleHeadline {
  /** The denominator of every share here: the counted turns. */
  denominator: number;
  /** False when nothing was counted; then every share and percent is null and the page shows its empty state. */
  hasData: boolean;
  voice: ThrottleShare;
  typed: ThrottleShare;
  /** The phone entry of `surfaces`, at the top because it is the second ring - with the count on the ring's
   * other side. */
  phone: ThrottleRingShare;
  /** Every surface, in the Gateway's drawing order, zero or not. */
  surfaces: ThrottleSurfaceShare[];
}

/** THE FIGURE: every count of turns the pages show, from one definition over one substrate. Mirrors the
 * Gateway ThrottleFigureDto. */
export interface ThrottleFigure {
  /** The definition, verbatim, so a reader can check the numbers against the sentence. */
  definition: string;
  /** The unit of every share ("submitted turns"). */
  unit: string;
  window: ThrottleWindow;
  ledger: ThrottleLedger;
  /** The finished headline. Rendered verbatim; never recomputed from the counts below. */
  headline: ThrottleHeadline;
  /** Turns the definition counted. */
  turns: number;
  voiceTurns: number;
  typedTurns: number;
  /** Distinct sessions the counted turns went into. */
  sessions: number;
  buckets: ThrottleBucket[];
  /** Turns per UTC hour, oldest first, hours with none omitted. */
  hourlyTurns: InputHour[];
  /** Per-agent split, most-driven first. */
  agents: AgentStat[];
  /** Per-repository split, most-driven first. */
  repos: RepoStat[];
  /** The Agents tab's headline cards, finished on the Gateway (fix-round finding F-01). */
  agentsSummary: AgentSummary;
  /** The Repos tab's headline cards, finished on the Gateway. */
  reposSummary: RepoSummary;
  /** Counted turns whose session has no repository on record - disclosed, never folded into a row. */
  reposUnattributedTurns: number;
  excluded: ThrottleExcluded;
  /** Turns the fleet drove into ITSELF over the window: one session prompting another. Beside your own
   *  turns, never inside them. The ratio of this to your own turns is your leverage. */
  agentDrivenTurns: number;
  /** The window's sessions by who started them; only the sessions you started are in the headline. */
  starters: ThrottleStarters;
}

/** GET /stats/data when the Gateway serves the figure. */
export interface ThrottleServed {
  available: true;
  /** When the Gateway generated this snapshot (ISO 8601 UTC), or "" when absent. */
  generatedAtUtc: string;
  /** The display time zone (IANA id) the hourly charts render local clock hours in. */
  timeZone: string;
  throttle: ThrottleFigure;
  /** Fleet concurrency (both series), or null when nothing is tracked or the statistics store is not up. */
  concurrency: ConcurrencyStats | null;
  /** The Gateway's reason the statistics-store blocks are null, or null when they are served. */
  statisticsUnavailableReason: string | null;
  /** Plain-English caveats about what the numbers do and do not include. */
  notCaptured: string[];
}

/** GET /stats/data when this Gateway has no figure to show - a self-hosted Gateway (owner's ruling R1).
 * The Gateway says why in one sentence and the pages render it verbatim (ruling R6). */
export interface ThrottleUnavailable {
  available: false;
  reason: string;
}

/** The GET /stats/data body. */
export type ThrottleData = ThrottleServed | ThrottleUnavailable;

/** The headline as the pages lay it out - EVERY value here is the Gateway's headline field, read verbatim
 * (final inspection finding F-01). Shares are fractions in [0,1] for drawing an arc; percents are the
 * whole numbers the rings print; both are null when there are no counted turns (an honest empty state -
 * never a fabricated 0%). */
export interface ThrottleSummary {
  /** The headline denominator: the counted turns. */
  totalTurns: number;
  voiceTurns: number;
  typedTurns: number;
  /** Voice share of TURNS, or null when no turns are counted. */
  voiceShare: number | null;
  /** The whole-number percentage the voice ring prints, as the Gateway rounded it; null when no turns. */
  voicePercent: number | null;
  /** Turns per surface. */
  turnsBySurface: Record<Surface, number>;
  /** Every surface with its share and percent, in the Gateway's drawing order. */
  surfaces: ThrottleSurfaceShare[];
  /** Phone share of TURNS, or null when no turns are counted. */
  phoneShare: number | null;
  /** The whole-number percentage the phone ring prints, as the Gateway rounded it; null when no turns. */
  phonePercent: number | null;
  /** The counted turns NOT from the phone - the phone ring's other side, served, never subtracted. */
  phoneRemainder: number;
  hasData: boolean;
}

const SURFACES: readonly Surface[] = ["desktop", "cockpit", "phone", "unknown"];

const MODALITIES: readonly Modality[] = ["typed", "voice"];

/** One bucket exactly as the Gateway wrote it. REFUSES TO GUESS: the library writes only "typed" and "voice"
 * and only the four surfaces, so any other token is an answer this client cannot render and is thrown as an
 * error the page shows. It used to map every non-"voice" token to typed and every unknown surface to
 * "unknown", which is how a renamed token would have silently become a typed turn (final inspection, F-01). */
function normalizeBucket(raw: unknown): ThrottleBucket {
  const b = (raw ?? {}) as Partial<Record<keyof ThrottleBucket, unknown>>;
  const modality = MODALITIES.find((m) => m === b.modality);
  if (modality === undefined) {
    throw new Error("GET /stats/data answered a bucket modality this client does not know: " + String(b.modality));
  }
  const surface = SURFACES.find((s) => s === b.surface);
  if (surface === undefined) {
    throw new Error("GET /stats/data answered a bucket surface this client does not know: " + String(b.surface));
  }
  return { modality, surface, turns: num(b.turns) };
}

async function gatewayErrorFrom(res: Response, label: string): Promise<GatewayError> {
  let detail = `${res.status}`;
  try {
    const text = await res.text();
    if (text.length > 0) {
      try {
        const body = JSON.parse(text) as { error?: string; detail?: string };
        detail = body.error ?? body.detail ?? text;
      } catch {
        detail = text;
      }
    }
  } catch {
    /* body unreadable - keep the status code */
  }
  return new GatewayError(res.status, `${label} failed: ${detail}`);
}

/** The optional window to ask the Gateway for, one of three shapes: a rolling length in days (one of the
 * served choices), an ISO week ("2026-W35", resolved by the Gateway in the account's display zone - the
 * shape the mentor report's link uses), or explicit UTC instants. Absent, the Gateway answers its default
 * and says so. The client sends what was chosen; what it means is decided on the Gateway. */
export type ThrottleWindowRequest =
  | { days: number }
  | { week: string }
  | { fromUtc: string; toUtc: string };

/** The window a page's URL asks for: `?week=2026-W35` (the mentor report's link) or `?days=N` (a selector
 * choice), else undefined for the Gateway's default. Both pages read their window from here and write a
 * choice back to the URL, which is what makes a hard navigation to the report's link show the Gateway's
 * answer for exactly that week. Nothing is validated here beyond its shape: a week the Gateway will not
 * serve, or a length that is not a choice, is sent as asked and refused by the Gateway with its reason,
 * which the page shows (the dumb-client rule, and no-fallback). */
export function throttleWindowFromSearch(params: URLSearchParams): ThrottleWindowRequest | undefined {
  const week = params.get("week");
  if (week !== null && week.trim().length > 0) return { week: week.trim() };
  const days = params.get("days");
  if (days !== null && /^[0-9]+$/.test(days.trim())) return { days: Number(days.trim()) };
  return undefined;
}

/** The query string for a window request, or "" for the default. */
export function throttleWindowQuery(request: ThrottleWindowRequest | undefined): string {
  if (request === undefined) return "";
  if ("days" in request) return `?days=${encodeURIComponent(String(request.days))}`;
  if ("week" in request) return `?week=${encodeURIComponent(request.week)}`;
  return `?from=${encodeURIComponent(request.fromUtc)}&to=${encodeURIComponent(request.toUtc)}`;
}

// GET /stats/data - the "Your Throttle" figure. Throws on transport failure so the page can show an explicit
// error banner (the no-fallback rule). A 200 carrying available=false is NOT a failure: it is the Gateway
// saying, in a sentence, that there is no figure here, and the page shows that sentence.
export async function getThrottle(signal?: AbortSignal, request?: ThrottleWindowRequest): Promise<ThrottleData> {
  const query = throttleWindowQuery(request);
  const res = await fetch(`/stats/data${query}`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /stats/data");
  const body = (await res.json()) as {
    available?: unknown;
    reason?: unknown;
    generatedAtUtc?: unknown;
    timeZone?: unknown;
    throttle?: unknown;
    concurrency?: unknown;
    statisticsUnavailableReason?: unknown;
    notCaptured?: unknown;
  } | null;
  if (body?.available === false) {
    return { available: false, reason: typeof body.reason === "string" ? body.reason : "" };
  }
  const notCaptured = Array.isArray(body?.notCaptured)
    ? body!.notCaptured.filter((x): x is string => typeof x === "string")
    : [];
  return {
    available: true,
    generatedAtUtc: typeof body?.generatedAtUtc === "string" ? body.generatedAtUtc : "",
    timeZone: safeTimeZone(typeof body?.timeZone === "string" ? body.timeZone : null),
    throttle: normalizeFigure(body?.throttle),
    concurrency: normalizeConcurrency(body?.concurrency),
    statisticsUnavailableReason:
      typeof body?.statisticsUnavailableReason === "string" ? body.statisticsUnavailableReason : null,
    notCaptured,
  };
}

const WINDOW_KINDS: readonly ThrottleWindowKind[] = ["default", "days", "week", "explicit"];

/** The window as the Gateway stated it. REFUSES TO GUESS: the selector renders the served choices and marks
 * the served kind, so an answer without them, or with a kind this client does not know, is not a window this
 * client can render - it is thrown as an error the page shows, never defaulted into a selector that offers
 * lengths the Gateway did not (CLAUDE.md rule 7). */
function normalizeWindow(raw: unknown): ThrottleWindow {
  const w = (raw ?? {}) as Partial<Record<keyof ThrottleWindow, unknown>>;
  if (!Array.isArray(w.choices)) {
    throw new Error("GET /stats/data answered without the window choices the period selector renders");
  }
  const choices = w.choices.map((c): ThrottleWindowChoice => {
    const choice = (c ?? {}) as Partial<Record<keyof ThrottleWindowChoice, unknown>>;
    if (typeof choice.days !== "number" || typeof choice.label !== "string") {
      throw new Error("GET /stats/data answered a window choice without a length and a label");
    }
    return { days: choice.days, label: choice.label };
  });
  const kind = WINDOW_KINDS.find((k) => k === w.kind);
  if (kind === undefined) {
    throw new Error(`GET /stats/data answered a window kind this client does not know: ${String(w.kind)}`);
  }
  return {
    fromUtc: typeof w.fromUtc === "string" ? w.fromUtc : "",
    toUtc: typeof w.toUtc === "string" ? w.toUtc : "",
    isDefault: w.isDefault === true,
    label: typeof w.label === "string" ? w.label : "",
    kind,
    days: typeof w.days === "number" ? w.days : null,
    week: typeof w.week === "string" ? w.week : null,
    choices,
  };
}

function normalizeFigure(raw: unknown): ThrottleFigure {
  const f = (raw ?? {}) as Partial<Record<keyof ThrottleFigure, unknown>>;
  const l = (f.ledger ?? {}) as Partial<Record<keyof ThrottleLedger, unknown>>;
  const x = (f.excluded ?? {}) as Partial<Record<keyof ThrottleExcluded, unknown>>;
  return {
    definition: typeof f.definition === "string" ? f.definition : "",
    unit: typeof f.unit === "string" ? f.unit : "",
    window: normalizeWindow(f.window),
    ledger: {
      retentionDays: num(l.retentionDays),
      earliestUtc: typeof l.earliestUtc === "string" ? l.earliestUtc : null,
    },
    headline: normalizeHeadline(f.headline),
    turns: num(f.turns),
    voiceTurns: num(f.voiceTurns),
    typedTurns: num(f.typedTurns),
    sessions: num(f.sessions),
    buckets: Array.isArray(f.buckets) ? f.buckets.map(normalizeBucket) : [],
    hourlyTurns: Array.isArray(f.hourlyTurns) ? f.hourlyTurns.map(normalizeInputHour) : [],
    agents: Array.isArray(f.agents) ? f.agents.map(normalizeAgent) : [],
    repos: Array.isArray(f.repos) ? f.repos.map(normalizeRepo) : [],
    agentsSummary: normalizeAgentsSummary(f.agentsSummary),
    reposSummary: normalizeReposSummary(f.reposSummary),
    reposUnattributedTurns: num(f.reposUnattributedTurns),
    excluded: {
      noInputOrigin: num(x.noInputOrigin),
      agentDriven: num(x.agentDriven),
      framework: num(x.framework),
      unresolved: num(x.unresolved),
      notStartedByYou: num(x.notStartedByYou),
    },
    agentDrivenTurns: num(f.agentDrivenTurns),
    starters: normalizeStarters(f.starters),
  };
}

/** Who runs the sessions, as the Gateway finished it. Refused when absent, like the summaries: without it the
 * page cannot say that its numbers cover only the sessions you started. */
function normalizeStarters(raw: unknown): ThrottleStarters {
  if (raw === null || typeof raw !== "object") throw new Error("GET /stats/data answered without who started the sessions; this client does not work it out");
  const s = raw as Partial<Record<keyof ThrottleStarters, unknown>>;
  if (typeof s.hasData !== "boolean" || !Array.isArray(s.groups)) {
    throw new Error("GET /stats/data answered who started the sessions without its empty state or its groups");
  }
  return {
    sessions: num(s.sessions),
    turns: num(s.turns),
    hasData: s.hasData,
    humanShare: ratioOrNull(s.humanShare, "the share of sessions you started"),
    humanPercent: ratioOrNull(s.humanPercent, "the percent of sessions you started"),
    groups: s.groups.map((raw): ThrottleStarterGroup => {
      const g = (raw ?? {}) as Partial<Record<keyof ThrottleStarterGroup, unknown>>;
      const kind = STARTER_KINDS.find((k) => k === g.kind);
      if (kind === undefined || typeof g.label !== "string") {
        throw new Error("GET /stats/data answered a starter group this client does not know: " + String(g.kind));
      }
      return {
        kind,
        label: g.label,
        sessions: num(g.sessions),
        turns: num(g.turns),
        sessionShare: ratioOrNull(g.sessionShare, "a starter group's session share"),
        sessionPercent: ratioOrNull(g.sessionPercent, "a starter group's session percent"),
      };
    }),
  };
}

/** The headline as the Gateway finished it. REFUSES TO GUESS: an answer without the headline, or with a
 * share that is not a number-or-null, or a surface this client does not know, is not a figure this client
 * can render - it is thrown as an error the page shows. It is never rebuilt from the counts, because
 * rebuilding it is exactly the second computation finding F-01 removed. */
function normalizeHeadline(raw: unknown): ThrottleHeadline {
  if (raw === null || typeof raw !== "object") {
    throw new Error("GET /stats/data answered without the headline the rings render; this client does not compute one");
  }
  const h = raw as Partial<Record<keyof ThrottleHeadline, unknown>>;
  if (typeof h.denominator !== "number" || typeof h.hasData !== "boolean" || !Array.isArray(h.surfaces)) {
    throw new Error("GET /stats/data answered a headline without its denominator, empty state, or surfaces");
  }
  const surfaces = h.surfaces.map((raw): ThrottleSurfaceShare => {
    const entry = (raw ?? {}) as Partial<Record<keyof ThrottleSurfaceShare, unknown>>;
    const surface = SURFACES.find((s) => s === entry.surface);
    if (surface === undefined || typeof entry.label !== "string") {
      throw new Error("GET /stats/data answered a headline surface this client does not know: " + String(entry.surface));
    }
    if (typeof entry.remainder !== "number") throw new Error("GET /stats/data answered the surface " + surface + " without the count on every other surface");
    return { surface, label: entry.label, remainder: entry.remainder, ...normalizeShare(entry, "surface " + surface) };
  });
  return {
    denominator: h.denominator,
    hasData: h.hasData,
    voice: normalizeShare(h.voice, "voice"),
    typed: normalizeShare(h.typed, "typed"),
    phone: normalizeRing(h.phone, "phone"),
    surfaces,
  };
}

/** A served ratio: a finite number, or null when the Gateway ruled there is nothing underneath it. Anything
 * else - the field absent, a string, NaN - is an answer this client cannot render and is refused; it is never
 * read as 0, because a 0% the Gateway did not say is a fabricated number (fix-round finding F-01). */
function ratioOrNull(v: unknown, what: string): number | null {
  if (v === null) return null;
  if (typeof v === "number" && Number.isFinite(v)) return v;
  throw new Error("GET /stats/data answered " + what + " that is neither a number nor null; this client does not compute one");
}

function textOrNull(v: unknown, what: string): string | null {
  if (v === null) return null;
  if (typeof v === "string") return v;
  throw new Error("GET /stats/data answered " + what + " that is neither text nor null");
}

function normalizeShare(raw: unknown, what: string): ThrottleShare {
  if (raw === null || typeof raw !== "object") {
    throw new Error("GET /stats/data answered a headline without its " + what + " share");
  }
  const s = raw as Partial<Record<keyof ThrottleShare, unknown>>;
  if (typeof s.turns !== "number") throw new Error("GET /stats/data answered a headline " + what + " share without its turns");
  return { turns: s.turns, share: ratioOrNull(s.share, "a headline " + what + " share"), percent: ratioOrNull(s.percent, "a headline " + what + " percent") };
}

function normalizeRing(raw: unknown, what: string): ThrottleRingShare {
  const share = normalizeShare(raw, what);
  const r = raw as Partial<Record<keyof ThrottleRingShare, unknown>>;
  if (typeof r.remainder !== "number") throw new Error("GET /stats/data answered the " + what + " ring without the count on its other side; this client does not subtract");
  return { ...share, remainder: r.remainder };
}

function normalizeRowShares(raw: Partial<Record<keyof RowShares, unknown>>, what: string): RowShares {
  return {
    turnShare: ratioOrNull(raw.turnShare, what + " turn share"),
    turnPercent: ratioOrNull(raw.turnPercent, what + " turn percent"),
    sessionShare: ratioOrNull(raw.sessionShare, what + " session share"),
    sessionPercent: ratioOrNull(raw.sessionPercent, what + " session percent"),
    voiceShare: ratioOrNull(raw.voiceShare, what + " voice share"),
    voicePercent: ratioOrNull(raw.voicePercent, what + " voice percent"),
  };
}

function normalizeAgent(raw: unknown): AgentStat {
  const a = (raw ?? {}) as Partial<Record<keyof AgentStat, unknown>>;
  return {
    agent: String(a.agent ?? ""),
    agentName: String(a.agentName ?? ""),
    turns: num(a.turns),
    voiceTurns: num(a.voiceTurns),
    typedTurns: num(a.typedTurns),
    sessions: num(a.sessions),
    agentDrivenTurns: num(a.agentDrivenTurns),
    ...normalizeRowShares(a, "an agent row's"),
  };
}

function normalizeRepo(raw: unknown): RepoStat {
  const r = (raw ?? {}) as Partial<Record<keyof RepoStat, unknown>>;
  return {
    repo: String(r.repo ?? ""),
    repoName: String(r.repoName ?? ""),
    turns: num(r.turns),
    voiceTurns: num(r.voiceTurns),
    typedTurns: num(r.typedTurns),
    sessions: num(r.sessions),
    checkouts: Array.isArray(r.checkouts) ? r.checkouts.map((c) => String(c)) : [],
    ...normalizeRowShares(r, "a repository row's"),
  };
}

function normalizeInputHour(raw: unknown): InputHour {
  const h = (raw ?? {}) as Partial<Record<keyof InputHour, unknown>>;
  return {
    hour: String(h.hour ?? ""),
    turns: num(h.turns),
    voiceTurns: num(h.voiceTurns),
    typedTurns: num(h.typedTurns),
    voiceShare: ratioOrNull(h.voiceShare, "an hour's voice share"),
    typedShare: ratioOrNull(h.typedShare, "an hour's typed share"),
  };
}

function normalizeAgentsSummary(raw: unknown): AgentSummary {
  if (raw === null || typeof raw !== "object") throw new Error("GET /stats/data answered without the Agents summary; this client does not total the rows");
  const s = raw as Partial<Record<keyof AgentSummary, unknown>>;
  if (typeof s.hasData !== "boolean") throw new Error("GET /stats/data answered an Agents summary without its empty state");
  return {
    agentCount: num(s.agentCount),
    totalTurns: num(s.totalTurns),
    totalSessions: num(s.totalSessions),
    voiceTurns: num(s.voiceTurns),
    voiceShare: ratioOrNull(s.voiceShare, "the Agents summary's voice share"),
    voicePercent: ratioOrNull(s.voicePercent, "the Agents summary's voice percent"),
    topAgentName: textOrNull(s.topAgentName, "the Agents summary's leading agent"),
    topShare: ratioOrNull(s.topShare, "the Agents summary's leading share"),
    topPercent: ratioOrNull(s.topPercent, "the Agents summary's leading percent"),
    agentDrivenTurns: num(s.agentDrivenTurns),
    leverage: ratioOrNull(s.leverage, "the Agents summary's leverage"),
    leverageText: textOrNull(s.leverageText, "the Agents summary's leverage text"),
    hasData: s.hasData,
  };
}

function normalizeReposSummary(raw: unknown): RepoSummary {
  if (raw === null || typeof raw !== "object") throw new Error("GET /stats/data answered without the Repos summary; this client does not total the rows");
  const s = raw as Partial<Record<keyof RepoSummary, unknown>>;
  if (typeof s.hasData !== "boolean") throw new Error("GET /stats/data answered a Repos summary without its empty state");
  return {
    repoCount: num(s.repoCount),
    totalTurns: num(s.totalTurns),
    totalSessions: num(s.totalSessions),
    voiceTurns: num(s.voiceTurns),
    voiceShare: ratioOrNull(s.voiceShare, "the Repos summary's voice share"),
    voicePercent: ratioOrNull(s.voicePercent, "the Repos summary's voice percent"),
    topRepoName: textOrNull(s.topRepoName, "the Repos summary's leading repository"),
    topShare: ratioOrNull(s.topShare, "the Repos summary's leading share"),
    topPercent: ratioOrNull(s.topPercent, "the Repos summary's leading percent"),
    hasData: s.hasData,
  };
}

function num(v: unknown): number {
  const n = Number(v);
  return Number.isFinite(n) ? n : 0;
}

function normalizeSeries(raw: unknown): ConcurrencySeries {
  const s = (raw ?? {}) as Partial<Record<keyof ConcurrencySeries, unknown>>;
  return {
    current: num(s.current),
    allTimeMax: num(s.allTimeMax),
    allTimeMaxAtUtc: typeof s.allTimeMaxAtUtc === "string" ? s.allTimeMaxAtUtc : null,
    weeklyMax: num(s.weeklyMax),
  };
}

function normalizeHour(raw: unknown): ConcurrencyHour {
  const h = (raw ?? {}) as Partial<Record<keyof ConcurrencyHour, unknown>>;
  return {
    hour: String(h.hour ?? ""),
    maxLive: num(h.maxLive),
    maxWorking: num(h.maxWorking),
    sessions: num(h.sessions),
    machines: num(h.machines),
    repos: num(h.repos),
  };
}

function normalizeConcurrency(raw: unknown): ConcurrencyStats | null {
  if (raw === null || typeof raw !== "object") return null;
  const c = raw as { live?: unknown; working?: unknown; hourly?: unknown };
  return {
    live: normalizeSeries(c.live),
    working: normalizeSeries(c.working),
    hourly: Array.isArray(c.hourly) ? c.hourly.map(normalizeHour) : [],
  };
}

/** Lay the Gateway's headline out for the pages. EVERY value is read from `figure.headline` verbatim: the
 * denominator, each count, each fraction, each rounded percent, and the empty state (final inspection
 * finding F-01). The counts and buckets on the figure are not consulted, so a served answer whose counts
 * disagree with its headline renders the headline - the same headline the mentor report renders. */
export function summarizeThrottle(figure: ThrottleFigure): ThrottleSummary {
  const h = figure.headline;
  const turnsBySurface: Record<Surface, number> = { desktop: 0, cockpit: 0, phone: 0, unknown: 0 };
  for (const s of h.surfaces) turnsBySurface[s.surface] = s.turns;
  return {
    totalTurns: h.denominator,
    voiceTurns: h.voice.turns,
    typedTurns: h.typed.turns,
    voiceShare: h.voice.share,
    voicePercent: h.voice.percent,
    turnsBySurface,
    surfaces: h.surfaces,
    phoneShare: h.phone.share,
    phonePercent: h.phone.percent,
    phoneRemainder: h.phone.remainder,
    hasData: h.hasData,
  };
}

/** The Repos page's headline cards, FINISHED ON THE GATEWAY (fix-round finding F-01). Mirrors the Gateway
 * ThrottleReposSummaryDto. The browser used to total the rows and divide for the leading repository's share
 * and the voice share; now it prints these. */
export interface RepoSummary {
  /** Repositories with a row. */
  repoCount: number;
  /** Every turn placed in a repository. */
  totalTurns: number;
  /** Every session placed in a repository. */
  totalSessions: number;
  voiceTurns: number;
  /** The spoken share of every placed turn, and the whole-number percent the card prints; null when none. */
  voiceShare: number | null;
  voicePercent: number | null;
  /** The most-driven repository's display name, or null when there is no row. */
  topRepoName: string | null;
  /** The most-driven repository's share of every placed turn, and its printed percent; null when none. */
  topShare: number | null;
  topPercent: number | null;
  hasData: boolean;
}

/** The Agents page's headline cards, FINISHED ON THE GATEWAY (fix-round finding F-01). Mirrors the Gateway
 * ThrottleAgentsSummaryDto. */
export interface AgentSummary {
  /** Agents with at least one counted turn. */
  agentCount: number;
  /** Every counted turn, across every agent. */
  totalTurns: number;
  /** Every counted session, across every agent. */
  totalSessions: number;
  voiceTurns: number;
  /** The spoken share of every counted turn, and the whole-number percent the card prints; null when none. */
  voiceShare: number | null;
  voicePercent: number | null;
  /** The most-driven agent's display name, or null when no agent has a counted turn. */
  topAgentName: string | null;
  /** The most-driven agent's share of every counted turn, and its printed percent; null when none. */
  topShare: number | null;
  topPercent: number | null;
  /** Turns the fleet drove into itself - one agent prompting another (issue #1636). */
  agentDrivenTurns: number;
  /** Leverage: agent-driven turns per turn YOU drove; null when you drove none. */
  leverage: number | null;
  /** The leverage as the card prints it ("3.0x"), finished on the Gateway; null when there is none. */
  leverageText: string | null;
  /** True when there is something to show - a counted turn, or a fleet driving itself while you drove
   * nothing, which is a real state, not an empty one. */
  hasData: boolean;
}

/** The Gateway's rounded whole-number percentage as the ring prints it, or the ASCII no-data placeholder.
 * No rounding happens here: the number arrives finished (final inspection finding F-01). */
export function formatPercent(percent: number | null): string {
  if (percent === null) return "n/a";
  return String(percent) + "%";
}

/** Human labels for the wire tokens (ASCII only). */
export const MODALITY_LABEL: Record<Modality, string> = { voice: "Voice", typed: "Typed" };
export const SURFACE_LABEL: Record<Surface, string> = {
  desktop: "Desktop",
  cockpit: "Cockpit",
  phone: "Phone",
  unknown: "Unknown",
};

/** The surfaces in a stable presentation order. */
export const SURFACE_ORDER = SURFACES;

// ---- Hourly-chart time window + time zone --------------------------------------------------------
//
// The two hourly series (turns, concurrency) only carry the hours that had activity, so slicing each to
// its own "last 24" produced two DIFFERENT windows that did not line up. Instead, both charts render one
// canonical window: the 24 consecutive UTC clock hours ending at the served window's end (clamped to now by
// the caller when the window ends in the future), with absent hours zero-filled. Taking the end from the
// window rather than from "now" is what keeps a week selected from the past from drawing an empty chart
// that reads as broken. Labels are then formatted in the configured display time zone, so the axis reads in
// local time.

function padHourKey(d: Date): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${d.getUTCFullYear()}-${p(d.getUTCMonth() + 1)}-${p(d.getUTCDate())}T${p(d.getUTCHours())}`;
}

/** The 24 consecutive UTC hour keys ("yyyy-MM-ddTHH") ending at the hour containing `endUtc`, oldest
 *  first. Both hourly charts render this SAME window so they line up exactly. The end is the served window's
 *  end (see {@link hourlyChartEnd}), not the clock. */
export function last24HourKeys(endUtc: Date): string[] {
  const topOfHour = Date.UTC(
    endUtc.getUTCFullYear(),
    endUtc.getUTCMonth(),
    endUtc.getUTCDate(),
    endUtc.getUTCHours(),
  );
  const keys: string[] = [];
  for (let i = 23; i >= 0; i--) keys.push(padHourKey(new Date(topOfHour - i * 3_600_000)));
  return keys;
}

/** The instant the 24-hour charts end at: the served window's end, clamped to `nowUtc` when the window ends
 *  in the future (a week still in progress, or a rolling window whose end is a moment ahead of the browser's
 *  clock). A window end that does not parse falls back to `nowUtc` - the Gateway always serves one, so that
 *  is a malformed answer, and the chart says which 24 hours it shows either way. */
export function hourlyChartEnd(window: ThrottleWindow, nowUtc: Date): Date {
  const end = Date.parse(window.toUtc);
  if (Number.isNaN(end) || end > nowUtc.getTime()) return nowUtc;
  return new Date(end);
}

/** Map an hourly series (keyed by UTC hour) onto a fixed window of hour keys, filling any absent hour with
 *  a zero entry, so a chart renders a continuous, aligned axis. */
export function windowSeries<T extends { hour: string }>(
  series: readonly T[],
  keys: readonly string[],
  zero: (hour: string) => T,
): T[] {
  const byHour = new Map(series.map((s) => [s.hour, s]));
  return keys.map((k) => byHour.get(k) ?? zero(k));
}

/** A zero-filled turn-hour for an absent hour in the window. */
export function emptyInputHour(hour: string): InputHour {
  return { hour, turns: 0, voiceTurns: 0, typedTurns: 0, voiceShare: null, typedShare: null };
}

/** A zero-filled concurrency-hour for an absent hour in the window. */
export function emptyConcurrencyHour(hour: string): ConcurrencyHour {
  return { hour, maxLive: 0, maxWorking: 0, sessions: 0, machines: 0, repos: 0 };
}

function canFormatZone(tz: string): boolean {
  try {
    new Intl.DateTimeFormat("en-US", { timeZone: tz });
    return true;
  } catch {
    return false;
  }
}

function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone ?? "";
  } catch {
    return "";
  }
}

/** A time zone id we can format with: the given id when usable, else the browser's zone, else "UTC" - so
 *  one bad setting never throws inside a render. */
export function safeTimeZone(timeZone: string | null | undefined): string {
  const candidate = (timeZone ?? "").trim();
  if (candidate.length > 0 && canFormatZone(candidate)) return candidate;
  const browser = browserTimeZone();
  if (browser.length > 0 && canFormatZone(browser)) return browser;
  return "UTC";
}

/** Format one UTC hour key ("yyyy-MM-ddTHH") as its 2-digit local hour ("00".."23") in the given IANA
 *  zone. Assumes `timeZone` is already a usable id (see {@link safeTimeZone}). */
export function localHourLabel(hourKeyUtc: string, timeZone: string): string {
  const d = new Date(`${hourKeyUtc}:00:00Z`);
  if (Number.isNaN(d.getTime())) return hourKeyUtc.slice(-2);
  return new Intl.DateTimeFormat("en-US", { hour: "2-digit", hourCycle: "h23", timeZone }).format(d);
}
