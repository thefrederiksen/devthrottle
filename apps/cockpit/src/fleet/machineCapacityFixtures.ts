import {
  CAPACITY_DASH,
  type FleetMachine,
  type FleetMachineCapacity,
  type MachineResourceHistory,
  type MachineResourceWindow,
} from "@devthrottle/client-core/fleet/machinesClient";

// Test fixtures for the capacity columns and the expanded machine (Mission: Resource Monitor, sections 5j and 5k).
// Each is shaped as the Gateway's ResourceCapacityFold / MachineResourceHistoryFold produces it - including the
// offline machine, which the Gateway sends with its dashes and its reason, never with zeros.

const GB = 1_073_741_824;

export const ONLINE: FleetMachineCapacity = {
  reporting: true,
  processorLabel: "28%",
  processorDetail: "peak 71%",
  processorTone: "ok",
  memoryLabel: "55%",
  memoryDetail: "8.9 / 16 GB",
  memoryTone: "ok",
  spareLabel: "7.1 GB",
  spareBytes: Math.round(7.1 * GB),
  statusLabel: null,
  statusTone: "ok",
  why: null,
  readingWindowStartUtc: "2026-11-06T11:55:00Z",
};

export const HOT: FleetMachineCapacity = {
  reporting: true,
  processorLabel: "88%",
  processorDetail: "peak 99%",
  processorTone: "warn",
  memoryLabel: "34%",
  memoryDetail: "13 / 39 GB",
  memoryTone: "ok",
  spareLabel: "26 GB",
  spareBytes: 26 * GB,
  statusLabel: "CPU hot",
  statusTone: "warn",
  why: null,
  readingWindowStartUtc: "2026-11-06T11:55:00Z",
};

export const OFFLINE_WHY = "No Director on this machine is running, so it is not measuring anything. Offline is not idle.";

export const OFFLINE: FleetMachineCapacity = {
  reporting: false,
  processorLabel: CAPACITY_DASH,
  processorDetail: null,
  processorTone: "idle",
  memoryLabel: CAPACITY_DASH,
  memoryDetail: null,
  memoryTone: "idle",
  spareLabel: CAPACITY_DASH,
  spareBytes: null,
  statusLabel: "Offline",
  statusTone: "idle",
  why: OFFLINE_WHY,
  readingWindowStartUtc: null,
};

export function machineWith(name: string, capacity: FleetMachineCapacity): FleetMachine {
  const action = { offered: false, label: "", reason: null };
  return {
    machine: name,
    reach: "Connected",
    reachLabel: "Launcher connected",
    reachTone: "ok",
    launcherVersionState: { label: "", tone: "idle", behind: false },
    canReportUpdateStatus: false,
    upToDate: true,
    update: action,
    restart: action,
    start: action,
    directors: [],
    capacity,
  };
}

/** A five-minute row with the processor and memory read. */
export function window(at: string, cpu: [number, number] | null, memory: [number, number] | null): MachineResourceWindow {
  return {
    windowStartUtc: at,
    processorAveragePercent: cpu?.[0] ?? null,
    processorPeakPercent: cpu?.[1] ?? null,
    memoryAveragePercent: memory?.[0] ?? null,
    memoryPeakPercent: memory?.[1] ?? null,
    crossings: [],
    refusedReason: null,
  };
}

export function history(windows: MachineResourceWindow[], factories: MachineResourceHistory["factories"] = []): MachineResourceHistory {
  return { machine: "devlinux", hours: 24, emptyMessage: null, windows, factories };
}
