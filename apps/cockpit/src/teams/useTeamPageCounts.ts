import { useEffect, useState } from "react";
import { getPageCount, type TeamSummary } from "@devthrottle/client-core/teams/teamsClient";

// THE COUNT BESIDE A TEAM PAGE IN THE RAIL (screen S8, devthrottle_internal#2307 review F8): how many questions wait on
// this person, beside Questions. Which page has a count, and where it is read, are the Gateway's - each page names its
// own `countPath`, or null - and the number is the Gateway's too, rendered verbatim (rule 7). The Cockpit never builds
// a count path and never counts.
//
// Read every 45 seconds, again on every route change like the Dictionary badge, and again the moment a page changes
// what waits on the person (an answer sent on Questions) - so the rail never says 1 beside a page that says none.
// No team on screen asks nothing.
export const TEAM_PAGE_COUNT_POLL_MS = 45_000;

const STALE_EVENT = "devthrottle:team-page-counts-stale";

/** Read every team page count again now: a page calls this once it has changed what waits on the person. */
export function refreshTeamPageCounts(): void {
  window.dispatchEvent(new Event(STALE_EVENT));
}

const NONE: Readonly<Record<string, number>> = {};

/** The Gateway's count for each page of the team on screen that names one, by page id. */
export function useTeamPageCounts(team: TeamSummary | null, pathname: string): Readonly<Record<string, number>> {
  const counted = team === null ? [] : team.app.pages.filter((p) => p.countPath !== null);
  // Which team and which paths the counts belong to: counts read for another team are never shown for this one.
  const key = team === null ? "" : `${team.id}|${counted.map((p) => `${p.id}=${p.countPath}`).join("|")}`;
  const [read, setRead] = useState<{ key: string; counts: Readonly<Record<string, number>> }>({ key: "", counts: NONE });

  useEffect(() => {
    if (counted.length === 0) return undefined;
    const controller = new AbortController();
    const poll = () => {
      for (const page of counted) {
        void getPageCount(page.countPath!, controller.signal).then(
          (n) => setRead((before) => ({ key, counts: { ...(before.key === key ? before.counts : NONE), [page.id]: n } })),
          // A failed read leaves the last count the Gateway gave; the page itself shows the Gateway's error.
          () => undefined,
        );
      }
    };
    poll();
    const id = window.setInterval(poll, TEAM_PAGE_COUNT_POLL_MS);
    window.addEventListener(STALE_EVENT, poll);
    return () => {
      controller.abort();
      window.clearInterval(id);
      window.removeEventListener(STALE_EVENT, poll);
    };
    // `counted` follows from `key`, and a route change is a reason to read again, so these two are the whole list.
  }, [key, pathname]);

  return read.key === key ? read.counts : NONE;
}
