// The Licenses surface of the Gateway (the License Switching mission): every Claude account this person's
// machines hold a sign-in for, which machine is on which, the forecast of the next switch, and the switch log.
// The shapes are the Gateway's own (src/CcDirector.Gateway.Contracts/LicenseCockpitDtos.cs), described with a
// real example of each in missions/license-switching/cockpit-api.md.
//
// THE CLIENT IS DUMB (CLAUDE.md rule 7). Every judgement in these answers is folded on the Gateway
// (LicenseDisplayFold): a status word with its label and tone, a window's level, a sign-in's expiry verdict,
// whether Switch now is offered and the sentence saying what it would do. A page shows them as sent. It never
// compares a percentage with the threshold and never compares a time with now.
//
// No answer here carries a sign-in key. The Gateway holds none.
import { authHeaders, gatewayFetch, GatewayError, POLL_TIMEOUT_MS } from "../api/client";

/** The tone a Gateway label is shown in. A page maps each one to a colour and to nothing else. */
export type LicenseTone = "good" | "warning" | "bad" | "neutral";

/** One usage window of an account: the 5-hour window or the week. */
export interface LicenseWindow {
  /** 0 to 100, or null when it was not read. Null is "not read" and is never drawn as zero. */
  utilization: number | null;
  /** "96%", or "Not known". */
  utilizationText: string;
  /** normal | at-limit | reset-since | not-known. */
  level: string;
  resetsAtUtc: string | null;
  /** "Resets in 1 hour 48 minutes", true at the answer's asOfUtc. */
  resetsText: string | null;
}

export interface LicenseExtraUsage {
  /** on | off | not-known. */
  state: string;
  text: string;
  utilization: number | null;
}

/** Another account as the target of Switch now. */
export interface LicenseSwitchTarget {
  accountEmail: string;
  canChoose: boolean;
  /** The sentence saying why it cannot be chosen; null when it can. */
  unavailableText: string | null;
  /** The target the Gateway takes when none is named. */
  isAutomaticChoice: boolean;
}

export interface LicenseAccount {
  accountEmail: string;
  plan: string | null;
  planText: string;
  rateLimitTier: string | null;
  /** The day the SUBSCRIPTION renews, which a person sets per account; null when it is not set. It is not the
   *  sign-in's expiry, which is on each holding. Absent from a Gateway older than the field. */
  renewsOn?: string | null;
  /** "Renews 17 Oct", or the sentence saying it is not set. */
  renewsText?: string | null;
  status: string;
  statusText: string;
  statusTone: LicenseTone;
  statusDetail: string;
  isInUse: boolean;
  fiveHour: LicenseWindow;
  sevenDay: LicenseWindow;
  /** The numbers are too old to trust, or there are none. Shown muted. */
  isStale: boolean;
  readAtUtc: string | null;
  readAgeText: string;
  readError: string | null;
  readErrorText: string | null;
  extraUsage: LicenseExtraUsage;
  activeOnMachines: string[];
  spareOnMachines: string[];
  canSwitchNow: boolean;
  /** What Switch now would do, or why it is not offered. Always a sentence. */
  switchNowText: string;
  switchNowTargetAccountEmail: string | null;
  switchTargets: LicenseSwitchTarget[];
}

/** One sign-in a machine holds: its active one, or a spare. */
export interface LicenseHolding {
  accountEmail: string;
  isActive: boolean;
  signedInAtUtc: string | null;
  signedInText: string;
  signInExpiresAtUtc: string | null;
  /** expired | expires soon | fine | not known. */
  expiryVerdict: string;
  expiryText: string;
  expiryTone: LicenseTone;
  isReadingStale: boolean;
  readError: string | null;
  readErrorText: string | null;
  /** The sentence when the last renewal of this sign-in failed; null when it worked. Absent from a Gateway
   *  older than the field. */
  renewalFailedText?: string | null;
}

export interface LicenseSwitchInProgress {
  switchId: string;
  fromAccountEmail: string;
  toAccountEmail: string;
  text: string;
}

export interface LicenseMachine {
  machineId: string;
  machineName: string;
  osUser: string;
  activeAccountEmail: string | null;
  status: string;
  statusText: string;
  statusTone: LicenseTone;
  isStale: boolean;
  lastReportAtUtc: string | null;
  lastReportText: string;
  active: LicenseHolding | null;
  spares: LicenseHolding[];
  switchInProgress: LicenseSwitchInProgress | null;
}

/** GET /gateway/licenses. */
export interface LicenseOverview {
  asOfUtc: string;
  /** No machine of this account has ever reported; emptyText is the sentence to show in place of the lists. */
  isEmpty: boolean;
  emptyText: string | null;
  accounts: LicenseAccount[];
  machines: LicenseMachine[];
}

export interface LicenseWindowForecast {
  utilization: number | null;
  resetsAtUtc: string | null;
  percentPerHour: number | null;
  rateText: string;
  expectedAtLimitUtc: string | null;
  expectedText: string;
}

export interface LicenseAccountForecast {
  accountEmail: string;
  isInUse: boolean;
  isStale: boolean;
  fiveHour: LicenseWindowForecast;
  sevenDay: LicenseWindowForecast;
  expectedAtLimitUtc: string | null;
  expectedText: string;
  nextSwitchTargetAccountEmail: string | null;
  nextSwitchText: string;
}

export interface LicenseMachineForecast {
  machineName: string;
  osUser: string;
  activeAccountEmail: string | null;
  nextAccountEmail: string | null;
  expectedSwitchAtUtc: string | null;
  /** The whole forecast for this machine in one sentence. */
  text: string;
}

export interface LicenseNextSwitch {
  fromAccountEmail: string;
  expectedAtUtc: string;
  window: string;
  windowText: string;
  targetAccountEmail: string | null;
  machines: string[];
}

/** GET /gateway/licenses/forecast. */
export interface LicenseForecast {
  asOfUtc: string;
  isEmpty: boolean;
  emptyText: string | null;
  nextSwitch: LicenseNextSwitch | null;
  nextSwitchText: string;
  accounts: LicenseAccountForecast[];
  machines: LicenseMachineForecast[];
}

/** One row of the switch log. A kind or a state this build does not know is still a row: show its words. */
export interface LicenseLogEntry {
  entryId: string;
  kind: string;
  kindText: string;
  atUtc: string;
  machineName: string;
  osUser: string;
  fromAccountEmail: string;
  toAccountEmail: string;
  why: string | null;
  whyText: string;
  state: string;
  stateText: string;
  stateTone: LicenseTone;
  isOpen: boolean;
  reasonCode: string | null;
  reasonText: string | null;
  message: string | null;
  /** The row in one sentence. Always set. */
  summary: string;
  /** How many times the command was sent, and when last. */
  sendCount: number;
  lastSentAtUtc: string | null;
  startedOnMachineAtUtc: string | null;
  finishedOnMachineAtUtc: string | null;
  exchangeMilliseconds: number | null;
  finishedAtUtc: string | null;
  /** "Took 2.0 seconds on the machine (the exchange itself 41 milliseconds)"; null when there is nothing to say. */
  timingText: string | null;
  /** After the switch the new sign-in answered a usage read; null when the machine gave no answer. */
  verifiedByUsageRead: boolean | null;
}

/** GET /gateway/licenses/log: one page, newest first. */
export interface LicenseLogPage {
  asOfUtc: string;
  isEmpty: boolean;
  emptyText: string | null;
  entries: LicenseLogEntry[];
  hasMore: boolean;
  /** Passed back exactly as given to read the next (older) page. Never read by the page. */
  nextCursor: string | null;
}

/** One machine that was sent a switch by Switch now, as its log row stood when the Gateway answered. */
export interface LicenseSwitchNowMachine {
  switchId: string;
  machineName: string;
  osUser: string;
  state: string;
  stateText: string;
  stateTone: LicenseTone;
  summary: string;
}

/** A machine on the account that was not sent a switch, and why. */
export interface LicenseSwitchNowSkipped {
  machineName: string;
  why: string;
}

/** The accepted answer of POST /gateway/licenses/switch-now. */
export interface LicenseSwitchNowResult {
  targetAccountEmail: string | null;
  switches: LicenseSwitchNowMachine[];
  skipped: LicenseSwitchNowSkipped[];
}

const JSON_GET: RequestInit = { method: "GET" };

async function readJson<T>(path: string, what: string, list: string, signal?: AbortSignal): Promise<T> {
  const res = await gatewayFetch(
    path,
    { ...JSON_GET, headers: { Accept: "application/json", ...authHeaders() }, signal },
    { timeoutMs: POLL_TIMEOUT_MS },
  );
  if (!res.ok) throw await GatewayError.from(res, what);
  const body = (await res.json()) as Record<string, unknown> | null;
  // A successful body without its list is not an empty account - showing "nothing here" would be a lie.
  if (!Array.isArray(body?.[list])) {
    throw new GatewayError(res.status, `The Gateway answered ${path} without a '${list}' list.`);
  }
  return body as T;
}

/** GET /gateway/licenses - the accounts and the machines. */
export function getLicenseOverview(signal?: AbortSignal): Promise<LicenseOverview> {
  return readJson<LicenseOverview>("/gateway/licenses", "read the licenses", "accounts", signal);
}

/** GET /gateway/licenses/forecast - when each account is expected to reach its limit, and the next switch. */
export function getLicenseForecast(signal?: AbortSignal): Promise<LicenseForecast> {
  return readJson<LicenseForecast>("/gateway/licenses/forecast", "read the license forecast", "machines", signal);
}

/**
 * GET /gateway/licenses/log - one page of the switch log, newest first. With no cursor it is the newest page;
 * with the nextCursor of a page it is the page of older rows after it.
 */
export function getLicenseLog(cursor?: string | null, signal?: AbortSignal): Promise<LicenseLogPage> {
  const query = cursor ? `?cursor=${encodeURIComponent(cursor)}` : "";
  return readJson<LicenseLogPage>(`/gateway/licenses/log${query}`, "read the switch log", "entries", signal);
}

/**
 * POST /gateway/licenses/switch-now - move every machine on `accountEmail` to `targetAccountEmail`, or to the
 * account the Gateway chooses when the target is null. It is by ACCOUNT, because usage is counted per account.
 * A refusal throws GatewayError carrying the Gateway's own sentence.
 */
export async function switchLicenseNow(
  accountEmail: string,
  targetAccountEmail: string | null,
  signal?: AbortSignal,
): Promise<LicenseSwitchNowResult> {
  const res = await gatewayFetch("/gateway/licenses/switch-now", {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ accountEmail, targetAccountEmail }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "switch the account");
  const body = (await res.json()) as LicenseSwitchNowResult | null;
  if (!Array.isArray(body?.switches) || !Array.isArray(body?.skipped)) {
    throw new GatewayError(res.status, "The Gateway answered Switch now without its 'switches' and 'skipped' lists.");
  }
  return body;
}
