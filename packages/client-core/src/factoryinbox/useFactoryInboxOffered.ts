import { useEffect, useState } from "react";
import { getFactoryAgentsSwitch } from "../factory/factoryAgentsClient";
import { describeAndReport } from "../errors/reportClientError";

// WHETHER THE INBOX IS OFFERED AT ALL (Factory Inbox mission, phase 4): the Factory inbox sits behind the factory area
// switch until the phase is whole, and the Gateway decides that switch for the signed-in account (rule 7). Asked once
// per page load and shared by every caller on the page, so the menu entry and the page can never disagree.
//
// "unknown" while the answer is on its way, and when it could not be had (the error is then the sentence to show,
// already reported). The entry is shown only for "on".

export type FactoryInboxOffer = "unknown" | "on" | "off";

let cached: Promise<boolean> | null = null;

function load(): Promise<boolean> {
  if (cached === null) {
    cached = getFactoryAgentsSwitch().then(
      (s) => s.enabled,
      (err: unknown) => {
        cached = null; // ask again next time rather than remember a failure
        throw err;
      },
    );
  }
  return cached;
}

/** Forget the cached answer (tests, and switching account, which reloads the app anyway). */
export function resetFactoryInboxOfferCache(): void {
  cached = null;
}

export interface FactoryInboxOfferAnswer {
  offer: FactoryInboxOffer;
  /** The sentence to show when the Gateway could not be asked, already reported; null otherwise. */
  error: string | null;
}

export function useFactoryInboxOffered(): FactoryInboxOfferAnswer {
  const [offer, setOffer] = useState<FactoryInboxOffer>("unknown");
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let live = true;
    load().then(
      (on) => {
        if (live) setOffer(on ? "on" : "off");
      },
      (err: unknown) => {
        if (live) setError(describeAndReport("factory-inbox", "ask the Gateway whether the factory inbox is on", err, { background: true }));
      },
    );
    return () => {
      live = false;
    };
  }, []);
  return { offer, error };
}
