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
  /** NoLauncher | NotConnected | NotStreamCapable | Reconnecting | Connected. */
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
  /** True when the launcher and every running Director are on the newest release - what an update is for. */
  upToDate: boolean;
  directors: FleetMachineDirector[];
  /** Processor, memory and spare capacity (Mission: Resource Monitor, section 5j), folded by the Gateway's
   *  ResourceCapacityFold. ALWAYS present: the Cockpit ships in the Gateway's own image, so there is no older Gateway
   *  to allow for, and an offline machine is sent with dashes rather than left out. */
  capacity: FleetMachineCapacity;
}

/**
 * One machine's processor, memory and spare capacity (C# FleetMachineCapacityDto). Every label and tone is folded on
 * the Gateway and drawn as sent. AN OFFLINE MACHINE ARRIVES WITH DASHES, NEVER ZEROS: the dash is the Gateway's
 * {@link CAPACITY_DASH}, not one a client invents.
 */
export interface FleetMachineCapacity {
  reporting: boolean;
  /** For example "28%", "cannot read", or the dash. */
  processorLabel: string;
  /** For example "peak 71%" or "peak not known". Null when there is nothing to say. */
  processorDetail?: string | null;
  processorTone: FleetTone;
  memoryLabel: string;
  /** For example "8.9 / 16 GB". */
  memoryDetail?: string | null;
  memoryTone: FleetTone;
  /** Free memory at the last hour's PEAK, for example "7.1 GB" - not free memory now. */
  spareLabel: string;
  /** The same figure in bytes, for sorting only. Null when not known, which sorts last. */
  spareBytes?: number | null;
  /** "CPU hot", "Memory critical", "Figures refused"... Null when the machine is not over a limit, and always null when
   *  it is not reporting: reachability is the reach line's verdict, and {@link why} carries the capacity detail. */
  statusLabel?: string | null;
  statusTone: FleetTone;
  /** Why the figures are dashes or "cannot read", in a sentence. Null for a normal reading. */
  why?: string | null;
  readingWindowStartUtc?: string | null;
}

/** The Gateway's dash for a figure that is not known (C# FleetMachineCapacityDto.Dash). Kept here only so tests and
 *  fixtures can name it; the view never substitutes it - it draws whatever label the Gateway sent. */
export const CAPACITY_DASH = "\u2014";

/** One amber or red crossing as the chart marks it (C# ResourceCrossingMarkDto). */
export interface ResourceCrossingMark {
  startedUtc: string;
  /** "CPU" or "Memory". */
  resource: string;
  /** For example "CPU red for 90 seconds, peak 99%". */
  label: string;
  tone: FleetTone;
}

/** One five-minute row (C# MachineResourceWindowDto). A null figure was not read; it is never zero. */
export interface MachineResourceWindow {
  windowStartUtc: string;
  processorAveragePercent?: number | null;
  processorPeakPercent?: number | null;
  memoryAveragePercent?: number | null;
  memoryPeakPercent?: number | null;
  crossings: ResourceCrossingMark[];
  /** Why the Gateway refused this window's figures. A refused window is a fault to show, not an idle stretch. */
  refusedReason?: string | null;
  /** Why this window has no processor figure (refused, or not read and why), in the Gateway's words. Null when it has one. */
  processorNote?: ResourceWindowNote | null;
  /** Why this window has no memory figure. Null when it has one. */
  memoryNote?: ResourceWindowNote | null;
}

/** Why one window has no figure for one resource (C# ResourceWindowNoteDto), drawn in place of the figure. */
export interface ResourceWindowNote {
  label: string;
  tone: FleetTone;
}

/** A stretch of the span with no five-minute summary from the machine (C# ResourceGapDto). Not an idle machine. */
export interface ResourceGap {
  startUtc: string;
  endUtc: string;
  label: string;
  tone: FleetTone;
}

/** One line of the expanded machine's event list (C# ResourceEventDto), oldest first, as the Gateway folded it. */
export interface ResourceEvent {
  atUtc: string;
  label: string;
  tone: FleetTone;
}

/** One factory's cost on the machine over the span (C# FactoryResourceCostDto) - grouped by Factory, never Mission. */
export interface FactoryResourceCost {
  factory: string;
  sessions: number;
  /** For example "22% / 61%" or "at least 22% / 61%" - average AND peak, always together. */
  processorLabel: string;
  /** For example "3.1 / 5.8 GB". */
  memoryLabel: string;
  processorPeakPercent?: number | null;
}

/** One window's free space for one drive (C# MachineDrivePointDto). */
export interface MachineDrivePoint {
  windowStartUtc: string;
  /** The lowest free space any sample in the window read. */
  lowestFreeBytes: number;
  /** How full the drive was at its fullest in the window, 0 to 100. */
  usedPercent: number;
}

/** One fixed drive on the machine page (C# MachineDriveHistoryDto) - every word and the tone are the Gateway's. */
export interface MachineDriveHistory {
  /** "C:" on Windows, the mount point elsewhere. */
  name: string;
  /** "6.40 GB free of 931 GB", or "cannot read: the reason". */
  headline: string;
  /** "red, under 10 GB", "amber, under 25 GB", "normal" or "cannot read". */
  levelLabel: string;
  tone: FleetTone;
  /** "lost 8 GB in the last 3 hours", "steady", or null with only one reading. */
  trendLabel?: string | null;
  points: MachineDrivePoint[];
}

/** GET /machines/{machine}/resources (C# MachineResourceHistoryDto). */
export interface MachineResourceHistory {
  machine: string;
  hours: number;
  /** The span the Gateway read, by its clock. The chart lays out exactly this, so its right edge is the Gateway's now. */
  fromUtc: string;
  toUtc: string;
  /** Where no summary arrived, oldest first. */
  gaps: ResourceGap[];
  /** Crossings, refused windows and gaps as one list, oldest first. */
  events: ResourceEvent[];
  /** Shown instead of the factory table when there are rows but no session was recorded. */
  noFactoriesMessage?: string | null;
  /** What to show instead of the chart and the table. Null when there are rows. */
  emptyMessage?: string | null;
  windows: MachineResourceWindow[];
  factories: FactoryResourceCost[];
  /** Each fixed drive's free space over the span (Disk Space on the Meter, 7 November 2026). */
  drives: MachineDriveHistory[];
  /** Shown instead of the drives when there are summaries but none reported a drive (an older Director). */
  noDrivesMessage?: string | null;
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

function hasBoth(average: number | null | undefined, peak: number | null | undefined): boolean {
  return typeof average === "number" && Number.isFinite(average) && typeof peak === "number" && Number.isFinite(peak);
}

/** GET /machines/{machine}/resources - one machine expanded: 24 hours of five-minute rows and the cost per factory. */
export async function getMachineResources(machine: string, signal?: AbortSignal): Promise<MachineResourceHistory> {
  const res = await gatewayFetch(`${machinePath(machine)}/resources`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  }, { timeoutMs: POLL_TIMEOUT_MS });
  if (!res.ok) throw await GatewayError.from(res, "read the machine's resource history");
  const body = (await res.json()) as MachineResourceHistory;
  // A body without the lists is not an idle machine - drawing an empty chart would read as one.
  if (!Array.isArray(body?.windows) || !Array.isArray(body?.factories) || !Array.isArray(body?.gaps) || !Array.isArray(body?.events)) {
    throw new GatewayError(res.status, "The Gateway answered the machine's resource history without its 'windows', 'factories', 'gaps' and 'events' lists.");
  }
  // The drives are a list the Gateway always sends; without it the page would show no disk at all and look healthy.
  if (!Array.isArray(body.drives)) {
    throw new GatewayError(res.status, "The Gateway answered the machine's resource history without its 'drives' list.");
  }
  // The chart lays out the Gateway's span. Without it the client would have to invent a clock, which is the defect the
  // span exists to prevent - so a history without one is refused, not drawn against the newest window.
  if (!Number.isFinite(Date.parse(body.fromUtc)) || !Number.isFinite(Date.parse(body.toUtc))) {
    throw new GatewayError(res.status, "The Gateway answered the machine's resource history without the span it covers.");
  }
  // A window missing a figure must carry the Gateway's note saying why. Without it the client would have to write a
  // sentence and pick a tone itself, so such a history is refused, the same as one without its span.
  const unexplained = body.windows.find(
    (w) =>
      (w.refusedReason != null || !hasBoth(w.processorAveragePercent, w.processorPeakPercent) ? w.processorNote == null : false) ||
      (w.refusedReason != null || !hasBoth(w.memoryAveragePercent, w.memoryPeakPercent) ? w.memoryNote == null : false),
  );
  if (unexplained !== undefined) {
    throw new GatewayError(res.status, `The Gateway answered the machine's resource history with a window at ${unexplained.windowStartUtc} that has no figure and no reason.`);
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
