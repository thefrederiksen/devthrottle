// THE TEAMS THAT NAME ONE PAGE, FOR A SHELL WITHOUT A TEAM SWITCHER (Watch your factory, piece 6). The phone has no
// current-team provider: it shows a contributor's own pages, which combine every team anyway. This reads the person's
// teams once per page load (GET /teams) and answers the ones whose page list - the Gateway's verdict - names `pageId`.
//
// One read per page load, shared by every caller: the menu and the page ask the same question. A failed read is
// forgotten, so the next caller asks again.

import { useEffect, useRef, useState } from "react";
import { describeAndReport } from "../errors/reportClientError";
import { getMyTeams, type MyTeamsAnswer, type TeamSummary } from "./teamsClient";
import { teamsWithPage } from "./contributorClient";

let pending: Promise<MyTeamsAnswer> | null = null;

function readOnce(): Promise<MyTeamsAnswer> {
  if (pending === null) {
    pending = getMyTeams().catch((err: unknown) => {
      pending = null;
      throw err;
    });
  }
  return pending;
}

/** For tests: forget the read, so the next caller reads again. */
export function forgetMyTeamsRead(): void {
  pending = null;
}

/** What `useMyTeamsWithPage` answers. */
export interface MyTeamsWithPage {
  /** The person's teams whose page list names the page, or null until the Gateway has answered. A Gateway with no
   *  teams answers an empty list. */
  teams: TeamSummary[] | null;
  /** Why the read failed, already reported. */
  error: string | null;
}

/** The person's teams whose page list names `pageId`. */
export function useMyTeamsWithPage(pageId: string, surface: string): MyTeamsWithPage {
  const [teams, setTeams] = useState<TeamSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const held = useRef<{ key: string; teams: TeamSummary[] } | null>(null);

  useEffect(() => {
    let live = true;
    readOnce().then(
      (answer) => {
        if (!live) return;
        const found = answer.kind === "teams" ? teamsWithPage(answer.teams, pageId) : [];
        const key = found.map((t) => `${t.id}|${t.name}`).join(",");
        if (held.current === null || held.current.key !== key) held.current = { key, teams: found };
        setTeams(held.current.teams);
        setError(null);
      },
      (err: unknown) => {
        if (!live) return;
        setError(describeAndReport(surface, "read your teams", err, { background: true }));
      },
    );
    return () => {
      live = false;
    };
  }, [pageId, surface]);

  return { teams, error };
}
