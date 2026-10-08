import { useEffect, useState } from "react";
import { getFactoryAgentsSwitch, type FactoryAgentsSwitch } from "@devthrottle/client-core/factory/factoryAgentsClient";

// Whether the Factory Agents area is on FOR THE SIGNED-IN ACCOUNT. The GATEWAY decides (its machine switch in
// config.json, or this account's own switch set by an administrator) and tells the Cockpit; the Cockpit never
// decides it (rule 7). Asked once per page load and shared by the rail item and the routes, so the rail and the pages
// can never disagree. Switching account reloads the whole app (accountActions), so the cached answer is always the
// answer for the account on screen.
//
// "unknown" while the answer is on its way, and when the Gateway could not be asked: the page says it could not reach
// the Gateway, rather than guessing either way. The menu row no longer asks - it is always shown (owner, 8 Oct 2026) -
// so this is the Factories pages' question only; "off" carries the Gateway's sentence for how to start.

export type FactorySwitchState = "unknown" | "on" | "off";

let cached: Promise<FactoryAgentsSwitch> | null = null;

function load(): Promise<FactoryAgentsSwitch> {
  if (cached === null) {
    cached = getFactoryAgentsSwitch()
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

// `enabled` false asks nothing (a Collaborator's three pages have no Factory Agents rail entry; devthrottle_internal
// #2306, review finding F2).
export function useFactorySwitch(enabled = true): { state: FactorySwitchState; howToStart: string | null; error: unknown } {
  const [answer, setAnswer] = useState<FactoryAgentsSwitch | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (!enabled) return undefined;
    let live = true;
    load().then(
      (s) => {
        if (live) setAnswer(s);
      },
      (err: unknown) => {
        if (live) setError(err);
      },
    );
    return () => {
      live = false;
    };
  }, [enabled]);
  const state: FactorySwitchState = answer === null ? "unknown" : answer.enabled ? "on" : "off";
  return { state, howToStart: answer?.howToStart ?? null, error };
}
