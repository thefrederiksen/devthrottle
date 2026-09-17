// @vitest-environment jsdom
//
// The controller joins the Gateway record, the frame host and the owner's draft. These tests drive it the way
// the page and the Gateway would: a ready and page messages over a stand-in port, and a fake of the four owner
// routes. Every status word and label below is deliberately odd, so a client-authored word would fail.

import { beforeEach, describe, expect, it } from "vitest";
import { GatewayError } from "../api/client";
import { DevReportController, type DevReportApi } from "./controller";
import type { DevReportDetail, DevReportSendUpdate } from "./devReportsClient";
import type { HostPort } from "./frameHost";
import { DEV_REPORT_CHANNEL, emptyPageState, type DevReportItem, type DevReportPageState } from "./protocol";
import { DevReportStateStore, type StateStorage } from "./stateStore";
import { APP_DEV_REPORT_THEME } from "./theme";

class FakePort implements HostPort {
  sent: Array<{ type: string; payload: Record<string, unknown> }> = [];
  closed = false;
  onmessage: ((event: MessageEvent) => void) | null = null;
  postMessage(message: unknown): void {
    this.sent.push(message as { type: string; payload: Record<string, unknown> });
  }
  close(): void {
    this.closed = true;
  }
  emit(type: string, payload: unknown): void {
    this.onmessage?.({ data: { channel: DEV_REPORT_CHANNEL, version: 1, type, payload } } as MessageEvent);
  }
  ofType(type: string) {
    return this.sent.filter((m) => m.type === type).map((m) => m.payload);
  }
}

class MemoryStorage implements StateStorage {
  private readonly map = new Map<string, string>();
  get length() {
    return this.map.size;
  }
  key(i: number) {
    return Array.from(this.map.keys())[i] ?? null;
  }
  getItem(k: string) {
    return this.map.get(k) ?? null;
  }
  setItem(k: string, v: string) {
    this.map.set(k, v);
  }
  removeItem(k: string) {
    this.map.delete(k);
  }
  keys() {
    return Array.from(this.map.keys());
  }
}

const REPORT = "8f7b0c1e-0000-4000-8000-000000000001";

const note: DevReportItem = {
  id: "n1",
  kind: "note",
  text: "This number is wrong",
  anchor: { type: "table-cell", selector: "#t td", quote: "42", rowLabel: "Gateway", columnLabel: "Failures" },
};
const answer: DevReportItem = {
  id: "a1",
  kind: "answer",
  questionId: "deploy-window",
  question: "When should we deploy?",
  optionValue: "tonight",
  optionLabel: "Tonight",
  comment: "",
};

function detail(version: number, extra: Partial<DevReportDetail> = {}): DevReportDetail {
  return {
    report: {
      id: REPORT,
      sessionId: "s1",
      key: "C:/r.html",
      title: "A report",
      status: "waiting-on-you",
      version,
      publishedAtUtc: "2026-09-17T10:00:00Z",
      updatedAtUtc: "2026-09-17T10:00:00Z",
      sessionEnded: false,
      openItems: 0,
    },
    items: [],
    replies: [],
    ...extra,
  };
}

class FakeApi implements DevReportApi {
  current: DevReportDetail | null = detail(1);
  htmlCalls: number[] = [];
  sendCalls: DevReportItem[][] = [];
  sendResult: () => Promise<DevReportSendUpdate[]> = async () => [];
  async getDetail() {
    return this.current;
  }
  async getHtml(_id: string, version: number) {
    this.htmlCalls.push(version);
    return { html: `<p>version ${version}</p>`, version };
  }
  async send(_id: string, items: DevReportItem[]) {
    this.sendCalls.push(items);
    return this.sendResult();
  }
}

const frameWindow = { name: "frame" };
let counter = 0;

function makeController(api: FakeApi, storage: MemoryStorage) {
  const controller = new DevReportController({
    reportId: REPORT,
    api,
    store: new DevReportStateStore(storage),
    container: document.createElement("div"),
    window,
    script: "",
    theme: APP_DEV_REPORT_THEME,
    random: () => (++counter).toString(16).padStart(8, "0"),
  });
  Object.defineProperty(controller.host.frame, "contentWindow", { value: frameWindow });
  return controller;
}

/** The page starts: the load the host caused completes, then the script posts its ready with the current token. */
function pageReady(controller: DevReportController): FakePort {
  controller.host.handleFrameLoad();
  const token = /data-dev-report-token="([0-9a-f]+)"/.exec(controller.host.frame.srcdoc)![1];
  const port = new FakePort();
  controller.host.handleWindowMessage({
    source: frameWindow,
    data: { channel: DEV_REPORT_CHANNEL, version: 1, type: "ready", payload: { questionIds: [] }, token },
    ports: [port],
  });
  return port;
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

beforeEach(() => {
  counter = 0;
});

describe("state the host holds", () => {
  it("is restored after every ready, including after the frame reloads", async () => {
    const api = new FakeApi();
    const storage = new MemoryStorage();
    const controller = makeController(api, storage);
    await controller.refresh();
    const port = pageReady(controller);
    expect(port.ofType("restore")).toEqual([{ state: emptyPageState() }]);

    const typed: DevReportPageState = { ...emptyPageState(), draft: { anchor: note.anchor as never, text: "half-typ" }, scroll: { x: 0, y: 640 } };
    port.emit("state-changed", { state: typed });
    // The report navigates away and the host loads it again: the new page gets the same state back.
    controller.host.handleFrameLoad();
    controller.host.load("<p>version 1</p>");
    const again = pageReady(controller);
    expect(again.ofType("restore")).toEqual([{ state: typed }]);
  });

  it("survives an app reload: a new controller on the same storage restores it", async () => {
    const api = new FakeApi();
    const storage = new MemoryStorage();
    const first = makeController(api, storage);
    await first.refresh();
    const typed: DevReportPageState = { ...emptyPageState(), queued: [note], scroll: { x: 0, y: 300 } };
    pageReady(first).emit("state-changed", { state: typed });
    first.dispose();

    const second = makeController(api, storage);
    await second.refresh();
    expect(pageReady(second).ofType("restore")).toEqual([{ state: typed }]);
  });

  it("seeds a new version from the previous one when the agent republishes, and reloads the frame in place", async () => {
    const api = new FakeApi();
    const storage = new MemoryStorage();
    const controller = makeController(api, storage);
    await controller.refresh();
    const port = pageReady(controller);
    const typed: DevReportPageState = { ...emptyPageState(), queued: [answer], draft: { anchor: note.anchor as never, text: "keep me" }, scroll: { x: 0, y: 1200 } };
    port.emit("state-changed", { state: typed });
    const tokenBefore = controller.host.frame.srcdoc;

    api.current = detail(2);
    await controller.refresh();
    expect(api.htmlCalls).toEqual([1, 2]);
    expect(port.closed).toBe(true);
    expect(controller.host.frame.srcdoc).not.toBe(tokenBefore);
    expect(controller.getSnapshot().loadedVersion).toBe(2);
    const next = pageReady(controller);
    expect(next.ofType("restore")).toEqual([{ state: typed }]);

    // The new version's state is kept under the new version, and the old version's is dropped.
    next.emit("state-changed", { state: typed });
    expect(storage.keys()).toEqual([`devthrottle.dev-report-state.${REPORT}.2`]);

    // An app reload now, with nothing in memory, still finds it.
    const reloaded = makeController(api, storage);
    await reloaded.refresh();
    expect(pageReady(reloaded).ofType("restore")).toEqual([{ state: typed }]);
  });

  it("seeds from storage alone when the app opens a version it never saw", async () => {
    const storage = new MemoryStorage();
    const saved: DevReportPageState = { ...emptyPageState(), queued: [note] };
    new DevReportStateStore(storage).save(REPORT, 3, saved);
    const api = new FakeApi();
    api.current = detail(4);
    const controller = makeController(api, storage);
    await controller.refresh();
    expect(pageReady(controller).ofType("restore")).toEqual([{ state: saved }]);
  });
});

describe("send", () => {
  it("posts the page's items and answers each one with the Gateway's status and label, verbatim", async () => {
    const api = new FakeApi();
    const controller = makeController(api, new MemoryStorage());
    await controller.refresh();
    const port = pageReady(controller);
    const updates = [
      { id: "n1", status: "zz-held-QX", statusLabel: "Gateway words #1 (not a client label)" },
      { id: "a1", status: "refused", statusLabel: "This session has ended - odd words 7" },
    ];
    api.sendResult = async () => updates;
    port.emit("send", { items: [note, answer] });
    await flush();
    expect(api.sendCalls).toEqual([[note, answer]]);
    expect(port.ofType("status")).toEqual([{ updates }]);
    expect(controller.getSnapshot().sendError).toBeNull();
    expect(controller.getSnapshot().sending).toBe(false);
  });

  it("answers every item refused, with the failure as its label, when the request itself fails", async () => {
    const api = new FakeApi();
    const controller = makeController(api, new MemoryStorage());
    await controller.refresh();
    const port = pageReady(controller);
    api.sendResult = async () => {
      throw new TypeError("Failed to fetch");
    };
    port.emit("send", { items: [note, answer] });
    await flush();
    const [status] = port.ofType("status") as Array<{ updates: Array<{ id: string; status: string; statusLabel: string }> }>;
    expect(status.updates.map((u) => [u.id, u.status])).toEqual([
      ["n1", "refused"],
      ["a1", "refused"],
    ]);
    expect(status.updates[0].statusLabel).toContain("Failed to fetch");
    expect(controller.getSnapshot().sendError).toBe(status.updates[0].statusLabel);
  });

  it("carries a Gateway error's own sentence when the Gateway answered with a failure", async () => {
    const api = new FakeApi();
    const controller = makeController(api, new MemoryStorage());
    await controller.refresh();
    const port = pageReady(controller);
    api.sendResult = async () => {
      throw new GatewayError(500, "x", { reason: "Store fell over: odd sentence 42" });
    };
    port.emit("send", { items: [note] });
    await flush();
    const [status] = port.ofType("status") as Array<{ updates: Array<{ status: string; statusLabel: string }> }>;
    expect(status.updates[0].status).toBe("refused");
    expect(status.updates[0].statusLabel).toContain("Store fell over: odd sentence 42");
  });

  it("sends everything still queued from the conversation panel", async () => {
    const api = new FakeApi();
    const controller = makeController(api, new MemoryStorage());
    await controller.refresh();
    const port = pageReady(controller);
    port.emit("state-changed", { state: { ...emptyPageState(), queued: [{ ...note, pending: true }, { ...answer, statusLabel: "was refused" }] } });
    await controller.sendQueued();
    expect(api.sendCalls).toEqual([[note, answer]]);
  });
});

describe("live refresh", () => {
  it("pushes the Gateway's changed words for sent and waiting items, and new replies, verbatim", async () => {
    const api = new FakeApi();
    const controller = makeController(api, new MemoryStorage());
    await controller.refresh();
    const port = pageReady(controller);
    port.emit("state-changed", {
      state: {
        ...emptyPageState(),
        queued: [{ ...answer, pending: true }, { ...note, id: "n9" }],
        sent: [{ ...note, status: "held", statusLabel: "old words" }],
      },
    });
    api.current = detail(1, {
      items: [
        { ...note, status: "delivered~odd", statusLabel: "Delivered, says the Gateway (odd)", sentAtUtc: null, deliveredAtUtc: null },
        { ...answer, status: "held~odd", statusLabel: "Held, says the Gateway (odd)", sentAtUtc: null, deliveredAtUtc: null },
        { ...note, id: "n9", status: "delivered", statusLabel: "a different device's n9", sentAtUtc: null, deliveredAtUtc: null },
      ],
      replies: [{ id: "r-1", text: "Fixed - odd reply text", at: "2026-09-17T11:00:00Z" }],
    });
    await controller.refresh();
    expect(port.ofType("status")).toEqual([
      {
        updates: [
          { id: "n1", status: "delivered~odd", statusLabel: "Delivered, says the Gateway (odd)" },
          { id: "a1", status: "held~odd", statusLabel: "Held, says the Gateway (odd)" },
        ],
      },
    ]);
    expect(port.ofType("reply")).toEqual([{ reply: { id: "r-1", text: "Fixed - odd reply text", at: "2026-09-17T11:00:00Z" } }]);

    // Once the page holds those words, the next refresh pushes nothing.
    port.emit("state-changed", {
      state: {
        ...emptyPageState(),
        sent: [
          { ...note, status: "delivered~odd", statusLabel: "Delivered, says the Gateway (odd)" },
          { ...answer, status: "held~odd", statusLabel: "Held, says the Gateway (odd)" },
        ],
        replies: [{ id: "r-1", text: "Fixed - odd reply text", at: "2026-09-17T11:00:00Z" }],
      },
    });
    const before = port.sent.length;
    await controller.refresh();
    expect(port.sent.length).toBe(before);
  });

  it("marks the report not found when the Gateway answers 404", async () => {
    const api = new FakeApi();
    api.current = null;
    const controller = makeController(api, new MemoryStorage());
    await controller.refresh();
    expect(controller.getSnapshot().notFound).toBe(true);
    expect(api.htmlCalls).toEqual([]);
  });
});
