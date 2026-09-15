// The machines surface of the Gateway (fleet maintenance, devthrottle_internal#2026, epic #2027): every machine
// this account owns, its launcher, the Directors on it, versions against the newest release, and the update,
// restart and start actions. Every label, tone and offered action is folded on the Gateway (FleetMachinesFold,
// DirectorUpdateViewFold); a client renders them as sent and never decides what a state means.
import { authHeaders, gatewayFetch, GatewayError, POLL_TIMEOUT_MS } from "../api/client";

/** The tone a Gateway label is shown in. A client maps each one to a colour and to nothing else. */
export type FleetTone = "ok" | "warn" | "bad" | "idle";

/** One thing that can be done to a machine: whether it can be done now, and why or why not. */
export interface FleetAction {
  offered: boolean;
  label: string;
  /** What pressing it does when offered, or why it is not offered. Null when there is nothing to say. */
  reason?: string | null;
}

export interface FleetVersionState {
  /** "current", "behind", "newer than release", "unreadable version", or empty when nothing to compare. */
  label: string;
  tone: FleetTone;
  behind: boolean;
}

export interface FleetMachineDirector {
  directorId: string;
  name: string;
  version?: string | null;
  sessions: number;
  stateLabel: string;
  stateTone: FleetTone;
  versionState: FleetVersionState;
}

export interface FleetMachine {
  machine: string;
  launcherVersion?: string | null;
  launcherLastSeenUtc?: string | null;
  /** NoLauncher | NotConnected | NotStreamCapable | Connected. */
  reach: string;
  reachLabel: string;
  reachTone: FleetTone;
  reachDetail?: string | null;
  launcherVersionState: FleetVersionState;
  /** True when the launcher can be asked for its update status (GET .../director/update-status). */
  canReportUpdateStatus: boolean;
  update: FleetAction;
  restart: FleetAction;
  start: FleetAction;
  directors: FleetMachineDirector[];
}

export interface NewestRelease {
  version?: string | null;
  /** The version, or "checking" / "unknown". */
  label: string;
  detail?: string | null;
  checkedAtUtc?: string | null;
}

export interface FleetHighlight {
  text: string;
  tone: FleetTone;
}

export interface FleetMachines {
  newestRelease: NewestRelease;
  highlights: FleetHighlight[];
  machines: FleetMachine[];
}

/** The Gateway's words for a Director update on one machine. */
export interface DirectorUpdateView {
  machine: string;
  headline: string;
  tone: FleetTone;
  inProgress: boolean;
  /** Whether a newer build is downloaded and waiting, in words. */
  downloaded: string;
  lastResultAt?: string | null;
}

const machinePath = (machine: string) => `/machines/${encodeURIComponent(machine)}`;

/** GET /machines - the whole machines view. Throws GatewayError with the Gateway's own sentence on failure. */
export async function getFleetMachines(signal?: AbortSignal): Promise<FleetMachines> {
  const res = await gatewayFetch("/machines", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  }, { timeoutMs: POLL_TIMEOUT_MS });
  if (!res.ok) throw await GatewayError.from(res, "load the machines");
  const body = (await res.json()) as FleetMachines;
  // A successful body without the list is not an empty fleet - showing "no machines" would be a lie.
  if (!Array.isArray(body?.machines)) {
    throw new GatewayError(res.status, "The Gateway answered the machine list without a 'machines' array.");
  }
  return body;
}

/** POST /machines/{machine}/director/update - install a downloaded Director update now, if it is empty. */
export async function updateDirectorNow(machine: string, signal?: AbortSignal): Promise<DirectorUpdateView> {
  const res = await gatewayFetch(`${machinePath(machine)}/director/update`, {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "update the Director");
  return (await res.json()) as DirectorUpdateView;
}

/** GET /machines/{machine}/director/update-status - a downloaded build waiting, an update running, the last result. */
export async function getDirectorUpdateStatus(machine: string, signal?: AbortSignal): Promise<DirectorUpdateView> {
  const res = await gatewayFetch(`${machinePath(machine)}/director/update-status`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  }, { timeoutMs: POLL_TIMEOUT_MS });
  if (!res.ok) throw await GatewayError.from(res, "read the update status");
  return (await res.json()) as DirectorUpdateView;
}

/** POST /machines/{machine}/director/restart with onlyIfEmpty - the launcher refuses while sessions are live. */
export async function restartDirectorIfEmpty(machine: string, signal?: AbortSignal): Promise<void> {
  const res = await gatewayFetch(`${machinePath(machine)}/director/restart`, {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ onlyIfEmpty: true }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "restart the Director");
}

/** POST /machines/{machine}/director/start - start the installed Director. */
export async function startDirector(machine: string, signal?: AbortSignal): Promise<void> {
  const res = await gatewayFetch(`${machinePath(machine)}/director/start`, {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "start the Director");
}
