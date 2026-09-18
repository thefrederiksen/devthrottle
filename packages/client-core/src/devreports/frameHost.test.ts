// @vitest-environment jsdom
//
// The frame host's trust rules (CONTRACT.md section 4, rules 1-6), tested with exact events. The frame's
// window and ports are stand-ins: jsdom does not run a sandboxed srcdoc document, and what these tests
// check is what the HOST accepts and refuses, which does not depend on a real page. The real page in a real
// browser is the job of the browser proof and of the hostile-report test on the built apps.

import { beforeEach, describe, expect, it } from "vitest";
import { buildFrameDocument, DevReportFrameHost, type HostPort } from "./frameHost";
import { DEV_REPORT_CHANNEL, emptyPageState, type DevReportItem, type DevReportPageState } from "./protocol";
import { APP_DEV_REPORT_THEME } from "./theme";

class FakePort implements HostPort {
  sent: Array<{ type: string; payload: unknown }> = [];
  closed = false;
  onmessage: ((event: MessageEvent) => void) | null = null;
  postMessage(message: unknown): void {
    this.sent.push(message as { type: string; payload: unknown });
  }
  close(): void {
    this.closed = true;
  }
  /** A message from the page over this port. */
  emit(data: unknown): void {
    this.onmessage?.({ data } as MessageEvent);
  }
}

const frameWindow = { name: "the frame's window" };
const otherWindow = { name: "some other window" };

function envelope(type: string, payload: unknown, token?: string) {
  return { channel: DEV_REPORT_CHANNEL, version: 1, type, payload, ...(token === undefined ? {} : { token }) };
}

const note: DevReportItem = {
  id: "n1",
  kind: "note",
  text: "This number is wrong",
  anchor: { type: "table-cell", selector: "#t > tr > td", quote: "42", rowLabel: "Gateway", columnLabel: "Failures" },
};

interface Harness {
  host: DevReportFrameHost;
  refused: string[];
  sends: DevReportItem[][];
  states: DevReportPageState[];
  connected: boolean[];
  restoreWith: { state: DevReportPageState };
}

let counter = 0;
function makeHost(): Harness {
  const harness = {
    refused: [] as string[],
    sends: [] as DevReportItem[][],
    states: [] as DevReportPageState[],
    connected: [] as boolean[],
    restoreWith: { state: emptyPageState() },
  } as Harness;
  // A container that is not in the document, so jsdom starts no browsing context of its own for the frame.
  const container = document.createElement("div");
  harness.host = new DevReportFrameHost({
    container,
    title: "Dev report",
    window,
    script: "window.__notes = true;",
    theme: APP_DEV_REPORT_THEME,
    random: () => (++counter).toString(16).padStart(8, "0"),
    restoreState: () => harness.restoreWith.state,
    onStateChanged: (s) => harness.states.push(s),
    onSend: (items) => harness.sends.push(items),
    onConnectedChange: (c) => harness.connected.push(c),
    onRefused: (why) => harness.refused.push(why),
  });
  Object.defineProperty(harness.host.frame, "contentWindow", { value: frameWindow });
  return harness;
}

/** The token the host put in the frame's document at its last load. */
function currentToken(host: DevReportFrameHost): string {
  const match = /data-dev-report-token="([0-9a-f]+)"/.exec(host.frame.srcdoc);
  if (!match) throw new Error("no token in the frame document");
  return match[1];
}

function ready(h: Harness, token: string, ports: unknown[], source: unknown = frameWindow): void {
  h.host.handleWindowMessage({ source, data: envelope("ready", { questionIds: ["q1"] }, token), ports });
}

/** Loads the report and completes the load the host caused, as the browser would. */
function loadReport(h: Harness, html = "<p>report</p>"): void {
  h.host.load(html);
  h.host.handleFrameLoad();
}

beforeEach(() => {
  counter = 0;
});

describe("buildFrameDocument", () => {
  it("writes the host's head first and the report's bytes after it, untouched", () => {
    const report = "<!-- <head><meta http-equiv=\"Content-Security-Policy\" content=\"script-src *\"> --><!doctype html><html><head><title>R</title></head><body><p>x</p></body></html>";
    const doc = buildFrameDocument("var a = 1;", report, "abc123", "def456", APP_DEV_REPORT_THEME);
    const headEnd = doc.indexOf("</head>");
    expect(doc.startsWith("<!doctype html><html><head><meta http-equiv=\"Content-Security-Policy\"")).toBe(true);
    expect(doc.slice(0, headEnd)).toContain("script-src 'nonce-abc123'");
    expect(doc.slice(0, headEnd)).toContain('<script nonce="abc123" data-dev-report-token="def456"');
    expect(doc.slice(headEnd + "</head>".length)).toBe(report);
  });

  it("escapes the theme into its attribute so it cannot end the element", () => {
    const doc = buildFrameDocument("", "", "aa", "bb", APP_DEV_REPORT_THEME);
    const attr = /data-dev-report-theme="([^"]*)"/.exec(doc);
    expect(attr).not.toBeNull();
    const decoded = attr![1].replace(/&quot;/g, '"').replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&amp;/g, "&");
    expect(JSON.parse(decoded)).toEqual(APP_DEV_REPORT_THEME);
  });

  it("refuses a nonce or token that is not hexadecimal, and a script that would close its own element", () => {
    expect(() => buildFrameDocument("", "", "a\" onload=\"x", "bb", APP_DEV_REPORT_THEME)).toThrow();
    expect(() => buildFrameDocument("", "", "aa", "b b", APP_DEV_REPORT_THEME)).toThrow();
    expect(() => buildFrameDocument("x = '</script>'", "", "aa", "bb", APP_DEV_REPORT_THEME)).toThrow();
  });
});

describe("the frame", () => {
  it("is sandboxed with allow-scripts only, and gets a fresh nonce and token on every load", () => {
    const h = makeHost();
    expect(h.host.frame.getAttribute("sandbox")).toBe("allow-scripts");
    h.host.load("<p>one</p>");
    const first = h.host.frame.srcdoc;
    const firstToken = currentToken(h.host);
    h.host.load("<p>one</p>");
    const secondToken = currentToken(h.host);
    expect(secondToken).not.toBe(firstToken);
    expect(/nonce-([0-9a-f]+)/.exec(h.host.frame.srcdoc)![1]).not.toBe(/nonce-([0-9a-f]+)/.exec(first)![1]);
  });
});

describe("the ready on the window", () => {
  it("is accepted once with the current token and one port, and the page is restored over that port", () => {
    const h = makeHost();
    const saved = { ...emptyPageState(), scroll: { x: 0, y: 900 } };
    h.restoreWith.state = saved;
    loadReport(h);
    const port = new FakePort();
    ready(h, currentToken(h.host), [port]);
    expect(h.host.connected).toBe(true);
    expect(port.sent).toEqual([{ channel: DEV_REPORT_CHANNEL, version: 1, type: "restore", payload: { state: saved } }]);
    expect(h.refused).toEqual([]);
  });

  it("forgets the token after the first ready, so a second ready with the same token is refused", () => {
    const h = makeHost();
    loadReport(h);
    const token = currentToken(h.host);
    const first = new FakePort();
    ready(h, token, [first]);
    const second = new FakePort();
    ready(h, token, [second]);
    expect(h.refused).toHaveLength(1);
    expect(second.sent).toEqual([]);
    expect(first.closed).toBe(false);
    // The accepted port is still the one in use.
    expect(h.host.pushStatus([{ id: "n1", status: "held", statusLabel: "x" }])).toBe(true);
    expect(first.sent.at(-1)?.type).toBe("status");
  });

  it("refuses a wrong token and a missing token", () => {
    const h = makeHost();
    loadReport(h);
    ready(h, "0000dead", [new FakePort()]);
    h.host.handleWindowMessage({ source: frameWindow, data: envelope("ready", { questionIds: [] }), ports: [new FakePort()] });
    expect(h.host.connected).toBe(false);
    expect(h.refused).toHaveLength(2);
  });

  it("refuses a ready with no port or with two ports", () => {
    const h = makeHost();
    loadReport(h);
    const token = currentToken(h.host);
    ready(h, token, []);
    ready(h, token, [new FakePort(), new FakePort()]);
    expect(h.host.connected).toBe(false);
    expect(h.refused).toHaveLength(2);
  });

  it("ignores a ready from any window other than the frame's", () => {
    const h = makeHost();
    loadReport(h);
    ready(h, currentToken(h.host), [new FakePort()], otherWindow);
    expect(h.host.connected).toBe(false);
  });

  it("refuses every other message on the window, before and after the ready", () => {
    const h = makeHost();
    loadReport(h);
    const token = currentToken(h.host);
    h.host.handleWindowMessage({ source: frameWindow, data: envelope("send", { items: [note] }, token), ports: [] });
    ready(h, token, [new FakePort()]);
    h.host.handleWindowMessage({ source: frameWindow, data: envelope("send", { items: [note] }, token), ports: [] });
    h.host.handleWindowMessage({ source: frameWindow, data: envelope("state-changed", { state: emptyPageState() }, token), ports: [] });
    expect(h.sends).toEqual([]);
    expect(h.states).toEqual([]);
    expect(h.refused).toHaveLength(3);
  });
});

describe("the port", () => {
  it("carries send and state-changed, each checked whole", () => {
    const h = makeHost();
    loadReport(h);
    const port = new FakePort();
    ready(h, currentToken(h.host), [port]);
    port.emit(envelope("send", { items: [note] }));
    port.emit(envelope("send", { items: [note, { id: "n2", kind: "note", text: "no anchor" }] }));
    const state = { ...emptyPageState(), queued: [note] };
    port.emit(envelope("state-changed", { state }));
    port.emit(envelope("state-changed", { state: { ...state, scroll: { x: "0", y: 0 } } }));
    port.emit({ ...envelope("send", { items: [note] }), version: 2 });
    expect(h.sends).toEqual([[note]]);
    expect(h.states).toEqual([state]);
    expect(h.refused).toHaveLength(3);
  });

  it("is closed by a frame load the host did not cause, and nothing more is heard from or sent to it", () => {
    const h = makeHost();
    loadReport(h);
    const port = new FakePort();
    ready(h, currentToken(h.host), [port]);
    h.host.handleFrameLoad();
    expect(port.closed).toBe(true);
    expect(h.host.connected).toBe(false);
    port.emit(envelope("send", { items: [note] }));
    expect(h.sends).toEqual([]);
    expect(h.host.pushStatus([{ id: "n1", status: "held", statusLabel: "x" }])).toBe(false);
    expect(h.host.pushReply({ id: "r1", text: "hi", at: "2026-09-17T10:00:00Z" })).toBe(false);
    expect(port.sent.map((m) => m.type)).toEqual(["restore"]);
  });

  it("stays closed to the navigated page, whose ready carries no current token, until the host loads the report again", () => {
    const h = makeHost();
    loadReport(h);
    const oldToken = currentToken(h.host);
    ready(h, oldToken, [new FakePort()]);
    h.host.handleFrameLoad(); // the report navigated the frame away
    ready(h, oldToken, [new FakePort()]);
    expect(h.host.connected).toBe(false);
    loadReport(h);
    const fresh = new FakePort();
    ready(h, currentToken(h.host), [fresh]);
    expect(h.host.connected).toBe(true);
    expect(fresh.sent.map((m) => m.type)).toEqual(["restore"]);
  });

  it("is closed when the host reloads the report, and the reload's own load does not count as a navigation", () => {
    const h = makeHost();
    loadReport(h);
    const port = new FakePort();
    ready(h, currentToken(h.host), [port]);
    h.host.load("<p>version 2</p>");
    expect(port.closed).toBe(true);
    h.host.handleFrameLoad();
    const next = new FakePort();
    ready(h, currentToken(h.host), [next]);
    expect(h.host.connected).toBe(true);
    expect(h.refused).toEqual([]);
  });

  it("is closed and the listener removed on dispose", () => {
    const h = makeHost();
    loadReport(h);
    const port = new FakePort();
    ready(h, currentToken(h.host), [port]);
    h.host.dispose();
    expect(port.closed).toBe(true);
    ready(h, currentToken(h.host), [new FakePort()]);
    expect(h.host.connected).toBe(false);
  });
});
