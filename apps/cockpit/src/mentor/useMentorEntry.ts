import { useEffect, useState } from "react";
import { reportClientError } from "@devthrottle/client-core/errors/reportClientError";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import { getMentorPage } from "@devthrottle/client-core/teams/mentorClient";

// WHETHER THE RAIL OFFERS "Mentor" (devthrottle_internal#2305). The rail never works it out from a role label (rule 7;
// finding F5 of the team switcher's review, devthrottle#3527): it asks the Gateway the same question the page asks, for
// the team on screen.
//
// The Tech Lead's ruling (review of devthrottle#3538, F2): the entry is HIDDEN only on the Gateway's two answers that
// mean "there is no page for you" - 403 (a Collaborator) and 404 (no such team for this person, or Teams off). Every
// other outcome - a network failure, a fault, an invalid week, an answer that breaks the contract - SHOWS the entry, so
// the page itself can say what went wrong, and is reported to the Gateway's client error log. A failure is never
// allowed to look like a refusal. Because a failed read shows the entry, there is nothing to ask again: the page asks
// on its own each time it is opened, and offers Try again.
//
// Asked once each time the team on screen changes, not on every route change: who may read a team's Mentor page does
// not change while one team stays on screen.

const SURFACE = "cockpit-mentor-entry";

export function useMentorEntry(): boolean {
  const { current } = useCurrentTeam();
  const teamId = current?.id ?? null;
  const [offered, setOffered] = useState<{ teamId: string; offered: boolean } | null>(null);

  useEffect(() => {
    if (teamId === null) return;
    const controller = new AbortController();
    getMentorPage(teamId, undefined, controller.signal).then(
      (answer) => {
        if (!controller.signal.aborted) setOffered({ teamId, offered: answer.kind === "page" });
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        reportClientError(SURFACE, "rail", `read the Mentor page for team ${teamId} to decide the Mentor entry`, err);
        setOffered({ teamId, offered: true });
      },
    );
    return () => controller.abort();
  }, [teamId]);

  return teamId !== null && offered !== null && offered.teamId === teamId && offered.offered;
}
