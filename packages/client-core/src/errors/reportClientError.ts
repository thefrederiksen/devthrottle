// The browser side of the client error channel. Every error a shell shows the user - and every uncaught
// browser error - is ALSO reported to the Gateway (POST /client-errors), which files it in the durable
// error store under the caller's account: the same store, the same scrubbing and the same retention as a
// Director's errors (the Error Logging mission, issue #3675). Before that a browser error existed only on
// the user's screen, then only in a Gateway memory list that a deploy erased.
//
// WHAT A REPORT CARRIES - and what it never does (the owner's ruling, 9 October 2026): the component
// (cockpit or mobile, set once at start-up), the surface, the action the user was taking, whether the user
// saw it, the error's name, its message as shown, and from a Gateway answer its HTTP status, error code and
// correlation id, plus the session it concerned. NO free-text detail, NO stack, NO page address: a browser
// error can carry anything on the page, a prompt included, and the prompt's words must never reach a
// report. The Gateway scrubs every free-text field again whatever this side sent.
//
// NOTHING IS SILENTLY DROPPED. Reporting still never throws into the UI, but a report that cannot be sent
// (no network, or the Gateway answering 5xx or 429) waits in a small queue kept across a page reload and
// goes out when the connection returns. A report lost because that queue is full is COUNTED, and the count
// goes out as a report of its own. A report the Gateway refuses outright (4xx) cannot be fixed by sending it
// again, so it is written to the console with the Gateway's reason.
//
// A raw fetch with keepalive (NOT gatewayFetch): a report during a bad connection must not feed the
// connection-health store or its retry machinery.

import { authHeaders, gatewayErrorMessage, GatewayError } from "../api/client";

/** The two browser shells. The Gateway refuses any other component on this route. */
export type ClientComponent = "cockpit" | "mobile";

/** One report as it goes over the wire: the body of POST /client-errors. Every field but the first four is
 *  optional, and nothing else is ever sent. */
export interface ClientErrorReport {
  component: ClientComponent;
  surface: string;
  action: string;
  message: string;
  user_visible: boolean;
  exception_type?: string;
  http_status?: number;
  error_code?: string;
  session_id?: string;
  correlation_id?: string;
}

/** What a caller knows about the failure beyond the error itself. */
export interface ReportContext {
  /** The DevThrottle session the action concerned. */
  sessionId?: string;
  /**
   * A background poll, not something a person pressed (the step 3 ruling, issue #3675). The shown sentence is
   * formatted WITHOUT the action, so a poll that cannot reach the Gateway keeps the shared "Can't reach the
   * Gateway - retrying." line; the report still records the action. And it is reported only when the sentence
   * it shows CHANGES for that surface and action, so a dead Gateway does not queue a report every few seconds.
   * Call {@link backgroundRecovered} when the poll succeeds, so the next failure is reported again.
   */
  background?: true;
}

/** Client-side cap: at most this many reports leave the browser per minute; the rest wait in the queue. The
 *  server enforces its own cap too - this one exists so a render-loop error does not even leave the browser. */
const MAX_REPORTS_PER_MINUTE = 20;
/** How many unsent reports the queue keeps. Past it a report is counted as lost, never kept unbounded. */
export const MAX_QUEUED_REPORTS = 50;
/** The longest error name sent; the Gateway caps and scrubs it again. */
const MAX_EXCEPTION_TYPE = 120;

const QUEUE_KEY = "devthrottle.clientErrors.queue.v1";
const LOST_KEY = "devthrottle.clientErrors.lost.v1";

let component: ClientComponent | null = null;
let queue: ClientErrorReport[] | null = null;
let lost = 0;
let flushing = false;

let windowStartMs = 0;
let reportsInWindow = 0;

/** The sentence each background surface and action last showed and reported, keyed "surface|action". */
const backgroundShown = new Map<string, string>();

/** Decide whether one more report may leave the browser this minute. Exported for unit tests. */
export function admitReport(nowMs: number): boolean {
  if (nowMs - windowStartMs >= 60_000) {
    windowStartMs = nowMs;
    reportsInWindow = 0;
  }
  if (reportsInWindow < MAX_REPORTS_PER_MINUTE) {
    reportsInWindow++;
    return true;
  }
  return false;
}

/** Reset every piece of module state (unit tests only). */
export function resetReportingForTests(): void {
  windowStartMs = 0;
  reportsInWindow = 0;
  component = null;
  queue = null;
  lost = 0;
  flushing = false;
  globalInstalled = false;
  backgroundShown.clear();
}

/** Name the shell once, at start-up. Every report carries it; the Gateway files it under that component. */
export function setReportingComponent(value: ClientComponent): void {
  component = value;
}

/** The reports waiting to be sent, oldest first (a copy). */
export function queuedReports(): ClientErrorReport[] {
  return [...loadQueue()];
}

/** How many reports have been lost to a full queue and not yet reported as lost. */
export function lostReportCount(): number {
  loadQueue();
  return lost;
}

/**
 * Report one error. Never throws; never blocks. The report is sent now when it may be, otherwise queued.
 * `surface` is where the user was ("mobile-session-controls"), `action` what they were doing, in their words
 * ("send prompt"), and `message` the error's text as it was shown. Never pass the user's own words - a
 * prompt, a dictation - in any of them.
 */
export function reportClientError(report: Omit<ClientErrorReport, "component">): void {
  try {
    if (component === null) {
      // A shell that reports before naming itself is a wiring bug. The Gateway would refuse the report, so
      // it is not queued - it is said, loudly, where the developer will look.
      console.error(`[client-errors] a report was made before setReportingComponent; it cannot be sent: ${report.action}`);
      return;
    }
    const full: ClientErrorReport = { component, ...report };
    // Online first: a report queued while offline must not use up the minute's allowance its delivery needs.
    if (!isOnline() || !admitReport(Date.now())) {
      enqueue(full);
      return;
    }
    void deliver(full).then((outcome) => {
      if (outcome === "sent") void flushQueue();
    });
  } catch (err) {
    console.error(`[client-errors] could not report an error: ${String(err)}`);
  }
}

/** The facts a report takes from the error itself: its name, and from a Gateway answer its status, code and
 *  correlation id. Never its stack, never a free-text detail. */
export function errorFacts(err: unknown): Pick<ClientErrorReport, "exception_type" | "http_status" | "error_code" | "correlation_id"> {
  const facts: Pick<ClientErrorReport, "exception_type" | "http_status" | "error_code" | "correlation_id"> = {};
  if (err instanceof Error && err.name) facts.exception_type = err.name.slice(0, MAX_EXCEPTION_TYPE);
  if (err instanceof GatewayError) {
    // A status of 0 or a made-up one (our own time limit) is not an HTTP answer the Gateway gave.
    if (err.status >= 100 && err.status <= 599) facts.http_status = err.status;
    if (err.code) facts.error_code = err.code;
    if (err.correlationId) facts.correlation_id = err.correlationId;
  }
  return facts;
}

/**
 * Render AND report, in one act (issue #2189, widened by the Error Logging mission, issue #3675).
 *
 * This is the ONE way a shell turns a caught error into on-screen text. It returns the sentence to display
 * and reports the same failure to the Gateway, marked as seen by the user, so an error the user is looking at
 * can no longer exist only on the user's screen. A call site that formats a message without reporting it is
 * the bug this function exists to prevent, so the two cannot be separated.
 *
 * `action` names what the user was trying to do, in their terms: "attach the image", "send prompt". It
 * shapes the sentence AND labels the stored record.
 */
export function describeAndReport(surface: string, action: string, err: unknown, context?: ReportContext): string {
  const message = gatewayErrorMessage(err, context?.background ? undefined : action);
  if (context?.background) {
    const key = backgroundKey(surface, action);
    if (backgroundShown.get(key) === message) return message;
    backgroundShown.set(key, message);
  }
  reportClientError({
    surface,
    action,
    message,
    user_visible: true,
    ...errorFacts(err),
    ...(context?.sessionId ? { session_id: context.sessionId } : {}),
  });
  return message;
}

/** A background poll for this surface and action succeeded: its next failure is reported even if it reads the same. */
export function backgroundRecovered(surface: string, action: string): void {
  backgroundShown.delete(backgroundKey(surface, action));
}

function backgroundKey(surface: string, action: string): string {
  return `${surface}|${action}`;
}

/**
 * Show AND report a failure the client found itself, with no error object to describe: "This browser cannot
 * store recordings", "the phone suspended the microphone". The sibling of describeAndReport for those sites -
 * describeAndReport would rewrite a sentence that is already right into "something unexpected went wrong".
 * Returns `message` unchanged, so the call can sit where the text is shown. Never pass the user's own words.
 * `background` works as it does for describeAndReport: reported only when the sentence changes for that surface
 * and action, until {@link backgroundRecovered}.
 */
export function reportShownError(surface: string, action: string, message: string, context?: ReportContext, cause?: unknown): string {
  if (context?.background) {
    const key = backgroundKey(surface, action);
    if (backgroundShown.get(key) === message) return message;
    backgroundShown.set(key, message);
  }
  reportClientError({
    surface,
    action,
    message,
    user_visible: true,
    ...(cause === undefined ? {} : errorFacts(cause)),
    ...(context?.sessionId ? { session_id: context.sessionId } : {}),
  });
  return message;
}

/**
 * Send what is waiting: the lost count first (as a report of its own), then the queue, oldest first, while
 * the per-minute cap allows. Stops at the first report that cannot be sent - the connection is not back.
 */
export async function flushQueue(): Promise<void> {
  if (flushing || component === null) return;
  flushing = true;
  try {
    const waiting = loadQueue();
    if (lost > 0 && admitReport(Date.now())) {
      const count = lost;
      const outcome = await deliver({
        component,
        surface: `${component}-client-errors`,
        action: "report errors",
        message: `${count} error reports were lost before they could be sent`,
        user_visible: false,
      }, { queueOnFailure: false });
      if (outcome === "retry") return;
      // Sent, or refused for good - the same rule as every queued row, so a refused count cannot pin the queue
      // behind it (review finding 4). A refusal is already on the console; the count goes with it.
      if (outcome === "refused") console.error(`[client-errors] the count of ${count} lost error reports was refused and is dropped`);
      lost -= count;
      saveQueue();
    }
    while (waiting.length > 0 && isOnline() && admitReport(Date.now())) {
      const next = waiting[0];
      const outcome = await deliver(next, { queueOnFailure: false });
      if (outcome === "retry") return;
      // Sent, or refused for good (already written to the console): either way it leaves the queue.
      waiting.shift();
      saveQueue();
    }
  } finally {
    flushing = false;
  }
}

type Outcome = "sent" | "retry" | "refused";

/** POST one report. A failure the connection can fix is queued (unless the caller owns the queue); a refusal
 *  is written to the console with the Gateway's reason. */
async function deliver(report: ClientErrorReport, opts: { queueOnFailure: boolean } = { queueOnFailure: true }): Promise<Outcome> {
  let res: Response;
  try {
    res = await fetch("/client-errors", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...authHeaders() },
      body: JSON.stringify(report),
      keepalive: true,
    });
  } catch (err) {
    if (opts.queueOnFailure) enqueue(report);
    console.warn(`[client-errors] the Gateway could not be reached; the report waits for the connection: ${String(err)}`);
    return "retry";
  }
  if (res.ok) return "sent";
  if (res.status === 429 || res.status >= 500) {
    if (opts.queueOnFailure) enqueue(report);
    console.warn(`[client-errors] the Gateway answered ${res.status}; the report waits and is sent again later`);
    return "retry";
  }
  const reason = await res.text().catch((err) => `(the reason could not be read: ${String(err)})`);
  console.error(`[client-errors] the Gateway refused an error report (${res.status}): ${reason}`);
  return "refused";
}

function enqueue(report: ClientErrorReport): void {
  const waiting = loadQueue();
  if (waiting.length >= MAX_QUEUED_REPORTS) {
    lost++;
    console.error(`[client-errors] the report queue is full (${MAX_QUEUED_REPORTS}); a report was lost and counted (${lost} lost so far)`);
  } else {
    waiting.push(report);
  }
  saveQueue();
}

function loadQueue(): ClientErrorReport[] {
  if (queue !== null) return queue;
  queue = [];
  const storage = localStore();
  if (!storage) return queue;
  try {
    const raw = storage.getItem(QUEUE_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    if (Array.isArray(parsed)) queue = (parsed as ClientErrorReport[]).slice(0, MAX_QUEUED_REPORTS);
    const lostRaw = Number(storage.getItem(LOST_KEY) ?? "0");
    lost = Number.isFinite(lostRaw) && lostRaw > 0 ? Math.floor(lostRaw) : 0;
  } catch (err) {
    console.error(`[client-errors] the saved report queue could not be read; starting an empty one: ${String(err)}`);
  }
  return queue;
}

function saveQueue(): void {
  const storage = localStore();
  if (!storage || queue === null) return;
  try {
    if (queue.length === 0) storage.removeItem(QUEUE_KEY);
    else storage.setItem(QUEUE_KEY, JSON.stringify(queue));
    if (lost === 0) storage.removeItem(LOST_KEY);
    else storage.setItem(LOST_KEY, String(lost));
  } catch (err) {
    // The queue still holds the reports in memory; only surviving a reload is lost, and that is said.
    console.error(`[client-errors] the report queue could not be saved on this device (it is kept until the page closes): ${String(err)}`);
  }
}

function localStore(): Storage | null {
  try {
    return typeof localStorage === "undefined" ? null : localStorage;
  } catch {
    // A browser that forbids storage access throws on the read itself; the queue then lives in memory only.
    return null;
  }
}

function isOnline(): boolean {
  return typeof navigator === "undefined" || navigator.onLine !== false;
}

let globalInstalled = false;

/**
 * Name the shell, and install the app-wide catchers for errors NOBODY handled: window "error" (uncaught throw)
 * and "unhandledrejection" (un-awaited promise failure). Call once from the shell's entry point. It also sends
 * whatever an earlier page load left queued, and sends the queue again whenever the connection returns.
 */
export function installGlobalErrorReporting(shell: ClientComponent): void {
  setReportingComponent(shell);
  if (globalInstalled || typeof window === "undefined") return;
  globalInstalled = true;
  window.addEventListener("error", (event) => {
    reportClientError({
      surface: `${shell}-global`,
      action: "unhandled error",
      message: event.message || "Uncaught error",
      user_visible: false,
      ...errorFacts(event.error),
    });
  });
  window.addEventListener("unhandledrejection", (event) => {
    const reason: unknown = event.reason;
    reportClientError({
      surface: `${shell}-global`,
      action: "unhandled error",
      message: reason instanceof Error ? reason.message : String(reason ?? "Unhandled promise rejection"),
      user_visible: false,
      ...errorFacts(reason),
    });
  });
  window.addEventListener("online", () => void flushQueue());
  void flushQueue();
}
