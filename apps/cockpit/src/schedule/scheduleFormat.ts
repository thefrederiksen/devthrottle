// The pure display logic of the Schedule page (issue #1245). Turning a cron string into plain English,
// deciding a job's type chip and one-line label, and phrasing a next/last run as a relative time are
// all pure functions with no React and no DOM, so they are unit-tested directly (scheduleFormat.test.ts).
// The grid and drawer (ScheduleView.tsx) only render what these return. Everything here is ASCII and
// spelled out in full words - no abbreviations leak into the interface.

import type { CronJob } from "@devthrottle/client-core/schedule/scheduleClient";

/** The three kinds of thing a scheduled job runs, shown as a small type chip in the grid. */
export type ActionType = "Skill" | "Work list" | "Prompt";

// Which type chip a job wears. A named work list drains that list; a seed that begins with a slash is a
// skill invocation (for example "/inbound-watch"); anything else is a free-text prompt. This replaces
// the old code that glued the raw internal word "skill" onto the front of the prompt body.
export function actionType(job: CronJob): ActionType {
  const workList = job.action.workListName ?? "";
  if (workList.trim().length > 0) return "Work list";
  return job.action.seed.trim().startsWith("/") ? "Skill" : "Prompt";
}

// The short, single-line label shown beside the type chip - never the prompt body. For a work list it
// is the list name. For a skill it is the command and its arguments from the first line. For a prompt
// it is the first non-empty line, whitespace-collapsed and truncated so the row stays one line high.
export function actionShortLabel(job: CronJob, maxLength = 60): string {
  const workList = job.action.workListName ?? "";
  if (workList.trim().length > 0) return workList.trim();

  const firstLine = firstNonEmptyLine(job.action.seed);
  if (firstLine.length === 0) return "(no prompt)";
  return truncate(firstLine, maxLength);
}

// The full prompt body for the drawer, exactly as stored (no truncation, no "skill" prefix). The drawer
// shows this read-only in a scrollable monospace block. A work-list job has no prompt body.
export function promptBody(job: CronJob): string {
  const workList = job.action.workListName ?? "";
  if (workList.trim().length > 0) return `Drains the work list "${workList.trim()}".`;
  return job.action.seed;
}

function firstNonEmptyLine(text: string): string {
  for (const line of text.split(/\r?\n/)) {
    const collapsed = line.trim().replace(/\s+/g, " ");
    if (collapsed.length > 0) return collapsed;
  }
  return "";
}

function truncate(text: string, maxLength: number): string {
  if (text.length <= maxLength) return text;
  return `${text.slice(0, Math.max(0, maxLength - 3)).trimEnd()}...`;
}

// ===== Relative times =========================================================================

// A future instant phrased as "in 46m" / "in 3h" / "in 2d", with "now" for the current minute and
// "overdue" for a time already past. `now` is injectable so a ticking page renders consistently and so
// the tests are deterministic. Returns "-" when the instant is absent or unparseable (fail visibly, do
// not invent a time).
export function relativeUntil(iso: string | null | undefined, now: number = Date.now()): string {
  if (iso === null || iso === undefined || iso.length === 0) return "-";
  const target = Date.parse(iso);
  if (Number.isNaN(target)) return "-";
  const seconds = Math.floor((target - now) / 1000);
  if (seconds < 0) return "overdue";
  if (seconds < 60) return "now";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `in ${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `in ${hours}h`;
  return `in ${Math.floor(hours / 24)}d`;
}

// ===== Active, paused and historical ==========================================================

/** The three lists the Schedule page shows, one tab each. Active is the default. */
export type ScheduleList = "active" | "paused" | "historical";

// Which tab a job belongs on, read from the lifecycle the Gateway stamped on it - the page never works out
// for itself whether a schedule will still run. A spent one-off (it fired) and a one-off switched off before
// it fired are both history; a switched-off recurring schedule is paused, because switching it back on resumes
// it. A job with no lifecycle is listed as active, so a schedule can never vanish from the default view.
export function scheduleListOf(job: CronJob): ScheduleList {
  switch (job.lifecycle) {
    case "paused":
      return "paused";
    case "spent":
    case "off":
      return "historical";
    default:
      return "active";
  }
}

// The Next run cell. Only an active schedule has a next run to count down to: a spent one-off ran, so it reads
// "ran <date>" and never "overdue"; a paused schedule reads "paused"; a one-off switched off before it fired
// reads "off".
export function nextRunLabel(job: CronJob, now: number = Date.now()): string {
  switch (job.lifecycle) {
    case "spent":
      return `ran ${utcDate(job.lastFiredUtc)}`;
    case "paused":
      return "paused";
    case "off":
      return "off";
    default:
      return relativeUntil(job.nextRunUtc, now);
  }
}

// The instant the Next run cell's hover shows: when a spent one-off ran, otherwise when the job next runs.
export function nextRunInstant(job: CronJob): string | null | undefined {
  return job.lifecycle === "spent" ? job.lastFiredUtc : job.nextRunUtc;
}

// "yyyy-MM-dd" in UTC, matching the rest of the page, or "-" when absent.
function utcDate(iso: string | null | undefined): string {
  const full = absoluteUtc(iso);
  return full === "-" ? full : full.slice(0, 10);
}

// ===== Grouping by factory (the owner's layout A, 2026-10-09) ====================================

/** The group key of a plain scheduled job in no factory. Factory groups are keyed by their factory id. */
export const NO_FACTORY_GROUP = "";

// Which group a schedule is listed under: its factory, from the schedule's own factory field. A schedule with no
// factory is a plain scheduled job - its NAME is never read to guess one.
export function scheduleGroupOf(job: CronJob): string {
  const factory = (job.factory ?? "").trim();
  return factory.length > 0 ? factory : NO_FACTORY_GROUP;
}

// The owner's own jobs first, then factory groups in title order.
export function compareScheduleGroups(a: string, b: string, titleOf: (groupKey: string) => string): number {
  if (a === b) return 0;
  // The owner's own jobs come first (2026-10-09): they are what this page is mostly for.
  if (a === NO_FACTORY_GROUP) return -1;
  if (b === NO_FACTORY_GROUP) return 1;
  return titleOf(a).localeCompare(titleOf(b), undefined, { sensitivity: "base" });
}

// Whether a schedule runs in a factory. Its home is the factory's page: the Schedule page hides it unless asked, and
// sends its Edit there (the owner, 2026-10-09).
export function isFactorySchedule(job: CronJob): boolean {
  return scheduleGroupOf(job) !== NO_FACTORY_GROUP;
}

// A group's heading: the factory's registered title, the factory id when it is not registered, or "Personal"
// for the plain ones.
export function scheduleGroupTitle(groupKey: string, rows: CronJob[]): string {
  // The owner, 2026-10-09: a schedule in no factory is allowed, and it is filed under Personal rather than refused.
  if (groupKey === NO_FACTORY_GROUP) return "Personal";
  const title = rows.find((job) => (job.factoryTitle ?? "").trim().length > 0)?.factoryTitle;
  return title ?? groupKey;
}

// The badge beside a schedule: whether it repeats or runs once.
export function repeatsLabel(job: CronJob): "REPEATS" | "ONCE" {
  return job.scheduleKind === "oneOff" ? "ONCE" : "REPEATS";
}

// The absolute wall-clock form shown on hover: "yyyy-MM-dd HH:mm UTC", or "-" when absent. Kept in UTC
// (matching the rest of the Schedule page) so a job's time reads the same on every machine.
export function absoluteUtc(iso: string | null | undefined): string {
  if (iso === null || iso === undefined || iso.length === 0) return "-";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "-";
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())} ${pad(
    date.getUTCHours(),
  )}:${pad(date.getUTCMinutes())} UTC`;
}

// A sortable epoch for a run time: the parsed milliseconds, or a very large number when absent so
// "never / no next run" rows sink to the bottom of an ascending (soonest-first) sort rather than
// pretending to be the earliest.
export function epochOrMax(iso: string | null | undefined): number {
  if (iso === null || iso === undefined || iso.length === 0) return Number.MAX_SAFE_INTEGER;
  const parsed = Date.parse(iso);
  return Number.isNaN(parsed) ? Number.MAX_SAFE_INTEGER : parsed;
}

// ===== Last-run outcome badge =================================================================

/** How a last-run outcome is coloured: a success, a failure, an in-between, or none recorded. */
export type OutcomeKind = "ok" | "err" | "warn" | "none";

export interface Outcome {
  kind: OutcomeKind;
  label: string;
}

// Classify a job's last-run status string into a badge. The Gateway's status vocabulary is small; an
// unknown value is shown verbatim as a neutral badge rather than being dropped, so nothing is hidden.
export function lastOutcome(status: string | null | undefined): Outcome {
  const value = (status ?? "").trim();
  if (value.length === 0) return { kind: "none", label: "" };
  const lower = value.toLowerCase();
  if (lower === "ok" || lower === "success" || lower === "succeeded" || lower === "completed") {
    return { kind: "ok", label: "OK" };
  }
  if (lower.includes("fail") || lower === "error" || lower.includes("timeout")) {
    return { kind: "err", label: value };
  }
  return { kind: "warn", label: value };
}

// ===== Cron to plain English ==================================================================

const DAY_NAMES = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const MONTH_NAMES = [
  "January",
  "February",
  "March",
  "April",
  "May",
  "June",
  "July",
  "August",
  "September",
  "October",
  "November",
  "December",
];

// Turn a 5-field cron expression (minute hour day-of-month month day-of-week) into a plain-English
// sentence, for example "13 8,14 * * 1-5" -> "At 8:13 AM and 2:13 PM, Monday through Friday". This
// never throws: an expression it cannot fully phrase falls back to a spelled-out field description
// ("At minute 13, hour 8 and 14, ...") rather than an error, and a malformed expression is returned as
// the raw string so the person still sees exactly what is stored. It phrases the common shapes the
// Cockpit's schedules actually use; the raw cron is always available on hover for the rest.
export function cronToEnglish(cron: string | null | undefined): string {
  const raw = (cron ?? "").trim();
  if (raw.length === 0) return "(no schedule)";
  const fields = raw.split(/\s+/);
  if (fields.length !== 5) return raw;

  const [minute, hour, dayOfMonth, month, dayOfWeek] = fields;
  const timeText = describeTime(minute, hour);
  if (timeText === null) return describeFieldsFallback(fields);

  const dayText = describeDays(dayOfMonth, dayOfWeek);
  const monthText = describeMonths(month);

  const parts = [timeText];
  if (dayText !== null) parts.push(dayText);
  if (monthText !== null) parts.push(monthText);
  return parts.join(", ");
}

// Phrase the minute+hour fields as a list of clock times ("8:13 AM and 2:13 PM"), or a per-minute /
// per-hour phrase, or null when the fields are too complex to render as clock times (the caller then
// uses the spelled-out fallback).
function describeTime(minute: string, hour: string): string | null {
  const minuteEveryStep = stepOf(minute, 60);
  if (minute === "*" && hour === "*") return "Every minute";
  if (minuteEveryStep !== null && hour === "*") return `Every ${minuteEveryStep} minutes`;

  const minutes = expandList(minute, 0, 59);
  const hours = expandList(hour, 0, 23);
  if (minutes === null || hours === null) return null;
  if (minutes.length === 0 || hours.length === 0) return null;
  // Only render explicit clock times when the count stays small enough to read as a list.
  if (minutes.length * hours.length > 6) return null;

  const times: string[] = [];
  for (const h of hours) {
    for (const m of minutes) {
      times.push(clockTime(h, m));
    }
  }
  return `At ${joinList(times)}`;
}

// Phrase the day-of-week and day-of-month fields. Day-of-week is preferred when present because that is
// how the Cockpit's schedules are written ("Monday through Friday"). Returns null when both are the
// wildcard (a daily job has no day clause).
function describeDays(dayOfMonth: string, dayOfWeek: string): string | null {
  if (dayOfWeek !== "*") {
    const range = rangeOf(dayOfWeek);
    if (range !== null) {
      return `${DAY_NAMES[range.start % 7]} through ${DAY_NAMES[range.end % 7]}`;
    }
    const days = expandList(dayOfWeek, 0, 7);
    if (days !== null && days.length > 0) {
      const names = days.map((d) => DAY_NAMES[d % 7]);
      return joinList(dedupeInOrder(names));
    }
  }
  if (dayOfMonth !== "*") {
    const days = expandList(dayOfMonth, 1, 31);
    if (days !== null && days.length > 0) {
      return `on the ${joinList(days.map(ordinal))} of the month`;
    }
  }
  return null;
}

function describeMonths(month: string): string | null {
  if (month === "*") return null;
  const months = expandList(month, 1, 12);
  if (months === null || months.length === 0) return null;
  return `in ${joinList(months.map((m) => MONTH_NAMES[(m - 1) % 12]))}`;
}

// The last-resort readable rendering: spell out each field. Still plain English, never an error.
function describeFieldsFallback(fields: string[]): string {
  const [minute, hour, dayOfMonth, month, dayOfWeek] = fields;
  const labels = [
    `minute ${fieldWords(minute)}`,
    `hour ${fieldWords(hour)}`,
    dayOfMonth === "*" ? null : `day-of-month ${fieldWords(dayOfMonth)}`,
    month === "*" ? null : `month ${fieldWords(month)}`,
    dayOfWeek === "*" ? null : `day-of-week ${fieldWords(dayOfWeek)}`,
  ].filter((label): label is string => label !== null);
  return `At ${labels.join(", ")}`;
}

function fieldWords(field: string): string {
  return field === "*" ? "every" : field.replace(/,/g, " and ");
}

// ===== small numeric helpers for the cron parser =============================================

// Expand a single cron field into the explicit sorted list of values it names, within [min, max].
// Handles "*", a single number, a comma list, a range "a-b", and a step "*/n" or "a-b/n". Returns null
// for anything outside this grammar so the caller can fall back rather than guess.
function expandList(field: string, min: number, max: number): number[] | null {
  if (field === "*") {
    const all: number[] = [];
    for (let value = min; value <= max; value++) all.push(value);
    return all;
  }
  const values = new Set<number>();
  for (const part of field.split(",")) {
    const expanded = expandPart(part, min, max);
    if (expanded === null) return null;
    for (const value of expanded) values.add(value);
  }
  return Array.from(values).sort((a, b) => a - b);
}

function expandPart(part: string, min: number, max: number): number[] | null {
  const stepMatch = /^(.+)\/(\d+)$/.exec(part);
  let base = part;
  let step = 1;
  if (stepMatch !== null) {
    base = stepMatch[1];
    step = Number(stepMatch[2]);
    if (step <= 0) return null;
  }

  let start: number;
  let end: number;
  if (base === "*") {
    start = min;
    end = max;
  } else {
    const rangeMatch = /^(\d+)-(\d+)$/.exec(base);
    if (rangeMatch !== null) {
      start = Number(rangeMatch[1]);
      end = Number(rangeMatch[2]);
    } else if (/^\d+$/.test(base)) {
      start = Number(base);
      end = stepMatch !== null ? max : start;
    } else {
      return null;
    }
  }
  if (start < min || end > max || start > end) return null;

  const result: number[] = [];
  for (let value = start; value <= end; value += step) result.push(value);
  return result;
}

// The step of a "*/n" field, or null when the field is not a simple step over the whole range.
function stepOf(field: string, wholeRangeSize: number): number | null {
  const match = /^\*\/(\d+)$/.exec(field);
  if (match === null) return null;
  const step = Number(match[1]);
  return step > 0 && step < wholeRangeSize ? step : null;
}

// A simple "a-b" range as start/end numbers, or null when the field is not exactly one range.
function rangeOf(field: string): { start: number; end: number } | null {
  const match = /^(\d+)-(\d+)$/.exec(field);
  if (match === null) return null;
  const start = Number(match[1]);
  const end = Number(match[2]);
  return start <= end ? { start, end } : null;
}

// A 24-hour (hour, minute) as a 12-hour clock time, for example (14, 5) -> "2:05 PM".
function clockTime(hour24: number, minute: number): string {
  const period = hour24 < 12 ? "AM" : "PM";
  let hour12 = hour24 % 12;
  if (hour12 === 0) hour12 = 12;
  return `${hour12}:${String(minute).padStart(2, "0")} ${period}`;
}

function ordinal(value: number): string {
  const tens = value % 100;
  if (tens >= 11 && tens <= 13) return `${value}th`;
  switch (value % 10) {
    case 1:
      return `${value}st`;
    case 2:
      return `${value}nd`;
    case 3:
      return `${value}rd`;
    default:
      return `${value}th`;
  }
}

// Join a list into an English series: "a", "a and b", "a, b and c".
function joinList(items: string[]): string {
  if (items.length === 0) return "";
  if (items.length === 1) return items[0];
  if (items.length === 2) return `${items[0]} and ${items[1]}`;
  return `${items.slice(0, -1).join(", ")} and ${items[items.length - 1]}`;
}

function dedupeInOrder(items: string[]): string[] {
  const seen = new Set<string>();
  const result: string[] = [];
  for (const item of items) {
    if (!seen.has(item)) {
      seen.add(item);
      result.push(item);
    }
  }
  return result;
}
