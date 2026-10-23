import { useEffect } from "react";
import { useLinkRequests } from "@devthrottle/client-core/fleet/LinkRequestsPanel";

/** The tag prefix of a message link request's notification. The Gateway writes it
 *  (MessageLinkRequestPushNotifier.TagPrefix); the two must stay the same. */
export const LINK_REQUEST_TAG_PREFIX = "devthrottle-link-request-";

/**
 * Close the notification of every request that no longer waits (issue #4373): a request answered at the Director or in
 * the Cockpit must not stay on the phone's lock screen asking for a tap that can no longer do anything. Runs on every
 * read of the list, inside a LinkRequestsPanelProvider. Renders nothing.
 */
export function CloseAnsweredLinkRequestNotifications() {
  const { pending, loaded } = useLinkRequests();
  useEffect(() => {
    // Before the first read the list is empty because nothing is known yet, not because nothing waits.
    if (!loaded) return;
    void closeAnsweredNotifications(new Set(pending.map((r) => r.requestId)));
  }, [pending, loaded]);
  return null;
}

/** Close every link request notification whose request is not in `waiting`. Returns how many were closed. */
export async function closeAnsweredNotifications(waiting: ReadonlySet<string>): Promise<number> {
  if (typeof navigator === "undefined" || !("serviceWorker" in navigator)) return 0;
  const registration = await navigator.serviceWorker.getRegistration();
  if (!registration) return 0;
  const shown = await registration.getNotifications();
  let closed = 0;
  for (const n of shown) {
    if (!n.tag.startsWith(LINK_REQUEST_TAG_PREFIX)) continue;
    if (waiting.has(n.tag.slice(LINK_REQUEST_TAG_PREFIX.length))) continue;
    n.close();
    closed++;
  }
  return closed;
}
