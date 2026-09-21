import { useEffect, useState } from "react";
import { getFactoryAgentsSwitch } from "@devthrottle/client-core/factory/factoryAgentsClient";

// Whether the Factory Agents area is on. The GATEWAY decides (its `factoryAgents.enabled` switch) and tells the
// Cockpit; the Cockpit never decides it (rule 7). Asked once per page load and shared by the rail item and the
// routes, so the rail and the pages can never disagree.
//
// "unknown" while the answer is on its way, and when the Gateway could not be asked: the rail item stays hidden
// and the page says it could not reach the Gateway, rather than guessing either way.

export type FactorySwitchState = "unknown" | "on" | "off";

let cached: Promise<FactorySwitchState> | null = null;

function load(): Promise<FactorySwitchState> {
  if (cached === null) {
    cached = getFactoryAgentsSwitch()
      .then((s): FactorySwitchState => (s.enabled ? "on" : "off"))
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

export function useFactorySwitch(): { state: FactorySwitchState; error: unknown } {
  const [state, setState] = useState<FactorySwitchState>("unknown");
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    let live = true;
    load().then(
      (s) => {
        if (live) setState(s);
      },
      (err: unknown) => {
        if (live) setError(err);
      },
    );
    return () => {
      live = false;
    };
  }, []);
  return { state, error };
}
