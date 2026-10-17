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
// No answer here carries a sign-in key. The Gateway holds none. Nor does any call here take or return the address a
// sign-in is approved at, or an approval: those pass between two Directors and are never the page's to see.
import { authHeaders, gatewayFetch, GatewayError, POLL_TIMEOUT_MS } from "../api/client";

/** The tone a Gateway label is shown in. A page maps each one to a colour and to nothing else. */
export type LicenseTone = "good" | "warning" | "bad" | "neutral";

/**
 * A machine, wherever a list names one. A machine is a machine name AND an operating system user: two users of one
 * computer are two machines, each with its own sign-ins. `label` is what to call it; a page shows the label and
 * never builds a name from the other fields. `machineId` is the machineId of its row in the overview's machines.
 */
export interface LicenseMachineRef {
  machineId: string;
  machineName: string;
  osUser: string;
  label: string;
}

/** One usage window of an account: the 5-hour window or the week. */
export interface LicenseWindow {
  /** 0 to 100, or null when it was not read. Null is "not read" and is never drawn as zero. */
  utilization: number | null;
  /** "96%", or "Not known". */
  utilizationText: string;
  /** not-known | stale | reset-since | at-limit | normal. The level alone is enough to draw the bar: under
   *  `stale` and `reset-since` a number is still given, and it says nothing about the window now. */
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

export interface LicenseSignInExpiry {
  expiresAtUtc: string | null;
  machine: LicenseMachineRef | null;
  /** expired | expires soon | fine | not known. */
  verdict: string;
  text: string;
  tone: LicenseTone;
}

/** The machine and browser profile that approve the sign-ins of one account. One per account at most. */
export interface LicenseApprover {
  accountEmail: string;
  /** The approving machine, as the owner set it, or as the machine itself spells it once it has reported. */
  machineName: string;
  osUser: string;
  /** The browser profile on that machine that is signed in to claude.ai as the account. */
  browserProfile: string;
  /** The machine's row in the overview; null while no such machine has reported to this account. */
  machineId: string | null;
  /** What to call the machine. */
  label: string;
  canApproveNow: boolean;
  /** ready | cannot-approve | not-connected | never-reported. */
  status: string;
  statusText: string;
  statusTone: LicenseTone;
  /** The whole of it in one or two sentences. A page shows this. */
  text: string;
  /** The profile's display name as its last check saw it; null until a check has named it. */
  browserProfileName: string | null;
  /** Who made the record: owner | registered. */
  source: string;
  /** Who made the record, in words: "Set by you." or "Registered by machine-a's Director.". Drawn as given. */
  sourceText: string;
  /** Why no sign-in can reach the approving machine now; null when its Director is connected. Drawn as given. */
  reachText: string | null;
  /** Its health now: can-approve | signed-out | needs-fresh-sign-in | wrong-account | check-failed | not-checked. */
  state: string;
  /** When it was last checked; null when it never was. */
  checkedAtUtc: string | null;
  /** The finished sentence of its health, naming the account, the machine and the profile. */
  healthText: string;
  /** The one thing the owner must do so it can approve; null when it can. */
  whatToDo: string | null;
}

export interface LicenseAccount {
  accountEmail: string;
  plan: string | null;
  planText: string;
  rateLimitTier: string | null;
  /** The day the SUBSCRIPTION renews (yyyy-MM-dd), which a person sets per account; null when it is not set. It
   *  is not the sign-in's expiry, which is on each holding. */
  renewsOn: string | null;
  /** "Renews 17 Oct", or "Renewal date not set". */
  renewsText: string;
  /** The sign-in for this account that expires first, across every machine holding it. Not the renewal date. */
  signInExpiry: LicenseSignInExpiry;
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
  activeOnMachines: LicenseMachineRef[];
  spareOnMachines: LicenseMachineRef[];
  canSwitchNow: boolean;
  /** What Switch now would do, or why it is not offered. Always a sentence. */
  switchNowText: string;
  /** The target Switch now takes when none is named; null when it would take none, and a call that names no
   *  target would be refused. A page then sends only a target the person picked. */
  switchNowTargetAccountEmail: string | null;
  switchTargets: LicenseSwitchTarget[];
  /** Until when the account is barred from the AUTOMATIC choice of a target, and the sentence saying so. Both are
   *  null when it is not barred. A barred account can still be chosen by hand. */
  barredUntilUtc: string | null;
  barredText: string | null;
  /** Who approves the sign-ins of this account; null when no machine is set. */
  approver: LicenseApprover | null;
  /** The sentence to show, whether or not one is set, and its tone. */
  approverText: string;
  approverTone: LicenseTone;
}

/** One sign-in a machine holds: its active one, or a spare. */
export interface LicenseHolding {
  accountEmail: string;
  isActive: boolean;
  /** "Active" or "Spare": what this sign-in is on its machine, ready to show. */
  roleText: string;
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
  /** When the last renewal of this sign-in failed, the sentence saying so, and its tone. All three are null when
   *  the last renewal worked or is not known. The expiry text above stays about the expiry only. */
  renewalFailedAtUtc: string | null;
  renewalFailedText: string | null;
  renewalFailedTone: LicenseTone | null;
  /** Renew now is offered for this sign-in. */
  canRenewNow: boolean;
  /** What Renew now does, or the sentence saying why it is not offered - the one the route refuses with. */
  renewNowText: string;
}

/** A sign-in a machine is in the middle of. */
export interface LicenseSignInInProgress {
  signInId: string;
  accountEmail: string;
  /** One sentence saying where the sign-in stands. */
  text: string;
}

export interface LicenseSwitchInProgress {
  switchId: string;
  fromAccountEmail: string;
  toAccountEmail: string;
  text: string;
}

export interface LicenseMachine extends LicenseMachineRef {
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
  /** The time the machine is held out of the automatic switch until; null when it is not held. Never compared here. */
  heldUntilUtc: string | null;
  /** The Gateway's sentence saying the machine is held, why and until when; null when it is not held. Drawn as given. */
  heldText: string | null;
  /** Add a spare is offered for this machine. */
  canAddSpare: boolean;
  /** What Add a spare does, or the sentence saying why it is not offered - the one the route refuses with. */
  addSpareText: string;
  /** The sign-ins on this machine that have not ended. */
  signInsInProgress: LicenseSignInInProgress[];
  /** The Director that sent the machine's last report: the one asked for its browser profiles. */
  directorId: string;
  /** That Director is connected, so its browser profiles can be listed now. */
  canListBrowserProfiles: boolean;
  /** What listing them does, or the sentence saying why they cannot be listed. Drawn as given. */
  browserProfilesText: string;
  /** "ready", "not-ready" or "no-room": whether it is ready for the account it would switch to next; null when it is on
   * no account. Never branched on here: the sentence beside it says what it means. */
  readiness: string | null;
  /** The account it would switch to next; null when no other account has room. */
  readinessTargetAccountEmail: string | null;
  /** The Gateway's sentence, "Ready for ..." or "Not ready: ..." with what is being done about it. Drawn as given. */
  readinessText: string | null;
  readinessTone: LicenseTone | null;
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

/** A window in the overview's own words, and then its forecast. */
export interface LicenseWindowForecast extends LicenseWindow {
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

export interface LicenseMachineForecast extends LicenseMachineRef {
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
  machines: LicenseMachineRef[];
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
  /** The machine the row is about. On a row about no machine, machineId is null and the other three are "". */
  machineId: string | null;
  machineName: string;
  osUser: string;
  label: string;
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
  /** The sentence for it, set only on a row whose switch was made; null on every other row. */
  verifiedText: string | null;
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
  /** The entryId of this machine's row in the switch log, which is where a page reads how the switch ended. */
  entryId: string;
  machineName: string;
  osUser: string;
  label: string;
  state: string;
  stateText: string;
  stateTone: LicenseTone;
  summary: string;
}

/** A machine on the account that was not sent a switch, and why. */
export interface LicenseSwitchNowSkipped {
  machineName: string;
  osUser: string;
  label: string;
  /** A finished sentence that names the machine: "the-linux-machine is not switched: its last report is too old." */
  why: string;
}

/** The accepted answer of POST /gateway/licenses/switch-now. */
export interface LicenseSwitchNowResult {
  targetAccountEmail: string | null;
  /** The whole outcome in one sentence, ready to show. */
  text: string;
  switches: LicenseSwitchNowMachine[];
  skipped: LicenseSwitchNowSkipped[];
}

/** The answer of PUT /gateway/licenses/accounts/renewal: what was stored, with its sentence already folded. */
export interface LicenseRenewalResult {
  accountEmail: string;
  renewsOn: string | null;
  renewsText: string;
}

/** GET /gateway/licenses/approvers: who approves the sign-ins of each account, in account email order. */
export interface LicenseApprovers {
  asOfUtc: string;
  /** No approver is set for any account; emptyText is the sentence to show in place of the list. */
  isEmpty: boolean;
  emptyText: string | null;
  approvers: LicenseApprover[];
}

/** The body of PUT /gateway/licenses/approvers. Setting an account that has an approver replaces it. */
export interface LicenseSetApprover {
  accountEmail: string;
  machineName: string;
  osUser: string;
  browserProfile: string;
}

/** One browser profile of a Director, as GET /directors/{id}/browsers lists it: only what the approver form reads. */
export interface LicenseBrowserProfile {
  /** The stable identifier. It is what an approver is set to, so renaming the profile does not break the approver. */
  id: string;
  /** What the owner calls it. */
  name: string;
}

/** The answer of DELETE /gateway/licenses/approvers: whether one was set, and the sentence saying which. */
export interface LicenseApproverRemoved {
  accountEmail: string;
  removed: boolean;
  text: string;
}

/**
 * The answer of the two routes that start a sign-in. It is given at once: a sign-in takes minutes. `entry` is the
 * sign-in's row of the log as it stands; the outcome arrives on that same row (same entryId) through the log route.
 */
export interface LicenseSignInStarted {
  signInId: string;
  accountEmail: string;
  machine: LicenseMachineRef;
  /** One sentence saying what was started and where its outcome appears. */
  text: string;
  entry: LicenseLogEntry;
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

/** One open license alarm (License Switching phase 6). `text` is the banner's whole line for it, as the Gateway wrote it. */
export interface LicenseAlarm {
  alarmId: string;
  /** The short stable word of the check that raised it, for example `not-ready-at-80`. */
  kind: string;
  machineName: string | null;
  accountEmail: string | null;
  text: string;
  openedAtUtc: string;
}

/**
 * The license alarms open now, with the red banner's finished words. A shell shows `summary` when it is not null, then
 * every alarm's `text` in order, then `linkText` as the link to the Licenses page - and nothing at all when `count` is 0.
 */
export interface LicenseAlarms {
  asOfUtc: string;
  count: number;
  summary: string | null;
  linkText: string | null;
  alarms: LicenseAlarm[];
}

/** GET /gateway/licenses/alarms - the license alarms open now, for the red banner on every page. */
export function getLicenseAlarms(signal?: AbortSignal): Promise<LicenseAlarms> {
  return readJson<LicenseAlarms>("/gateway/licenses/alarms", "read the license alarms", "alarms", signal);
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

// Every write is for a signed-in person only, and the Gateway refuses a body not sent as application/json before it
// reads a byte of it. One set of headers, so no write that has a body can lose the content type.
function jsonWriteHeaders(): HeadersInit {
  return { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() };
}

// Switch now answers when every machine has answered or its 60-second wait on the Gateway has ended. The page waits
// longer than that, so a slow machine is an answer and never a time limit of this client's own.
export const SWITCH_NOW_TIMEOUT_MS = 90000;

/**
 * POST /gateway/licenses/switch-now - move every machine on `accountEmail` to `targetAccountEmail`, or to the
 * account the Gateway chooses when the target is null. It is by ACCOUNT, because usage is counted per account.
 * A refusal throws GatewayError carrying the Gateway's own sentence. A page sends a null target only for an account
 * whose switchNowTargetAccountEmail is not null: where it is null the Gateway refuses a call that names none.
 */
export async function switchLicenseNow(
  accountEmail: string,
  targetAccountEmail: string | null,
  signal?: AbortSignal,
): Promise<LicenseSwitchNowResult> {
  const res = await gatewayFetch(
    "/gateway/licenses/switch-now",
    { method: "POST", headers: jsonWriteHeaders(), body: JSON.stringify({ accountEmail, targetAccountEmail }), signal },
    { timeoutMs: SWITCH_NOW_TIMEOUT_MS },
  );
  if (!res.ok) throw await GatewayError.from(res, "switch the account");
  const body = (await res.json()) as LicenseSwitchNowResult | null;
  if (!Array.isArray(body?.switches) || !Array.isArray(body?.skipped)) {
    throw new GatewayError(res.status, "The Gateway answered Switch now without its 'switches' and 'skipped' lists.");
  }
  return body;
}

/**
 * PUT /gateway/licenses/accounts/renewal - set the day the subscription of `accountEmail` is billed again
 * (`yyyy-MM-dd`), or clear it with null. `renewsOn` is always sent, because the Gateway refuses a body that leaves it
 * out. The answer is what was stored with its sentence; a refusal throws GatewayError carrying the Gateway's own.
 */
export async function setLicenseRenewalDate(
  accountEmail: string,
  renewsOn: string | null,
  signal?: AbortSignal,
): Promise<LicenseRenewalResult> {
  const res = await gatewayFetch("/gateway/licenses/accounts/renewal", {
    method: "PUT",
    headers: jsonWriteHeaders(),
    body: JSON.stringify({ accountEmail, renewsOn }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "set the renewal date");
  const body = (await res.json()) as LicenseRenewalResult | null;
  // An answer without its sentence would leave the page nothing true to show for the date it just stored.
  if (typeof body?.renewsText !== "string") {
    throw new GatewayError(res.status, "The Gateway answered the renewal date without its 'renewsText' sentence.");
  }
  return body;
}

/** GET /gateway/licenses/approvers - which machine and browser profile approve each account's sign-ins. */
export function getLicenseApprovers(signal?: AbortSignal): Promise<LicenseApprovers> {
  return readJson<LicenseApprovers>("/gateway/licenses/approvers", "read who approves sign-ins", "approvers", signal);
}

/**
 * PUT /gateway/licenses/approvers - set the machine and browser profile that approve the sign-ins of one account.
 * The answer is the approver as stored, with its status and sentence already folded; a refusal throws GatewayError
 * carrying the Gateway's own sentence.
 */
export async function setLicenseApprover(approver: LicenseSetApprover, signal?: AbortSignal): Promise<LicenseApprover> {
  const { accountEmail, machineName, osUser, browserProfile } = approver;
  const res = await gatewayFetch("/gateway/licenses/approvers", {
    method: "PUT",
    headers: jsonWriteHeaders(),
    body: JSON.stringify({ accountEmail, machineName, osUser, browserProfile }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "set who approves the sign-ins");
  const body = (await res.json()) as LicenseApprover | null;
  // An answer without its sentence would leave the page nothing true to show for what it just stored.
  if (typeof body?.text !== "string") {
    throw new GatewayError(res.status, "The Gateway answered the approver without its 'text' sentence.");
  }
  return body;
}

/**
 * GET /directors/{directorId}/browsers - the browser profiles of one Director, for choosing an approver's profile.
 * `directorId` is a machine's directorId in the overview. A Director that cannot be reached throws GatewayError
 * carrying the Gateway's own sentence.
 */
export async function listLicenseBrowserProfiles(directorId: string, signal?: AbortSignal): Promise<LicenseBrowserProfile[]> {
  const body = await readJson<{ browsers: LicenseBrowserProfile[] }>(
    `/directors/${encodeURIComponent(directorId)}/browsers`,
    "list the browser profiles",
    "browsers",
    signal,
  );
  return body.browsers.map(({ id, name }) => ({ id, name }));
}

/**
 * DELETE /gateway/licenses/approvers?accountEmail=... - remove the approver of one account. It has no body: the
 * account is named in the query. Answered whether or not one was set; the answer's sentence says which.
 */
export async function removeLicenseApprover(accountEmail: string, signal?: AbortSignal): Promise<LicenseApproverRemoved> {
  const res = await gatewayFetch(`/gateway/licenses/approvers?accountEmail=${encodeURIComponent(accountEmail)}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "remove who approves the sign-ins");
  const body = (await res.json()) as LicenseApproverRemoved | null;
  if (typeof body?.text !== "string") {
    throw new GatewayError(res.status, "The Gateway answered the removal of an approver without its 'text' sentence.");
  }
  return body;
}

async function startLicenseSignIn(
  path: string,
  what: string,
  accountEmail: string,
  machineId: string,
  signal?: AbortSignal,
): Promise<LicenseSignInStarted> {
  const res = await gatewayFetch(path, {
    method: "POST",
    headers: jsonWriteHeaders(),
    body: JSON.stringify({ accountEmail, machineId }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, what);
  const body = (await res.json()) as LicenseSignInStarted | null;
  // The row is where the outcome is read from; an answer without it could never be followed to its end.
  if (typeof body?.text !== "string" || typeof body?.entry?.entryId !== "string") {
    throw new GatewayError(res.status, `The Gateway answered ${path} without its 'text' sentence and its 'entry' row.`);
  }
  return body;
}

/**
 * POST /gateway/licenses/sign-ins/add-spare - sign the machine `machineId` (its machineId in the overview) in to an
 * account it does not hold, to be kept there as a spare. Answered at once; the outcome arrives on the answer's row of
 * the log. A refusal throws GatewayError carrying the Gateway's own sentence.
 */
export function addLicenseSpare(accountEmail: string, machineId: string, signal?: AbortSignal): Promise<LicenseSignInStarted> {
  return startLicenseSignIn("/gateway/licenses/sign-ins/add-spare", "add a spare", accountEmail, machineId, signal);
}

/**
 * POST /gateway/licenses/sign-ins/renew - sign the machine `machineId` in again to an account it holds, replacing the
 * sign-in it has. Answered at once; the outcome arrives on the answer's row of the log.
 */
export function renewLicenseSignIn(accountEmail: string, machineId: string, signal?: AbortSignal): Promise<LicenseSignInStarted> {
  return startLicenseSignIn("/gateway/licenses/sign-ins/renew", "renew the sign-in", accountEmail, machineId, signal);
}
