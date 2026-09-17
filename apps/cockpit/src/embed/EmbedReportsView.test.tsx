// @vitest-environment jsdom
// The chrome-less reports page a host application embeds (dev reports mission, phase 4, issue #3019).
//
// The page has no credential of its own, so everything worth proving here is about the one exchange that
// gives it one. Each test below asserts the REAL consequence rather than the internal state: the header
// that actually went to the Gateway, and whether any request went at all. A page that held a key but sent
// the wrong one, or sent one it should have refused, would pass a test that only read a variable back.
//
// The failure these tests exist to prevent is a quiet fallback. This route is served by the same Gateway
// as the rest of the Cockpit, so an ordinary signed-in browser can open its address; if the page ever read
// the browser's own device key when no host answered, that address would become a second Reports page
// running as whoever is signed in there, and nothing on screen would say so.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { authHeaders } from "@devthrottle/client-core/api/client";
import { hostSuppliedKey, setHostSuppliedKey } from "@devthrottle/client-core/auth/hostKey";
import { EmbedReportsView } from "./EmbedReportsView";
import { HOST_KEY_MESSAGE, HOST_KEY_WAIT_MS, HOST_READY_MESSAGE } from "./hostBridge";

const SESSION = "session-odd-41";
const HOST_KEY = "host-key-odd-73";

// The WebView2 bridge a host puts on the window. It records what the page posts and lets a test play the
// host's answer back through the same channel WebView2 delivers on.
interface FakeBridge {
  posted: unknown[];
  listeners: ((event: MessageEvent) => void)[];
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: MessageEvent) => void): void;
  removeEventListener(type: "message", listener: (event: MessageEvent) => void): void;
  answer(data: unknown): void;
}

function installBridge(): FakeBridge {
  const bridge: FakeBridge = {
    posted: [],
    listeners: [],
    postMessage(message) {
      bridge.posted.push(message);
    },
    addEventListener(_type, listener) {
      bridge.listeners.push(listener);
    },
    removeEventListener(_type, listener) {
      bridge.listeners = bridge.listeners.filter((each) => each !== listener);
    },
    answer(data) {
      // WebView2 delivers the host's message on the bridge, with no source window.
      const event = new MessageEvent("message", { data });
      act(() => {
        for (const listener of [...bridge.listeners]) listener(event);
      });
    },
  };
  Object.defineProperty(window, "chrome", { value: { webview: bridge }, configurable: true, writable: true });
  return bridge;
}

const fetchMock = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>();

function gatewayCalls(): { url: string; authorization: string | undefined }[] {
  return fetchMock.mock.calls.map(([input, init]) => ({
    url: String(input),
    authorization: (init?.headers as Record<string, string> | undefined)?.Authorization,
  }));
}

function renderPage(sessionId = SESSION) {
  return render(
    <MemoryRouter initialEntries={[`/embed/reports/${sessionId}`]}>
      <Routes>
        <Route path="/embed/reports/:sessionId" element={<EmbedReportsView />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  setHostSuppliedKey(null);
  fetchMock.mockReset();
  fetchMock.mockResolvedValue(new Response(JSON.stringify({ reports: [] }), { status: 200 }));
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  setHostSuppliedKey(null);
  Reflect.deleteProperty(window as unknown as Record<string, unknown>, "chrome");
});

describe("the embedded reports page", () => {
  it("shows Loading from the first paint and asks the Gateway for nothing until a host supplies a key", () => {
    const bridge = installBridge();

    renderPage();

    // Synchronous: this is the first render, before any effect could have fetched anything.
    expect(screen.getByTestId("embed-reports-waiting").textContent).toContain("Loading...");
    expect(fetchMock).not.toHaveBeenCalled();
    // And it has told the host it is up and waiting, naming the session it is showing.
    expect(bridge.posted).toEqual([{ kind: HOST_READY_MESSAGE, sessionId: SESSION }]);
  });

  it("uses the key the host supplies as the Bearer on the report calls", async () => {
    const bridge = installBridge();
    renderPage();

    bridge.answer({ kind: HOST_KEY_MESSAGE, key: HOST_KEY, sessionId: SESSION });

    await screen.findByTestId("dev-report-list");
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const calls = gatewayCalls();
    expect(calls[0].url).toContain(`/dev-reports?sessionId=${SESSION}`);
    expect(calls[0].authorization).toBe(`Bearer ${HOST_KEY}`);
    // Every call, not just the first: nothing in the page builds a Bearer of its own.
    for (const call of calls) expect(call.authorization).toBe(`Bearer ${HOST_KEY}`);
  });

  it("refuses a key minted for a different session, and still asks the Gateway for nothing", async () => {
    const bridge = installBridge();
    renderPage();

    bridge.answer({ kind: HOST_KEY_MESSAGE, key: HOST_KEY, sessionId: "a-different-session-odd-2" });

    await Promise.resolve();
    expect(screen.getByTestId("embed-reports-waiting")).toBeTruthy();
    expect(hostSuppliedKey()).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("refuses a key posted by a frame inside the page, which is never the host", async () => {
    installBridge();
    renderPage();

    // The one sender that can exist inside this page and is not the host: the report frame, after a
    // report navigated it somewhere whose scripts run (CONTRACT.md section 4, rule 6).
    const frame = document.createElement("iframe");
    document.body.appendChild(frame);
    act(() => {
      window.dispatchEvent(
        new MessageEvent("message", {
          data: { kind: HOST_KEY_MESSAGE, key: "key-from-a-frame-odd-8", sessionId: SESSION },
          source: frame.contentWindow,
        }),
      );
    });

    await Promise.resolve();
    expect(screen.getByTestId("embed-reports-waiting")).toBeTruthy();
    expect(hostSuppliedKey()).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
    frame.remove();
  });

  it("keeps the first key the host supplied - a later message cannot replace it", async () => {
    const bridge = installBridge();
    renderPage();

    bridge.answer({ kind: HOST_KEY_MESSAGE, key: HOST_KEY, sessionId: SESSION });
    await screen.findByTestId("dev-report-list");

    bridge.answer({ kind: HOST_KEY_MESSAGE, key: "a-second-key-odd-6", sessionId: SESSION });

    expect(hostSuppliedKey()).toBe(HOST_KEY);
    expect((authHeaders() as Record<string, string>).Authorization).toBe(`Bearer ${HOST_KEY}`);
  });

  it("says in plain words that no key arrived after ten seconds, and still asks the Gateway for nothing", () => {
    vi.useFakeTimers();
    installBridge();
    renderPage();

    act(() => vi.advanceTimersByTime(HOST_KEY_WAIT_MS));

    const refusal = screen.getByTestId("embed-reports-no-host-key").textContent ?? "";
    expect(refusal).toContain("only for an application that embeds it");
    expect(refusal).toContain("has no sign-in of its own");
    expect(screen.queryByTestId("dev-report-list")).toBeNull();
    expect(hostSuppliedKey()).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("works with no bridge at all - an ordinary browser opening this address reads nothing", () => {
    vi.useFakeTimers();

    renderPage();

    expect(screen.getByTestId("embed-reports-waiting")).toBeTruthy();
    act(() => vi.advanceTimersByTime(HOST_KEY_WAIT_MS));
    expect(screen.getByTestId("embed-reports-no-host-key")).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("forgets the host key when the pane goes away", async () => {
    const bridge = installBridge();
    const view = renderPage();

    bridge.answer({ kind: HOST_KEY_MESSAGE, key: HOST_KEY, sessionId: SESSION });
    await screen.findByTestId("dev-report-list");
    expect(hostSuppliedKey()).toBe(HOST_KEY);

    view.unmount();

    expect(hostSuppliedKey()).toBeNull();
    expect((authHeaders() as Record<string, string>).Authorization).toBeUndefined();
    // And the page let go of the host's channel as well.
    expect(bridge.listeners).toHaveLength(0);
  });

  it("never writes the host key to localStorage, sessionStorage or a cookie", async () => {
    const bridge = installBridge();
    renderPage();

    bridge.answer({ kind: HOST_KEY_MESSAGE, key: HOST_KEY, sessionId: SESSION });
    await screen.findByTestId("dev-report-list");

    expect(JSON.stringify(window.localStorage)).not.toContain(HOST_KEY);
    expect(JSON.stringify(window.sessionStorage)).not.toContain(HOST_KEY);
    expect(document.cookie).not.toContain(HOST_KEY);
  });
});
