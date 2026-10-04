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
//
// A BROWSER THAT HAS NEVER CHOSEN STARTS WHERE THE GATEWAY SAYS (devthrottle_internal#2306, review finding F1). GET /teams
// carries `start`: the person's own account, one team, or the chooser (S11). Until it answers, a browser with no
// remembered choice is `resolving` too - so a Collaborator's first arrival never flashes the whole app - and the answer
// is then remembered like a pick, so later loads do not wait. A failed read with nothing remembered starts on the own
// account, as before. "Your own account" picked on purpose is remembered as such, apart from "never chose".
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { gatewayErrorMessage } from "../api/client";
import { activeAccount } from "../auth/accountStore";
import { getMyTeams, type MyTeamsAnswer, type TeamStart, type TeamSummary } from "./teamsClient";

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
  /** True while the Gateway has not yet confirmed a team this browser remembers, or - for a browser with nothing
   *  remembered - has not yet said where to start. While true, a null `current` does NOT mean the own account. */
  resolving: boolean;
  /** True when the Gateway's answer for a browser that has never chosen is the team chooser (S11): the person has
   *  several teams and no computer on their own account, so they pick. `current` is null meanwhile. */
  choosing: boolean;
  /** Why the latest read of the teams failed, when `status` is `error`. */
  error: string | null;
  /** Put a team on screen by its id, or the person's own account with null, and answer the team now on screen. An id
   *  not in `teams` is refused. */
  choose: (teamId: string | null) => TeamSummary | null;
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

/** What a browser remembers: nothing (it has never chosen), the person's own account, or one team. */
type Choice = { kind: "none" } | { kind: "own" } | { kind: "team"; id: string };

const NOTHING: Choice = { kind: "none" };

/** The stored value for "the own account, picked". A team id is a GUID, so it can never be this. */
const OWN_ACCOUNT = "own-account";

function readRemembered(): Choice {
  try {
    const value = window.localStorage.getItem(currentTeamStorageKey());
    if (value === null) return NOTHING;
    return value === OWN_ACCOUNT ? { kind: "own" } : { kind: "team", id: value };
  } catch {
    // Storage turned off: the choice is not remembered across loads, which costs a click, not correctness.
    return NOTHING;
  }
}

function remember(choice: Choice): void {
  try {
    if (choice.kind === "none") window.localStorage.removeItem(currentTeamStorageKey());
    else window.localStorage.setItem(currentTeamStorageKey(), choice.kind === "own" ? OWN_ACCOUNT : choice.id);
  } catch {
    // As above: a browser with storage off still switches, it just forgets on the next load.
  }
}

/**
 * Put a team on screen in THIS browser for the next shell that mounts - used by the accept page (S3), which sits
 * outside the shell: the person who just joined a team lands in it (devthrottle_internal#2306, review finding F1).
 */
export function rememberTeamOnThisBrowser(teamId: string): void {
  remember({ kind: "team", id: teamId });
}

/** The Gateway's start verdict as a choice; the chooser is no choice yet. */
function fromStart(start: TeamStart | null): Choice {
  if (start === null || start.where === "choose") return NOTHING;
  return start.where === "team" ? { kind: "team", id: start.teamId } : { kind: "own" };
}

interface Loaded {
  status: CurrentTeamStatus;
  teams: TeamSummary[];
  start: TeamStart | null;
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
  const [loaded, setLoaded] = useState<Loaded>({ status: "loading", teams: [], start: null, error: null });
  const [choice, setChoice] = useState<Choice>(readRemembered);

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
              ? { status: "ready", teams: answer.teams, start: answer.start, error: null }
              : { status: "not-offered", teams: [], start: null, error: null },
          );
        },
        (err: unknown) => {
          if (controller.signal.aborted) return;
          failures += 1;
          setLoaded({ status: "error", teams: [], start: null, error: gatewayErrorMessage(err, "read your teams") });
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
  // forgotten, so the browser starts where the Gateway says again. And a browser that has never chosen takes the
  // Gateway's start - remembered, so the next load does not wait for it. The chooser is not remembered: it waits for
  // the person.
  useEffect(() => {
    if (loaded.status !== "ready") return;
    if (choice.kind === "team" && !loaded.teams.some((t) => t.id === choice.id)) {
      remember(NOTHING);
      setChoice(NOTHING);
      return;
    }
    if (choice.kind === "none") {
      const started = fromStart(loaded.start);
      if (started.kind !== "none") {
        remember(started);
        setChoice(started);
      }
    }
  }, [loaded, choice]);

  const choose = useCallback(
    (teamId: string | null): TeamSummary | null => {
      if (teamId === null) {
        remember({ kind: "own" });
        setChoice({ kind: "own" });
        return null;
      }
      const team = loaded.teams.find((t) => t.id === teamId);
      if (team === undefined) throw new Error("That team is not one of yours, so it cannot be put on screen.");
      remember({ kind: "team", id: teamId });
      setChoice({ kind: "team", id: teamId });
      return team;
    },
    [loaded.teams],
  );

  const value = useMemo<CurrentTeamState>(() => {
    const ready = loaded.status === "ready";
    const effective = choice.kind === "none" && ready ? fromStart(loaded.start) : choice;
    const current = ready && effective.kind === "team" ? loaded.teams.find((t) => t.id === effective.id) ?? null : null;
    const resolving =
      (choice.kind === "team" && (loaded.status === "loading" || loaded.status === "error")) ||
      (choice.kind === "none" && loaded.status === "loading");
    const choosing = ready && choice.kind === "none" && loaded.start?.where === "choose";
    return { status: loaded.status, teams: loaded.teams, current, resolving, choosing, error: loaded.error, choose };
  }, [loaded, choice, choose]);

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
