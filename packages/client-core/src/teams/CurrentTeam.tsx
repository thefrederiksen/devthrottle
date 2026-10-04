// THE ONE "CURRENT TEAM" FOR A SHELL (devthrottle_internal#2312). The team switcher at the top of the rail
// writes it; every page that shows one team at a time - the Fleet Map, the Team page (#2303), the Skills page
// (#2304) - reads it from here, so two pages can never disagree about which team is on screen and no page builds
// its own list of teams.
//
// `current` is null for the person's OWN account - the fleet they had before Teams existed. That is where every
// shell starts, and where it stays for a person with no team: nothing changes for them until they pick a team.
//
// READ `resolving` BEFORE TREATING A NULL `current` AS "THE OWN ACCOUNT" (review finding F3). While the Gateway has
// not yet confirmed a team this browser remembers - the first read is in flight, or it failed - `current` is null
// but the person may well be on a team. A page that shows one team at a time shows a loading state (or `error`)
// while `resolving` is true, rather than flashing the person's own data. `resolving` is never true for a person who
// has never picked a team, so they see no change.
//
// THE READ RECOVERS BY ITSELF (review finding F1). The teams are read once per page load; a failed read is asked again
// after 15 seconds, then 30, then every 60 until it succeeds - never a tight loop - so a Gateway that was restarting
// for a moment does not leave the tab without its teams until a reload.
//
// The choice is remembered per browser AND per signed-in account (an origin's storage holds every account the
// browser has signed in with, so a choice keyed to the origin alone would carry one person's team over to
// another). A remembered team that is no longer in the Gateway's list - the person left it - is dropped, and the
// shell goes back to their own account.
//
// The list and the roles are the Gateway's (rule 7): this store only remembers which of the Gateway's teams was
// picked.
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { gatewayErrorMessage } from "../api/client";
import { activeAccount } from "../auth/accountStore";
import { getMyTeams, type MyTeamsAnswer, type TeamSummary } from "./teamsClient";

/**
 * - `loading`: the Gateway has not answered yet.
 * - `not-offered`: this Gateway has no teams (Teams dark, or self-hosted). Nothing about teams is shown.
 * - `ready`: the Gateway listed the person's teams - possibly none.
 * - `error`: the last attempt to read the teams failed. `error` says why; the read is being asked again.
 */
export type CurrentTeamStatus = "loading" | "not-offered" | "ready" | "error";

export interface CurrentTeamState {
  status: CurrentTeamStatus;
  /** The person's teams, in the Gateway's order. Empty unless `status` is `ready`. */
  teams: TeamSummary[];
  /** The team on screen, or null for the person's own account. See `resolving`. */
  current: TeamSummary | null;
  /** True while this browser remembers a team the Gateway has not yet confirmed. While true, a null `current`
   *  does NOT mean the own account. Never true for a person who has never picked a team. */
  resolving: boolean;
  /** Why the latest read of the teams failed, when `status` is `error`. */
  error: string | null;
  /** Put a team on screen by its id, or the person's own account with null. An id not in `teams` is refused. */
  choose: (teamId: string | null) => void;
}

const CurrentTeamContext = createContext<CurrentTeamState | null>(null);

const STORAGE_PREFIX = "devthrottle.currentTeam";

/** How long to wait before asking again after the Nth failure in a row (1-based), in milliseconds. */
export function retryDelayMs(failuresInARow: number): number {
  if (failuresInARow <= 1) return 15_000;
  if (failuresInARow === 2) return 30_000;
  return 60_000;
}

/** The storage key for the signed-in account's choice. Exported for tests. */
export function currentTeamStorageKey(): string {
  const account = activeAccount();
  return account ? `${STORAGE_PREFIX}.${account.id}` : STORAGE_PREFIX;
}

function readRemembered(): string | null {
  try {
    return window.localStorage.getItem(currentTeamStorageKey());
  } catch {
    // Storage turned off: the choice is not remembered across loads, which costs a click, not correctness.
    return null;
  }
}

function remember(teamId: string | null): void {
  try {
    if (teamId === null) window.localStorage.removeItem(currentTeamStorageKey());
    else window.localStorage.setItem(currentTeamStorageKey(), teamId);
  } catch {
    // As above: a browser with storage off still switches, it just forgets on the next load.
  }
}

interface Loaded {
  status: CurrentTeamStatus;
  teams: TeamSummary[];
  error: string | null;
}

export function CurrentTeamProvider({
  children,
  load = getMyTeams,
}: {
  children: ReactNode;
  /** How the teams are read. The Gateway's GET /teams; replaced only in tests. */
  load?: (signal?: AbortSignal) => Promise<MyTeamsAnswer>;
}) {
  const [loaded, setLoaded] = useState<Loaded>({ status: "loading", teams: [], error: null });
  const [chosenId, setChosenId] = useState<string | null>(readRemembered);

  useEffect(() => {
    const controller = new AbortController();
    let timer: number | undefined;
    let failures = 0;

    const read = () => {
      load(controller.signal).then(
        (answer) => {
          if (controller.signal.aborted) return;
          failures = 0;
          setLoaded(
            answer.kind === "teams"
              ? { status: "ready", teams: answer.teams, error: null }
              : { status: "not-offered", teams: [], error: null },
          );
        },
        (err: unknown) => {
          if (controller.signal.aborted) return;
          failures += 1;
          setLoaded({ status: "error", teams: [], error: gatewayErrorMessage(err, "read your teams") });
          timer = window.setTimeout(read, retryDelayMs(failures));
        },
      );
    };

    read();
    return () => {
      controller.abort();
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [load]);

  // A remembered team the Gateway no longer lists for this person (they left it, or it is another account's) is
  // forgotten, so the next load does not try it again.
  useEffect(() => {
    if (loaded.status === "ready" && chosenId !== null && !loaded.teams.some((t) => t.id === chosenId)) {
      remember(null);
      setChosenId(null);
    }
  }, [loaded, chosenId]);

  const choose = useCallback(
    (teamId: string | null) => {
      if (teamId !== null && !loaded.teams.some((t) => t.id === teamId)) {
        throw new Error("That team is not one of yours, so it cannot be put on screen.");
      }
      remember(teamId);
      setChosenId(teamId);
    },
    [loaded.teams],
  );

  const value = useMemo<CurrentTeamState>(() => {
    const current = loaded.status === "ready" ? loaded.teams.find((t) => t.id === chosenId) ?? null : null;
    const resolving = chosenId !== null && (loaded.status === "loading" || loaded.status === "error");
    return { status: loaded.status, teams: loaded.teams, current, resolving, error: loaded.error, choose };
  }, [loaded, chosenId, choose]);

  return <CurrentTeamContext.Provider value={value}>{children}</CurrentTeamContext.Provider>;
}

/** The current team. Must be called under a CurrentTeamProvider - every shell mounts one around its pages. */
export function useCurrentTeam(): CurrentTeamState {
  const state = useContext(CurrentTeamContext);
  if (state === null) {
    throw new Error("useCurrentTeam was called outside a CurrentTeamProvider. Mount the provider in the shell.");
  }
  return state;
}
