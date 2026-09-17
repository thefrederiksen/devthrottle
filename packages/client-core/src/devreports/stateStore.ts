// Where the host keeps a report page's state (handoff ruling 3). The page runs in a sandboxed frame with no
// storage, so everything it must remember - queued items, half-typed text, unqueued answers, scroll - is
// handed to the host in state-changed and kept here, per report and version, in this device's local
// storage. It is the owner's own unsent draft, so it stays on the owner's device.
//
// When the agent publishes a new version, the new version starts from the previous version's state, so a
// republish does not throw away what the owner had typed. Only the newest version's state is kept.

import { isValidPageState, type DevReportPageState } from "./protocol";

const PREFIX = "devthrottle.dev-report-state.";

/** The part of the Storage interface this uses, so a test can hand in its own. */
export type StateStorage = Pick<Storage, "getItem" | "setItem" | "removeItem" | "key" | "length">;

function keyFor(reportId: string, version: number): string {
  return `${PREFIX}${reportId}.${version}`;
}

export class DevReportStateStore {
  constructor(private readonly storage: StateStorage) {}

  /** The stored versions of one report, newest first. */
  private versionsOf(reportId: string): number[] {
    const prefix = `${PREFIX}${reportId}.`;
    const versions: number[] = [];
    for (let i = 0; i < this.storage.length; i++) {
      const key = this.storage.key(i);
      if (key === null || !key.startsWith(prefix)) continue;
      const version = Number(key.slice(prefix.length));
      if (Number.isInteger(version) && version >= 1) versions.push(version);
    }
    return versions.sort((a, b) => b - a);
  }

  private read(reportId: string, version: number): DevReportPageState | null {
    const raw = this.storage.getItem(keyFor(reportId, version));
    if (raw === null) return null;
    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch (err) {
      console.warn(`[DevReportStateStore] the saved state of report ${reportId} version ${version} is not JSON; it is discarded`, err);
      this.storage.removeItem(keyFor(reportId, version));
      return null;
    }
    if (!isValidPageState(parsed)) {
      console.warn(`[DevReportStateStore] the saved state of report ${reportId} version ${version} is not a page state; it is discarded`);
      this.storage.removeItem(keyFor(reportId, version));
      return null;
    }
    return parsed;
  }

  /**
   * The state to restore for this version: its own when there is one, otherwise the state of the newest
   * earlier version (a republish carries the draft across), otherwise null - nothing saved yet.
   */
  load(reportId: string, version: number): DevReportPageState | null {
    const own = this.read(reportId, version);
    if (own) return own;
    for (const earlier of this.versionsOf(reportId)) {
      if (earlier >= version) continue;
      const seeded = this.read(reportId, earlier);
      if (seeded) return seeded;
    }
    return null;
  }

  /** Keeps this version's state and drops every other version of the same report. */
  save(reportId: string, version: number, state: DevReportPageState): void {
    this.storage.setItem(keyFor(reportId, version), JSON.stringify(state));
    for (const other of this.versionsOf(reportId)) {
      if (other !== version) this.storage.removeItem(keyFor(reportId, other));
    }
  }
}
