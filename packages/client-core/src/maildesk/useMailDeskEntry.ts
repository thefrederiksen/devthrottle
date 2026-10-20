import { useEffect, useState } from "react";
import { errorFacts, reportClientError } from "../errors/reportClientError";
import { retryDelayMs } from "../teams/CurrentTeam";
import { getMailDeskChannels } from "./mailDeskClient";

// WHETHER A SHELL OFFERS "Mail Desk" (Mail Desk mission, phase 7, contract section 1): the Cockpit's rail and the
// phone's navigation drawer both ask this. Neither works it out (rule 7): it asks the Gateway the same read the page
// makes, GET /gateway/mail-desk/channels, because the switch's only answer is that read answering 404 - a second,
// ungated "is it on?" route would tell an account that is off that the Mail Desk exists.
//
// THE ENTRY IS DRAWN ONLY ON A POSITIVE ANSWER: a channels read that succeeded (contract section 1, corrected after
// the phase review's finding F1, reviews/PHASE-REVIEW.md). 404 - the switch off - hides it, and so does every failure
// - offline, a timeout, a fault, an answer that breaks the contract - because this hook cannot know the account's
// switch while the read fails, and an entry drawn then would show the Mail Desk to every account that is off whenever
// the phone is offline or the Gateway is restarting. This is NOT the Mentor entry's ruling, which shows its entry on a
// failure: there the hidden answer is a refusal for a page the product offers; here it is the whole feature being
// absent for the account. A failure still never looks like "switched off" where it matters: the page at /mail-desk,
// reached by its address, shows the error itself.
//
// A failed read is reported to the Gateway's client error log and ASKED AGAIN on the shell's own rhythm
// (CurrentTeam's retryDelayMs: 15 seconds, 30, then every 60) until the Gateway answers. There is no second retry
// mechanism: the delays are the shell's. Once a read has succeeded it is settled for the page load (below), so no later
// failure is ever asked and none can take the entry away.
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
          // Not shown to the person: the entry stays hidden and the read is asked again.
          reportClientError({
            surface: SURFACE,
            action: "read the Mail Desk",
            message: `read the Mail Desk to decide the Mail Desk entry (failure ${failures}, asking again)`,
            user_visible: false,
            ...errorFacts(err),
          });
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
