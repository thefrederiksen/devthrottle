import { useEffect, useState } from "react";
import { errorFacts, reportClientError } from "../errors/reportClientError";
import { retryDelayMs } from "../teams/CurrentTeam";
import { getMailDeskChannels } from "./mailDeskClient";

// WHETHER A SHELL OFFERS "Mail Desk" (Mail Desk mission, phase 7, contract section 1): the Cockpit's rail and the
// phone's navigation drawer both ask this. Neither works it out (rule 7): it asks the Gateway the same read the page
// makes, GET /gateway/mail-desk/channels, because the switch's only answer is that read answering 404 - a second,
// ungated "is it on?" route would tell an account that is off that the Mail Desk exists.
//
// The rulings are the Mentor entry's (apps/cockpit/src/mentor/useMentorEntry.ts): the entry is HIDDEN only on the
// Gateway's answer that means "there is no page for you" - 404, the switch off. Every other outcome - a network
// failure, a fault, an answer that breaks the contract - SHOWS the entry, so the page itself can say what went wrong,
// and is reported to the Gateway's client error log. A failure is never allowed to look like "switched off".
//
// A failed read is then ASKED AGAIN on the shell's own rhythm (CurrentTeam's retryDelayMs: 15 seconds, 30, then every
// 60) until the Gateway answers, so an account that is off does not keep an entry one failed read put there. There is
// no second retry mechanism: the delays are the shell's.
//
// A SETTLED answer is kept for the page load: the switch belongs to the account, and switching account reloads the
// whole app, so the answer cannot go stale under the shell. That keeps the phone's drawer, which mounts again every
// time the session list does, from asking again each time. A failure is never kept.

const SURFACE = "mail-desk-entry";

let settled: boolean | null = null;

/** Forget the settled answer (tests). */
export function resetMailDeskEntryCache(): void {
  settled = null;
}

export function useMailDeskEntry(): boolean {
  const [offered, setOffered] = useState<boolean>(settled ?? false);

  useEffect(() => {
    if (settled !== null) return undefined;
    const controller = new AbortController();
    let timer: number | undefined;
    let failures = 0;

    const ask = () => {
      getMailDeskChannels(controller.signal).then(
        (answer) => {
          if (controller.signal.aborted) return;
          settled = answer.kind === "channels";
          setOffered(settled);
        },
        (err: unknown) => {
          if (controller.signal.aborted) return;
          failures += 1;
          // Not shown to the person: the entry stays offered and the read is asked again.
          reportClientError({
            surface: SURFACE,
            action: "read the Mail Desk",
            message: `read the Mail Desk to decide the Mail Desk entry (failure ${failures}, asking again)`,
            user_visible: false,
            ...errorFacts(err),
          });
          setOffered(true);
          timer = window.setTimeout(ask, retryDelayMs(failures));
        },
      );
    };

    ask();
    return () => {
      controller.abort();
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, []);

  return offered;
}
