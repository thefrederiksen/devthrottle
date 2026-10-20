import { useEffect, useState } from "react";
import { gatewayErrorMessage } from "../api/client";
import { getAccountStatus } from "../account/accountClient";
import { errorFacts, reportClientError } from "../errors/reportClientError";
import { retryDelayMs } from "../teams/CurrentTeam";

// WHETHER FACTORY BOXES IS ON FOR THIS ACCOUNT (Factory Boxes mission, phase 1, contract section 5): the Cockpit's rail
// asks it to decide the "Boxes" entry, and the /boxes address asks it to decide between the page and the ordinary
// "Page not found". Neither works it out (rule 7): the answer is the Gateway's `boxes` on GET /account/status, the same
// ruling the Boxes routes' gate makes.
//
// THE ENTRY AND THE PAGE ARE SHOWN ONLY ON A POSITIVE ANSWER. `off` is the Gateway's false. A failed read is neither
// on nor off: the rail draws no entry (an entry drawn then would show Boxes to every account that is off whenever the
// Gateway is restarting), and the /boxes page shows the failure itself rather than a "Page not found" that would be a
// guess. A failure is reported to the Gateway's client error log and asked again on the shell's own rhythm
// (CurrentTeam's retryDelayMs: 15 seconds, 30, then every 60).
//
// A SETTLED answer is kept for the page load: the switch belongs to the account, and switching account reloads the
// whole app, so it cannot go stale under the shell, and the rail and the page do not each ask on every navigation.
// A failure is never kept.

export type BoxesSwitch =
  | { state: "asking" }
  | { state: "on" }
  | { state: "off" }
  | { state: "failed"; message: string };

const SURFACE = "boxes-switch";

let settled: boolean | null = null;

/** Forget the settled answer (tests). */
export function resetBoxesSwitchCache(): void {
  settled = null;
}

function fromSettled(value: boolean | null): BoxesSwitch {
  if (value === null) return { state: "asking" };
  return value ? { state: "on" } : { state: "off" };
}

export function useBoxesSwitch(): BoxesSwitch {
  const [answer, setAnswer] = useState<BoxesSwitch>(() => fromSettled(settled));

  useEffect(() => {
    if (settled !== null) {
      setAnswer(fromSettled(settled));
      return undefined;
    }
    const controller = new AbortController();
    let timer: number | undefined;
    let failures = 0;

    const ask = () => {
      getAccountStatus(controller.signal).then(
        (status) => {
          if (controller.signal.aborted) return;
          settled = status.boxes === true;
          setAnswer(fromSettled(settled));
        },
        (err: unknown) => {
          if (controller.signal.aborted) return;
          failures += 1;
          const message = gatewayErrorMessage(err, "read whether Boxes is on for your account");
          reportClientError({
            surface: SURFACE,
            action: "read the account status",
            message: `read the account status to decide the Boxes screen (failure ${failures}, asking again)`,
            user_visible: false,
            ...errorFacts(err),
          });
          setAnswer({ state: "failed", message });
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

  return answer;
}
