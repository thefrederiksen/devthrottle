// The Gateway credential a HOST APPLICATION hands to a page it embeds - held in memory, and nowhere else.
//
// Every other page in this workspace authenticates with the per-device key this browser obtained at
// enrollment (deviceKey.ts), which lives in the browser's storage. The Director's reports pane is not that
// page: it is the Cockpit's /embed/reports/{sessionId} route loaded in WebView2, in a browser profile that
// has never enrolled and never signs in. The Director already holds a credential the Gateway accepts - its
// own per-device key, or the local machine token - so it hands that key to the page over the WebView2
// message bridge for the life of the pane, and the page uses it as the Bearer.
//
// WHY A MODULE VARIABLE AND NOTHING ELSE. localStorage, sessionStorage, a cookie and the URL all outlive
// the pane. A credential the HOST owns, left in any of them, is on that machine afterwards for any later
// page on this origin to read, and neither the host nor the person who closed the pane would ever know.
// So this value is written to one variable, it dies with the document, and the embed route clears it on
// unmount as well. Nothing in this file writes to storage; ensureGatewayCookie in api/client.ts reads the
// DEVICE key directly, never gatewayToken(), so the host's key is never mirrored into the cookie either.
//
// It carries no authority of its own: it is whatever credential the host already had, used by the page the
// host chose to load. A browser that no host embeds never has one, and there is no way for a page to ask
// for one - only a host can supply it.

let hostKey: string | null = null;

/**
 * Hold the host's Gateway key for this page, or forget it with null. In memory only.
 * An empty string is the same as null: a host that supplies nothing has supplied no key.
 */
export function setHostSuppliedKey(key: string | null): void {
  hostKey = key !== null && key.length > 0 ? key : null;
}

/** The host's Gateway key, or null when no host has supplied one. */
export function hostSuppliedKey(): string | null {
  return hostKey;
}
