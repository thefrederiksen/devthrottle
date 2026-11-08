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
type State = {
  queued: Item[];
  sent: Item[];
  replies: unknown[];
  draft: { anchor: Anchor; text: string } | null;
  answerDrafts: unknown[];
  scroll: { x: number; y: number };
};
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
    // The third radio is the "Something else" option the script adds to every question; it starts unchecked.
    expect(checked).toEqual([false, true, false]);
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

  it("draws the Something else box and the microphone row in the report's light look, whatever the host theme", () => {
    const css = loadAsInjected({ "data-dev-report-theme": JSON.stringify(theme) }).cssText();
    const rule = (selector: string) => {
      const start = css.indexOf(`${selector} {`);
      expect(start, selector).toBeGreaterThanOrEqual(0);
      return css.slice(start, css.indexOf("}", start));
    };
    const box = rule(".drn-own textarea");
    expect(box).toContain("background: #ffffff");
    expect(box).toContain("color: #1f2937");
    expect(box).toContain("border: 1px solid #9ca3af");
    expect(rule(".drn-mic-btn")).toContain("background: #e5e7eb");
    expect(rule(".drn-mic-state")).toContain("background: #f3f4f6");
    expect(rule(".drn-mic-state")).toContain("color: #1f2937");
    for (const selector of [".drn-own", ".drn-own textarea", ".drn-mic", ".drn-mic-btn", ".drn-mic-state"]) {
      for (const key of ["background", "surface", "surface2", "border", "text"] as const) {
        expect(rule(selector), `${selector} uses the host's ${key}`).not.toContain(theme[key]);
      }
    }
    // The note box floats over the report on the app's surface: its microphone row keeps the host theme.
    expect(rule(".drn-notebox .drn-mic-btn")).toContain(`background: ${theme.surface2}`);
    expect(rule(".drn-notebox .drn-mic-btn")).toContain(`color: ${theme.text}`);
    expect(rule(".drn-notebox .drn-mic-state")).toContain(`background: ${theme.surface2}`);
    expect(rule(".drn-notebox .drn-mic")).toContain(`color: ${theme.text}`);
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
// The tray: the pill that used to float in the corner of the report. Hosted, the app's panel holds these
// controls and the page draws none of them (issue #3077).
const TRAY_PARTS = ["toggle", "pick", "note-selection"];
// The note box, which is NOT the tray: it has its own shadow host and is placed against the element the note
// is about, so it stays in the page whoever is hosting it.
const NOTE_BOX_PARTS = ["composer", "composer-text", "composer-queue", "composer-cancel"];

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
  fromPage: Array<{ type: string; payload: { state: State; items?: Item[]; picking?: boolean; selectionQuote?: string | null } }>;
  restore(state?: State): Promise<void>;
  /** Post any host message over the real port, and wait for the page to have handled it. */
  post(type: string, payload: unknown): Promise<void>;
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
    async post(type: string, payload: unknown) {
      const before = fromPage.length;
      port.postMessage(envelope(type, payload));
      // Same trick as restore: a status is answered with a state-changed, and a port delivers in order, so
      // that answer arriving proves the message ahead of it was handled.
      port.postMessage(envelope("status", { updates: [] }));
      for (let i = 0; i < 400 && fromPage.length === before; i++) await new Promise((resolve) => setTimeout(resolve, 5));
      if (fromPage.length === before) throw new Error("the page never answered its host");
    },
  };
}

describe("hosted, the app owns the conversation", () => {
  it("draws no tray at all - no conversation, and no floating note controls over the report", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.restore();

    expect(host.page.isHosted()).toBe(true);
    for (const part of CONVERSATION_PARTS) expect(ui(`[data-drn=${part}]`), part).toBeNull();
    expect(uiAll(".drn-h")).toHaveLength(0);
    for (const heading of ["Queued - not sent yet", "Sent", "Replies from the agent"]) {
      expect(uiText(), heading).not.toContain(heading);
    }
    // ISSUE #3077. These three used to stay behind as a pill pinned over the report - a queued count the
    // app's panel was already showing, and two buttons on top of the words. The app holds them now.
    for (const part of TRAY_PARTS) expect(ui(`[data-drn=${part}]`), part).toBeNull();
    expect(uiText()).not.toContain("Add a note");
    // What stays: the note box, which has to be in here because it is placed against what the note is
    // about, and each question's own Queue button, which is part of the report.
    for (const part of NOTE_BOX_PARTS) expect(ui(`[data-drn=${part}]`), part).not.toBeNull();
    expect(uiAll("[data-drn=queue-answer]")).toHaveLength(1);
  });

  it("unhosted, the page keeps the whole tray - the conversation and Send belong to a page with no app", () => {
    document.body.innerHTML = pageReport;
    const page = api.start({ window });
    expect(page.isHosted()).toBe(false);
    for (const part of CONVERSATION_PARTS.concat(TRAY_PARTS, NOTE_BOX_PARTS)) expect(ui(`[data-drn=${part}]`), part).not.toBeNull();
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
    //
    // Hosted, the note is armed by the app (a note-mode message) and unhosted by the page's own button. The
    // arming differs; everything after the click is the same code, and the state that comes out must match.
    function noteAndAnswer(armed: () => void) {
      armed();
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
    await host.post("note-mode", { mode: "pick" });
    noteAndAnswer(() => {});
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
    noteAndAnswer(() => clickOn(ui("[data-drn=pick]")!));

    expect(withoutIds(hostedState)).toEqual(withoutIds(unhosted.model.snapshot()));
  });
});

// THE APP'S NOTE CONTROLS, REACHING INTO THE PAGE (issue #3077).
//
// The controls are in the app's panel and the anchor can only be picked inside the report, so the two talk
// over the port they already share: the app says pick / selection / off, and the page says back where
// note-taking stands. Both directions are checked here against the real script.
describe("note-mode", () => {
  it("arms picking when the app asks, and a click then opens the note box against what was clicked", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.restore();

    await host.post("note-mode", { mode: "pick" });

    expect(document.documentElement.classList.contains("drn-picking")).toBe(true);
    const armed = host.fromPage.filter((m) => m.type === "note-mode-changed");
    expect(armed[armed.length - 1].payload.picking).toBe(true);

    clickOn(document.getElementById("para")!);

    // The note box is open, and it is about the paragraph that was clicked - which is the whole reason this
    // part of note-taking cannot move into the app.
    expect(ui("[data-drn=composer]")!.hasAttribute("hidden")).toBe(false);
    expect(host.page.model.snapshot().draft!.anchor.quote).toBe("A paragraph.");
    // And picking ended BY ITSELF, so the app is told without having asked.
    await new Promise((resolve) => setTimeout(resolve, 0));
    const after = host.fromPage.filter((m) => m.type === "note-mode-changed");
    expect(after[after.length - 1].payload.picking).toBe(false);
  });

  it("cancels picking when the app asks", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.restore();
    await host.post("note-mode", { mode: "pick" });

    await host.post("note-mode", { mode: "off" });

    expect(document.documentElement.classList.contains("drn-picking")).toBe(false);
    const said = host.fromPage.filter((m) => m.type === "note-mode-changed");
    expect(said[said.length - 1].payload.picking).toBe(false);
  });

  it("tells the app what the reader has selected, and notes it when the app asks", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.restore();

    const para = document.getElementById("para")!;
    const range = document.createRange();
    range.selectNodeContents(para);
    const selection = window.getSelection()!;
    // ONE EVENT, AND IT IS WAITED FOR. Changing the selection makes the document fire selectionchange by itself a
    // moment later, as a browser does. This test used to fire one by hand as well and go on after a pause, with
    // the document's own still on its way: when that one arrived after the app had asked for the note, the page
    // offered the selection again and the last line below failed. Nothing is selected at the start, so adding the
    // range is one change and one event, and nothing is asked of the page until it has been heard.
    expect(selection.rangeCount).toBe(0);
    let heard = 0;
    const count = () => (heard += 1);
    document.addEventListener("selectionchange", count);
    const before = host.fromPage.length;
    selection.addRange(range);
    for (let i = 0; i < 400 && heard === 0; i++) await new Promise((resolve) => setTimeout(resolve, 5));
    // The page told the app in that same event; its message crosses the port a moment after.
    for (let i = 0; i < 400 && host.fromPage.length === before; i++) await new Promise((resolve) => setTimeout(resolve, 5));

    const told = host.fromPage.filter((m) => m.type === "note-mode-changed");
    expect(told[told.length - 1].payload.selectionQuote).toBe("A paragraph.");

    await host.post("note-mode", { mode: "selection" });

    const draft = host.page.model.snapshot().draft!;
    expect(draft.anchor.type).toBe("text");
    expect(draft.anchor.quote).toBe("A paragraph.");
    // The selection has been used up, so the app stops offering it.
    const afterwards = host.fromPage.filter((m) => m.type === "note-mode-changed");
    expect(afterwards[afterwards.length - 1].payload.selectionQuote).toBeNull();
    // And no second event was on its way: the one that was waited for is the only one there has been.
    document.removeEventListener("selectionchange", count);
    expect(heard).toBe(1);
  });

  it("says where note-taking stands as soon as it is hosted, before anything is asked of it", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();

    await host.restore();

    const said = host.fromPage.filter((m) => m.type === "note-mode-changed");
    expect(said.length).toBeGreaterThan(0);
    expect(said[0].payload).toEqual({ picking: false, selectionQuote: null });
  });

  it("ignores a note-mode message that is not one of the three modes", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.restore();

    await host.post("note-mode", { mode: "delete-everything" });

    expect(document.documentElement.classList.contains("drn-picking")).toBe(false);
    expect(host.page.model.snapshot().draft).toBeNull();
  });

  it("says nothing about note-taking while it is unhosted - there is no panel to tell", () => {
    document.body.innerHTML = pageReport;
    const page = api.start({ window });

    clickOn(ui("[data-drn=pick]")!);

    expect(page.isHosted()).toBe(false);
    expect(document.documentElement.classList.contains("drn-picking")).toBe(true);
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

// ---------------------------------------------------------------------------------------------------
// REPORT ANSWERS BY VOICE: "Something else" on every question, and dictation into every box.
// ---------------------------------------------------------------------------------------------------

const listReport = `
  <section data-dev-report="questions">
    <div data-dev-report-question="stall" data-dev-report-question-text="Tell you when it stalls?">
      <ul>
        <li><label><input type="radio" name="stall" value="seven" data-recommended> After 7 quiet days</label></li>
        <li><input type="radio" id="gitte" name="stall" value="gitte"> <label for="gitte">Email Gitte instead</label></li>
      </ul>
    </div>
  </section>`;

function ownRadio(): HTMLInputElement {
  return document.querySelector<HTMLInputElement>("input[value=own-answer]")!;
}

function pick(radio: HTMLInputElement) {
  radio.checked = true;
  radio.dispatchEvent(new Event("change", { bubbles: true }));
}

function type(box: HTMLTextAreaElement, text: string) {
  box.value = text;
  box.dispatchEvent(new Event("input", { bubbles: true }));
}

describe("Something else - the owner's own answer", () => {
  it("adds one last option, Something else, to every question, after the author's last option", () => {
    document.body.innerHTML = pageReport;
    api.start({ window });
    const radios = Array.from(document.querySelectorAll<HTMLInputElement>("[data-dev-report-question=deploy] input[type=radio]"));
    expect(radios.map((r) => r.value)).toEqual(["tonight", "monday", "own-answer"]);
    expect(ownRadio().closest("label")!.textContent!.trim()).toBe("Something else");
    // It shares the question's name, so the browser treats it as one more option of the same question.
    expect(ownRadio().name).toBe("deploy");
    // Not checked at start: the recommendation is.
    expect(ownRadio().checked).toBe(false);
  });

  it("keeps a list a list, and goes after an option whose label follows its radio", () => {
    document.body.innerHTML = listReport;
    api.start({ window });
    const items = Array.from(document.querySelectorAll("[data-dev-report-question=stall] li"));
    expect(items).toHaveLength(3);
    expect(items[2].querySelector("input")!.value).toBe("own-answer");
  });

  it("adds none where the author already used the reserved value, so the two cannot be confused", () => {
    document.body.innerHTML = pageReport.replace('value="monday"', 'value="own-answer"');
    api.start({ window });
    expect(document.querySelectorAll("input[value=own-answer]")).toHaveLength(1);
    expect(document.querySelector("[data-drn-own-answer-option]")).toBeNull();
  });

  it("shows its box only while it is picked, and Queue answer waits for words in it", () => {
    document.body.innerHTML = pageReport;
    api.start({ window });
    const box = ui("[data-drn=own-answer-box]")!;
    const queue = ui<HTMLButtonElement>("[data-drn=queue-answer]")!;
    expect(box.hasAttribute("hidden")).toBe(true);
    expect(queue.disabled).toBe(false);

    pick(ownRadio());
    expect(box.hasAttribute("hidden")).toBe(false);
    expect(queue.disabled).toBe(true);

    type(ui<HTMLTextAreaElement>("[data-drn=own-answer]")!, "   ");
    expect(queue.disabled).toBe(true);
    type(ui<HTMLTextAreaElement>("[data-drn=own-answer]")!, "Email me after 3 days");
    expect(queue.disabled).toBe(false);

    pick(document.querySelector<HTMLInputElement>("input[value=monday]")!);
    expect(box.hasAttribute("hidden")).toBe(true);
    expect(queue.disabled).toBe(false);
  });

  it("queues the owner's own words as the answer, marked as their own and not one of the author's options", () => {
    document.body.innerHTML = pageReport;
    const page = api.start({ window });
    pick(ownRadio());
    type(ui<HTMLTextAreaElement>("[data-drn=own-answer]")!, "  Email me after 3 days, not 7, and copy Gitte  ");
    type(document.querySelector<HTMLTextAreaElement>("textarea[data-dev-report-comment]")!, "thanks");
    clickOn(ui("[data-drn=queue-answer]")!);

    expect(page.model.snapshot().queued).toEqual([
      {
        id: expect.stringMatching(/^a-[0-9a-f]{32}$/),
        kind: "answer",
        questionId: "deploy",
        question: "When should we deploy?",
        optionValue: "own-answer",
        optionLabel: "Something else",
        ownAnswer: "Email me after 3 days, not 7, and copy Gitte",
        comment: "thanks",
      },
    ]);
    expect(ui("[data-drn=question-state]")!.textContent).toContain('Queued: Something else: "Email me after 3 days, not 7, and copy Gitte"');
  });

  it("keeps the own words in the draft the host holds, and puts them back on a restore", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.restore();
    pick(ownRadio());
    type(ui<HTMLTextAreaElement>("[data-drn=own-answer]")!, "half an answer");
    // A round trip over the port, so the state the typing posted has arrived before it is read.
    await host.post("status", { updates: [] });
    const state = host.fromPage[host.fromPage.length - 1].payload.state;
    expect(state.answerDrafts).toEqual([{ questionId: "deploy", optionValue: "own-answer", comment: "", ownAnswer: "half an answer" }]);

    // A fresh page, restored from that state: the option is picked again and its box shows the words.
    document.body.innerHTML = pageReport;
    delete (window as unknown as Record<symbol, unknown>)[api.started];
    const again = startFramed();
    await again.restore(state);
    expect(ownRadio().checked).toBe(true);
    expect(ui<HTMLTextAreaElement>("[data-drn=own-answer]")!.value).toBe("half an answer");
    expect(ui("[data-drn=own-answer-box]")!.hasAttribute("hidden")).toBe(false);
  });

  it("refuses an own answer without words, and any other answer that carries ownAnswer", () => {
    const base = { id: "a1", kind: "answer", questionId: "q", question: "Q", optionLabel: "L", comment: "" };
    const validItem = (globals.DevReportNotes as unknown as { validItem(i: unknown): boolean }).validItem;
    expect(validItem({ ...base, optionValue: "own-answer", ownAnswer: "words" })).toBe(true);
    expect(validItem({ ...base, optionValue: "own-answer" })).toBe(false);
    expect(validItem({ ...base, optionValue: "own-answer", ownAnswer: "  " })).toBe(false);
    expect(validItem({ ...base, optionValue: "yes", ownAnswer: "words" })).toBe(false);
    expect(validItem({ ...base, optionValue: "yes" })).toBe(true);
  });
});

async function untilFromPage(host: Hosted, type: string): Promise<{ type: string; payload: Record<string, unknown> }> {
  for (let i = 0; i < 400; i++) {
    const found = host.fromPage.find((m) => m.type === type);
    if (found) return found as unknown as { type: string; payload: Record<string, unknown> };
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  throw new Error(`the page never posted ${type}`);
}

function micFor(fieldId: string) {
  const row = ui(`[data-drn=mic][data-field-id="${fieldId}"]`)!;
  return {
    row,
    button: row.querySelector<HTMLButtonElement>("[data-drn=mic-button]")!,
    state: row.querySelector<HTMLElement>("[data-drn=mic-state]")!,
  };
}

describe("dictation into a report's boxes", () => {
  it("draws no microphone on a plain file with no host", () => {
    document.body.innerHTML = pageReport;
    api.start({ window });
    const rows = uiAll("[data-drn=mic]");
    // The comment box, the own answer box and the note box each have one - all hidden.
    expect(rows.map((r) => r.getAttribute("data-field-id")).sort()).toEqual(["note", "q0:comment", "q0:own"]);
    for (const row of rows) expect(row.hasAttribute("hidden")).toBe(true);
  });

  it("draws no microphone when the host does not record, and one under every box when it does", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.restore();
    for (const row of uiAll("[data-drn=mic]")) expect(row.hasAttribute("hidden")).toBe(true);

    await host.post("restore", { state: emptyState, dictation: true });
    for (const row of uiAll("[data-drn=mic]")) expect(row.hasAttribute("hidden")).toBe(false);
  });

  it("asks the host to record on the first tap and to stop on the red square, showing each state in the box", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.post("restore", { state: emptyState, dictation: true });
    const mic = micFor("q0:comment");

    clickOn(mic.button);
    expect((await untilFromPage(host, "dictation-start")).payload).toEqual({ fieldId: "q0:comment" });
    // Feedback at once, before the host has said anything.
    expect(mic.state.textContent).toBe("Starting the microphone...");

    await host.post("dictation", { fieldId: "q0:comment", phase: "recording", seconds: 7, level: 0.5 });
    expect(mic.state.textContent).toBe("Recording 0:07 - tap the square to stop");
    expect(mic.button.getAttribute("aria-label")).toBe("Stop recording");
    expect(mic.row.classList.contains("drn-live")).toBe(true);

    clickOn(mic.button);
    expect((await untilFromPage(host, "dictation-stop")).payload).toEqual({ fieldId: "q0:comment" });
    expect(mic.state.textContent).toBe("Transcribing...");
    expect(mic.button.disabled).toBe(true);
  });

  it("adds the words to the end of what was typed, exactly as the host sent them, and saves the draft", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.post("restore", { state: emptyState, dictation: true });
    const comment = document.querySelector<HTMLTextAreaElement>("textarea[data-dev-report-comment]")!;
    type(comment, "Typed first.");
    clickOn(micFor("q0:comment").button);
    await untilFromPage(host, "dictation-start");

    await host.post("dictation", { fieldId: "q0:comment", phase: "done", text: "then said,  this" });

    expect(comment.value).toBe("Typed first. then said,  this");
    expect(micFor("q0:comment").state.textContent).toBe("Added what you said. Read it before you queue.");
    const state = host.fromPage[host.fromPage.length - 1].payload.state;
    expect(state.answerDrafts).toEqual([{ questionId: "deploy", optionValue: "tonight", comment: "Typed first. then said,  this" }]);
  });

  it("fills the own answer box and enables Queue answer once the words land", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.post("restore", { state: emptyState, dictation: true });
    pick(ownRadio());
    const queue = ui<HTMLButtonElement>("[data-drn=queue-answer]")!;
    expect(queue.disabled).toBe(true);
    clickOn(micFor("q0:own").button);
    await untilFromPage(host, "dictation-start");

    await host.post("dictation", { fieldId: "q0:own", phase: "done", text: "Email me after 3 days" });

    expect(ui<HTMLTextAreaElement>("[data-drn=own-answer]")!.value).toBe("Email me after 3 days");
    expect(queue.disabled).toBe(false);
  });

  it("says a failure plainly and leaves what was typed unchanged", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.post("restore", { state: emptyState, dictation: true });
    const comment = document.querySelector<HTMLTextAreaElement>("textarea[data-dev-report-comment]")!;
    type(comment, "Keep me.");
    clickOn(micFor("q0:comment").button);
    await untilFromPage(host, "dictation-start");

    await host.post("dictation", { fieldId: "q0:comment", phase: "failed", reason: "The microphone is in use by another application." });

    expect(comment.value).toBe("Keep me.");
    const mic = micFor("q0:comment");
    expect(mic.state.textContent).toBe("Not added: The microphone is in use by another application. What was in the box is unchanged.");
    expect(mic.row.classList.contains("drn-failed")).toBe(true);
    expect(mic.button.disabled).toBe(false);
  });

  it("records one box at a time: the other microphones are disabled until it finishes", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.post("restore", { state: emptyState, dictation: true });
    clickOn(micFor("q0:comment").button);
    await untilFromPage(host, "dictation-start");

    expect(micFor("q0:own").button.disabled).toBe(true);
    expect(micFor("note").button.disabled).toBe(true);
    clickOn(micFor("note").button);
    expect(host.fromPage.filter((m) => m.type === "dictation-start")).toHaveLength(1);

    await host.post("dictation", { fieldId: "q0:comment", phase: "done", text: "done" });
    expect(micFor("note").button.disabled).toBe(false);
  });

  it("dictates into the note box that Add a note opens", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.post("restore", { state: emptyState, dictation: true });
    await host.post("note-mode", { mode: "pick" });
    clickOn(document.getElementById("para")!);
    clickOn(micFor("note").button);
    await untilFromPage(host, "dictation-start");

    await host.post("dictation", { fieldId: "note", phase: "done", text: "Say more here" });

    expect(ui<HTMLTextAreaElement>("[data-drn=composer-text]")!.value).toBe("Say more here");
    expect(host.page.model.snapshot().draft!.text).toBe("Say more here");
  });

  it("ignores a dictation message that is out of shape or names no box on this page", async () => {
    document.body.innerHTML = pageReport;
    const host = startFramed();
    await host.post("restore", { state: emptyState, dictation: true });
    expect(api.parseInbound(envelope("dictation", { fieldId: "q0:comment", phase: "done" }))).toBeNull();
    expect(api.parseInbound(envelope("dictation", { fieldId: "q0:comment", phase: "shouting" }))).toBeNull();
    expect(api.parseInbound(envelope("dictation", { fieldId: "q0:comment", phase: "recording", level: 2 }))).toBeNull();
    expect(api.parseInbound(envelope("dictation", { fieldId: "", phase: "starting" }))).toBeNull();

    const comment = document.querySelector<HTMLTextAreaElement>("textarea[data-dev-report-comment]")!;
    await host.post("dictation", { fieldId: "q9:comment", phase: "done", text: "nowhere" });
    expect(comment.value).toBe("");
  });
});
