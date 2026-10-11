import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it, vi } from "vitest";

// Behaviour test for the phone's push handler (public/push-sw.js). The handler runs in the service-worker global scope,
// so its source is loaded into a faked `self` and its listeners are driven as the browser would. These hold down the
// Secret Handoff push (issue #2943, phase 5): a waiting transfer draws its own notification in the Gateway's words,
// never disturbs the "needs you" dot, and a tap steers an open app window to the approval card - while the dot's tap
// still only brings the app forward.

const swSource = readFileSync(fileURLToPath(new URL("../../public/push-sw.js", import.meta.url)), "utf8");

interface Shown {
  title: string;
  options: Record<string, unknown>;
  closed: boolean;
  close: () => void;
}

interface FakeClient {
  url: string;
  focused: boolean;
  navigatedTo?: string;
  focus: () => Promise<FakeClient>;
  navigate: (url: string) => Promise<FakeClient>;
}

function appWindow(url: string): FakeClient {
  return {
    url,
    focused: false,
    focus() {
      this.focused = true;
      return Promise.resolve(this);
    },
    navigate(to: string) {
      this.navigatedTo = to;
      return Promise.resolve(this);
    },
  };
}

function loadWorker(clients: FakeClient[] = []) {
  const listeners: Record<string, ((event: unknown) => void)[]> = {};
  const shown: Shown[] = [];
  const opened: string[] = [];
  const self = {
    addEventListener(type: string, fn: (event: unknown) => void) {
      (listeners[type] ??= []).push(fn);
    },
    registration: {
      showNotification(title: string, options: Record<string, unknown>) {
        shown.push({
          title,
          options,
          closed: false,
          close() {
            this.closed = true;
          },
        });
        return Promise.resolve();
      },
      getNotifications({ tag }: { tag: string }) {
        return Promise.resolve(shown.filter((n) => !n.closed && n.options.tag === tag));
      },
    },
    navigator: { setAppBadge: vi.fn(async () => undefined), clearAppBadge: vi.fn(async () => undefined) },
    clients: {
      matchAll: async () => clients,
      openWindow: async (url: string) => {
        opened.push(url);
        return undefined;
      },
    },
  };
  new Function("self", swSource)(self);

  async function dispatch(type: string, event: Record<string, unknown>) {
    const waits: Promise<unknown>[] = [];
    for (const fn of listeners[type] ?? []) fn({ ...event, waitUntil: (p: Promise<unknown>) => waits.push(p) });
    await Promise.all(waits);
  }
  return { dispatch, shown, opened, self };
}

const SECRET_PUSH = {
  kind: "secret-transfer",
  title: "Secret transfer waiting",
  body: "Approve sending vercel-bypass from SOREN_NORTH to devthrottle-mac-mini?",
  tag: "devthrottle-secret-transfer-tr-1",
  url: "/mobile/secret-transfers",
};

describe("phone push handler", () => {
  it("a secret transfer push draws its own audible notification in the Gateway's words", async () => {
    const w = loadWorker();
    await w.dispatch("push", { data: { json: () => SECRET_PUSH } });
    expect(w.shown).toHaveLength(1);
    expect(w.shown[0].title).toBe("Secret transfer waiting");
    expect(w.shown[0].options.body).toBe(SECRET_PUSH.body);
    expect(w.shown[0].options.tag).toBe("devthrottle-secret-transfer-tr-1");
    expect(w.shown[0].options.silent).toBe(false);
  });

  it("a secret transfer push leaves the needs-you dot alone", async () => {
    const w = loadWorker();
    await w.dispatch("push", { data: { json: () => ({ count: 2 }) } });
    await w.dispatch("push", { data: { json: () => SECRET_PUSH } });
    expect(w.shown[0].options.tag).toBe("devthrottle-needs-you");
    expect(w.shown[0].closed).toBe(false);
    expect(w.self.navigator.clearAppBadge).not.toHaveBeenCalled();
  });

  it("tapping a secret transfer steers the open app to the approval card", async () => {
    const app = appWindow("https://gw.example/mobile/");
    const w = loadWorker([app]);
    await w.dispatch("push", { data: { json: () => SECRET_PUSH } });
    await w.dispatch("notificationclick", { notification: { close: () => undefined, data: w.shown[0].options.data } });
    expect(app.navigatedTo).toBe("/mobile/secret-transfers");
    expect(app.focused).toBe(true);
  });

  it("tapping a secret transfer with the app closed opens the approval card", async () => {
    const w = loadWorker();
    await w.dispatch("push", { data: { json: () => SECRET_PUSH } });
    await w.dispatch("notificationclick", { notification: { close: () => undefined, data: w.shown[0].options.data } });
    expect(w.opened).toEqual(["/mobile/secret-transfers"]);
  });

  it("tapping the needs-you dot only brings the app forward", async () => {
    const app = appWindow("https://gw.example/mobile/session/abc");
    const w = loadWorker([app]);
    await w.dispatch("push", { data: { json: () => ({ count: 1 }) } });
    await w.dispatch("notificationclick", { notification: { close: () => undefined, data: w.shown[0].options.data } });
    expect(app.focused).toBe(true);
    expect(app.navigatedTo).toBeUndefined();
  });
});
