// The two messages between a HOST APPLICATION and the chrome-less reports page it embeds
// (dev reports mission, phase 4, issue #3019). One file, so the page and the host cannot drift:
// the Director reads these names from here, and there is no second copy to update.
//
// The page has no credential of its own. It is the Cockpit's own route, loaded by the Director in
// WebView2 in a browser profile that has never enrolled, so the only way it can read anything is for
// the host to hand it the key the host already holds. That makes the exchange below the whole of the
// page's sign-in, and there is no other way in: no enrollment, no cookie, no key in the address.
//
//   page -> host   { "kind": "dev-report-host-ready", "sessionId": "<the route's session>" }
//   host -> page   { "kind": "dev-report-host-key", "key": "<the host's Gateway key>",
//                    "sessionId": "<the same session>" }
//
// The page posts READY on mount and the host answers with the KEY. The session identifier travels in
// both directions so that a host driving more than one pane answers the pane that asked, and so that
// the page can refuse a key minted for a different session rather than read another session's reports
// with it.
//
// FOR THE HOST: the key message must arrive as an OBJECT, so a WebView2 host sends it with
// PostWebMessageAsJson, not PostWebMessageAsString - the string form arrives as a string and
// isHostKeyMessage below refuses it, which on screen looks exactly like a host that never answered.
// The page's own READY message is posted with window.chrome.webview.postMessage, which the host reads
// from WebMessageReceivedEventArgs as JSON.

/** What the page posts to the host on mount, to say it is up and waiting for a key. */
export const HOST_READY_MESSAGE = "dev-report-host-ready";

/** What the host posts back, carrying the Gateway key the page uses as its Bearer. */
export const HOST_KEY_MESSAGE = "dev-report-host-key";

/**
 * How long the page waits for the host's answer before it says in plain words that no key arrived.
 * Ten seconds: long enough for a host that is still starting up, short enough that a pane wired to
 * nothing says so while the person is still looking at it. Waiting for ever would leave "Loading..."
 * on screen as the permanent state, which reads as a broken page rather than as a page nobody answered.
 */
export const HOST_KEY_WAIT_MS = 10000;

/** What the page posts to the host. */
export interface HostReadyMessage {
  kind: typeof HOST_READY_MESSAGE;
  sessionId: string;
}

/** What the host posts to the page. */
export interface HostKeyMessage {
  kind: typeof HOST_KEY_MESSAGE;
  key: string;
  sessionId: string;
}

/** True when this is a well-formed host key message. Says nothing about WHICH session it is for. */
export function isHostKeyMessage(data: unknown): data is HostKeyMessage {
  if (typeof data !== "object" || data === null) return false;
  const message = data as Partial<HostKeyMessage>;
  return (
    message.kind === HOST_KEY_MESSAGE &&
    typeof message.key === "string" &&
    message.key.length > 0 &&
    typeof message.sessionId === "string" &&
    message.sessionId.length > 0
  );
}

/** The WebView2 bridge object a host puts on the window, when there is one. */
export interface HostBridge {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: MessageEvent) => void): void;
  removeEventListener(type: "message", listener: (event: MessageEvent) => void): void;
}

/**
 * The host's bridge, or null in an ordinary browser. WebView2 puts it at window.chrome.webview and
 * delivers the host's messages there, not on the window - so the page listens on both and this is how
 * it finds the one that exists.
 */
export function hostBridge(win: Window): HostBridge | null {
  const chrome = (win as unknown as { chrome?: { webview?: HostBridge } }).chrome;
  const bridge = chrome?.webview;
  return bridge && typeof bridge.postMessage === "function" && typeof bridge.addEventListener === "function"
    ? bridge
    : null;
}
