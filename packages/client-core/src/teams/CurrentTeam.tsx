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
// carries `start`: the person's own account, one team, or the chooser (S11).
//
// NOTHING REMEMBERED NEVER WAITS (delta review D1). A browser that remembers no team draws the own account at once,
// exactly as before Teams, and takes the Gateway's start when it answers. That is what keeps a Gateway with Teams dark,
// and a person with no team, unchanged: their Cockpit never waits on GET /teams and never says "Loading your team".
// The cost falls on one person only - a Collaborator's first load in a browser that has never chosen shows their own
// account for the length of one read before their pages; accepting an invitation in this browser avoids even that.
//
// THE GATEWAY'S ANSWER IS STORED AS AN ANSWER, NOT AS A PICK (delta review D2). A pick (the switcher, the chooser, the
// accept page) outranks the Gateway and is kept. The Gateway's start is stored apart, prefixed "gateway:", and EVERY
// later answer replaces it - so a person who had no team when this browser first opened, and was invited later,
// starts in their team on the next load here. A stored answer that names a team waits for the list like a picked
// team does (`resolving`); a stored own-account answer draws at once.
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

/** What a browser remembers: nothing (it has never chosen), the person's own account, or one team - each either
 *  `picked` by the person, or the Gateway's start answer, which the next answer replaces. */
type Choice = { kind: "none" } | { kind: "own"; picked: boolean } | { kind: "team"; id: string; picked: boolean };

const NOTHING: Choice = { kind: "none" };

/** The stored value for "the own account". A team id is a GUID, so it can never be this. */
const OWN_ACCOUNT = "own-account";

/** The prefix of a stored Gateway answer, apart from a pick. */
const GATEWAY_ANSWER = "gateway:";

function readRemembered(): Choice {
  try {
    const value = window.localStorage.getItem(currentTeamStorageKey());
    if (value === null) return NOTHING;
    const picked = !value.startsWith(GATEWAY_ANSWER);
    const what = picked ? value : value.slice(GATEWAY_ANSWER.length);
    return what === OWN_ACCOUNT ? { kind: "own", picked } : { kind: "team", id: what, picked };
  } catch {
    // Storage turned off: the choice is not remembered across loads, which costs a click, not correctness.
    return NOTHING;
  }
}

function remember(choice: Choice): void {
  try {
    if (choice.kind === "none") {
      window.localStorage.removeItem(currentTeamStorageKey());
      return;
    }
    const what = choice.kind === "own" ? OWN_ACCOUNT : choice.id;
    window.localStorage.setItem(currentTeamStorageKey(), choice.picked ? what : `${GATEWAY_ANSWER}${what}`);
  } catch {
    // As above: a browser with storage off still switches, it just forgets on the next load.
  }
}

function sameChoice(a: Choice, b: Choice): boolean {
  if (a.kind === "none" || b.kind === "none") return a.kind === b.kind;
  if (a.kind !== b.kind || a.picked !== b.picked) return false;
  return a.kind === "own" || (b.kind === "team" && a.id === b.id);
}

/**
 * Put a team on screen in THIS browser for the next shell that mounts - used by the accept page (S3), which sits
 * outside the shell: the person who just joined a team lands in it (devthrottle_internal#2306, review finding F1).
 */
export function rememberTeamOnThisBrowser(teamId: string): void {
  remember({ kind: "team", id: teamId, picked: true });
}

/** The Gateway's start verdict as a choice it made, not the person; the chooser is no choice yet. */
function fromStart(start: TeamStart | null): Choice {
  if (start === null || start.where === "choose") return NOTHING;
  return start.where === "team" ? { kind: "team", id: start.teamId, picked: false } : { kind: "own", picked: false };
}

/** Whether the person chose this, rather than the Gateway or nobody. */
function isPick(choice: Choice): boolean {
  return choice.kind !== "none" && choice.picked;
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

  // A picked team the Gateway no longer lists for this person (they left it, or it is another account's) is forgotten,
  // so the browser starts where the Gateway says again. Anything the person did not pick takes the Gateway's latest
  // start, stored as the Gateway's answer so the next answer replaces it (delta review D2). The chooser is stored as
  // nothing: it waits for the person.
  useEffect(() => {
    if (loaded.status !== "ready") return;
    if (isPick(choice)) {
      if (choice.kind === "team" && !loaded.teams.some((t) => t.id === choice.id)) {
        remember(NOTHING);
        setChoice(NOTHING);
      }
      return;
    }
    const started = fromStart(loaded.start);
    if (!sameChoice(started, choice)) {
      remember(started);
      setChoice(started);
    }
  }, [loaded, choice]);

  const choose = useCallback(
    (teamId: string | null): TeamSummary | null => {
      if (teamId === null) {
        const own: Choice = { kind: "own", picked: true };
        remember(own);
        setChoice(own);
        return null;
      }
      const team = loaded.teams.find((t) => t.id === teamId);
      if (team === undefined) throw new Error("That team is not one of yours, so it cannot be put on screen.");
      const picked: Choice = { kind: "team", id: teamId, picked: true };
      remember(picked);
      setChoice(picked);
      return team;
    },
    [loaded.teams],
  );

  const value = useMemo<CurrentTeamState>(() => {
    const ready = loaded.status === "ready";
    const effective = !isPick(choice) && ready ? fromStart(loaded.start) : choice;
    const current = ready && effective.kind === "team" ? loaded.teams.find((t) => t.id === effective.id) ?? null : null;
    // Only a remembered TEAM waits for the list (picked, or the Gateway's last answer). Nothing remembered, or the own
    // account, never waits (delta review D1).
    const resolving = choice.kind === "team" && (loaded.status === "loading" || loaded.status === "error");
    const choosing = ready && !isPick(choice) && loaded.start?.where === "choose";
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
