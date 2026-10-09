// The cron-schedule surface of the Gateway (issue #976, epic #967): the typed, same-origin client
// the React Cockpit's Schedule page reads and writes. It is the shared-library port of the Blazor
// Cockpit's GatewayClient cron methods (GetCronJobsAsync / CreateCronJobAsync / UpdateCronJobAsync /
// DeleteCronJobAsync / RunCronJobNowAsync / GetCronRunsAsync), so the desktop React shell keeps
// exactly one copy of each /cron/jobs contract.
//
// Every request is root-relative to the Gateway front door (never a Director address), the same
// Gateway-only-ingress rule the rest of client-core obeys, and carries the same Bearer via
// authHeaders(). A non-2xx throws GatewayError carrying the Gateway's own { error } message when it
// has one (e.g. a 400 for an invalid cron expression, a 409 overlap), so the Schedule form can show
// the real reason inline instead of a bare status code - no fallback that hides the problem.
import { authHeaders, GatewayError } from "../api/client";

// ===== The cron-job shape as it travels over the Gateway REST surface =====
// camelCase mirrors of the C# CronJobDto / CronJobTarget / CronJobAction / CronRunRecord
// (CcDirector.Gateway.Contracts). These are not in the generated OpenAPI schema (the cron endpoints
// return via Results.Json without a [Produces] annotation), so they are declared here as narrow
// local shapes, the same pattern api/client uses for DirectorInfo and the queue types.

/** Which machine a cron job runs on. A job targets a MACHINE, not a specific Director (#503). */
export interface CronJobTarget {
  machine: string;
}

/** What a cron job runs when it fires: a named work list (drain) or a seed skill/prompt. */
export interface CronJobAction {
  repoPath: string;
  seed: string;
  /** When set, the fire drains this named work list instead of seeding a single session. */
  workListName?: string | null;
}

/** A scheduled job: WHEN (scheduleKind + cronExpression | runAt in timeZoneId), WHICH machine
 *  (target), and WHAT to run (action). Round-trips the firing engine's lastFiredUtc / nextRunUtc /
 *  lastStatus fields unchanged. */
export interface CronJob {
  /** Empty on a create body; assigned by the store and present on every read. */
  id: string;
  name: string;
  enabled: boolean;
  /** "recurring" (uses cronExpression), "oneOff" (uses runAt) or "random" (its settings in cronExpression). */
  scheduleKind: string;
  cronExpression?: string | null;
  runAt?: string | null;
  timeZoneId: string;
  target: CronJobTarget;
  action: CronJobAction;
  preventOverlap: boolean;
  /** Run-complete notification policy (#622): "none" | "always" | "failure". */
  notifyOn: string;
  notifyWebhookUrl?: string | null;
  createdUtc?: string;
  /** UTC instant the job last fired, or null if it never has. */
  lastFiredUtc?: string | null;
  /** The next UTC instant the job is due, computed by the Gateway, or null if none. */
  nextRunUtc?: string | null;
  /** Outcome of the most recent run, or null. */
  lastStatus?: string | null;
  /** A random schedule in words, folded by the Gateway (issue #3622); null for the other kinds. Read-only. */
  scheduleText?: string | null;
  /** A random schedule's fire times still to come today, local "HH:mm" (issue #3622); null otherwise. Read-only. */
  remainingToday?: string[] | null;
  /** Whether the schedule will still run, folded by the Gateway: "active" | "paused" | "spent" | "off". Read-only. */
  lifecycle?: string | null;
}

/** One execution of a cron job. The two status fields are deliberately separate: infraStatus is
 *  "did the session START", taskStatus is "did the WORK finish". */
export interface CronRunRecord {
  scheduledUtc: string;
  firedUtc: string;
  machine: string;
  targetDirectorId: string;
  sessionId?: string | null;
  infraStatus: string;
  taskStatus: string;
}

// Pull the Gateway's own { error } message out of a non-2xx body so the caller shows the real
// reason (an invalid cron, an overlap) rather than a bare status. Falls back to the status code when
// the body is not the expected shape.
async function gatewayErrorFrom(res: Response, label: string): Promise<GatewayError> {
  let detail = `${res.status}`;
  try {
    const body = (await res.json()) as { error?: string };
    if (typeof body.error === "string" && body.error.length > 0) detail = body.error;
  } catch {
    /* non-JSON error body - keep the status code */
  }
  return new GatewayError(res.status, `${label} failed: ${detail}`);
}

// GET /cron/jobs -> { jobs: [ CronJobDto ] }. Read path: an empty list on a null/absent body.
export async function getCronJobs(signal?: AbortSignal): Promise<CronJob[]> {
  // include=random: random schedules (issue #3622) are listed only to a caller that knows the kind.
  const res = await fetch("/cron/jobs?include=random", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /cron/jobs");
  const body = (await res.json()) as { jobs?: CronJob[] };
  return body.jobs ?? [];
}

// POST /cron/jobs (body CronJobDto) -> 201 CronJobDto. Throws on failure (incl. a 400 for an invalid
// cron expression), carrying the Gateway's message so the form can show it inline.
export async function createCronJob(job: CronJob, signal?: AbortSignal): Promise<CronJob> {
  const res = await fetch("/cron/jobs", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(job),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "POST /cron/jobs");
  return (await res.json()) as CronJob;
}

// PUT /cron/jobs/{id} (body CronJobDto) -> 200 CronJobDto. Throws on failure (400 invalid / 404 gone).
export async function updateCronJob(id: string, job: CronJob, signal?: AbortSignal): Promise<CronJob> {
  const jid = encodeURIComponent(id);
  const res = await fetch(`/cron/jobs/${jid}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(job),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `PUT /cron/jobs/${id}`);
  return (await res.json()) as CronJob;
}

// DELETE /cron/jobs/{id} -> { id, deleted } | 404. Throws on failure.
export async function deleteCronJob(id: string, signal?: AbortSignal): Promise<void> {
  const jid = encodeURIComponent(id);
  const res = await fetch(`/cron/jobs/${jid}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `DELETE /cron/jobs/${id}`);
}

// POST /cron/jobs/{id}/run -> 200 CronRunRecord | 409 (overlap) | 404. Fires the job immediately;
// throws with the Gateway's message on a conflict / missing job so the page can surface it.
export async function runCronJobNow(id: string, signal?: AbortSignal): Promise<CronRunRecord> {
  const jid = encodeURIComponent(id);
  const res = await fetch(`/cron/jobs/${jid}/run`, {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `POST /cron/jobs/${id}/run`);
  return (await res.json()) as CronRunRecord;
}

// GET /cron/jobs/{id}/runs -> { jobId, runs: [ CronRunRecord ] }, newest first (server order preserved).
export async function getCronRuns(id: string, signal?: AbortSignal): Promise<CronRunRecord[]> {
  const jid = encodeURIComponent(id);
  const res = await fetch(`/cron/jobs/${jid}/runs`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `GET /cron/jobs/${id}/runs`);
  const body = (await res.json()) as { runs?: CronRunRecord[] };
  return body.runs ?? [];
}
