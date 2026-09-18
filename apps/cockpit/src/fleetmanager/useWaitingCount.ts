import { useEffect, useState, useSyncExternalStore } from "react";
import { getFleetManagerPage } from "@devthrottle/client-core/fleetmanager/pageClient";
import { fleetManagerPageStore } from "./pageStore";

// The rail's Fleet Manager badge (the Fleet Manager mission, step 6): the Gateway's count of what is waiting on the
// owner, rendered verbatim - the client never counts it.
//
// Away from the page it is read every 30 seconds and again on every route change, like the Dictionary badge. On the
// page it follows the page's own live read, so the badge and "Waiting on you" move together the moment a card is
// answered - reading the snapshot adds no polling, because the page already holds the store open.
export const WAITING_COUNT_POLL_MS = 30_000;
export const FLEET_MANAGER_PATH = "/fleet-manager";

const noSubscribe = () => () => undefined;

export function useFleetManagerWaitingCount(pathname: string): number {
  const onPage = pathname === FLEET_MANAGER_PATH || pathname.startsWith(`${FLEET_MANAGER_PATH}/`);
  const live = useSyncExternalStore(
    onPage ? fleetManagerPageStore.subscribe : noSubscribe,
    fleetManagerPageStore.getSnapshot,
    fleetManagerPageStore.getSnapshot,
  );
  const [polled, setPolled] = useState(0);

  useEffect(() => {
    if (onPage) return;
    const controller = new AbortController();
    const poll = () =>
      void getFleetManagerPage(controller.signal).then(
        (page) => setPolled(page.waitingCount),
        // A failed read leaves the last count the Gateway gave; the page itself shows the Gateway's error.
        () => undefined,
      );
    poll();
    const id = window.setInterval(poll, WAITING_COUNT_POLL_MS);
    return () => {
      controller.abort();
      window.clearInterval(id);
    };
  }, [onPage, pathname]);

  return onPage && live.data !== null ? live.data.waitingCount : polled;
}
