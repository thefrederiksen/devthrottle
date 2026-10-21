// WHAT IS USING THE LICENSE, AND MAKE IT LAST (the usage-by-session mandate of 2026-10-20): the client of
// GET /gateway/licenses/usage, GET /gateway/licenses/plan and PUT /gateway/licenses/accounts/price.
//
// Every number, sentence and tone here is the Gateway's (CLAUDE.md rule 7: LicensePlanFold and SessionUsageFold). A
// page shows them as sent; it never works out whether an account lasts, what to stop, or what a session costs.
// A percent the Gateway could not measure yet arrives as null and is shown as such, never as a zero.

import { authHeaders, gatewayFetch, GatewayError, POLL_TIMEOUT_MS } from "../api/client";
import type { LicenseTone } from "./licensesClient";

/** One Claude account the usage can be asked about. */
export interface LicenseUsageAccountChoice {
  accountEmail: string;
  inUse: boolean;
  label: string;
}

/** What one weighted unit is worth on this account: measured from its weekly use's rise against the sessions' use. */
export interface LicenseUsageCalibration {
  isMeasured: boolean;
  percentPerMillionWeighted: number | null;
  fromUtc: string | null;
  toUtc: string | null;
  risePercent: number | null;
  weighted: number | null;
  /** The Gateway's sentence: how it was measured, or why it is not yet. */
  text: string;
}

/** One session that used the account. Percents are of the account's weekly limit; null means not measured yet. */
export interface LicenseUsageSession {
  sessionId: string;
  number: number | null;
  name: string;
  missionId: string | null;
  missionName: string | null;
  topLevelSessionId: string;
  isLive: boolean;
  /** "working", "waiting" or "gone". */
  state: string;
  stateText: string;
  machineName: string;
  model: string | null;
  agentKind: string;
  /** Over the last hour. */
  rateNowPercentPerHour: number | null;
  last4HoursPercent: number | null;
  /** Since it started, through every compaction. */
  soFarPercent: number | null;
  runningHours: number;
  averagePercentPerHour: number | null;
  dollarsSoFar: number | null;
  weightedLastHour: number;
  weightedLast4Hours: number;
  weightedSoFar: number;
  startedAtUtc: string;
  lastSeenAtUtc: string;
}

/** A top-level session with every session under it, or a Mission. */
export interface LicenseUsageGroup {
  id: string | null;
  number: number | null;
  label: string;
  sessionIds: string[];
  liveSessionIds: string[];
  rateNowPercentPerHour: number | null;
  last4HoursPercent: number | null;
  soFarPercent: number | null;
  dollarsSoFar: number | null;
}

export interface LicenseUsage {
  asOfUtc: string;
  accountEmail: string | null;
  accountInUse: boolean;
  accounts: LicenseUsageAccountChoice[];
  emptyText: string | null;
  calibration: LicenseUsageCalibration;
  /** The sentences that say what the one unit is, in order. */
  unitText: string[];
  weekUsedPercent: number | null;
  weekResetsAtUtc: string | null;
  burnPercentPerHour: number | null;
  burnText: string;
  monthlyPriceUsd: number | null;
  dollarsPerPercent: number | null;
  sessions: LicenseUsageSession[];
  topLevel: LicenseUsageGroup[];
  missions: LicenseUsageGroup[];
}

/** A time to make the account last until: another account's return ("room:<email>"), its own reset ("reset"). */
export interface LicensePlanTarget {
  id: string;
  label: string;
  atUtc: string;
}

export interface LicensePlanSuggestion {
  sessionIds: string[];
  savesPercentPerHour: number | null;
  text: string;
}

/** A top-level session offered as one choice: every live session under it. */
export interface LicensePlanChoice {
  topLevelSessionId: string;
  number: number | null;
  label: string;
  sessionIds: string[];
  rateNowPercentPerHour: number | null;
  working: number;
  waiting: number;
}

export interface LicensePlanSession {
  sessionId: string;
  number: number | null;
  name: string;
  topLevelSessionId: string;
  state: string;
  stateText: string;
  rateNowPercentPerHour: number | null;
  suggested: boolean;
}

/** The answer when the sessions asked about stopped. */
export interface LicensePlanWhatIf {
  stoppedSessionIds: string[];
  notFound: string[];
  savesPercentPerHour: number | null;
  burnPercentPerHour: number | null;
  runsOutAtUtc: string | null;
  lasts: boolean | null;
  verdict: string;
  verdictTone: LicenseTone;
}

/** Will the account last until the target, and what to stop if not. Suggestions only: nothing stops a session. */
export interface LicensePlan {
  asOfUtc: string;
  accountEmail: string | null;
  accountInUse: boolean;
  emptyText: string | null;
  targets: LicensePlanTarget[];
  /** "reset", "room:<email>" or "time" (a time that was asked for). */
  targetId: string;
  targetAtUtc: string | null;
  targetText: string;
  weekUsedPercent: number | null;
  leftPercent: number | null;
  hoursToGo: number | null;
  allowedPercentPerHour: number | null;
  burnPercentPerHour: number | null;
  burnText: string;
  shortfallPercentPerHour: number | null;
  runsOutAtUtc: string | null;
  lasts: boolean | null;
  verdict: string;
  verdictTone: LicenseTone;
  suggestion: LicensePlanSuggestion;
  choices: LicensePlanChoice[];
  sessions: LicensePlanSession[];
  whatIf: LicensePlanWhatIf | null;
}

/** What is asked of the planner. Every field is optional; the Gateway picks the account in use and its first target. */
export interface LicensePlanQuestion {
  account?: string | null;
  /** A target id, or a time in ISO 8601. */
  until?: string | null;
  stop?: ReadonlyArray<string>;
  stopGroups?: ReadonlyArray<string>;
}

export interface LicensePriceSet {
  accountEmail: string;
  monthlyPriceUsd: number | null;
}

async function readChecked<T>(path: string, what: string, check: (body: Record<string, unknown>) => boolean,
  missing: string, signal?: AbortSignal): Promise<T> {
  const res = await gatewayFetch(
    path,
    { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal },
    { timeoutMs: POLL_TIMEOUT_MS },
  );
  if (!res.ok) throw await GatewayError.from(res, what);
  const body = (await res.json()) as Record<string, unknown> | null;
  // An answer without its parts is not an empty account - showing "nothing here" would be a lie.
  if (body === null || !check(body)) throw new GatewayError(res.status, `The Gateway answered ${path} without ${missing}.`);
  return body as T;
}

/** The query string of a usage read: the account (default: the one in use) and whether gone sessions are listed. */
export function usageQuery(account: string | null, closed: boolean): string {
  const params = new URLSearchParams();
  if (account) params.set("account", account);
  if (closed) params.set("closed", "true");
  const text = params.toString();
  return text ? `?${text}` : "";
}

/** The query string of a plan read. Ids are sorted, so one question is always one string. */
export function planQuery(question: LicensePlanQuestion): string {
  const params = new URLSearchParams();
  if (question.account) params.set("account", question.account);
  if (question.until) params.set("until", question.until);
  if (question.stop && question.stop.length > 0) params.set("stop", [...question.stop].sort().join(","));
  if (question.stopGroups && question.stopGroups.length > 0) params.set("stopGroup", [...question.stopGroups].sort().join(","));
  const text = params.toString();
  return text ? `?${text}` : "";
}

/** GET /gateway/licenses/usage - which sessions use one account, how fast, and how much so far. */
export function getLicenseUsage(account: string | null, closed: boolean, signal?: AbortSignal): Promise<LicenseUsage> {
  return readChecked<LicenseUsage>(`/gateway/licenses/usage${usageQuery(account, closed)}`, "read what is using the license",
    (b) => Array.isArray(b.sessions) && Array.isArray(b.topLevel), "its 'sessions' and 'topLevel' lists", signal);
}

/** GET /gateway/licenses/plan - will the account last until a time, what to stop, and what if the given ones stopped. */
export function getLicensePlan(question: LicensePlanQuestion, signal?: AbortSignal): Promise<LicensePlan> {
  return readChecked<LicensePlan>(`/gateway/licenses/plan${planQuery(question)}`, "read the plan",
    (b) => typeof b.verdict === "string" && Array.isArray(b.sessions) && Array.isArray(b.targets),
    "its verdict, 'sessions' and 'targets'", signal);
}

/**
 * PUT /gateway/licenses/accounts/price - what an account's subscription costs a month, in dollars, or null to clear
 * it. The owner's own; a session is refused. A refusal throws GatewayError carrying the Gateway's own sentence.
 */
export async function setLicenseMonthlyPrice(accountEmail: string, monthlyPriceUsd: number | null,
  signal?: AbortSignal): Promise<LicensePriceSet> {
  const res = await gatewayFetch("/gateway/licenses/accounts/price", {
    method: "PUT",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ accountEmail, monthlyPriceUsd }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "set the plan price");
  const body = (await res.json()) as LicensePriceSet | null;
  if (typeof body?.accountEmail !== "string") {
    throw new GatewayError(res.status, "The Gateway answered the plan price without the account it set.");
  }
  return body;
}
