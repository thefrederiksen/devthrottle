// @vitest-environment jsdom
//
// Unit tests for the dev report note-taking script (dev-report-notes.js). The script is plain
// JavaScript injected into report pages, so the test loads the SAME file the apps inject - read from
// disk and run in this jsdom window - rather than a module copy of it.

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { beforeEach, describe, expect, it } from "vitest";

type Anchor = { type: string; selector: string; quote: string; rowLabel?: string; columnLabel?: string; label?: string };
type Item = Record<string, unknown> & { id: string; kind: string };
type State = { queued: Item[]; sent: Item[]; replies: unknown[]; draft: unknown; scroll: { x: number; y: number } };
type Model = {
  snapshot(): State;
  queueNote(anchor: Anchor, text: string): Item;
  queueAnswer(answer: Record<string, string>): Item;
  remove(id: string): void;
  sendMessage(): { channel: string; version: number; type: string; payload: { items: Item[] } };
  markSent(): Item[];
  applyStatus(updates: unknown[]): void;
  restore(state: unknown): void;
};
type Api = {
  selectorFor(el: Element): string;
  escapeIdent(value: string): string;
  anchorFor(el: Element): Anchor | null;
  tableCellAnchor(el: Element): Anchor | null;
  svgPartAnchor(el: Element): Anchor | null;
  parseInbound(data: unknown): { type: string; payload: unknown } | null;
  validState(state: unknown): boolean;
  createModel(): Model;
  start(options: { window: Window }): { model: Model; isHosted(): boolean; lastPayload(): unknown };
};

const source = readFileSync(join(dirname(fileURLToPath(import.meta.url)), "dev-report-notes.js"), "utf8");
const globals = window as unknown as { DevReportNotes: Api; DEV_REPORT_NOTES_MANUAL_START: boolean };

function load(): Api {
  globals.DEV_REPORT_NOTES_MANUAL_START = true;
  new Function(source)();
  return globals.DevReportNotes;
}

function envelope(type: string, payload: unknown) {
  return { channel: "devthrottle.dev-report", version: 1, type, payload };
}

const emptyState: State = { queued: [], sent: [], replies: [], draft: null, scroll: { x: 0, y: 0 } };

let api: Api;
beforeEach(() => {
  document.documentElement.removeAttribute("data-dev-report-notes-started");
  document.head.innerHTML = "";
  document.body.innerHTML = "";
  api = load();
});

describe("the script file", () => {
  it("is ASCII only", () => {
    expect(/^[\x00-\x7F]*$/.test(source)).toBe(true);
  });

  it("makes no network calls and touches no storage", () => {
    for (const banned of ["fetch(", "XMLHttpRequest", "WebSocket", "localStorage", "sessionStorage", "indexedDB", "document.cookie", "sendBeacon"]) {
      expect(source.includes(banned), banned).toBe(false);
    }
  });
});

describe("selectorFor", () => {
  it("round-trips every element of a page back to the same element", () => {
    document.body.innerHTML = `
      <main><section><p>one</p><p>two <b>bold</b></p></section>
      <section id="detail"><div><span>a</span><span>b</span></div></section>
      <div id="dup"></div><div id="dup"><em>under a duplicate id</em></div>
      <div id="1weird:id"><i>escaped id</i></div>
      <svg><g><rect></rect><rect></rect></g><linearGradient id="grad"></linearGradient></svg></main>`;
    const all = Array.from(document.body.querySelectorAll("*"));
    expect(all.length).toBeGreaterThan(15);
    for (const el of all) {
      const selector = api.selectorFor(el);
      expect(document.querySelector(selector), selector).toBe(el);
    }
  });

  it("starts from the nearest unique id", () => {
    document.body.innerHTML = `<section id="detail"><div><span>a</span><span>b</span></div></section>`;
    const span = document.querySelectorAll("span")[1];
    expect(api.selectorFor(span)).toBe("#detail > div > span:nth-of-type(2)");
  });
});

describe("table cell anchors", () => {
  const table = `
    <table id="results">
      <thead><tr><th>Component</th><th>Runs</th><th>Failures</th></tr></thead>
      <tbody>
        <tr><td>Director</td><td>10</td><td>0</td></tr>
        <tr><td>Gateway</td><td>12</td><td>42</td></tr>
      </tbody>
    </table>`;

  it("names the row by its first cell and the column by its header", () => {
    document.body.innerHTML = table;
    const cell = document.querySelectorAll("tbody tr")[1].querySelectorAll("td")[2];
    const anchor = api.anchorFor(cell);
    expect(anchor).toEqual({
      type: "table-cell",
      selector: "#results > tbody > tr:nth-of-type(2) > td:nth-of-type(3)",
      quote: "42",
      rowLabel: "Gateway",
      columnLabel: "Failures",
    });
    expect(document.querySelector(anchor!.selector)).toBe(cell);
  });

  it("anchors a click on something inside a cell to the cell", () => {
    document.body.innerHTML = table.replace("<td>42</td>", "<td><b>42</b></td>");
    const bold = document.querySelector("b")!;
    const anchor = api.anchorFor(bold)!;
    expect(anchor.type).toBe("table-cell");
    expect(anchor.columnLabel).toBe("Failures");
    expect(anchor.rowLabel).toBe("Gateway");
  });

  it("uses a first row of th as the header when there is no thead", () => {
    document.body.innerHTML = `<table><tr><th>Name</th><th>State</th></tr><tr><td>alpha</td><td>green</td></tr></table>`;
    const anchor = api.anchorFor(document.querySelectorAll("td")[1])!;
    expect(anchor.rowLabel).toBe("alpha");
    expect(anchor.columnLabel).toBe("State");
  });

  it("prefers a th scope=row as the row label", () => {
    document.body.innerHTML = `<table><thead><tr><th>#</th><th>Name</th><th>State</th></tr></thead>
      <tbody><tr><td>1</td><th scope="row">alpha</th><td>green</td></tr></tbody></table>`;
    expect(api.anchorFor(document.querySelectorAll("td")[1])!.rowLabel).toBe("alpha");
  });

  it("gives no column label under a spanning header rather than guessing", () => {
    document.body.innerHTML = `<table><thead><tr><th>Name</th><th colspan="2">Counts</th></tr></thead>
      <tbody><tr><td>alpha</td><td>1</td><td>2</td></tr></tbody></table>`;
    expect(api.anchorFor(document.querySelectorAll("td")[2])!.columnLabel).toBe("");
  });

  it("gives no labels for a row shifted by an earlier rowspan", () => {
    document.body.innerHTML = `<table><thead><tr><th>Group</th><th>Name</th><th>State</th></tr></thead>
      <tbody><tr><td rowspan="2">g1</td><td>alpha</td><td>green</td></tr><tr><td>beta</td><td>red</td></tr></tbody></table>`;
    const anchor = api.anchorFor(document.querySelectorAll("tbody tr")[1].querySelectorAll("td")[1])!;
    expect(anchor.rowLabel).toBe("");
    expect(anchor.columnLabel).toBe("");
    expect(anchor.quote).toBe("red");
  });

  it("gives a header cell no row label", () => {
    document.body.innerHTML = table;
    const anchor = api.anchorFor(document.querySelectorAll("th")[1])!;
    expect(anchor.rowLabel).toBe("");
    expect(anchor.columnLabel).toBe("Runs");
  });
});

describe("svg and element anchors", () => {
  it("labels an SVG part by its nearest aria-label, data-label or title", () => {
    document.body.innerHTML = `<svg><g aria-label="Gateway box"><rect></rect><text>Gateway</text></g>
      <g><title>Phone</title><circle></circle></g><g data-label="Director"><path></path></g><line></line></svg>`;
    expect(api.anchorFor(document.querySelector("rect")!)).toMatchObject({ type: "svg-part", label: "Gateway box" });
    expect(api.anchorFor(document.querySelector("circle")!)).toMatchObject({ type: "svg-part", label: "Phone" });
    expect(api.anchorFor(document.querySelector("path")!)).toMatchObject({ type: "svg-part", label: "Director" });
    expect(api.anchorFor(document.querySelector("line")!)).toMatchObject({ type: "svg-part", label: "" });
  });

  it("anchors a paragraph with its text", () => {
    document.body.innerHTML = `<p>  The   Gateway keeps the report.</p>`;
    expect(api.anchorFor(document.querySelector("p")!)).toEqual({ type: "element", selector: "html > body > p", quote: "The Gateway keeps the report." });
  });

  it("never anchors to the notes tray", () => {
    document.body.innerHTML = `<div data-dev-report-ui><button>Send</button></div>`;
    expect(api.anchorFor(document.querySelector("button")!)).toBeNull();
  });
});

describe("the queue", () => {
  const anchor: Anchor = { type: "element", selector: "p", quote: "x" };

  it("keeps queued items in order with unique ids", () => {
    const model = api.createModel();
    model.queueNote(anchor, "first");
    model.queueAnswer({ questionId: "q1", question: "Q?", optionValue: "a", optionLabel: "A", comment: "" });
    model.queueNote(anchor, "second");
    const ids = model.snapshot().queued.map((i) => i.id);
    expect(ids).toEqual(["n1", "a2", "n3"]);
  });

  it("replaces the earlier answer to the same question in place", () => {
    const model = api.createModel();
    model.queueAnswer({ questionId: "q1", question: "Q?", optionValue: "a", optionLabel: "A", comment: "" });
    model.queueNote(anchor, "note");
    model.queueAnswer({ questionId: "q1", question: "Q?", optionValue: "b", optionLabel: "B", comment: "changed my mind" });
    const queued = model.snapshot().queued;
    expect(queued).toHaveLength(2);
    expect(queued[0]).toMatchObject({ id: "a1", optionValue: "b", comment: "changed my mind" });
  });

  it("refuses an empty note", () => {
    expect(() => api.createModel().queueNote(anchor, "   ")).toThrow(/needs some text/);
  });

  it("sends the whole queue and moves it to sent, where the host's status words replace ours", () => {
    const model = api.createModel();
    model.queueNote(anchor, "one");
    model.queueNote(anchor, "two");
    expect(model.sendMessage()).toMatchObject({ channel: "devthrottle.dev-report", version: 1, type: "send" });
    expect(model.sendMessage().payload.items.map((i) => i.text)).toEqual(["one", "two"]);
    model.markSent();
    model.applyStatus([{ id: "n2", status: "held", statusLabel: "Delivered when the agent finishes" }, { id: "zz", status: "x", statusLabel: "x" }]);
    const s = model.snapshot();
    expect(s.queued).toEqual([]);
    expect(s.sent.map((i) => i.statusLabel)).toEqual(["Sent to the app", "Delivered when the agent finishes"]);
  });

  it("does not reuse an id already in the sent list", () => {
    const model = api.createModel();
    model.queueNote(anchor, "one");
    model.markSent();
    expect(model.queueNote(anchor, "two").id).toBe("n2");
  });
});

describe("inbound message validation", () => {
  const good = envelope("restore", { state: emptyState });

  it("accepts well-formed restore, status and reply messages", () => {
    expect(api.parseInbound(good)?.type).toBe("restore");
    expect(api.parseInbound(envelope("status", { updates: [{ id: "n1", status: "held", statusLabel: "Held" }] }))?.type).toBe("status");
    expect(api.parseInbound(envelope("reply", { reply: { id: "r1", text: "done", at: "now" } }))?.type).toBe("reply");
  });

  it("ignores the wrong channel, the wrong version and unknown types", () => {
    expect(api.parseInbound({ ...good, channel: "other" })).toBeNull();
    expect(api.parseInbound({ ...good, version: 2 })).toBeNull();
    expect(api.parseInbound(envelope("delete-everything", {}))).toBeNull();
    expect(api.parseInbound("restore")).toBeNull();
    expect(api.parseInbound(null)).toBeNull();
  });

  it("rejects a restore whose state is malformed anywhere, not just at the top", () => {
    const badItem = { ...emptyState, queued: [{ id: "n1", kind: "note", text: "x", anchor: { type: "nope", selector: "", quote: "" } }] };
    const badSent = { ...emptyState, sent: [{ id: "n1", kind: "note", text: "x", anchor: { type: "element", selector: "", quote: "" } }] };
    const badScroll = { ...emptyState, scroll: { x: "0", y: 0 } };
    const badDraft = { ...emptyState, draft: { text: "x" } };
    const tooLong = { ...emptyState, replies: [{ id: "r", text: "x".repeat(20001), at: "" }] };
    for (const state of [badItem, badSent, badScroll, badDraft, tooLong]) {
      expect(api.parseInbound(envelope("restore", { state }))).toBeNull();
    }
  });

  it("rejects a status update missing its words", () => {
    expect(api.parseInbound(envelope("status", { updates: [{ id: "n1", status: "held" }] }))).toBeNull();
  });
});

describe("the page", () => {
  const report = `
    <header data-dev-report="header" data-dev-report-status="waiting-on-you"><h1>Report</h1></header>
    <section data-dev-report="questions">
      <div data-dev-report-question="deploy" data-dev-report-question-text="When should we deploy?">
        <label><input type="radio" name="deploy" value="tonight" data-recommended> Tonight</label>
        <label><input type="radio" name="deploy" value="monday"> Monday</label>
        <textarea data-dev-report-comment></textarea>
      </div>
    </section>
    <section data-dev-report="detail"><p id="para">A paragraph.</p></section>`;

  function click(el: Element) {
    el.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
  }

  it("preselects the recommendation and queues an answer with one Queue button per question", () => {
    document.body.innerHTML = report;
    const page = api.start({ window });
    const tonight = document.querySelector<HTMLInputElement>("input[value=tonight]")!;
    expect(tonight.checked).toBe(true);
    const buttons = document.querySelectorAll("[data-drn=queue-answer]");
    expect(buttons).toHaveLength(1);
    document.querySelector<HTMLTextAreaElement>("textarea[data-dev-report-comment]")!.value = "after 8pm";
    click(buttons[0]);
    expect(page.model.snapshot().queued).toEqual([
      { id: "a1", kind: "answer", questionId: "deploy", question: "When should we deploy?", optionValue: "tonight", optionLabel: "Tonight", comment: "after 8pm" },
    ]);
    expect(document.querySelector("[data-drn=question-state]")!.textContent).toContain("Queued: Tonight");
  });

  it("notes a clicked element through the tray and shows it as queued, not sent", () => {
    document.body.innerHTML = report;
    const page = api.start({ window });
    click(document.querySelector("[data-drn=pick]")!);
    click(document.getElementById("para")!);
    const text = document.querySelector<HTMLTextAreaElement>("[data-drn=composer-text]")!;
    text.value = "Say more here";
    click(document.querySelector("[data-drn=composer-queue]")!);
    const queued = page.model.snapshot().queued;
    expect(queued).toHaveLength(1);
    expect(queued[0]).toMatchObject({ kind: "note", text: "Say more here", anchor: { type: "element", selector: "#para" } });
    expect(document.querySelector("[data-drn=queued]")!.textContent).toContain("Say more here");
    expect(document.querySelector("[data-drn=sent]")!.textContent).not.toContain("Say more here");
  });

  it("with no host, Send shows the exact payload and keeps the queue", () => {
    document.body.innerHTML = report;
    const page = api.start({ window });
    click(document.querySelector("[data-drn=queue-answer]")!);
    click(document.querySelector("[data-drn=send]")!);
    expect(page.isHosted()).toBe(false);
    const shown = JSON.parse(document.querySelector("[data-drn=payload]")!.textContent!);
    expect(shown).toEqual(page.lastPayload());
    expect(shown).toMatchObject({ type: "send", payload: { items: [{ questionId: "deploy" }] } });
    expect(page.model.snapshot().queued).toHaveLength(1);
    expect(page.model.snapshot().sent).toHaveLength(0);
  });

  it("refuses to start twice on one page", () => {
    document.body.innerHTML = report;
    api.start({ window });
    expect(() => api.start({ window })).toThrow(/already started/);
  });
});
