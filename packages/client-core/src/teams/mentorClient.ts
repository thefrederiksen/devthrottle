// The Mentor's weekly page for one team (Teams, first version - devthrottle_internal#2305, screens S6 and S7): the typed,
// same-origin read of GET /teams/{teamId}/mentor?week=YYYY-Www. The contract is
// docs/proof/teams-2305/contract.md, written by the Gateway Developer of #2305; every field here is one of its fields.
//
// CRITICAL RULE 7 - THE GATEWAY DECIDES WHO READS WHAT. Whether the caller gets every block (an Owner or Manager), only
// their own (a Developer) or nothing (a Collaborator, or someone not in the team) is the Gateway's answer, and so is
// every word of every block. This client carries that answer verbatim; it never filters, sorts, scores or rewrites a
// block, and it never works out a role. A quote's text in particular is the person's own prompt as the Gateway copied
// it from the prompt log - it is passed through untouched.
//
// THE ANSWER HAS FOUR KINDS, and only one of them is a failure:
//   - `page`: the Gateway answered 200 with the week's blocks (possibly none);
//   - `refused`: 403 - the caller's role in the team has no Mentor page (a Collaborator);
//   - `not-offered`: 404 (not a member, or no such team - one answer for both) or the Cockpit's own HTML shell, which
//     is what a Gateway with Teams dark answers for a route it never mapped;
//   - anything else (a week the Gateway calls invalid, a fault, a body that is not the contract) is thrown.
//
// AN ANSWER THAT BREAKS THE CONTRACT IS THROWN, NEVER DRAWN AS A GUESS (review of devthrottle#3538, F3 and F4): an
// unknown tone, a date that is not a date, quotes the contract does not allow, more than
// one block on a person's own page, or nobody listed as reading the page. Each would otherwise reach the screen as
// something plausible - above all "nobody else reads this", composed from an absence.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/** One of the person's own prompts, quoted by the Mentor as the example of where the week went badly. */
export interface MentorQuote {
  /** A stable reference to the prompt record. */
  promptId: string;
  /** When the prompt was sent, in UTC. */
  at: string;
  /** The prompt itself, copied verbatim from the prompt log by the Gateway. Never written by a model. */
  text: string;
}

/** One person's block for one week. Every field is the Gateway's; a page lays them out and changes none. */
export interface MentorBlock {
  /** How the block is headed - the way the Team page shows people. null when the person has no email on record. */
  personEmail: string | null;
  /** The name shown above the email, when the Gateway holds one for the person; null otherwise. */
  personName: string | null;
  /** The person's role in the team now, as the Team page names it. */
  role: string;
  /** good, mixed or hard - used only to colour the label beside the person. */
  tone: string;
  /** The words to show for the tone: "a good week", "a mixed week", "a hard week". */
  toneLabel: string;
  workedOn: string;
  howItWent: string | null;
  wentBadlyAndWhy: string | null;
  /** One or two quoted prompts when `wentBadlyAndWhy` is present; empty when it is null. */
  quotes: MentorQuote[];
  oneThingToTry: string;
  writtenAtUtc: string;
  /** Whether this block is about the person reading the page. The person's account identifier is not given out. */
  isYou: boolean;
}

/** Someone else who reads the caller's page: the team's Owner and Managers, as the Gateway lists them. */
export interface MentorReader {
  /** null when the reader has no email on record. */
  email: string | null;
  role: string;
}

/** The 200 answer for one team and one week, or for a person's own account and one week. */
export interface MentorPage {
  /** The team's id; null on a person's own page (scope "personal"). */
  teamId: string | null;
  /** The ISO week, for example "2026-W40". */
  week: string;
  /** The Monday and the Sunday of that week in the team's own time zone, as calendar dates (YYYY-MM-DD). */
  weekStart: string;
  weekEnd: string;
  timeZone: string;
  /** "everyone" for an Owner or Manager, "own" for a Developer, "personal" on a person's own account. Who is in `blocks`
   *  is already decided. */
  scope: "everyone" | "own" | "personal";
  /** Whether the Mentor's run for this team and week has happened. Written with no block for someone does not say
   *  why - the Gateway does not tell, so a page must not guess. */
  written: boolean;
  /** The Gateway's line for a week still being written - `written` false WITH blocks: the blocks so far are shown
   *  under it. null otherwise. Shown as given. */
  writingNote: string | null;
  blocks: MentorBlock[];
  /** Who else reads the caller's page, in the Gateway's order. Empty only on a person's own page: nobody else reads it. */
  readers: MentorReader[];
  /** On a person's own page with no block, the Gateway's sentence for it ("your first page arrives after..."); null
   *  otherwise. Shown as given. */
  emptyNote: string | null;
}

export type MentorAnswer = { kind: "page"; page: MentorPage } | { kind: "refused" } | { kind: "not-offered" };

/** The tones the contract allows. */
const TONES = ["good", "mixed", "hard"];

function contentType(res: Response): string {
  return (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
}

/**
 * The Mentor's page for one team and one ISO week. Leave `week` out for the most recent week that has closed in the
 * team's time zone - the Gateway decides which week that is.
 */
export async function getMentorPage(teamId: string, week?: string, signal?: AbortSignal): Promise<MentorAnswer> {
  return readMentor(`/teams/${encodeURIComponent(teamId)}/mentor`, false, week, signal);
}

/**
 * The Mentor's page for the person's OWN account (owner, 8 Oct 2026): the same page, about them only, read from
 * GET /account/mentor. The same four kinds of answer as a team's page.
 */
export async function getPersonalMentorPage(week?: string, signal?: AbortSignal): Promise<MentorAnswer> {
  return readMentor("/account/mentor", true, week, signal);
}

async function readMentor(path: string, personal: boolean, week: string | undefined, signal: AbortSignal | undefined): Promise<MentorAnswer> {
  const query = week === undefined ? "" : `?week=${encodeURIComponent(week)}`;
  const res = await gatewayFetch(`${path}${query}`, {
    headers: { ...authHeaders(), Accept: "application/json" },
    signal,
  });

  if (res.status === 403) return { kind: "refused" };
  if (res.status === 404) return { kind: "not-offered" };
  if (!res.ok) throw await GatewayError.from(res, "read the Mentor page");

  // Teams dark: the route is not mapped, so the read reached the Cockpit's page fallback and got the app shell.
  if (contentType(res) === "text/html") return { kind: "not-offered" };
  if (contentType(res) !== "application/json") {
    throw new GatewayError(
      502,
      `The Gateway answered the Mentor page with ${contentType(res) || "an unlabelled body"} instead of the Mentor's blocks.`,
    );
  }

  // The Gateway WAS reached; a body that is not JSON is its fault and is said so - never left to surface as a
  // SyntaxError, which the error wording reads as "cannot reach the Gateway" (review F8).
  const body = await res.text();
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    throw new GatewayError(502, `${UNREADABLE}: its answer is not valid JSON.`);
  }
  const page = readPage(parsed, personal);
  // A week the page did not ask for would put one week in the title and another in the blocks (review of the delta, D3).
  if (week !== undefined && page.week !== week) {
    throw new GatewayError(502, `${UNREADABLE}: it was asked for week ${week} and answered week ${page.week}.`);
  }
  return { kind: "page", page };
}

const UNREADABLE = "The Gateway sent a Mentor page the Cockpit cannot read";

function isText(value: unknown): value is string {
  return typeof value === "string";
}

/** A real calendar date written YYYY-MM-DD. */
function isDate(value: unknown): value is string {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const [y, m, d] = value.split("-").map(Number);
  const date = new Date(Date.UTC(y, m - 1, d));
  return date.getUTCFullYear() === y && date.getUTCMonth() === m - 1 && date.getUTCDate() === d;
}

/** A label that says something: a string with at least one character that is not a space. */
function isLabel(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

/** An email as the contract gives it: a label, or null for "no email on record" - never an empty string, which would
 *  head a block with nothing (review of the delta, D1). */
function isEmailOrNull(value: unknown): value is string | null {
  return value === null || isLabel(value);
}

function isTextOrNull(value: unknown): value is string | null {
  return value === null || typeof value === "string";
}

function readPage(raw: unknown, personal: boolean): MentorPage {
  const p = (raw ?? {}) as Record<string, unknown>;
  if (
    !(personal ? p.teamId === null : isText(p.teamId)) ||
    !isText(p.week) ||
    !isDate(p.weekStart) ||
    !isDate(p.weekEnd) ||
    !isText(p.timeZone) ||
    !isText(p.scope) ||
    typeof p.written !== "boolean" ||
    !isTextOrNull(p.writingNote) ||
    !Array.isArray(p.blocks) ||
    !Array.isArray(p.readers)
  ) {
    throw new GatewayError(502, `${UNREADABLE}: it is missing its week, its dates, its scope, its blocks or its readers.`);
  }
  // A team's page is "everyone" or "own"; a person's own page is "personal" and nothing else - a team page that called
  // itself personal, or the reverse, would be drawn with the wrong readers.
  if (personal ? p.scope !== "personal" : p.scope !== "everyone" && p.scope !== "own") {
    throw new GatewayError(502, `${UNREADABLE}: its scope "${p.scope}" is not one this page can have.`);
  }
  const scope = p.scope as MentorPage["scope"];
  // The chooser steps a week at a time from the Monday, so the week must be the Monday-to-Sunday ISO week it names
  // (review of the delta, D3).
  if (isoWeekOf(p.weekStart) !== p.week || weekday(p.weekStart) !== 1 || daysBetween(p.weekStart, p.weekEnd) !== 6) {
    throw new GatewayError(
      502,
      `${UNREADABLE}: week ${p.week} is not the Monday ${p.weekStart} to the Sunday ${p.weekEnd}.`,
    );
  }
  if (scope !== "everyone" && p.blocks.length > 1) {
    throw new GatewayError(502, `${UNREADABLE}: a person's own page holds more than one block.`);
  }
  const blocks = p.blocks.map(readBlock);
  if (scope !== "everyone" && blocks.some((b) => !b.isYou)) {
    throw new GatewayError(502, `${UNREADABLE}: a person's own page holds a block about someone else.`);
  }
  if (blocks.filter((b) => b.isYou).length > 1) {
    throw new GatewayError(502, `${UNREADABLE}: more than one block is marked as the reader's own.`);
  }
  // The line and the week are worked out together on the Gateway: the line is there exactly when an Owner's or
  // Manager's week is still being written. A finished week under "still writing", an unfinished week drawn as finished,
  // or an empty line, would each be drawn as something plausible and false (review J8).
  const stillBeingWritten = !p.written && p.blocks.length > 0 && p.scope === "everyone";
  if (stillBeingWritten !== (p.writingNote !== null) || p.writingNote === "") {
    throw new GatewayError(502, `${UNREADABLE}: its "still writing" line does not match the week it is on.`);
  }
  if (personal) {
    // Nobody else reads a person's own page; a reader listed there would be a false sentence on screen.
    if (p.readers.length !== 0) {
      throw new GatewayError(502, `${UNREADABLE}: a person's own page lists someone else as reading it.`);
    }
    // The empty sentence is there exactly when the page has no block.
    if (!isTextOrNull(p.emptyNote) || (blocks.length === 0) !== (typeof p.emptyNote === "string" && p.emptyNote.trim() !== "")) {
      throw new GatewayError(502, `${UNREADABLE}: its empty-week sentence does not match the blocks it holds.`);
    }
  } else if (p.readers.length === 0) {
    throw new GatewayError(502, `${UNREADABLE}: nobody is listed as reading the page, yet every team has an Owner.`);
  }
  return {
    teamId: personal ? null : (p.teamId as string),
    week: p.week,
    weekStart: p.weekStart,
    weekEnd: p.weekEnd,
    timeZone: p.timeZone,
    scope,
    written: p.written,
    writingNote: p.writingNote,
    blocks,
    readers: p.readers.map(readReader),
    emptyNote: personal ? (p.emptyNote as string | null) : null,
  };
}

function readBlock(raw: unknown): MentorBlock {
  const b = (raw ?? {}) as Record<string, unknown>;
  if (
    !isEmailOrNull(b.personEmail) ||
    !(b.personName === undefined || isTextOrNull(b.personName)) ||
    !isLabel(b.role) ||
    !(isText(b.tone) && TONES.includes(b.tone)) ||
    !isText(b.toneLabel) ||
    !isText(b.workedOn) ||
    !isTextOrNull(b.howItWent) ||
    !isTextOrNull(b.wentBadlyAndWhy) ||
    !Array.isArray(b.quotes) ||
    !isText(b.oneThingToTry) ||
    !isText(b.writtenAtUtc) ||
    typeof b.isYou !== "boolean"
  ) {
    throw new GatewayError(502, `${UNREADABLE}: a block is missing one of its parts.`);
  }
  // The contract ties the quotes to "where it went badly": a quote with nothing to illustrate would have no place on
  // the page, so a block carrying one is not the contract and is said so rather than shown with a quote hidden.
  if (b.wentBadlyAndWhy === null && b.quotes.length > 0) {
    throw new GatewayError(502, `${UNREADABLE}: a block quotes a prompt but says nothing about where the week went badly.`);
  }
  if (b.wentBadlyAndWhy !== null && (b.quotes.length < 1 || b.quotes.length > 2)) {
    throw new GatewayError(
      502,
      `${UNREADABLE}: a block says where the week went badly with ${b.quotes.length} quoted prompts, not one or two.`,
    );
  }
  return {
    personEmail: b.personEmail,
    personName: typeof b.personName === "string" ? b.personName : null,
    role: b.role,
    tone: b.tone,
    toneLabel: b.toneLabel,
    workedOn: b.workedOn,
    howItWent: b.howItWent,
    wentBadlyAndWhy: b.wentBadlyAndWhy,
    quotes: b.quotes.map(readQuote),
    oneThingToTry: b.oneThingToTry,
    writtenAtUtc: b.writtenAtUtc,
    isYou: b.isYou,
  };
}

function readQuote(raw: unknown): MentorQuote {
  const q = (raw ?? {}) as Record<string, unknown>;
  if (!isText(q.promptId) || !isText(q.at) || !isText(q.text)) {
    throw new GatewayError(502, `${UNREADABLE}: a quoted prompt is missing its text, its time or its reference.`);
  }
  return { promptId: q.promptId, at: q.at, text: q.text };
}

function readReader(raw: unknown): MentorReader {
  const r = (raw ?? {}) as Record<string, unknown>;
  if (!isEmailOrNull(r.email) || !isLabel(r.role)) {
    throw new GatewayError(502, `${UNREADABLE}: someone listed as reading the page has no email or role.`);
  }
  return { email: r.email, role: r.role };
}

// THE WEEK CHOOSER'S ARITHMETIC. The Gateway names a week ("2026-W40") and its Monday ("2026-09-28") in the team's
// own time zone; stepping to the week before is calendar arithmetic on that Monday, not a decision about time zones,
// so it is done on the date alone (in UTC, where a date has no daylight-saving edge).

function utcDay(date: string): Date {
  const [y, m, d] = date.split("-").map(Number);
  return new Date(Date.UTC(y, m - 1, d));
}

/** 1 for Monday to 7 for Sunday. */
function weekday(date: string): number {
  const day = utcDay(date).getUTCDay();
  return day === 0 ? 7 : day;
}

function daysBetween(from: string, to: string): number {
  return Math.round((utcDay(to).getTime() - utcDay(from).getTime()) / 86_400_000);
}

/** The ISO week ("YYYY-Www") that contains a calendar date given as YYYY-MM-DD. */
export function isoWeekOf(date: string): string {
  const [y, m, d] = date.split("-").map(Number);
  const day = new Date(Date.UTC(y, m - 1, d));
  // The ISO year of a week is the year of its Thursday.
  const weekday = day.getUTCDay() === 0 ? 7 : day.getUTCDay();
  const thursday = new Date(day.getTime() + (4 - weekday) * 86_400_000);
  const isoYear = thursday.getUTCFullYear();
  const firstThursdayOrdinal = Math.floor((thursday.getTime() - Date.UTC(isoYear, 0, 1)) / 86_400_000);
  const week = Math.floor(firstThursdayOrdinal / 7) + 1;
  return `${isoYear}-W${String(week).padStart(2, "0")}`;
}

/** The week `weeks` weeks away from the one starting on `weekStart` (a Monday, YYYY-MM-DD): its ISO week and its
 *  Monday, so a page can name the week it is asking for before the Gateway has answered. */
export function shiftWeek(weekStart: string, weeks: number): { week: string; weekStart: string } {
  const [y, m, d] = weekStart.split("-").map(Number);
  const monday = new Date(Date.UTC(y, m - 1, d) + weeks * 7 * 86_400_000).toISOString().slice(0, 10);
  return { week: isoWeekOf(monday), weekStart: monday };
}
