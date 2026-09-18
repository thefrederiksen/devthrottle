import { createPollingStore } from "@devthrottle/client-core/polling/pollingStore";
import { getFleetManagerPage, type FleetManagerPage } from "@devthrottle/client-core/fleetmanager/pageClient";

// The Fleet Manager page's one live read (the Fleet Manager mission, step 6): GET /gateway/fleet-manager/page, the
// cards, the right panel and the badge count, folded on the Gateway. Polled like the session list - often, and
// quiet while the tab is hidden - so the panel stays live while the Fleet Manager is thinking. The store lives here,
// outside the view, so the rail's badge and the page read the same snapshot and cannot disagree.
export const FLEET_MANAGER_PAGE_POLL_MS = 3000;

export const fleetManagerPageStore = createPollingStore<FleetManagerPage>({
  fetcher: (signal) => getFleetManagerPage(signal),
  intervalMs: FLEET_MANAGER_PAGE_POLL_MS,
});
