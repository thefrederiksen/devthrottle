import { useCallback, useEffect, useState } from "react";
import { getFactoryAgentsSwitch, type FactoryAgentsSwitch } from "@devthrottle/client-core/factory/factoryAgentsClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-factory-switch";
const MISSING_HOW_TO_START =
  "the Gateway says Factories is off, but did not say how to start it. This is a fault in the Gateway; please report it.";
const MISSING_UPGRADE_PAGE =
  "the Gateway says Factories needs a plan, but did not send the page's heading or pricing address. This is a fault in the Gateway; please report it.";

// Whether the Factory Agents area is on FOR THE SIGNED-IN ACCOUNT. The GATEWAY decides (its machine switch in
// config.json, or this account's own switch set by an administrator) and tells the Cockpit; the Cockpit never
// decides it (rule 7). Asked once per page load and shared by the rail item and the routes, so the rail and the pages
// can never disagree. Switching account reloads the whole app (accountActions), so the cached answer is always the
// answer for the account on screen.
//
// "unknown" while the answer is on its way, and when the Gateway could not be asked: the page says it could not reach
// the Gateway, rather than guessing either way. The menu row no longer asks - it is always shown (owner, 8 Oct 2026) -
// so this is the Factories pages' question only; "off" carries the Gateway's sentence for how to start.
//
// "plan_required" is the hosted Gateway saying the account's plan does not include Factories: the page becomes the
// upgrade page, with the Gateway's heading, sentence and pricing address. Every other off reason - and a Gateway too old
// to send a reason at all - keeps the how-to-start page.

export type FactorySwitchState = "unknown" | "on" | "off";

let cached: Promise<FactoryAgentsSwitch> | null = null;

function load(): Promise<FactoryAgentsSwitch> {
  if (cached === null) {
    cached = getFactoryAgentsSwitch()
      .then((s) => {
        // "plan_unknown" is the hosted Gateway saying its plan read failed and to try again shortly: a failure, not a
        // settled answer, so it is not remembered either - the next visit to Factories asks again.
        if (s.reason === "plan_unknown") cached = null;
        return s;
      })
      .catch((err: unknown) => {
        cached = null; // ask again next time rather than remembering a failure
        throw err;
      });
  }
  return cached;
}

/** Forget the cached answer (tests, and a Gateway that was switched while the page was open). */
export function resetFactorySwitchCache(): void {
  cached = null;
}

export interface FactorySwitchAnswer {
  state: FactorySwitchState;
  /** "plan_required" shows the upgrade page; anything else that is off shows howToStart. Null while on or unknown, and
   *  from a Gateway that sends no reason. */
  reason: FactoryAgentsSwitch["reason"];
  howToStart: string | null;
  title: string | null;
  subscribeUrl: string | null;
  error: string | null;
  /** Forget the cached answer and ask the Gateway again (the upgrade page's Try again, after subscribing). */
  askAgain: () => void;
}

// `enabled` false asks nothing (a Collaborator's three pages have no Factory Agents rail entry; devthrottle_internal
// #2306, review finding F2).
// `error` is the sentence to show, already reported when the failure was caught - so a page that re-renders never
// reports the same failure twice.
export function useFactorySwitch(enabled = true): FactorySwitchAnswer {
  const [answer, setAnswer] = useState<FactoryAgentsSwitch | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!enabled) return undefined;
    let live = true;
    load().then(
      (s) => {
        if (!live) return;
        // The Gateway's contract is a sentence with every "off". Without one that is a fault in the Gateway the user
        // is looking at, so it is shown as an error and reported like one.
        if (!s.enabled && s.howToStart === null) {
          setError(describeAndReport(SURFACE, "read how to start Factories", new Error(MISSING_HOW_TO_START)));
          return;
        }
        if (s.reason === "plan_required" && (!s.title || !s.subscribeUrl)) {
          setError(describeAndReport(SURFACE, "read the Factories upgrade page", new Error(MISSING_UPGRADE_PAGE)));
          return;
        }
        setAnswer(s);
      },
      (err: unknown) => {
        if (live) setError(describeAndReport(SURFACE, "ask the Gateway whether Factories is on", err));
      },
    );
    return () => {
      live = false;
    };
  }, [enabled, attempt]);
  const askAgain = useCallback(() => {
    resetFactorySwitchCache();
    setAnswer(null);
    setError(null);
    setAttempt((n) => n + 1);
  }, []);
  const state: FactorySwitchState = answer === null ? "unknown" : answer.enabled ? "on" : "off";
  return {
    state,
    reason: answer?.reason ?? null,
    howToStart: answer?.howToStart ?? null,
    title: answer?.title ?? null,
    subscribeUrl: answer?.subscribeUrl ?? null,
    error,
    askAgain,
  };
}
