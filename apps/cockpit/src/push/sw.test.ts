import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it, vi } from "vitest";

// Behavior test for the Cockpit push service worker (public/sw.js, issue #1257). The worker runs in the
// service-worker global scope, so we load its source into a faked `self` (registration / clients /
// navigator) plus a stubbed `fetch`, capture the listeners it registers, and drive them exactly as the
// browser would. This proves the acceptance path without a live push service:
//   - a "needs you" push draws one desktop notification with the right wording,
//   - the falling-edge zero closes the standing notification instead of drawing another,
//   - clicking the notification lands on the single waiting session (deep link) or the roster,
//   - the foreground clear message closes the notification.

const swSource = readFileSync(
  fileURLToPath(new URL("../../public/sw.js", import.meta.url)),
  "utf8",
);

interface ShownNotification {
  title: string;
  options: Record<string, unknown>;
  close: () => void;
  closed: boolean;
}

interface FakeWindowClient {
  url: string;
  focus: () => Promise<FakeWindowClient>;
  navigate?: (url: string) => Promise<FakeWindowClient>;
  focused: boolean;
  navigatedTo?: string;
}

function loadWorker(options: {
  sessions?: unknown;
  fetchOk?: boolean;
  fetchRejects?: boolean;
  clients?: FakeWindowClient[];
}) {
  const listeners: Record<string, ((event: unknown) => void)[]> = {};
  const shown: ShownNotification[] = [];
  const openedWindows: string[] = [];

  const fetchStub = vi.fn(async () => {
    if (options.fetchRejects) throw new Error("network down");
    return {
      ok: options.fetchOk !== false,
      json: async () => options.sessions ?? [],
    } as unknown as Response;
  });

  const self = {
    addEventListener(type: string, fn: (event: unknown) => void) {
      (listeners[type] ??= []).push(fn);
    },
    registration: {
      showNotification(title: string, opts: Record<string, unknown>) {
        const entry: ShownNotification = {
          title,
          options: opts,
          closed: false,
          close() {
            this.closed = true;
          },
        };
        shown.push(entry);
        return Promise.resolve();
      },
      getNotifications({ tag }: { tag: string }) {
        return Promise.resolve(shown.filter((n) => !n.closed && n.options.tag === tag));
      },
    },
    navigator: {
      setAppBadge: vi.fn(async () => undefined),
      clearAppBadge: vi.fn(async () => undefined),
    },
    clients: {
      matchAll: async () => options.clients ?? [],
      openWindow: async (url: string) => {
        openedWindows.push(url);
        return {} as FakeWindowClient;
      },
    },
  };

  // Run the worker source with `self` and `fetch` shadowing the globals it reaches for.
  // eslint-disable-next-line @typescript-eslint/no-implied-eval
  new Function("self", "fetch", swSource)(self, fetchStub);

  async function dispatch(type: string, event: Record<string, unknown>) {
    const waits: Promise<unknown>[] = [];
    const withWait = { ...event, waitUntil: (p: Promise<unknown>) => waits.push(p) };
    for (const fn of listeners[type] ?? []) fn(withWait);
    await Promise.all(waits);
  }

  return { dispatch, shown, openedWindows, fetchStub, self };
}

function pushEvent(count: number) {
  return { data: { json: () => ({ count }) } };
}

afterEach(() => vi.restoreAllMocks());

describe("cockpit push service worker", () => {
  it("draws one notification naming the count when a session needs you", async () => {
    const w = loadWorker({});
    await w.dispatch("push", pushEvent(2));
    expect(w.shown).toHaveLength(1);
    expect(w.shown[0].title).toBe("DevThrottle");
    expect(w.shown[0].options.body).toBe("2 sessions need you");
    expect(w.shown[0].options.tag).toBe("devthrottle-needs-you");
    expect((w.shown[0].options.data as { url: string }).url).toBe("/");
  });

  it("uses the singular wording for a single waiting session", async () => {
    const w = loadWorker({});
    await w.dispatch("push", pushEvent(1));
    expect(w.shown[0].options.body).toBe("1 session needs you");
  });

  it("closes the standing notification on the falling-edge zero without drawing a new one", async () => {
    const w = loadWorker({});
    await w.dispatch("push", pushEvent(3));
    expect(w.shown[0].closed).toBe(false);
    await w.dispatch("push", pushEvent(0));
    expect(w.shown).toHaveLength(1); // no second notification
    expect(w.shown[0].closed).toBe(true);
    expect(w.self.navigator.clearAppBadge).toHaveBeenCalled();
  });

  it("clicking deep-links to the one session that needs you", async () => {
    const w = loadWorker({
      sessions: [
        { sessionId: "s-1", triageBucket: "active" },
        { sessionId: "s-red", triageBucket: "needsYou" },
      ],
    });
    await w.dispatch("notificationclick", {
      notification: { close: () => undefined, data: { url: "/" } },
    });
    expect(w.fetchStub).toHaveBeenCalledWith("/sessions", expect.objectContaining({ credentials: "include" }));
    expect(w.openedWindows).toEqual(["/session/s-red"]);
  });

  it("clicking lands on the roster when several sessions need you", async () => {
    const w = loadWorker({
      sessions: [
        { sessionId: "a", triageBucket: "needsYou" },
        { sessionId: "b", triageBucket: "needsYou" },
      ],
    });
    await w.dispatch("notificationclick", {
      notification: { close: () => undefined, data: { url: "/" } },
    });
    expect(w.openedWindows).toEqual(["/"]);
  });

  it("clicking falls back to the roster when the roster read fails", async () => {
    const w = loadWorker({ fetchRejects: true });
    await w.dispatch("notificationclick", {
      notification: { close: () => undefined, data: { url: "/" } },
    });
    expect(w.openedWindows).toEqual(["/"]);
  });

  it("focuses an existing Cockpit tab instead of opening a new one", async () => {
    const cockpitTab: FakeWindowClient = {
      url: "https://gw.example/session/old",
      focused: false,
      focus() {
        this.focused = true;
        return Promise.resolve(this);
      },
      navigate(url: string) {
        this.navigatedTo = url;
        return Promise.resolve(this);
      },
    };
    const w = loadWorker({
      sessions: [{ sessionId: "s-red", triageBucket: "needsYou" }],
      clients: [cockpitTab],
    });
    await w.dispatch("notificationclick", {
      notification: { close: () => undefined, data: { url: "/" } },
    });
    expect(cockpitTab.focused).toBe(true);
    expect(cockpitTab.navigatedTo).toBe("/session/s-red");
    expect(w.openedWindows).toEqual([]); // reused the existing tab, did not open a second
  });

  it("does not treat the mobile app window as a Cockpit tab to reuse", async () => {
    // The mobile app serves at /mobile (re-based from /m); a Cockpit push must not hijack that window.
    const mobileTab: FakeWindowClient = {
      url: "https://gw.example/mobile/",
      focused: false,
      focus() {
        this.focused = true;
        return Promise.resolve(this);
      },
    };
    const w = loadWorker({
      sessions: [{ sessionId: "a", triageBucket: "needsYou" }, { sessionId: "b", triageBucket: "needsYou" }],
      clients: [mobileTab],
    });
    await w.dispatch("notificationclick", {
      notification: { close: () => undefined, data: { url: "/" } },
    });
    expect(mobileTab.focused).toBe(false);
    expect(w.openedWindows).toEqual(["/"]); // opened a Cockpit window rather than hijacking the /mobile tab
  });

  it("the foreground clear message closes the notification", async () => {
    const w = loadWorker({});
    await w.dispatch("push", pushEvent(2));
    await w.dispatch("message", { data: { type: "devthrottle-clear-needs-you" } });
    expect(w.shown[0].closed).toBe(true);
    expect(w.self.navigator.clearAppBadge).toHaveBeenCalled();
  });

  // Secret Handoff (issue #2943, phase 5): a transfer waiting for the owner arrives as its own push.
  const SECRET_PUSH = {
    kind: "secret-transfer",
    title: "Secret transfer waiting",
    body: "Approve sending vercel-bypass from SOREN_NORTH to devthrottle-mac-mini?",
    tag: "devthrottle-secret-transfer-tr-1",
    url: "/mobile/secret-transfers",
  };

  it("a secret transfer push draws its own notification in the Gateway's words", async () => {
    const w = loadWorker({});
    await w.dispatch("push", { data: { json: () => SECRET_PUSH } });
    expect(w.shown).toHaveLength(1);
    expect(w.shown[0].title).toBe("Secret transfer waiting");
    expect(w.shown[0].options.body).toBe(SECRET_PUSH.body);
    expect(w.shown[0].options.tag).toBe("devthrottle-secret-transfer-tr-1");
  });

  it("a secret transfer push never clears the needs-you notification", async () => {
    const w = loadWorker({});
    await w.dispatch("push", pushEvent(2));
    await w.dispatch("push", { data: { json: () => SECRET_PUSH } });
    expect(w.shown[0].closed).toBe(false);
    expect(w.self.navigator.clearAppBadge).not.toHaveBeenCalled();
  });

  it("clicking a secret transfer notification opens the Cockpit's sessions screen, not the phone page", async () => {
    const w = loadWorker({});
    await w.dispatch("push", { data: { json: () => SECRET_PUSH } });
    await w.dispatch("notificationclick", { notification: { close: () => undefined, data: w.shown[0].options.data } });
    expect(w.fetchStub).not.toHaveBeenCalled();
    expect(w.openedWindows).toEqual(["/sessions"]);
  });
});
