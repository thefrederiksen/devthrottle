import { useEffect, useState } from "react";
import { errorFacts, reportClientError } from "@devthrottle/client-core/errors/reportClientError";
import { retryDelayMs, useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import { getMentorPage, getPersonalMentorPage } from "@devthrottle/client-core/teams/mentorClient";

// WHETHER THE RAIL OFFERS "Mentor" (devthrottle_internal#2305). The rail never works it out from a role label (rule 7;
// finding F5 of the team switcher's review, devthrottle#3527): it asks the Gateway the same question the page asks, for
// the team on screen.
//
// The Tech Lead's rulings (reviews of devthrottle#3538, F2 and D2): the entry is HIDDEN only on the Gateway's two
// answers that mean "there is no page for you" - 403 (a Collaborator) and 404 (no such team for this person, or Teams
// off). Every other outcome - a network failure, a fault, an invalid week, an answer that breaks the contract - SHOWS
// the entry, so the page itself can say what went wrong, and is reported to the Gateway's client error log. A failure
// is never allowed to look like a refusal.
//
// A failed read is then ASKED AGAIN on the shell's own rhythm (CurrentTeam's retryDelayMs: 15 seconds, 30, then every
// 60) until the Gateway answers, so a Collaborator or someone just removed from the team does not keep an entry that
// one failed read put there. There is no second retry mechanism: the delays are the shell's.
//
// Asked once each time the team on screen changes, not on every route change: who may read a team's Mentor page does
// not change while one team stays on screen.
//
// ON THE PERSON'S OWN ACCOUNT (owner, 8 Oct 2026) the question is the personal page's: GET /account/mentor. A Gateway
// with Teams dark, or a self-hosted one, answers it 404 and the entry stays hidden there, as it always was.

const SURFACE = "cockpit-mentor-entry";

/** The key for the person's own account, which has no team id. Not a team id the Gateway could ever mint. */
const OWN = "(own account)";

export function useMentorEntry(): boolean {
  const { current, status, resolving, choosing } = useCurrentTeam();
  // Which page to ask about: the team on screen, or the person's own account once the shell knows it is the own
  // account on screen (not while it is still learning the team).
  const settled = status !== "loading" && !resolving && !choosing;
  const teamId = current?.id ?? (settled ? OWN : null);
  const [offered, setOffered] = useState<{ teamId: string; offered: boolean } | null>(null);

  useEffect(() => {
    if (teamId === null) return;
    const controller = new AbortController();
    let timer: number | undefined;
    let failures = 0;

    const ask = () => {
      (teamId === OWN ? getPersonalMentorPage(undefined, controller.signal) : getMentorPage(teamId, undefined, controller.signal)).then(
        (answer) => {
          if (!controller.signal.aborted) setOffered({ teamId, offered: answer.kind === "page" });
        },
        (err: unknown) => {
          if (controller.signal.aborted) return;
          failures += 1;
          // Not shown to the person: the entry stays offered and the read is asked again.
          reportClientError({
            surface: SURFACE,
            action: "read the Mentor page",
            message: `read the Mentor page for ${teamId === OWN ? "the own account" : `team ${teamId}`} to decide the Mentor entry (failure ${failures}, asking again)`,
            user_visible: false,
            ...errorFacts(err),
          });
          setOffered({ teamId, offered: true });
          timer = window.setTimeout(ask, retryDelayMs(failures));
        },
      );
    };

    ask();
    return () => {
      controller.abort();
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [teamId]);

  return teamId !== null && offered !== null && offered.teamId === teamId && offered.offered;
}
