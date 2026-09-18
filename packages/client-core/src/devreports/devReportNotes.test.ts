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
type State = { queued: Item[]; sent: Item[]; replies: unknown[]; draft: unknown; answerDrafts: unknown[]; scroll: { x: number; y: number } };
type Model = {
  snapshot(): State;
  queueNote(anchor: Anchor, text: string): Item;
  queueAnswer(answer: Record<string, string>): Item;
  remove(id: string): void;
  sendMessage(): { channel: string; version: number; type: string; payload: { items: Item[] } };
  markPending(): void;
  applyStatus(updates: unknown[]): void;
  restore(state: unknown): void;
};
type Rect = { top: number; bottom: number; left: number; right: number; width: number; height: number };
type Placed = { left: number; top: number; scrollBy: number };
type Api = {
  selectorFor(el: Element): string;
  escapeIdent(value: string): string;
  placeNoteBox(anchor: Rect, question: Rect | null, box: { width: number; height: number }, viewport: { width: number; height: number }): Placed;
  anchorFor(el: Element): Anchor | null;
  tableCellAnchor(el: Element): Anchor | null;
  svgPartAnchor(el: Element): Anchor | null;
  parseInbound(data: unknown): { type: string; payload: unknown } | null;
  validState(state: unknown): boolean;
  parseTheme(text: string): Record<string, string> | null;
  theme(): Record<string, string>;
  cssText(): string;
  createModel(): Model;
  started: symbol;
  start(options: { window: Window }): { model: Model; isHosted(): boolean; lastPayload(): unknown };
};

const source = readFileSync(join(dirname(fileURLToPath(import.meta.url)), "dev-report-notes.js"), "utf8");
const globals = window as unknown as { DevReportNotes: Api; DEV_REPORT_NOTES_MANUAL_START: boolean };

function load(): Api {
  globals.DEV_REPORT_NOTES_MANUAL_START = true;
  new Function(source)();
  return globals.DevReportNotes;
}

// The notes interface lives in shadow roots, so page queries cannot see it. Look inside every host.
function uiAll<T extends Element = HTMLElement>(selector: string): T[] {
  const found: T[] = [];
  for (const host of Array.from(document.querySelectorAll("[data-dev-report-ui]"))) {
    if (host.shadowRoot) found.push(...Array.from(host.shadowRoot.querySelectorAll<T>(selector)));
  }
  return found;
}

function ui<T extends Element = HTMLElement>(selector: string): T | null {
  return uiAll<T>(selector)[0] ?? null;
}

// Every word the notes interface draws on the page, across all of its shadow roots.
function uiText(): string {
  return Array.from(document.querySelectorAll("[data-dev-report-ui]"))
    .map((host) => host.shadowRoot?.textContent ?? "")
    .join(" ");
}

function envelope(type: string, payload: unknown) {
  return { channel: "devthrottle.dev-report", version: 1, type, payload };
}

const emptyState: State = { queued: [], sent: [], replies: [], draft: null, answerDrafts: [], scroll: { x: 0, y: 0 } };

let api: Api;
beforeEach(() => {
  document.head.innerHTML = "";
  document.body.innerHTML = "";
  api = load();
  delete (window as unknown as Record<symbol, unknown>)[api.started];
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

  it("never anchors to the notes interface the script added", () => {
    document.body.innerHTML = `<p>text</p>`;
    api.start({ window });
    // The tray and the note box, each in its own shadow root.
    const hosts = Array.from(document.querySelectorAll("[data-dev-report-ui]")).filter((h) => h.shadowRoot);
    expect(hosts.length).toBe(2);
    // A click inside a shadow root reaches the page as a click on its host, so the host is what is checked.
    for (const host of hosts) expect(api.anchorFor(host)).toBeNull();
    expect(ui("[data-drn=send]")).not.toBeNull();
    expect(ui("[data-drn=composer]")).not.toBeNull();
  });

  it("keeps the tray and the note box out of reach of the report's CSS", () => {
    // Second review, finding 6: a report stylesheet could hide the tray by its class name.
    document.body.innerHTML = `<p>text</p>`;
    api.start({ window });
    expect(document.querySelector(".drn-tray")).toBeNull();
    expect(document.querySelector(".drn-notebox")).toBeNull();
    const hosts = Array.from(document.querySelectorAll<HTMLElement>("[data-dev-report-ui]")).filter((h) => h.shadowRoot);
    expect(hosts.find((h) => h.shadowRoot!.querySelector(".drn-tray"))).toBeTruthy();
    expect(hosts.find((h) => h.shadowRoot!.querySelector(".drn-notebox"))).toBeTruthy();
    // The note box gets the same inline protections as the tray - a report cannot move it or hide it.
    for (const host of hosts) {
      for (const property of ["display", "visibility", "opacity", "position", "transform", "filter", "clip-path"]) {
        expect(host.style.getPropertyPriority(property), property).toBe("important");
      }
    }
  });

  it("does not let report markup copy the tray's attribute to opt out of notes", () => {
    // Review finding 6.
    document.body.innerHTML = `<p data-dev-report-ui>text</p>`;
    document.body.setAttribute("data-dev-report-ui", "");
    api.start({ window });
    expect(api.anchorFor(document.querySelector("p")!)).toMatchObject({ type: "element", quote: "text" });
    document.body.removeAttribute("data-dev-report-ui");
  });
});

describe("the queue", () => {
  const anchor: Anchor = { type: "element", selector: "p", quote: "x" };
  const RANDOM_NOTE_ID = /^n-[0-9a-f]{32}$/;
  const RANDOM_ANSWER_ID = /^a-[0-9a-f]{32}$/;

  it("keeps queued items in order with unique ids", () => {
    const model = api.createModel();
    model.queueNote(anchor, "first");
    model.queueAnswer({ questionId: "q1", question: "Q?", optionValue: "a", optionLabel: "A", comment: "" });
    model.queueNote(anchor, "second");
    const queued = model.snapshot().queued;
    expect(queued.map((i) => i.text ?? i.optionValue)).toEqual(["first", "a", "second"]);
    expect(queued[0].id).toMatch(RANDOM_NOTE_ID);
    expect(queued[1].id).toMatch(RANDOM_ANSWER_ID);
    expect(queued[2].id).toMatch(RANDOM_NOTE_ID);
    expect(new Set(queued.map((i) => i.id)).size).toBe(3);
  });

  it("gives a second browser's first note an id the first browser's note does not have", () => {
    // Phase 3 proof, E9: the phone and the Cockpit each keep their own state for one report. A per-page counter
    // named both first notes n1, the Gateway took the Cockpit's as the phone's, and the note was lost while
    // reading as delivered. Each browser here is a fresh model, exactly as each app starts one.
    const phone = api.createModel();
    const cockpit = api.createModel();
    const fromPhone = phone.queueNote(anchor, "from the phone");
    const fromCockpit = cockpit.queueNote(anchor, "from the Cockpit");
    expect(fromCockpit.id).not.toBe(fromPhone.id);
    expect(cockpit.queueAnswer({ questionId: "q1", question: "Q?", optionValue: "a", optionLabel: "A", comment: "" }).id)
      .not.toBe(phone.queueAnswer({ questionId: "q1", question: "Q?", optionValue: "a", optionLabel: "A", comment: "" }).id);
  });

  it("replaces the earlier answer to the same question in place", () => {
    const model = api.createModel();
    const first = model.queueAnswer({ questionId: "q1", question: "Q?", optionValue: "a", optionLabel: "A", comment: "" });
    model.queueNote(anchor, "note");
    model.queueAnswer({ questionId: "q1", question: "Q?", optionValue: "b", optionLabel: "B", comment: "changed my mind" });
    const queued = model.snapshot().queued;
    expect(queued).toHaveLength(2);
    expect(queued[0]).toMatchObject({ kind: "answer", optionValue: "b", comment: "changed my mind" });
    expect(queued[0].id).toBe(first.id);
  });

  it("refuses an empty note", () => {
    expect(() => api.createModel().queueNote(anchor, "   ")).toThrow(/needs some text/);
  });

  it("keeps sent items in the queue until the host confirms each one", () => {
    // Review finding 2: posting a message is not the host accepting it.
    const model = api.createModel();
    const one = model.queueNote(anchor, "one");
    const two = model.queueNote(anchor, "two");
    expect(model.sendMessage()).toMatchObject({ channel: "devthrottle.dev-report", version: 1, type: "send" });
    expect(model.sendMessage().payload.items.map((i) => i.text)).toEqual(["one", "two"]);
    model.markPending();
    expect(model.snapshot().queued.map((i) => i.pending)).toEqual([true, true]);
    expect(model.snapshot().sent).toEqual([]);
    expect(model.sendMessage().payload.items[0]).not.toHaveProperty("pending");

    model.applyStatus([{ id: two.id, status: "held", statusLabel: "Delivered when the agent finishes" }, { id: "zz", status: "x", statusLabel: "x" }]);
    const s = model.snapshot();
    expect(s.queued.map((i) => i.id)).toEqual([one.id]);
    expect(s.sent).toEqual([{ id: two.id, kind: "note", text: "two", anchor, status: "held", statusLabel: "Delivered when the agent finishes" }]);
  });

  it("keeps a refused item queued with the host's reason, ready to send again", () => {
    const model = api.createModel();
    const one = model.queueNote(anchor, "one");
    model.markPending();
    model.applyStatus([{ id: one.id, status: "refused", statusLabel: "This session has ended" }]);
    expect(model.snapshot().queued).toEqual([{ id: one.id, kind: "note", text: "one", anchor, pending: false, statusLabel: "This session has ended" }]);
    expect(model.snapshot().sent).toEqual([]);
  });

  it("cannot remove an item the host already has", () => {
    const model = api.createModel();
    const one = model.queueNote(anchor, "one");
    model.markPending();
    model.remove(one.id);
    expect(model.snapshot().queued).toHaveLength(1);
  });

  it("gives a re-answer a new id when the earlier answer is already with the host", () => {
    const model = api.createModel();
    const first = model.queueAnswer({ questionId: "q1", question: "Q?", optionValue: "a", optionLabel: "A", comment: "" });
    model.markPending();
    const again = model.queueAnswer({ questionId: "q1", question: "Q?", optionValue: "b", optionLabel: "B", comment: "" });
    expect(again.id).toMatch(RANDOM_ANSWER_ID);
    expect(again.id).not.toBe(first.id);
    expect(model.snapshot().queued.map((i) => i.id)).toEqual([first.id, again.id]);
  });

  it("keeps at most the pending answer and the newest revision when an answer is changed twice", () => {
    // Second review, new finding B.
    const model = api.createModel();
    const answer = (v: string) => ({ questionId: "q1", question: "Q?", optionValue: v, optionLabel: v, comment: "" });
    const a = model.queueAnswer(answer("A"));
    model.markPending();
    const b = model.queueAnswer(answer("B"));
    const c = model.queueAnswer(answer("C"));
    expect(c.id).toBe(b.id);
    expect(model.sendMessage().payload.items.map((i) => `${i.id}:${i.optionValue}`)).toEqual([`${a.id}:A`, `${b.id}:C`]);
  });

  it("does not reuse an id already in the sent list", () => {
    const model = api.createModel();
    const one = model.queueNote(anchor, "one");
    model.markPending();
    model.applyStatus([{ id: one.id, status: "delivered", statusLabel: "Delivered" }]);
    const two = model.queueNote(anchor, "two");
    expect(two.id).toMatch(RANDOM_NOTE_ID);
    expect(two.id).not.toBe(one.id);
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
    const noAnswerDrafts = { ...emptyState, answerDrafts: undefined };
    const badAnswerDraft = { ...emptyState, answerDrafts: [{ questionId: "", optionValue: "a", comment: "" }] };
    const badPending = { ...emptyState, queued: [{ id: "n1", kind: "note", text: "x", anchor: { type: "element", selector: "", quote: "" }, pending: "yes" }] };
    for (const state of [badItem, badSent, badScroll, badDraft, tooLong, noAnswerDrafts, badAnswerDraft, badPending]) {
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
    const buttons = uiAll("[data-drn=queue-answer]");
    expect(buttons).toHaveLength(1);
    document.querySelector<HTMLTextAreaElement>("textarea[data-dev-report-comment]")!.value = "after 8pm";
    click(buttons[0]);
    expect(page.model.snapshot().queued).toEqual([
      { id: expect.stringMatching(/^a-[0-9a-f]{32}$/), kind: "answer", questionId: "deploy", question: "When should we deploy?", optionValue: "tonight", optionLabel: "Tonight", comment: "after 8pm" },
    ]);
    expect(ui("[data-drn=question-state]")!.textContent).toContain("Queued: Tonight");
  });

  it("notes a clicked element through the tray and shows it as queued, not sent", () => {
    document.body.innerHTML = report;
    const page = api.start({ window });
    click(ui("[data-drn=pick]")!);
    click(document.getElementById("para")!);
    const text = ui<HTMLTextAreaElement>("[data-drn=composer-text]")!;
    text.value = "Say more here";
    click(ui("[data-drn=composer-queue]")!);
    const queued = page.model.snapshot().queued;
    expect(queued).toHaveLength(1);
    expect(queued[0]).toMatchObject({ kind: "note", text: "Say more here", anchor: { type: "element", selector: "#para" } });
    expect(ui("[data-drn=queued]")!.textContent).toContain("Say more here");
    expect(ui("[data-drn=sent]")!.textContent).not.toContain("Say more here");
  });

  it("with no host, Send shows the exact payload and keeps the queue", () => {
    document.body.innerHTML = report;
    const page = api.start({ window });
    click(ui("[data-drn=queue-answer]")!);
    click(ui("[data-drn=send]")!);
    expect(page.isHosted()).toBe(false);
    const shown = JSON.parse(ui("[data-drn=payload]")!.textContent!);
    expect(shown).toEqual(page.lastPayload());
    expect(shown).toMatchObject({ type: "send", payload: { items: [{ questionId: "deploy" }] } });
    expect(page.model.snapshot().queued).toHaveLength(1);
    expect(page.model.snapshot().sent).toHaveLength(0);
  });

  it("answers a question with its own options only, never a nested question's", () => {
    // Review finding 5.
    document.body.innerHTML = `
      <div data-dev-report-question="outer">
        <label><input type="radio" name="o" value="o1" data-recommended> Outer 1</label>
        <label><input type="radio" name="o" value="o2"> Outer 2</label>
        <div data-dev-report-question="inner">
          <label><input type="radio" name="i" value="i1" data-recommended> Inner 1</label>
          <label><input type="radio" name="i" value="i2"> Inner 2</label>
        </div>
      </div>`;
    const page = api.start({ window });
    click(ui("[data-question-id=outer]")!);
    expect(page.model.snapshot().queued[0]).toMatchObject({ questionId: "outer", optionValue: "o1", optionLabel: "Outer 1" });
  });

  it("says a too-long comment is too long instead of throwing", () => {
    // Review finding 7.
    document.body.innerHTML = report;
    const page = api.start({ window });
    const comment = document.querySelector<HTMLTextAreaElement>("textarea[data-dev-report-comment]")!;
    expect(comment.getAttribute("maxlength")).toBe("20000");
    comment.value = "x".repeat(20001);
    expect(() => click(ui("[data-drn=queue-answer]")!)).not.toThrow();
    expect(page.model.snapshot().queued).toEqual([]);
    expect(ui("[data-drn=question-state]")!.textContent).toContain("the comment is longer than 20000 characters");
  });

  it("queues the option the owner clicked even when the options do not share one radio name", () => {
    // Inspection finding 2: with two names the browser kept the recommendation checked beside the owner's
    // click, and the answer queued was the recommendation.
    document.body.innerHTML = `
      <div data-dev-report-question="pick" data-dev-report-question-text="Pick one">
        <label><input type="radio" name="first" value="first"> First</label>
        <label><input type="radio" name="second" value="recommended" data-recommended> Second</label>
      </div>`;
    const page = api.start({ window });
    const [first, second] = Array.from(document.querySelectorAll<HTMLInputElement>("input[type=radio]"));
    expect([first.checked, second.checked]).toEqual([false, true]);
    first.click();
    expect([first.checked, second.checked]).toEqual([true, false]);
    click(ui("[data-question-id=pick]")!);
    expect(page.model.snapshot().queued[0]).toMatchObject({ questionId: "pick", optionValue: "first", optionLabel: "First" });
  });

  it("keeps only one option checked at start when the markup checks several", () => {
    document.body.innerHTML = `
      <div data-dev-report-question="pick">
        <label><input type="radio" name="a" value="a" checked> A</label>
        <label><input type="radio" name="b" value="b" checked data-recommended> B</label>
      </div>`;
    api.start({ window });
    const checked = Array.from(document.querySelectorAll<HTMLInputElement>("input[type=radio]")).map((r) => r.checked);
    expect(checked).toEqual([false, true]);
  });

  it("says an overlong question text is too long and does not queue a shortened copy", () => {
    // Inspection finding 4: the text was cut to 20000 characters before the length check, so it queued.
    document.body.innerHTML = report.replace('data-dev-report-question-text="When should we deploy?"', `data-dev-report-question-text="${"q".repeat(20001)}"`);
    const page = api.start({ window });
    click(ui("[data-drn=queue-answer]")!);
    expect(page.model.snapshot().queued).toEqual([]);
    expect(ui("[data-drn=question-state]")!.textContent).toContain("the question text is longer than 20000 characters");
  });

  it("queues a heading question text longer than a quote in full", () => {
    // Inspection finding 4: a heading was cut to 240 characters, so the answer carried different words.
    const long = "Why ".repeat(100).trim() + "?";
    document.body.innerHTML = report.replace(' data-dev-report-question-text="When should we deploy?"', "").replace("<label>", `<h3>${long}</h3><label>`);
    const page = api.start({ window });
    click(ui("[data-drn=queue-answer]")!);
    expect(page.model.snapshot().queued[0]).toMatchObject({ question: long });
  });

  it("gives a question with the id __proto__ its state line like any other", () => {
    // Review finding 11.
    document.body.innerHTML = report.split('"deploy"').join('"__proto__"');
    const page = api.start({ window });
    click(ui("[data-drn=queue-answer]")!);
    expect(page.model.snapshot().queued).toHaveLength(1);
    expect(ui("[data-drn=question-state]")!.textContent).toContain("Queued: Tonight");
  });

  it("clears a question's draft when its answer is queued, so a later edit is the newer thing", () => {
    // Second review, finding 8.
    document.body.innerHTML = report;
    const page = api.start({ window });
    const comment = document.querySelector<HTMLTextAreaElement>("textarea[data-dev-report-comment]")!;
    comment.value = "first";
    comment.dispatchEvent(new Event("input", { bubbles: true }));
    expect(page.model.snapshot().answerDrafts).toHaveLength(1);
    click(ui("[data-drn=queue-answer]")!);
    expect(page.model.snapshot().answerDrafts).toEqual([]);
    comment.value = "unfinished";
    comment.dispatchEvent(new Event("input", { bubbles: true }));
    expect(page.model.snapshot().answerDrafts).toEqual([{ questionId: "deploy", optionValue: "tonight", comment: "unfinished" }]);
    expect(page.model.snapshot().queued[0]).toMatchObject({ comment: "first" });
  });

  it("keeps an unqueued choice and comment in the state it hands the host", () => {
    // Review finding 8.
    document.body.innerHTML = report;
    const page = api.start({ window });
    const monday = document.querySelector<HTMLInputElement>("input[value=monday]")!;
    monday.checked = true;
    monday.dispatchEvent(new Event("change", { bubbles: true }));
    const comment = document.querySelector<HTMLTextAreaElement>("textarea[data-dev-report-comment]")!;
    comment.value = "half an answer";
    comment.dispatchEvent(new Event("input", { bubbles: true }));
    expect(page.model.snapshot().answerDrafts).toEqual([{ questionId: "deploy", optionValue: "monday", comment: "half an answer" }]);
  });

  it("starts even when the report carries the old started attribute or a look-alike global id", () => {
    // Review finding 6.
    document.documentElement.setAttribute("data-dev-report-notes-started", "1");
    document.body.innerHTML = report + `<div id="DEV_REPORT_NOTES_MANUAL_START"></div>`;
    expect(() => api.start({ window })).not.toThrow();
    expect(ui("[data-drn=send]")).not.toBeNull();
    document.documentElement.removeAttribute("data-dev-report-notes-started");
  });

  it("refuses to start twice on one page", () => {
    document.body.innerHTML = report;
    api.start({ window });
    expect(() => api.start({ window })).toThrow(/already started/);
  });
});

describe("the tray's theme", () => {
  const theme = {
    background: "#010203",
    surface: "#111213",
    surface2: "#212223",
    border: "#313233",
    text: "#414243",
    textDim: "#515253",
    accent: "#616263",
    accentText: "#fff",
    font: '"Odd Sans", Arial, sans-serif',
    monoFont: '"Odd Mono", monospace',
  };

  // The script reads its attributes from document.currentScript, which only a host-injected element has.
  function loadAsInjected(attributes: Record<string, string>): Api {
    const script = document.createElement("script");
    for (const [name, value] of Object.entries(attributes)) script.setAttribute(name, value);
    document.head.appendChild(script);
    Object.defineProperty(document, "currentScript", { value: script, configurable: true });
    try {
      const loaded = load();
      expect(script.hasAttribute("data-dev-report-theme")).toBe(false);
      return loaded;
    } finally {
      Object.defineProperty(document, "currentScript", { value: null, configurable: true });
    }
  }

  it("uses the app's dark palette and type when no theme is given, with the font on the tray and not on :host", () => {
    const css = api.cssText();
    expect(css).toContain("#141a2e");
    expect(css).toContain('"Segoe UI"');
    expect(css).not.toContain(":host");
    expect(css).toMatch(/\.drn-tray, \.drn-notebox, \.drn-row \{ font-family: -apple-system/);
  });

  it("bakes a host theme into the shadow-root stylesheet and removes the attribute", () => {
    const themed = loadAsInjected({ "data-dev-report-theme": JSON.stringify(theme) });
    expect(themed.theme()).toEqual(theme);
    const css = themed.cssText();
    for (const value of Object.values(theme)) expect(css).toContain(value);
    document.body.innerHTML = "<p>report</p>";
    themed.start({ window });
    const host = document.querySelector("[data-dev-report-ui]:not(style)") as HTMLElement;
    expect(host.shadowRoot!.querySelector("style")!.textContent).toContain('"Odd Sans", Arial, sans-serif');
  });

  it("refuses a theme that is not exactly the documented shape, whole", () => {
    expect(api.parseTheme(JSON.stringify(theme))).toEqual(theme);
    expect(api.parseTheme(JSON.stringify({ ...theme, extra: "#000" }))).toBeNull();
    const { accent: _dropped, ...missing } = theme;
    expect(api.parseTheme(JSON.stringify(missing))).toBeNull();
    expect(api.parseTheme(JSON.stringify({ ...theme, accent: "red" }))).toBeNull();
    expect(api.parseTheme(JSON.stringify({ ...theme, accent: "#000; } body { display:none" }))).toBeNull();
    expect(api.parseTheme(JSON.stringify({ ...theme, font: "Arial; } .drn-tray { display: none" }))).toBeNull();
    expect(api.parseTheme(JSON.stringify({ ...theme, font: "x".repeat(201) }))).toBeNull();
    expect(api.parseTheme("not json")).toBeNull();
    expect(api.parseTheme("[]")).toBeNull();
  });

  it("keeps the default look when the host's theme is refused", () => {
    const themed = loadAsInjected({ "data-dev-report-theme": JSON.stringify({ ...theme, accent: "url(x)" }) });
    expect(themed.theme().surface).toBe("#141a2e");
  });
});

// -------------------------------------------------------------------------------------------------
// Hosted: the app owns the conversation (phase 3b). The owner saw the page's own queued/sent/replies
// lists and its Send next to the app's, and one of the two Sends did nothing. Hosted, the page draws
// only the note-taking parts.
// -------------------------------------------------------------------------------------------------

const CONVERSATION_PARTS = ["conversation", "queued", "sent", "replies", "send", "payload-box"];
const NOTE_TAKING_PARTS = ["toggle", "pick", "note-selection", "composer", "composer-text", "composer-queue", "composer-cancel"];

const pageReport = `
  <header data-dev-report="header" data-dev-report-status="waiting-on-you"><h1>Report</h1></header>
  <section data-dev-report="questions">
    <div data-dev-report-question="deploy" data-dev-report-question-text="When should we deploy?">
      <label><input type="radio" name="deploy" value="tonight" data-recommended> Tonight</label>
      <label><input type="radio" name="deploy" value="monday"> Monday</label>
      <textarea data-dev-report-comment></textarea>
    </div>
  </section>
  <section data-dev-report="detail"><p id="para">A paragraph.</p></section>`;

function clickOn(el: Element) {
  el.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
}

// A host, the way CONTRACT.md section 3 describes one: the page posts its ready to the parent with one end
// of a MessageChannel it made, and everything after that goes over that port. jsdom's window IS the top
// window, so the test hands start() a window whose parent is a stand-in host and keeps the port the real
// script really handed over - nothing about the message path is faked.
type Hosted = {
  page: { model: Model; isHosted(): boolean; lastPayload(): unknown };
  ready: { type: string; payload: { questionIds: string[] } };
  fromPage: Array<{ type: string; payload: { state: State; items?: Item[] } }>;
  restore(state?: State): Promise<void>;
};

function startFramed(): Hosted {
  let handed: MessagePort | null = null;
  let ready: Hosted["ready"] | null = null;
  const parent = {
    postMessage(message: Hosted["ready"], _targetOrigin: string, ports: MessagePort[]) {
      ready = message;
      handed = ports[0];
    },
  };
  const framedWindow = new Proxy(window, {
    get(target, property) {
      if (property === "parent") return parent;
      const value = Reflect.get(target, property, target);
      return typeof value === "function" ? value.bind(target) : value;
    },
  }) as unknown as Window;
  const page = api.start({ window: framedWindow });
  if (!handed || !ready) throw new Error("the page did not hand its host a port with its ready");
  const port = handed as MessagePort;
  const fromPage: Hosted["fromPage"] = [];
  port.onmessage = (event: MessageEvent) => fromPage.push(event.data);
  return {
    page,
    ready,
    fromPage,
    async restore(state: State = emptyState) {
      const before = fromPage.length;
      port.postMessage(envelope("restore", { state }));
      // A status makes the page post state-changed straight back. A port delivers in order, so the arrival of
      // that answer proves the restore ahead of it has already been handled - no sleeping and hoping.
      port.postMessage(envelope("status", { updates: [] }));
      for (let i = 0; i < 400 && fromPage.length === before; i++) await new Promise((resolve) => setTimeout(resolve, 5));
      if (fromPage.length === before) throw new Error("the page never answered its host");
    },
  };
}

describe("hosted, the app owns the conversation", () => {
  it("draws only the note-taking parts - no queued, sent or replies list, no Send, no payload box", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.restore();

    expect(host.page.isHosted()).toBe(true);
    for (const part of CONVERSATION_PARTS) expect(ui(`[data-drn=${part}]`), part).toBeNull();
    expect(uiAll(".drn-h")).toHaveLength(0);
    for (const heading of ["Queued - not sent yet", "Sent", "Replies from the agent"]) {
      expect(uiText(), heading).not.toContain(heading);
    }
    for (const part of NOTE_TAKING_PARTS) expect(ui(`[data-drn=${part}]`), part).not.toBeNull();
    expect(uiAll("[data-drn=queue-answer]")).toHaveLength(1);
  });

  it("unhosted, the page keeps the whole tray - the conversation and Send belong to a page with no app", () => {
    document.body.innerHTML = pageReport;
    const page = api.start({ window });
    expect(page.isHosted()).toBe(false);
    for (const part of CONVERSATION_PARTS.concat(NOTE_TAKING_PARTS)) expect(ui(`[data-drn=${part}]`), part).not.toBeNull();
    for (const heading of ["Queued - not sent yet", "Sent", "Replies from the agent"]) {
      expect(uiText(), heading).toContain(heading);
    }
    // And Send still does what it does today: it shows the exact message it would have posted.
    clickOn(ui("[data-drn=queue-answer]")!);
    clickOn(ui("[data-drn=send]")!);
    const shown = JSON.parse(ui("[data-drn=payload]")!.textContent!);
    expect(shown).toEqual(page.lastPayload());
    expect(shown).toMatchObject({ type: "send", payload: { items: [{ questionId: "deploy", optionValue: "tonight" }] } });
    expect(ui("[data-drn=payload-box]")!.hasAttribute("hidden")).toBe(false);
    expect(page.model.snapshot().queued).toHaveLength(1);
  });

  it("is the RESTORE that takes Send away, not being framed: a framed page whose host never answers keeps it", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    // Framed, its ready posted, its port handed over - and still unhosted, because no restore has come back.
    expect(host.ready.type).toBe("ready");
    expect(host.page.isHosted()).toBe(false);
    expect(ui("[data-drn=send]")).not.toBeNull();
    expect(ui("[data-drn=queued]")).not.toBeNull();

    await host.restore();

    expect(host.page.isHosted()).toBe(true);
    expect(ui("[data-drn=send]")).toBeNull();
    expect(ui("[data-drn=queued]")).toBeNull();
  });

  it("tells the owner where the one Send is, in words true of either app and naming nothing internal", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.restore();
    clickOn(ui("[data-drn=queue-answer]")!);
    const line = ui("[data-drn=question-state]")!.textContent!;
    expect(line).toBe("Queued: Tonight - press Send in the app to send it.");
    expect(line).not.toContain("tray");
    expect(line).not.toMatch(/deploy|report|session|[0-9a-f]{8}/);
  });

  it("hands the app the same state hosted as the page keeps unhosted - the panel still gets everything", async () => {
    // Nothing about what the page SENDS changes; only what it draws.
    function noteAndAnswer() {
      clickOn(ui("[data-drn=pick]")!);
      clickOn(document.getElementById("para")!);
      ui<HTMLTextAreaElement>("[data-drn=composer-text]")!.value = "This number is wrong";
      clickOn(ui("[data-drn=composer-queue]")!);
      document.querySelector<HTMLTextAreaElement>("textarea[data-dev-report-comment]")!.value = "after 8pm";
      clickOn(ui("[data-drn=queue-answer]")!);
    }
    // Ids are 16 random bytes, so they differ between two pages by design; everything else must match.
    const withoutIds = (state: State) => ({ ...state, queued: state.queued.map(({ id, ...rest }) => rest) });

    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.restore();
    noteAndAnswer();
    const hostedState = host.page.model.snapshot();
    // A port delivers on the next turn of the loop, so let what the page posted arrive before reading it.
    await new Promise((resolve) => setTimeout(resolve, 0));
    const pushed = host.fromPage.filter((m) => m.type === "state-changed");
    expect(pushed.length).toBeGreaterThan(0);
    expect(pushed[pushed.length - 1].payload.state).toEqual(hostedState);
    expect(hostedState.queued).toHaveLength(2);

    document.head.innerHTML = "";
    document.body.innerHTML = "";
    api = load();
    delete (window as unknown as Record<symbol, unknown>)[api.started];
    document.body.innerHTML = pageReport;
    const unhosted = api.start({ window });
    noteAndAnswer();

    expect(withoutIds(hostedState)).toEqual(withoutIds(unhosted.model.snapshot()));
  });
});

// -------------------------------------------------------------------------------------------------
// Where the note box goes. The rule is a pure function of rectangles, so it is checked as one.
// -------------------------------------------------------------------------------------------------

describe("placing the note box", () => {
  const box = { width: 320, height: 160 };
  const viewport = { width: 1400, height: 900 };
  const rect = (top: number, left: number, height: number, width: number): Rect =>
    ({ top, left, bottom: top + height, right: left + width, width, height });
  const boxRect = (at: Placed): Rect => rect(at.top, at.left, box.height, box.width);
  const overlap = (a: Rect, b: Rect) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;

  it("goes below what the note is about when there is room, without touching it", () => {
    const anchor = rect(200, 300, 40, 120);
    const at = api.placeNoteBox(anchor, null, box, viewport);
    expect(at.scrollBy).toBe(0);
    expect(at.top).toBeGreaterThan(anchor.bottom);
    expect(overlap(boxRect(at), anchor)).toBe(false);
    expect(at.left).toBe(anchor.left);
  });

  it("goes above when there is no room below, without touching it", () => {
    const anchor = rect(700, 300, 40, 120);
    const at = api.placeNoteBox(anchor, null, box, viewport);
    expect(at.scrollBy).toBe(0);
    expect(at.top + box.height).toBeLessThan(anchor.top);
    expect(overlap(boxRect(at), anchor)).toBe(false);
  });

  it("clears the whole question that contains the anchor, above and below", () => {
    // A note on a paragraph near the top of a tall question: below the paragraph is still inside the question.
    const question = rect(120, 280, 500, 600);
    const anchor = rect(140, 300, 30, 200);
    const below = api.placeNoteBox(anchor, question, box, viewport);
    expect(overlap(boxRect(below), anchor)).toBe(false);
    expect(overlap(boxRect(below), question)).toBe(false);
    expect(below.top).toBeGreaterThan(question.bottom);

    // And with the question running to the bottom of the screen, it goes above the whole question.
    const tall = rect(300, 280, 580, 600);
    const anchorInTall = rect(820, 300, 30, 200);
    const above = api.placeNoteBox(anchorInTall, tall, box, viewport);
    expect(overlap(boxRect(above), anchorInTall)).toBe(false);
    expect(overlap(boxRect(above), tall)).toBe(false);
    expect(above.top + box.height).toBeLessThan(tall.top);
  });

  it("stays inside the viewport when the anchor is at the right edge or off the left", () => {
    const atRight = api.placeNoteBox(rect(100, 1380, 20, 20), null, box, viewport);
    expect(atRight.left + box.width).toBeLessThanOrEqual(viewport.width);
    const atLeft = api.placeNoteBox(rect(100, -40, 20, 20), null, box, viewport);
    expect(atLeft.left).toBeGreaterThanOrEqual(0);
    const narrow = api.placeNoteBox(rect(100, 10, 20, 20), null, box, { width: 300, height: 900 });
    expect(narrow.left).toBeGreaterThanOrEqual(0);
  });

  it("when it fits neither above nor below, scrolls the page and still goes below, never on top", () => {
    // A tall anchor filling most of a short screen: there is no room either side of it as the page stands.
    const viewportShort = { width: 800, height: 400 };
    const anchor = rect(60, 100, 300, 300);
    const at = api.placeNoteBox(anchor, null, box, viewportShort);
    expect(at.scrollBy).toBeGreaterThan(0);
    // top is where the box lands AFTER that scroll, and the anchor has moved up by the same amount.
    const anchorAfter = rect(anchor.top - at.scrollBy, anchor.left, anchor.height, anchor.width);
    expect(overlap(boxRect(at), anchorAfter)).toBe(false);
    expect(at.top).toBeGreaterThan(anchorAfter.bottom);
    // What the note is about is still on screen - the page never scrolls it away to make room.
    expect(anchorAfter.bottom).toBeGreaterThan(0);
  });
});
