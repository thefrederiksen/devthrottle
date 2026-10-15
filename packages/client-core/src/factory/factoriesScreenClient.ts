// The Factories screen (Factories screen mission, phase D): the typed, same-origin client the Cockpit's Factories
// pages read - the list of factories, one factory's page, its Seats tab, and the Talk button.
//
// CRITICAL RULE 7 - THE CLIENT IS DUMB. These types mirror src/CcDirector.Gateway.Contracts/FactoriesScreenDtos.cs
// (the views, folded by FactoriesScreenFold) and the Talk answer in FactoryRegistryDtos.cs. Every word, tone and order
// arrives finished; a page renders it verbatim and never counts, sorts or decides what a status means.
import { authHeaders, GatewayError } from "../api/client";
import type {
  FactoryActivityLine,
  FactoryNumber,
  FactoryPause,
  FactoryTab,
  FactoryTone,
  FactoryWaitingItem,
  FactoryWokenBy,
} from "./factoryAgentsClient";
import type { CronJob } from "../schedule/scheduleClient";

/** What a Talk button starts: the Cockpit sends the two ids back and never works out which agent a button means. */
export interface FactoryTalkTarget {
  /** "Talk to the boss", or "Talk" on a seat row. */
  label: string;
  /** What the button says while the talk is being started: "Starting the talk with the boss...", or the seat's name on a seat row. */
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
  /** Exactly one of FAILING, NEEDS YOU, PAUSED, RUNNING, TALK ONLY. */
  statusWord: string;
  statusTone: FactoryTone;
  /** Where statusWord stands worst first: 0 FAILING, 1 NEEDS YOU, 2 PAUSED, 3 RUNNING, 4 TALK ONLY. The Cockpit
   *  sorts and groups by it. */
  statusRank: number;
  statusReason: string;
  /** The one short line under the status word ("3 schedules switched off", "Sender: 4 failures, ...", "Nothing
   *  scheduled. You run it by talking to it."); null for RUNNING. */
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
  /** The boss's Talk button ("Talk to the boss"); null when the factory has no boss. */
  talk: FactoryTalkTarget | null;
  /** "No boss named" when talk is null. */
  noBossText: string | null;
  /** The factory's one-line purpose from the registry, shown under its name on the card; null when none is set. */
  purpose: string | null;
  /** The boss's role word ("Boss", or "CFO") for the card's avatar and name line - never a person's name; null
   *  when no boss is named. */
  bossName: string | null;
  /** The flag for seats that do not close themselves ("Does not close itself: Scout - stayed open 3 of its last 7
   *  runs"); null when every seat closes itself or has nothing recorded. Shown amber, verbatim. */
  leftOpenText: string | null;
  /** The Seats tab, where each seat shows its marker; null with no flag. */
  leftOpenHref: string | null;
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
  /** Every active schedule that is no seat of any factory, as the Gateway ordered them. */
  outsideRows: FactoryOutsideScheduleRow[];
  /** Set when there are none. */
  outsideEmptyText: string | null;
}

/** One active schedule that is no seat of any registered factory (issue #3650). */
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

/** The factory emails that covered a factory, kept for 30 days. Every word is the Gateway's. */
export interface FactoryPageEmails {
  /** "Reports emailed to you". */
  heading: string;
  note: string;
  /** Newest first. */
  items: FactoryPageEmailItem[];
  emptyText: string | null;
}

/** One kept factory email. */
export interface FactoryPageEmailItem {
  /** As GET /gateway/factory-emails/{id} takes it. */
  id: string;
  /** Its subject line, as sent. */
  title: string;
  /** "Morning report" or "Shift check". */
  kindText: string;
  /** "Sent today 08:00". */
  whenText: string;
  /** "3 problems listed". */
  problemText: string;
  /** "Read". */
  readLabel: string;
  /** The Cockpit page that shows it as sent. */
  href: string;
}

/** One kept factory email with its HTML exactly as sent (GET /gateway/factory-emails/{id}). */
export interface FactoryEmail {
  id: string;
  title: string;
  /** The email's body, exactly as it was sent. */
  html: string;
}

/** A factory's open problems, from the one problems fold. Every word is the Gateway's. */
export interface FactoryPageProblems {
  /** "Open problems". */
  heading: string;
  /** How a problem stops being open. */
  note: string;
  /** Newest first. */
  items: FactoryProblemItem[];
  /** "No open problems." when there are none. */
  emptyText: string | null;
}

/** One open problem of one scheduled run. */
export interface FactoryProblemItem {
  /** The run, as `cc-devthrottle run resolve` names it. */
  runId: string;
  /** "Scout - Tallyhand - Scout - daily scan". */
  seatText: string;
  /** "Did not report: its session ended without reporting how the run went.". */
  what: string;
  /** "Started Sun 11 Oct 06:15", or "Due Sun 11 Oct 06:15". */
  whenText: string;
  /** "morning shift, Sun 11 Oct, 08:00-16:00". */
  shiftText: string;
  /** "open 3 hours". */
  openForText: string;
  sessionId: string | null;
  sessionLabel: string | null;
  /** Set when the run never started a session. */
  noSessionText: string | null;
  handle: FactoryProblemHandle;
  /** "Resolve". */
  resolveLabel: string;
  /** "Why is it resolved? One line." */
  resolvePrompt: string;
  /** "Resolve with this reason". */
  resolveConfirmLabel: string;
  /** "Resolving...". */
  resolveBusyLabel: string;
}

/** Handle the issue: opens a talk with the factory's boss, briefed on the problem - or, for a factory with no boss, a
 *  talk with the Factory Manager, briefed in the same words. */
export interface FactoryProblemHandle {
  /** "Handle the issue". */
  label: string;
  /** "Starting the talk with the boss...", or "...with the Factory Manager..." for a factory with no boss. */
  busyLabel: string;
  factoryId: string;
  runId: string;
  available: boolean;
  /** Why it cannot, when available is false. */
  unavailableText: string | null;
}

export interface FactoryPageFailures {
  /** "Failing". */
  heading: string;
  note: string;
  items: FactoryFailureItem[];
}

export interface FactoryBossLatest {
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
  /** The one short line under the status word ("3 schedules switched off", "Sender: 4 failures, ...", "Nothing
   *  scheduled. You run it by talking to it."); null for RUNNING. */
  statusLine: string | null;
  /** Where clicking the status word goes (the failures, the waiting items, or the Seats tab); null for RUNNING. */
  statusHref: string | null;
  /** The boss seat's role word - "Boss", or a distinct one the factory registered ("CFO") - or "No boss named". */
  bossText: string;
  /** "4 seats". */
  seatCountText: string;
  /** The same flag as the list row's leftOpenText; null when every seat closes itself or has nothing recorded. */
  leftOpenText: string | null;
  /** The Seats tab; null with no flag. */
  leftOpenHref: string | null;
  /** "runs on SOREN_NORTH". */
  computerText: string;
  talk: FactoryTalkTarget | null;
  /** Overview, Seats (n), Activity, Reports, Memory, Documents. */
  tabs: FactoryTab[];
  goal: FactoryGoalCard;
  goalNumber: FactoryGoalNumberCard;
  /** What is failing now (where FAILING links to); null when nothing is. */
  failures: FactoryPageFailures | null;
  /** The open problems of this factory's scheduled runs, each with Handle the issue and Resolve (Factory Control,
   *  step 1b); null when the view was folded without them. */
  problems: FactoryPageProblems | null;
  /** The factory emails sent to the account that covered this factory, newest first, each readable again as sent;
   *  null when the view was folded without them. */
  emails: FactoryPageEmails | null;
  waiting: FactoryPageWaiting;
  bossLatest: FactoryBossLatest;
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
  /** Whether its sessions close themselves: "closes itself - its last 7 runs", "stayed open 3 of its last 7 runs",
   *  or "no runs recorded yet". */
  closingText: string;
  /** ok, amber or grey. */
  closingTone: FactoryTone;
  /** Every shift the seat works in, in words: "Morning shift", "Night and evening shifts", "No run in the next 7 days". */
  shiftText: string;
  talk: FactoryTalkTarget;
  /** Every schedule the seat names, with its words and the button that edits it; empty when it has none. */
  schedules: FactorySeatSchedule[];
  /** "Open seat": every seat row opens its own page, with or without a schedule (Factory Seats mission). */
  openLabel: string;
  /** The seat's page. */
  href: string;
}

/** One schedule of a seat on the Seats tab (the owner, 2026-10-09: a factory's schedules are edited in the factory). */
export interface FactorySeatSchedule {
  jobId: string;
  /** "Daily 06:15", or "Schedule cj_x is missing". */
  whenText: string;
  /** "Edit schedule"; null for a schedule that no longer exists. */
  editLabel: string | null;
  /** True when the schedule is paused; the button beside it then resumes it. */
  paused: boolean;
  /** "Pause" or "Resume" (issue #3910); null for a schedule that no longer exists or a one-off that has run. */
  toggleLabel: string | null;
  /** The shifts this schedule starts runs in, in words: "Morning shift". */
  shiftText: string;
}

/**
 * One shift's group on the Seats tab (Factory Control, step 3), folded by the Gateway: night, morning, evening, then
 * "none" for the seats with no run in the next 7 days. A seat in two shifts is in both, each row with only that
 * shift's schedules. Render in the order given.
 */
export interface FactorySeatGroup {
  /** "night", "morning", "evening" or "none". */
  shift: string;
  /** "Morning shift, 08:00-16:00", or "No run in the next 7 days". */
  title: string;
  rows: FactorySeatRow[];
}

export interface FactorySeatsView {
  factoryId: string;
  title: string;
  /** "Factories / WarmForward / Seats". */
  crumb: string;
  /** "Seat", "When it runs", "Last run", "Computer". */
  columns: string[];
  rows: FactorySeatRow[];
  /** The seats grouped by shift; the Seats tab renders these. */
  groups: FactorySeatGroup[];
  note: string;
}

// One seat's page (Factory Seats mission, phase 1), mirroring FactorySeatPageDtos.cs. Every seat has one, whether or
// not anything is scheduled for it. Every word, tone and order is the Gateway's (FactorySeatPageFold).

/**
 * "What this seat does": the seat's brief (FactorySeatBriefDto). A seat with a published brief on the Gateway shows
 * its text, its version, who published it and when; a seat with only drafts says so; a seat with no brief on the
 * Gateway shows where its brief file is. The page draws the fields that are not null.
 */
export interface FactorySeatBrief {
  heading: string;
  /** How the brief stands, in the Gateway's word. The page lays out fields; it never branches on what this means. */
  state: "published" | "drafts" | "file";
  /** The published brief's whole text; null when nothing is published. */
  text: string | null;
  /** "Version 3, published by a person (...) today 20:14."; null when nothing is published. */
  versionText: string | null;
  /** That a newer draft waits, or that the seat has drafts and none is published; null when no draft waits. */
  draftText: string | null;
  /** Set when as many drafts wait as a session may leave; null below that. */
  waitingText: string | null;
  /** "Brief file"; null when the brief is published on the Gateway. */
  pathLabel: string | null;
  /** The brief file's full path on its computer; null when the brief is published on the Gateway. */
  pathText: string | null;
  /** "Computer". */
  computerLabel: string | null;
  computerText: string | null;
  /** That the brief is a file on that computer and what a talk and a run are told. */
  note: string | null;
  /** The button that opens the editor: "Edit", "Open the newest draft" or "Write the brief here". */
  editLabel: string;
  /** "History"; null for a seat with no version on the Gateway. */
  historyLabel: string | null;
}

/** "Take off this seat" and the confirm it asks first; every sentence is the Gateway's. */
export interface FactorySeatUnlink {
  jobId: string;
  label: string;
  busyLabel: string;
  confirmTitle: string;
  confirmLines: string[];
  confirmLabel: string;
}

/** One schedule of a seat, on the seat's page. */
export interface FactorySeatPageSchedule {
  jobId: string;
  /** The schedule's name; null for one the Gateway no longer has. */
  name: string | null;
  /** "Daily 06:15", or "Schedule cj_x is missing". */
  whenText: string;
  shiftText: string;
  /** "Paused" when it is switched off, else null. */
  pausedText: string | null;
  /** What it means that the Gateway no longer has this schedule, else null. */
  missingText: string | null;
  editLabel: string | null;
  paused: boolean;
  toggleLabel: string | null;
  unlink: FactorySeatUnlink | null;
}

/** "How it starts": the seat's schedules and triggers, or that it is talk only. */
export interface FactorySeatStarts {
  heading: string;
  /** Set when the seat has no schedule and no trigger. */
  talkOnlyText: string | null;
  schedules: FactorySeatPageSchedule[];
  triggers: FactoryWokenBy[];
  /** Pause or Resume for the seat's triggers; null when it has none. */
  triggersPause: FactoryPause | null;
  /** For a factory with a definition: its schedules come from it and change there, so none offers Edit or Take off. */
  definedText: string | null;
  /** "Open the Definition tab"; null when `definedText` is. */
  definitionLabel: string | null;
  /** The factory's Definition tab; null when `definedText` is. */
  definitionHref: string | null;
}

/** "What it has left behind": the seat's last talk, last run, recent activity and the factory's memory. */
export interface FactorySeatLeftBehind {
  heading: string;
  lastTalkLabel: string;
  lastTalkText: string;
  lastRunLabel: string;
  lastRunText: string;
  lastRunTone: FactoryTone;
  /** Whether its sessions close themselves; null for a seat with no schedule. */
  closingText: string | null;
  closingTone: FactoryTone | null;
  numbersTitle: string;
  numbers: FactoryNumber[];
  recentTitle: string;
  recent: FactoryActivityLine[];
  recentEmptyText: string | null;
  allLabel: string;
  allHref: string;
  memoryLabel: string;
  memoryText: string;
  memoryLinkLabel: string;
  memoryHref: string;
}

export interface FactorySeatPageView {
  factoryId: string;
  factoryTitle: string;
  seatId: string;
  /** "Factories / WarmForward / Seats / Scout". */
  crumb: string;
  /** Where each part of the crumb but the last goes, in order. */
  crumbHrefs: string[];
  /** The seat's name, or "Boss" for the boss seat. */
  name: string;
  role: string;
  /** "runs on SOREN_NORTH". */
  computerText: string;
  /** "Talk only", "Scheduled", "Triggered", "Scheduled and triggered", "Paused" or "Schedule missing". */
  startsWord: string;
  startsTone: FactoryTone;
  talk: FactoryTalkTarget;
  brief: FactorySeatBrief;
  starts: FactorySeatStarts;
  goal: FactoryGoalCard;
  goalNumber: FactoryGoalNumberCard;
  leftBehind: FactorySeatLeftBehind;
  truncatedText: string | null;
}

/** What taking a schedule off its seat did (CronUnlinkResultDto). */
export interface FactorySeatUnlinked {
  /** What happened, in the Gateway's words. */
  text: string;
  factory: string;
  seat: string | null;
  /** The schedule as it is now: in no factory, switched as it was. */
  job: CronJob;
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

// The factory floor (owner decision, 8 October 2026), mirroring FactoryFloorDtos.cs. Every position, word and tone
// is the Gateway's; the Cockpit only draws it.
export type FactoryFloorLine = "solid" | "dashed" | "dotted";

export interface FactoryFloorOffice {
  title: string;
  sub: string;
  tone: FactoryTone;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface FactoryFloorLane {
  name: string;
  hue: number;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface FactoryFloorBay {
  id: string;
  kind: "seat" | "source";
  title: string;
  sub: string;
  tone: FactoryTone;
  toneText: string;
  dashed: boolean;
  x: number;
  y: number;
  width: number;
  height: number;
  href: string | null;
}

export interface FactoryFloorArrow {
  from: string;
  to: string;
  label: string;
  tone: FactoryTone;
  line: FactoryFloorLine;
  path: string;
  head: string;
  labelX: number;
  labelY: number;
}

export interface FactoryFloorDeskItem {
  title: string;
  text: string;
  tone: FactoryTone;
  href: string | null;
}

export interface FactoryFloorDesk {
  title: string;
  sub: string;
  x: number;
  y: number;
  width: number;
  height: number;
  items: FactoryFloorDeskItem[];
  emptyText: string | null;
  note: string | null;
}

export interface FactoryFloorLegend {
  text: string;
  tone: FactoryTone;
  line: FactoryFloorLine;
}

export interface FactoryFloorView {
  factoryId: string;
  title: string;
  width: number;
  height: number;
  office: FactoryFloorOffice | null;
  lanes: FactoryFloorLane[];
  bays: FactoryFloorBay[];
  arrows: FactoryFloorArrow[];
  desk: FactoryFloorDesk;
  legend: FactoryFloorLegend[];
  notes: string[];
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

/** One seat's page: every seat has one, with or without a schedule. */
export function getFactorySeatPage(factory: string, seat: string, signal?: AbortSignal): Promise<FactorySeatPageView> {
  return getJson<FactorySeatPageView>(
    `${PREFIX}/${encodeURIComponent(factory)}/seats/${encodeURIComponent(seat)}`,
    "load this seat",
    signal,
  );
}

/**
 * "Take off this seat": the schedule stays, switched as it was, and belongs to no factory. One route, never an update
 * with the factory left blank - a blank factory on an update means "said nothing" and keeps the link. A refusal
 * throws a GatewayError carrying the Gateway's own sentence.
 */
export async function takeScheduleOffSeat(jobId: string, signal?: AbortSignal): Promise<FactorySeatUnlinked> {
  const res = await fetch(`/cron/jobs/${encodeURIComponent(jobId)}/unlink`, {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "take the schedule off this seat");
  assertJson(res);
  return (await res.json()) as FactorySeatUnlinked;
}

/** The factory's floor: its production lines, bays, arrows and the owner's desk, laid out by the Gateway. */
export function getFactoryFloor(factory: string, signal?: AbortSignal): Promise<FactoryFloorView> {
  return getJson<FactoryFloorView>(`${PREFIX}/${encodeURIComponent(factory)}/floor`, "load this factory's floor", signal);
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

/**
 * Handle the issue: the Gateway opens a talk with the factory's boss, already told about the problem, as the person's
 * own session. A refusal (no boss yet, the problem already resolved, no Director on the boss's computer) throws a
 * GatewayError carrying the Gateway's own sentence.
 */
export async function startProblemTalk(handle: FactoryProblemHandle, signal?: AbortSignal): Promise<FactoryTalkStarted> {
  const path = `${TALK_PREFIX}/${encodeURIComponent(handle.factoryId)}/problems/${encodeURIComponent(handle.runId)}/handle`;
  const res = await fetch(path, { method: "POST", headers: { Accept: "application/json", ...authHeaders() }, signal });
  if (!res.ok) throw await GatewayError.from(res, "start the talk");
  assertJson(res);
  return (await res.json()) as FactoryTalkStarted;
}

/**
 * Resolve a run's problem with a one-line reason - the same route `cc-devthrottle run resolve` calls. The reason is
 * sent as typed: the Gateway decides whether it is acceptable, and a refusal (no reason, several lines, already
 * resolved) throws a GatewayError carrying its own sentence.
 */
export async function resolveRunProblem(runId: string, reason: string, signal?: AbortSignal): Promise<void> {
  const res = await fetch(`/cron/runs/${encodeURIComponent(runId)}/resolve`, {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ reason }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "resolve it");
  assertJson(res);
}

/**
 * One kept factory email, with its HTML exactly as sent. A refusal (no such email in this account, a malformed id)
 * throws a GatewayError carrying the Gateway's own sentence.
 */
export async function getFactoryEmail(id: string, signal?: AbortSignal): Promise<FactoryEmail> {
  return getJson<FactoryEmail>(`/gateway/factory-emails/${encodeURIComponent(id)}`, "load the report", signal);
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

// ---- the Factory Manager (Factory Control, step 5) ----------------------------------------------------------------
// Mirrors FactoryManagerDtos.cs. Every word, tone and finding is the Gateway's (FactoryManagerEndpoints.CardOf and
// FactoryNameCheck), rendered verbatim.

/** One seat the name check flagged. */
export interface FactoryNameFinding {
  factory: string;
  factoryTitle: string;
  seatId: string;
  /** "ceo" or "not-a-role-word". */
  kind: string;
  /** The Gateway's sentence. */
  text: string;
}

/** The name check over every factory that is not archived. */
export interface FactoryNameCheck {
  count: number;
  factoriesChecked: number;
  /** "Name check: 2 seats flagged", or "Name check: nothing flagged". */
  headingText: string;
  ruleText: string;
  /** "Every factory passes." when nothing is flagged; null otherwise. */
  passText: string | null;
  findings: FactoryNameFinding[];
}

/** The Factories screen's Factory Manager card. */
export interface FactoryManagerCard {
  title: string;
  purpose: string;
  stateText: string;
  stateTone: FactoryTone;
  /** Where the running Factory Manager opens; null when none runs. */
  sessionHref: string | null;
  /** "Talk to the Factory Manager", or "Open the Factory Manager" while one runs. */
  actionLabel: string;
  busyLabel: string;
  /** True when no computer and folder are saved: the card asks for them before the first start. */
  needsPlacement: boolean;
  /** The computers with a running Director, to choose from; empty once a placement is saved. */
  computers: string[];
  placementText: string | null;
  /** The computer picker's label; null once a placement is saved. */
  computerLabel: string | null;
  /** The folder field's label; null once a placement is saved. */
  folderLabel: string | null;
  placementSavedText: string | null;
  nameCheck: FactoryNameCheck;
}

/** Where a first start puts the Factory Manager: both, or neither (the saved ones). */
export interface FactoryManagerPlacement {
  computer: string;
  folder: string;
}

const FACTORY_MANAGER_PREFIX = "/gateway/factory-agents/factory-manager";

export function getFactoryManagerCard(signal?: AbortSignal): Promise<FactoryManagerCard> {
  return getJson<FactoryManagerCard>(FACTORY_MANAGER_PREFIX, "load the Factory Manager", signal);
}

/**
 * Talk to the Factory Manager: the Gateway answers with the running one, or starts a new top-level session owned by
 * the person who pressed it - on the placement sent, or the saved one - and binds it. A refusal (no placement yet, no
 * Director on the computer, the switch off) throws a GatewayError carrying the Gateway's own sentence.
 */
export async function startFactoryManagerTalk(
  placement: FactoryManagerPlacement | null,
  signal?: AbortSignal,
): Promise<FactoryTalkStarted> {
  const res = await fetch(`${FACTORY_MANAGER_PREFIX}/talk`, {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: placement === null ? undefined : JSON.stringify(placement),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "start the Factory Manager");
  assertJson(res);
  return (await res.json()) as FactoryTalkStarted;
}
