import { createPollingStore } from "@devthrottle/client-core/polling/pollingStore";
import { getFleetStanding, type FleetStanding } from "@devthrottle/client-core/fleetmanager/standingClient";

// The owner's lessons and standing preferences (issue #3559, part 4): GET /gateway/fleet-manager/standing. Read only
// while the Fleet Manager page is open, slowly - the list changes a few times a week - and at once after each change
// (refreshNow). Kept apart from the page answer, which is polled every few seconds wherever the Cockpit is open.
export const FLEET_STANDING_POLL_MS = 30000;

export const fleetStandingStore = createPollingStore<FleetStanding>({
  fetcher: (signal) => getFleetStanding(signal),
  intervalMs: FLEET_STANDING_POLL_MS,
});
