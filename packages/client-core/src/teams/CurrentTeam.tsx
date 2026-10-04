// THE ONE "CURRENT TEAM" FOR A SHELL (devthrottle_internal#2312). The team switcher at the top of the rail
// writes it; every page that shows one team at a time - the Fleet Map, the Team page (#2303), the Skills page
// (#2304) - reads it from here, so two pages can never disagree about which team is on screen and no page builds
// its own list of teams.
//
// `current` is null for the person's OWN account - the fleet they had before Teams existed. That is where every
// shell starts, and where it stays for a person with no team: nothing changes for them until they pick a team.
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
 * - `error`: the Gateway could not be asked. `error` says why.
 */
export type CurrentTeamStatus = "loading" | "not-offered" | "ready" | "error";

export interface CurrentTeamState {
  status: CurrentTeamStatus;
  /** The person's teams, in the Gateway's order. Empty unless `status` is `ready`. */
  teams: TeamSummary[];
  /** The team on screen, or null for the person's own account. */
  current: TeamSummary | null;
  /** Why the teams could not be read, when `status` is `error`. */
  error: string | null;
  /** Put a team on screen by its id, or the person's own account with null. An id not in `teams` is refused. */
  choose: (teamId: string | null) => void;
}

const CurrentTeamContext = createContext<CurrentTeamState | null>(null);

const STORAGE_PREFIX = "devthrottle.currentTeam";

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
    load(controller.signal).then(
      (answer) => {
        if (controller.signal.aborted) return;
        setLoaded(
          answer.kind === "teams"
            ? { status: "ready", teams: answer.teams, error: null }
            : { status: "not-offered", teams: [], error: null },
        );
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        setLoaded({ status: "error", teams: [], error: gatewayErrorMessage(err, "read your teams") });
      },
    );
    return () => controller.abort();
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
    return { status: loaded.status, teams: loaded.teams, current, error: loaded.error, choose };
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
