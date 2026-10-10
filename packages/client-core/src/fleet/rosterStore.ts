// The one shared fleet-roster store (issue #1239). Before this, three pages - Sessions, Fleet Map, and
// Directors - each ran their own timer polling GET /sessions?envelope=true independently, so opening
// them together meant three overlapping full fan-outs to every machine every couple of seconds, and a
// backgrounded Cockpit kept all of them hammering the Gateway forever. This module is the single source
// of truth: every fleet-reading page subscribes to `rosterStore`, so there is exactly ONE roster poll
// loop no matter how many of those pages are mounted, they all read the identical roster at the same
// moment, and a hidden tab makes no roster requests after its one first load (returning to it refreshes
// within one interval).
//
// Follow-on (noted in the issue, not built here): once this store is the single reader, the Gateway can
// push roster deltas over the existing GET /events Server-Sent Events stream instead of the store
// re-pulling; the store is the seam that makes that swap a one-file change.
import { gatewayErrorMessage, type SessionDto } from "../api/client";
import { getSessionsEnvelope, type DirectorReachability, type MachineError, type SessionsEnvelope } from "./fleetClient";
import { createPollingStore } from "../polling/pollingStore";
import { usePollingStore } from "../polling/usePollingStore";

// One poll loop total, at the tightest cadence any roster page used (the Fleet Map's 2s), so the shared
// loop is at least as fresh as the busiest page was on its own.
export const ROSTER_POLL_MS = 2000;

// The process-wide roster loop. It is created once at module load and shared by every subscriber; the
// loop itself only runs while at least one page is mounted and the tab is visible.
export const rosterStore = createPollingStore<SessionsEnvelope>({
  fetcher: getSessionsEnvelope,
  intervalMs: ROSTER_POLL_MS,
  mapError: gatewayErrorMessage,
  reporting: { surface: "session-roster", action: "load the sessions" },
});

// The roster as a page reads it: the live sessions (null until the first load, so a page can tell
// "loading" from "empty fleet"), the machines that failed the last sweep, and the per-Director
// reachability - plus the keep-last error banner text and an imperative refresh for right after a
// mutation (e.g. creating a session).
export interface SharedRoster {
  sessions: SessionDto[] | null;
  machineErrors: MachineError[];
  directors: DirectorReachability[];
  /** The Gateway's finished fleet-wide warning line, or null when nothing is wrong. Printed verbatim. */
  unreachableBanner: string | null;
  error: string | null;
  refreshNow: () => void;
}

export function useSharedRoster(): SharedRoster {
  const { data, error } = usePollingStore(rosterStore);
  return {
    sessions: data === null ? null : data.sessions,
    machineErrors: data?.machineErrors ?? [],
    directors: data?.directors ?? [],
    unreachableBanner: data?.unreachableBanner ?? null,
    error,
    refreshNow: rosterStore.refreshNow,
  };
}
