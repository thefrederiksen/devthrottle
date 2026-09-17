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
type Api = {
  selectorFor(el: Element): string;
  escapeIdent(value: string): string;
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

  it("never anchors to the notes tray the script added", () => {
    document.body.innerHTML = `<p>text</p>`;
    api.start({ window });
    const hosts = Array.from(document.querySelectorAll("[data-dev-report-ui]")).filter((h) => h.shadowRoot);
    expect(hosts.length).toBe(1);
    // A click inside a shadow root reaches the page as a click on its host, so the host is what is checked.
    expect(api.anchorFor(hosts[0])).toBeNull();
    expect(ui("[data-drn=send]")).not.toBeNull();
  });

  it("keeps the tray out of reach of the report's CSS", () => {
    // Second review, finding 6: a report stylesheet could hide the tray by its class name.
    document.body.innerHTML = `<p>text</p>`;
    api.start({ window });
    expect(document.querySelector(".drn-tray")).toBeNull();
    const host = Array.from(document.querySelectorAll<HTMLElement>("[data-dev-report-ui]")).find((h) => h.shadowRoot)!;
    expect(host.shadowRoot!.querySelector(".drn-tray")).not.toBeNull();
    expect(host.style.getPropertyPriority("display")).toBe("important");
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

  it("keeps sent items in the queue until the host confirms each one", () => {
    // Review finding 2: posting a message is not the host accepting it.
    const model = api.createModel();
    model.queueNote(anchor, "one");
    model.queueNote(anchor, "two");
    expect(model.sendMessage()).toMatchObject({ channel: "devthrottle.dev-report", version: 1, type: "send" });
    expect(model.sendMessage().payload.items.map((i) => i.text)).toEqual(["one", "two"]);
    model.markPending();
    expect(model.snapshot().queued.map((i) => i.pending)).toEqual([true, true]);
    expect(model.snapshot().sent).toEqual([]);
    expect(model.sendMessage().payload.items[0]).not.toHaveProperty("pending");

    model.applyStatus([{ id: "n2", status: "held", statusLabel: "Delivered when the agent finishes" }, { id: "zz", status: "x", statusLabel: "x" }]);
    const s = model.snapshot();
    expect(s.queued.map((i) => i.id)).toEqual(["n1"]);
    expect(s.sent).toEqual([{ id: "n2", kind: "note", text: "two", anchor, status: "held", statusLabel: "Delivered when the agent finishes" }]);
  });

  it("keeps a refused item queued with the host's reason, ready to send again", () => {
    const model = api.createModel();
    model.queueNote(anchor, "one");
    model.markPending();
    model.applyStatus([{ id: "n1", status: "refused", statusLabel: "This session has ended" }]);
    expect(model.snapshot().queued).toEqual([{ id: "n1", kind: "note", text: "one", anchor, pending: false, statusLabel: "This session has ended" }]);
    expect(model.snapshot().sent).toEqual([]);
  });

  it("cannot remove an item the host already has", () => {
    const model = api.createModel();
    model.queueNote(anchor, "one");
    model.markPending();
    model.remove("n1");
    expect(model.snapshot().queued).toHaveLength(1);
  });

  it("gives a re-answer a new id when the earlier answer is already with the host", () => {
    const model = api.createModel();
    model.queueAnswer({ questionId: "q1", question: "Q?", optionValue: "a", optionLabel: "A", comment: "" });
    model.markPending();
    const again = model.queueAnswer({ questionId: "q1", question: "Q?", optionValue: "b", optionLabel: "B", comment: "" });
    expect(again.id).toBe("a2");
    expect(model.snapshot().queued.map((i) => i.id)).toEqual(["a1", "a2"]);
  });

  it("keeps at most the pending answer and the newest revision when an answer is changed twice", () => {
    // Second review, new finding B.
    const model = api.createModel();
    const answer = (v: string) => ({ questionId: "q1", question: "Q?", optionValue: v, optionLabel: v, comment: "" });
    model.queueAnswer(answer("A"));
    model.markPending();
    model.queueAnswer(answer("B"));
    model.queueAnswer(answer("C"));
    expect(model.sendMessage().payload.items.map((i) => `${i.id}:${i.optionValue}`)).toEqual(["a1:A", "a2:C"]);
  });

  it("does not reuse an id already in the sent list", () => {
    const model = api.createModel();
    model.queueNote(anchor, "one");
    model.markPending();
    model.applyStatus([{ id: "n1", status: "delivered", statusLabel: "Delivered" }]);
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
      { id: "a1", kind: "answer", questionId: "deploy", question: "When should we deploy?", optionValue: "tonight", optionLabel: "Tonight", comment: "after 8pm" },
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
    expect(css).toMatch(/\.drn-tray, \.drn-row \{ font-family: -apple-system/);
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
