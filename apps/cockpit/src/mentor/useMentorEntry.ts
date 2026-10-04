import { useEffect, useState } from "react";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import { getMentorPage } from "@devthrottle/client-core/teams/mentorClient";

// WHETHER THE RAIL OFFERS "Mentor" (devthrottle_internal#2305). The rail never works it out from a role label (rule 7,
// review finding F5): it asks the Gateway the same question the page asks, for the team on screen, and offers the
// entry only when the Gateway answers with a page. A Collaborator is refused, a person on their own account has no
// team to ask about, and a Gateway with Teams off has no such route - none of them sees the entry.
//
// Asked once each time the team on screen changes, not on every route change: who may read a team's Mentor page does
// not change while one team stays on screen. A read that fails hides the entry rather than guessing it.

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
      () => {
        if (!controller.signal.aborted) setOffered({ teamId, offered: false });
      },
    );
    return () => controller.abort();
  }, [teamId]);

  return teamId !== null && offered !== null && offered.teamId === teamId && offered.offered;
}
