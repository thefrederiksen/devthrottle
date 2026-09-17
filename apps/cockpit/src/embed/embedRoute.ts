// Where the chrome-less reports page lives, and the one question the app's startup asks about it
// (dev reports mission, phase 4, issue #3019).
//
// The Cockpit's startup, in main.tsx, runs for EVERY route: it mirrors the device key into a cookie,
// installs the global error channel, and registers the push service worker. A page a host embeds is not
// a browser somebody installed the Cockpit into, and one of those startup steps outlives the pane:
// a registered service worker stays in that browser profile after the pane is closed and intercepts
// every later request to this origin, and a push subscription belongs to a person's own browser, not to
// a pane inside a desktop application. So the pane skips that one step. Everything else still runs -
// they are all no-ops or wanted there.
//
// The path is written here once and read by both the router and the startup, so a page that moves cannot
// leave the startup testing an address that no longer exists.

/** The path prefix of the chrome-less reports page. The session identifier is the segment after it. */
export const EMBED_REPORTS_PATH_PREFIX = "/embed/reports/";

/**
 * Whether this address is a page a host application embeds, rather than the Cockpit somebody opened.
 * Read at startup from the document's own path, before the router exists.
 */
export function isEmbeddedPanePath(pathname: string): boolean {
  return pathname.startsWith(EMBED_REPORTS_PATH_PREFIX);
}
