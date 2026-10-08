// The Factories screen (Factories screen mission, phase D): the typed, same-origin client the Cockpit's Factories
// pages read - the list of factories, one factory's page, its Seats tab, and the Talk button.
//
// CRITICAL RULE 7 - THE CLIENT IS DUMB. These types mirror src/CcDirector.Gateway.Contracts/FactoriesScreenDtos.cs
// (the views, folded by FactoriesScreenFold) and the Talk answer in FactoryRegistryDtos.cs. Every word, tone and order
// arrives finished; a page renders it verbatim and never counts, sorts or decides what a status means.
import { authHeaders, GatewayError } from "../api/client";
import type { FactoryTab, FactoryTone, FactoryWaitingItem } from "./factoryAgentsClient";

/** What a Talk button starts: the Cockpit sends the two ids back and never works out which agent a button means. */
export interface FactoryTalkTarget {
  /** "Talk to Nora Hale", "Talk to the CEO", or "Talk" on a seat row. */
  label: string;
  /** What the button says while the talk is being started: "Starting the talk with Nora Hale...". */
  busyLabel: string;
  factoryId: string;
  seatId: string;
}

/**
 * One owner-only action and the confirm it asks first (round 2): every word is the Gateway's. What the Cockpit sends
 * back is exactly what the confirm showed - the cut-off and count, or the schedules it named - so the Gateway can
 * refuse rather than do something other than what the owner read.
 */
export interface FactoryOwnerAction {
  /** "handled-older", "archive" or "restore". */
  action: "handled-older" | "archive" | "restore";
  factoryId: string;
  label: string;
  busyLabel: string;
  confirmTitle: string;
  /** The confirm's sentences, in order. */
  confirmLines: string[];
  confirmLabel: string;
  /** True when the confirm's button is destructive (red); the Gateway decides. */
  danger: boolean;
  cutoffUtc: string | null;
  expectedCount: number | null;
  schedules: string[];
}

/** What an owner action did, in one sentence. */
export interface FactoryOwnerActionResult {
  text: string;
  marked: number;
  schedulesSwitched: string[];
}

/** One archived factory, under Show archived. */
export interface FactoryArchivedRow {
  id: string;
  title: string;
  href: string;
  /** "Archived 6 Oct 23:50 by the owner". */
  archivedText: string;
  restore: FactoryOwnerAction;
}

export interface FactoryListRow {
  id: string;
  title: string;
  /** Exactly one of FAILING, NEEDS YOU, PAUSED, RUNNING. */
  statusWord: string;
  statusTone: FactoryTone;
  /** Where statusWord stands worst first: 0 FAILING, 1 NEEDS YOU, 2 PAUSED, 3 RUNNING. The Cockpit sorts by it. */
  statusRank: number;
  statusReason: string;
  /** The one short line under the status word ("Nothing scheduled", "Sender: 4 failures, ..."); null for RUNNING. */
  statusLine: string | null;
  /** Where clicking the status word goes (the failures, the waiting items, or the Seats tab); null for RUNNING. */
  statusHref: string | null;
  /** "1 question", "2 decisions", or "-". */
  waitingText: string;
  /** How many open items waitingText counts (questions plus decisions); 0 for "-". The Cockpit sorts by it. */
  waitingCount: number;
  /** Where clicking the waiting count goes; null when nothing is waiting. */
  waitingHref: string | null;
  href: string;
  /** The factory head's Talk button ("Talk to Ruth Calder"), whatever the head's title. */
  talk: FactoryTalkTarget | null;
  /** "No head named" when talk is null. */
  noCeoText: string | null;
}

export interface FactoriesListView {
  title: string;
  subtitle: string;
  /** Factories, Activity, Reports. */
  tabs: FactoryTab[];
  /** "Factory", "Waiting on you", "Status". */
  columns: string[];
  /** Worst first, as the Gateway sorted them. */
  /** Worst first, then by title; the Cockpit re-sorts them in the order the owner picked. */
  rows: FactoryListRow[];
  /** The Gateway's old "Worst first: ..." line, kept for older clients; the Cockpit states its own order instead. */
  footerText: string | null;
  emptyText: string | null;
  truncatedText: string | null;
  /** "Show archived (1)". */
  showArchivedLabel: string;
  /** "Hide archived". */
  hideArchivedLabel: string;
  /** The archived factories; never in rows. */
  archivedRows: FactoryArchivedRow[];
  /** "No factory is archived." when there are none. */
  archivedEmptyText: string | null;
  /** "Schedules outside any factory" (issue #3650). */
  outsideTitle: string;
  /** What the list is and what to do about a row on it. */
  outsideText: string;
  /** Every enabled schedule that is no seat of any factory, as the Gateway ordered them. */
  outsideRows: FactoryOutsideScheduleRow[];
  /** Set when there are none. */
  outsideEmptyText: string | null;
}

/** One enabled schedule that is no seat of any registered factory (issue #3650). */
export interface FactoryOutsideScheduleRow {
  id: string;
  name: string;
  /** When it runs, in words. */
  whenText: string;
  /** The computer it runs on. */
  machine: string;
  /** "In no factory", or the factory or seat it names that the registry does not have. */
  reason: string;
}

export interface FactoryGoalCard {
  heading: string;
  text: string | null;
  note: string | null;
  emptyText: string | null;
}

export interface FactoryGoalNumberCard {
  heading: string;
  valueText: string | null;
  asOfText: string | null;
  linkHref: string | null;
  linkLabel: string | null;
  emptyText: string | null;
}

export interface FactoryPageWaiting {
  heading: string;
  items: FactoryWaitingItem[];
  emptyText: string | null;
  /** The order the items are in, said once. */
  orderText: string | null;
  /** "Mark everything older than 7 days as handled", or null when nothing is that old. */
  bulkHandled: FactoryOwnerAction | null;
  /** "Nothing here is older than 7 days." */
  bulkHandledNote: string | null;
}

/** One failure that still makes the factory FAILING. */
export interface FactoryFailureItem {
  /** The failed row; null for a schedule that could not start its run, which clears by itself. */
  id: string | null;
  subject: string | null;
  what: string;
  /** "Sender, today 12:02". */
  by: string;
  sessionId: string | null;
  sessionLabel: string | null;
  link: string | null;
  linkLabel: string | null;
  /** "Handled", or null when the item cannot be marked handled. */
  handledLabel: string | null;
  handledBusyLabel: string | null;
  /** How the item clears when it cannot be marked handled. */
  note: string | null;
}

export interface FactoryPageFailures {
  /** "Failing". */
  heading: string;
  note: string;
  items: FactoryFailureItem[];
}

export interface FactoryCeoLatest {
  heading: string;
  lines: string[];
  emptyText: string | null;
  allLabel: string | null;
  allHref: string | null;
}

export interface FactoryLastTalk {
  heading: string;
  text: string;
}

export interface FactoryPageView {
  id: string;
  title: string;
  /** "Factories / WarmForward". */
  crumb: string;
  crumbHref: string;
  statusWord: string;
  statusTone: FactoryTone;
  statusReason: string;
  /** The one short line under the status word ("Nothing scheduled", "Sender: 4 failures, ..."); null for RUNNING. */
  statusLine: string | null;
  /** Where clicking the status word goes (the failures, the waiting items, or the Seats tab); null for RUNNING. */
  statusHref: string | null;
  /** The head's own role and name - "CEO Nora Hale", "CFO Ruth Calder" - or "No head named". */
  ceoText: string;
  /** "4 seats". */
  seatCountText: string;
  /** "runs on SOREN_NORTH". */
  computerText: string;
  talk: FactoryTalkTarget | null;
  /** Overview, Seats (n), Activity, Reports, Memory, Documents. */
  tabs: FactoryTab[];
  goal: FactoryGoalCard;
  goalNumber: FactoryGoalNumberCard;
  /** What is failing now (where FAILING links to); null when nothing is. */
  failures: FactoryPageFailures | null;
  waiting: FactoryPageWaiting;
  ceoLatest: FactoryCeoLatest;
  lastTalk: FactoryLastTalk;
  /** What the Documents tab says: the definitions are not on the Gateway yet. */
  documentsText: string;
  truncatedText: string | null;
  /** "Archive factory", or null when it is archived. */
  archive: FactoryOwnerAction | null;
  /** Set when it is archived. */
  archivedText: string | null;
  /** "Restore", when it is archived. */
  restore: FactoryOwnerAction | null;
}

export interface FactorySeatRow {
  seatId: string;
  name: string;
  role: string;
  whenText: string;
  lastRunText: string;
  lastRunTone: FactoryTone;
  computerText: string;
  talk: FactoryTalkTarget;
}

export interface FactorySeatsView {
  factoryId: string;
  title: string;
  /** "Factories / WarmForward / Seats". */
  crumb: string;
  /** "Seat", "When it runs", "Last run", "Computer". */
  columns: string[];
  rows: FactorySeatRow[];
  note: string;
}

/** The Gateway's answer to a Talk: the top-level session it started, and where the Cockpit opens it. */
export interface FactoryTalkStarted {
  sessionId: string;
  sessionName: string;
  /** "/session/<id>". */
  href: string;
  factory: string;
  seat: string;
  computer: string;
  directorId: string;
}

const PREFIX = "/gateway/factories";
const TALK_PREFIX = "/gateway/factory-agents/factories";

const NOT_SERVED =
  "This Gateway does not serve the Factories screen - it answered with a web page instead of data. Upgrade or redeploy the Gateway.";

// A 2XX is not proof the Gateway understood the request: a Gateway from before this screen answers unknown paths with
// the app's own HTML shell. Every answer is asserted to be JSON. A refusal keeps the Gateway's own sentence.
function assertJson(res: Response): void {
  const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  if (type !== "application/json") throw new GatewayError(502, NOT_SERVED);
}

async function getJson<T>(path: string, what: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal });
  if (!res.ok) throw await GatewayError.from(res, what);
  assertJson(res);
  return (await res.json()) as T;
}

/**
 * The sentence a refused Talk shows. The Talk route refuses with `{ error }`; the session create behind it can
 * refuse with a problem-details body (`{ title, detail }`, e.g. a Director that is not connected), and some bodies
 * carry both an error and a detail. Every sentence the Gateway wrote is shown - none is dropped for another.
 */
export function talkRefusalReason(body: unknown): string | undefined {
  if (!body || typeof body !== "object") return undefined;
  const b = body as { error?: unknown; title?: unknown; detail?: unknown };
  const error = typeof b.error === "string" && b.error.trim().length > 0 ? b.error.trim() : undefined;
  const detail = typeof b.detail === "string" && b.detail.trim().length > 0 ? b.detail.trim() : undefined;
  const title = typeof b.title === "string" && b.title.trim().length > 0 ? b.title.trim() : undefined;
  const head = error ?? title;
  if (head !== undefined && detail !== undefined && head !== detail) return `${terminated(head)} ${terminated(detail)}`;
  return head ?? detail;
}

function terminated(sentence: string): string {
  return /[.!?]$/.test(sentence) ? sentence : `${sentence}.`;
}

export function getFactoriesList(signal?: AbortSignal): Promise<FactoriesListView> {
  return getJson<FactoriesListView>(PREFIX, "load the factories", signal);
}

export function getFactoryPage(factory: string, signal?: AbortSignal): Promise<FactoryPageView> {
  return getJson<FactoryPageView>(`${PREFIX}/${encodeURIComponent(factory)}`, "load this factory", signal);
}

export function getFactorySeats(factory: string, signal?: AbortSignal): Promise<FactorySeatsView> {
  return getJson<FactorySeatsView>(`${PREFIX}/${encodeURIComponent(factory)}/seats`, "load this factory's seats", signal);
}

/**
 * "Handled" on a failure: the Gateway appends a NEW row that corrects it; the failed row is never changed. A refusal
 * throws a GatewayError carrying the Gateway's own sentence.
 */
export async function markFactoryFailureHandled(factory: string, id: string, signal?: AbortSignal): Promise<void> {
  const path = `${PREFIX}/${encodeURIComponent(factory)}/failures/${encodeURIComponent(id)}/handled`;
  const res = await fetch(path, { method: "POST", headers: { Accept: "application/json", ...authHeaders() }, signal });
  if (!res.ok) throw await GatewayError.from(res, "mark it handled");
  assertJson(res);
}

/**
 * Press Talk: the Gateway starts a new top-level session owned by the person who pressed it, seated as that seat.
 * A refusal (no Director on the seat's computer, an unknown seat, the switch off) throws a GatewayError carrying the
 * Gateway's own sentence.
 */
export async function startFactoryTalk(target: FactoryTalkTarget, signal?: AbortSignal): Promise<FactoryTalkStarted> {
  const path = `${TALK_PREFIX}/${encodeURIComponent(target.factoryId)}/seats/${encodeURIComponent(target.seatId)}/talk`;
  const res = await fetch(path, {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    let parsed: unknown = undefined;
    try {
      parsed = text.length > 0 ? JSON.parse(text) : undefined;
    } catch {
      parsed = undefined;
    }
    const reason =
      talkRefusalReason(parsed) ??
      (parsed === undefined && text.length > 0 && text.length <= 300 && !text.trimStart().startsWith("<")
        ? text.trim()
        : undefined);
    throw new GatewayError(res.status, reason ?? `Could not start the talk (error ${res.status}).`, { reason });
  }
  assertJson(res);
  return (await res.json()) as FactoryTalkStarted;
}

const OWNER_ACTION_PATH: Record<FactoryOwnerAction["action"], string> = {
  "handled-older": "waiting/handled-older",
  archive: "archive",
  restore: "restore",
};

/**
 * Carry out an owner action the owner confirmed: the body is what the confirm showed, sent back unchanged. A refusal
 * (another caller than the owner's own browser or phone, a count or schedules that changed since the confirm) throws
 * a GatewayError carrying the Gateway's own sentence.
 */
export async function runFactoryOwnerAction(action: FactoryOwnerAction, signal?: AbortSignal): Promise<FactoryOwnerActionResult> {
  const path = `${PREFIX}/${encodeURIComponent(action.factoryId)}/${OWNER_ACTION_PATH[action.action]}`;
  const res = await fetch(path, {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ cutoffUtc: action.cutoffUtc, expectedCount: action.expectedCount, schedules: action.schedules }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, action.label.toLowerCase());
  assertJson(res);
  return (await res.json()) as FactoryOwnerActionResult;
}
