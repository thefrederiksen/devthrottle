// The verdict panel, rendered against a fake Gateway (the Wingman-on-every-turn mission, slice E).
//
// What these prove that a Gateway test cannot: every field the Gateway stamped reaches the screen, in the order
// the plan names, and a tap sends exactly the request the route expects - one request, the verdict it came from,
// the options in the order they were picked. A refusal is the other half: the route's sentence is the one on the
// screen, character for character, so a panel that swallowed it or composed its own would fail.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { SessionDto } from "../api/client";
import { VerdictPanel } from "./VerdictPanel";
import type { TurnVerdict } from "./verdictAnswer";
import { VERDICT_WORDS } from "./verdictVocabulary";

const SID = "5b0c2e7a-0000-4000-8000-000000000001";

function verdict(overrides: Partial<TurnVerdict> = {}): TurnVerdict {
  return {
    verdictId: "tv-panel-1",
    verdict: "needed-you",
    confidence: "high",
    evidence: "Apply the migration to the local database now?",
    label: "Apply the migration now?",
    summary: "The migration is written; the session is asking before it changes the database.",
    agentRecommends: null,
    answerVia: "keys",
    menu: { question: "Apply the migration now?", selectionMode: "single", submit: "" },
    options: [
      { key: "Yes, apply it", send: "1", recommended: true, note: "Changes the local database." },
      { key: "No, leave it", send: "2", recommended: false, note: "Nothing changes." },
    ],
    risk: "none",
    ...overrides,
  };
}

function session(
  v: TurnVerdict | null,
  verdictState = "judged",
  agent: { agent: string; agentToolDisplay?: string } = { agent: "ClaudeCode", agentToolDisplay: "Claude Code" },
): SessionDto {
  return { sessionId: SID, verdictState, turnVerdict: v, ...agent } as unknown as SessionDto;
}

let calls: { url: string; method: string; body: unknown }[] = [];

function fakeGateway(status: number, body: Record<string, unknown>) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({
        url,
        method: init?.method ?? "GET",
        body: init?.body === undefined ? undefined : JSON.parse(String(init.body)),
      });
      return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    }),
  );
}

const SENT = { accepted: true, code: "owner-answered", reason: "Sent to the session.", verdictId: "tv-panel-1" };

/** A fake Gateway that answers each route in its own words, for the journeys that make more than one call - the
 *  history read that finds a superseded verdict, and then the correction sent about it. */
function fakeGatewayByRoute(routes: { match: string; status: number; body: Record<string, unknown> }[]) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({
        url,
        method: init?.method ?? "GET",
        body: init?.body === undefined ? undefined : JSON.parse(String(init.body)),
      });
      const route = routes.find((r) => url.includes(r.match));
      if (route === undefined) throw new Error(`the test fake has no answer for ${url}`);
      return new Response(JSON.stringify(route.body), {
        status: route.status,
        headers: { "Content-Type": "application/json" },
      });
    }),
  );
}

/** The history read, answering with the newest record - superseded, as it is by the time the owner looks. */
const HISTORY = (v: TurnVerdict | null) => ({
  match: "/turn-verdicts",
  status: 200,
  body: { sessionId: SID, verdicts: v === null ? [] : [v] },
});

beforeEach(() => {
  calls = [];
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the verdict panel", () => {
  it("renders nothing for another session's row, and answers to the screen's own session id", async () => {
    fakeGateway(200, SENT);
    const OTHER = "5b0c2e7a-0000-4000-8000-000000000002";
    const { container, rerender } = render(<VerdictPanel sessionId={OTHER} session={session(verdict())} />);
    expect(container.innerHTML).toBe("");

    rerender(<VerdictPanel sessionId={SID} session={session(verdict())} />);
    fireEvent.click(screen.getByRole("button", { name: "Yes, apply it" }));
    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0].url).toBe(`/sessions/${SID}/turn-verdict/answer`);
  });

  // ASSERTION CHANGED IN ROUND 2, and the old one is the defect it was pinning: it said a row carrying no judged
  // verdict renders nothing FULL STOP, which is exactly how the owner lost the reporting control the moment he
  // answered. What is true now is narrower and is what this asserts - nothing is rendered when the row carries
  // none AND the history holds none either.
  it("renders nothing when the row carries no judged verdict and the history holds none", async () => {
    fakeGatewayByRoute([HISTORY(null)]);
    const { container, rerender } = render(<VerdictPanel sessionId={SID} session={session(null)} />);
    await waitFor(() => expect(calls).toHaveLength(1));
    expect(container.innerHTML).toBe("");

    // A failed or reading row is not an answer to show either, and the same history read decides it.
    rerender(<VerdictPanel sessionId={SID} session={session(verdict(), "failed")} />);
    await waitFor(() => expect(container.innerHTML).toBe(""));
  });

  // ---- The record a session left behind (slice G, round 2) ----

  it("shows the last verdict from the history, collapsed, when the row carries none", async () => {
    const past = verdict({ verdictId: "tv-superseded", supersededAtUtc: "2026-09-16T01:00:00Z" });
    fakeGatewayByRoute([HISTORY(past)]);

    render(<VerdictPanel sessionId={SID} session={session(null)} />);

    // The read is the history one, and it is made once.
    await waitFor(() => expect(screen.getByText("Apply the migration now?")).toBeTruthy());
    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe("GET");
    expect(calls[0].url).toContain(`/sessions/${SID}/turn-verdicts`);

    // COLLAPSED: the receipt is there and closed, where a live stop shows it open.
    const receipt = screen.getByText("Claude Code said").closest("details");
    expect(receipt?.hasAttribute("open")).toBe(false);

    // ONLY "This is wrong" is live. The options are not offered at all: the screen this was formed on has moved
    // on, so a button that pretended to answer it would be a button the route refuses.
    expect(screen.getByRole("button", { name: "This is wrong" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Yes, apply it" })).toBeNull();
    expect(screen.queryByRole("button", { name: "No, leave it" })).toBeNull();
    expect(screen.getByLabelText("Wingman verdict, superseded")).toBeTruthy();
  });

  it("reports the superseded verdict wrong, naming the id the history gave it", async () => {
    const past = verdict({ verdictId: "tv-superseded", supersededAtUtc: "2026-09-16T01:00:00Z" });
    fakeGatewayByRoute([
      HISTORY(past),
      {
        match: "/turn-verdict/feedback",
        status: 200,
        body: {
          accepted: true,
          code: "feedback-recorded",
          reason: "Recorded. This stop will be graded against your word, not the Wingman's.",
          verdictId: "tv-superseded",
        },
      },
    ]);

    render(<VerdictPanel sessionId={SID} session={session(null)} />);
    await waitFor(() => expect(screen.getByRole("button", { name: "This is wrong" })).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "This is wrong" }));
    fireEvent.change(screen.getByLabelText("What should it have said?"), { target: { value: "finished" } });
    fireEvent.click(screen.getByRole("button", { name: "Send the correction" }));

    await waitFor(() => expect(calls).toHaveLength(2));
    expect(calls[1].method).toBe("POST");
    expect(calls[1].url).toContain(`/sessions/${SID}/turn-verdict/feedback`);
    expect(calls[1].body).toEqual({ verdictId: "tv-superseded", correctVerdict: "finished", note: "" });
    await waitFor(() =>
      expect(screen.getByText("Recorded. This stop will be graded against your word, not the Wingman's.")).toBeTruthy(),
    );
  });

  it("shows the history read's own refusal rather than rendering nothing", async () => {
    fakeGatewayByRoute([
      { match: "/turn-verdicts", status: 403, body: { error: "this account's turn verdicts are a shadow record" } },
    ]);

    render(<VerdictPanel sessionId={SID} session={session(null)} />);

    // "The read was refused" and "this session was never judged" are different facts, and the panel must not
    // render them the same way.
    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("shadow record"));
  });

  it("shows the receipt expanded, the label and the summary, verbatim", () => {
    render(<VerdictPanel sessionId={SID} session={session(verdict())} />);

    const receipt = screen.getByText("Claude Code said").closest("details");
    expect(receipt).not.toBeNull();
    expect(receipt!.open).toBe(true);
    expect(screen.getByText("Apply the migration to the local database now?")).toBeTruthy();
    expect(screen.getByText("Apply the migration now?", { selector: ".verdict-label" })).toBeTruthy();
    expect(
      screen.getByText("The migration is written; the session is asking before it changes the database."),
    ).toBeTruthy();
  });

  it("heads the receipt with the row's own agent, as the Gateway stamped its name", () => {
    const { container, rerender } = render(
      <VerdictPanel sessionId={SID} session={session(verdict(), "judged", { agent: "Codex", agentToolDisplay: "Codex" })} />,
    );
    const heading = () => container.querySelector(".verdict-receipt summary")!.textContent;
    expect(heading()).toBe("Codex said");

    rerender(<VerdictPanel sessionId={SID} session={session(verdict(), "judged", { agent: "Pi", agentToolDisplay: "Pi" })} />);
    expect(heading()).toBe("Pi said");

    rerender(
      <VerdictPanel sessionId={SID} session={session(verdict(), "judged", { agent: "Copilot", agentToolDisplay: "GitHub Copilot" })} />,
    );
    expect(heading()).toBe("GitHub Copilot said");
  });

  it("never heads a kind with no display name with another agent's name", () => {
    // A future kind the Gateway's fold has no nicer spelling for is stamped as its own word.
    const { container, rerender } = render(
      <VerdictPanel sessionId={SID} session={session(verdict(), "judged", { agent: "FutureTool", agentToolDisplay: "FutureTool" })} />,
    );
    const heading = () => container.querySelector(".verdict-receipt summary")!.textContent;
    expect(heading()).toBe("FutureTool said");

    // A row that arrived with no stamp says so, in the words the phone's roster card uses, and names no agent.
    rerender(<VerdictPanel sessionId={SID} session={session(verdict(), "judged", { agent: "Codex" })} />);
    expect(heading()).toBe("Agent tool not reported said");
    expect(heading()).not.toMatch(/Claude|Codex/);
  });

  it("shows no risk line for none, and the risk FIRST for any other word", () => {
    const { container, rerender } = render(<VerdictPanel sessionId={SID} session={session(verdict())} />);
    expect(container.querySelector(".verdict-risk")).toBeNull();

    rerender(<VerdictPanel sessionId={SID} session={session(verdict({ risk: "irreversible" }))} />);
    const risk = screen.getByRole("note");
    expect(risk.textContent).toBe("Risk: irreversible");
    const panel = container.querySelector(".verdict-panel")!;
    expect(panel.firstElementChild).toBe(risk);

    // The plan's order, top to bottom: risk, receipt, label, summary, options.
    const order = [".verdict-risk", ".verdict-receipt", ".verdict-label", ".verdict-summary", ".verdict-options"]
      .map((selector) => panel.querySelector(selector)!);
    for (let i = 1; i < order.length; i++) {
      expect(order[i - 1].compareDocumentPosition(order[i]) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    }
  });

  it("shows each option's label, note and recommendation", () => {
    render(<VerdictPanel sessionId={SID} session={session(verdict())} />);
    expect(screen.getByRole("button", { name: "Yes, apply it" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "No, leave it" })).toBeTruthy();
    expect(screen.getByText("Changes the local database.")).toBeTruthy();
    expect(screen.getAllByText("Recommended")).toHaveLength(1);
    // The bytes an option sends are the route's business and never reach the screen.
    expect(screen.queryByText("1")).toBeNull();
  });

  it("answers a single-select tap with ONE request naming the verdict and that option", async () => {
    fakeGateway(200, SENT);
    render(<VerdictPanel sessionId={SID} session={session(verdict())} />);

    fireEvent.click(screen.getByRole("button", { name: "No, leave it" }));

    await waitFor(() => expect(screen.getByText("Sent to the session.")).toBeTruthy());
    expect(calls).toEqual([
      {
        url: `/sessions/${SID}/turn-verdict/answer`,
        method: "POST",
        body: { verdictId: "tv-panel-1", optionIndexes: [1] },
      },
    ]);
  });

  it("collects a multiple-select's picks and sends them in the order picked, in ONE request", async () => {
    fakeGateway(200, SENT);
    const multi = verdict({
      menu: { question: "Which parts?", selectionMode: "multiple", submit: "\r" },
      options: [
        { key: "the schema", send: "1", recommended: false, note: "" },
        { key: "the data", send: "2", recommended: false, note: "" },
        { key: "the seed rows", send: "3", recommended: false, note: "" },
      ],
    });
    render(<VerdictPanel sessionId={SID} session={session(multi)} />);

    const sendButton = screen.getByRole("button", { name: "Send the chosen options" }) as HTMLButtonElement;
    expect(sendButton.disabled).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "the seed rows" }));
    fireEvent.click(screen.getByRole("button", { name: "the schema" }));
    // A pick is a toggle, not a send.
    expect(calls).toHaveLength(0);
    expect(screen.getByRole("button", { name: "the seed rows" }).getAttribute("aria-pressed")).toBe("true");

    fireEvent.click(sendButton);

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0].body).toEqual({ verdictId: "tv-panel-1", optionIndexes: [2, 0] });
  });

  it("gives the parked reply one button that says it sends the typed reply, and sends an empty list", async () => {
    fakeGateway(200, SENT);
    const parked = verdict({
      menu: { question: "Send the reply already typed: run the tests first", selectionMode: "single", submit: "\r" },
      options: [],
    });
    render(<VerdictPanel sessionId={SID} session={session(parked)} />);

    expect(screen.getByText("Send the reply already typed: run the tests first")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Confirm" })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Send the typed reply" }));

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0].body).toEqual({ verdictId: "tv-panel-1", optionIndexes: [] });
  });

  it("shows the route's refusal sentence exactly as the route wrote it", async () => {
    const reason = "The screen has changed since the Wingman read it, so nothing was sent. Look at the session again.";
    fakeGateway(409, { accepted: false, code: "answer-screen-changed", reason, verdictId: "tv-panel-1" });
    render(<VerdictPanel sessionId={SID} session={session(verdict())} />);

    fireEvent.click(screen.getByRole("button", { name: "Yes, apply it" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toBe(reason);
    expect(screen.queryByText("Sent to the session.")).toBeNull();
  });

  // ---- "this is wrong": the report that feeds the graded corpus (slice G) --------------------------------

  it("shows the this-is-wrong action live, opening the picker rather than sending anything", () => {
    render(<VerdictPanel sessionId={SID} session={session(verdict())} />);

    const wrong = screen.getByRole("button", { name: "This is wrong" }) as HTMLButtonElement;
    // It used to be unpressable, waiting for this slice. It is pressable now, and it is not a send: opening the
    // picker must never be a report, because the word has not been chosen yet.
    expect(wrong.disabled).toBe(false);
    expect(screen.queryByLabelText("What should it have said?")).toBeNull();

    fireEvent.click(wrong);

    expect(screen.getByLabelText("What should it have said?")).toBeTruthy();
    expect(calls).toHaveLength(0);
  });

  it("offers every word of the shared vocabulary, in the vocabulary's own spelling, with none preselected", () => {
    render(<VerdictPanel sessionId={SID} session={session(verdict())} />);
    fireEvent.click(screen.getByRole("button", { name: "This is wrong" }));

    const picker = screen.getByLabelText("What should it have said?") as HTMLSelectElement;
    const offered = Array.from(picker.options).map((option) => option.value);

    // The placeholder, then the seven words in the vocabulary's order. Nothing prettified, nothing hidden: a word
    // the Gateway would accept is never missing here, and one it would refuse is never offered.
    expect(offered).toEqual(["", ...VERDICT_WORDS]);
    expect(picker.value).toBe("");
    // ...and nothing can be sent until he chooses one.
    expect((screen.getByRole("button", { name: "Send the correction" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("sends ONE request naming the verdict, the chosen word and the note, and shows the route's sentence", async () => {
    fakeGateway(200, { accepted: true, code: "feedback-recorded", reason: "Recorded.", verdictId: "tv-panel-1" });
    render(<VerdictPanel sessionId={SID} session={session(verdict())} />);
    fireEvent.click(screen.getByRole("button", { name: "This is wrong" }));

    fireEvent.change(screen.getByLabelText("What should it have said?"), { target: { value: "continues-alone" } });
    fireEvent.change(screen.getByLabelText("Anything to add (optional)"), {
      target: { value: "It said it would carry on and it did." },
    });
    fireEvent.click(screen.getByRole("button", { name: "Send the correction" }));

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0]).toEqual({
      url: `/sessions/${SID}/turn-verdict/feedback`,
      method: "POST",
      body: {
        verdictId: "tv-panel-1",
        correctVerdict: "continues-alone",
        note: "It said it would carry on and it did.",
      },
    });
    expect(await screen.findByText("Recorded.")).toBeTruthy();
    // The picker closes once the correction is stored - the question has been answered.
    await waitFor(() => expect(screen.queryByLabelText("What should it have said?")).toBeNull());
  });

  it("sends an empty note when he adds nothing, rather than inventing one", async () => {
    fakeGateway(200, { accepted: true, code: "feedback-recorded", reason: "Recorded.", verdictId: "tv-panel-1" });
    render(<VerdictPanel sessionId={SID} session={session(verdict())} />);
    fireEvent.click(screen.getByRole("button", { name: "This is wrong" }));
    fireEvent.change(screen.getByLabelText("What should it have said?"), { target: { value: "cannot-tell" } });

    fireEvent.click(screen.getByRole("button", { name: "Send the correction" }));

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0].body).toEqual({ verdictId: "tv-panel-1", correctVerdict: "cannot-tell", note: "" });
  });

  it("shows the route's refusal sentence exactly as the route wrote it, and keeps the picker open", async () => {
    const reason = "That verdict is not one of this session's, so nothing was recorded.";
    fakeGateway(404, { accepted: false, code: "feedback-verdict-not-found", reason, verdictId: "tv-panel-1" });
    render(<VerdictPanel sessionId={SID} session={session(verdict())} />);
    fireEvent.click(screen.getByRole("button", { name: "This is wrong" }));
    fireEvent.change(screen.getByLabelText("What should it have said?"), { target: { value: "needed-you" } });

    fireEvent.click(screen.getByRole("button", { name: "Send the correction" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toBe(reason);
    // A refusal is shown, never swallowed, and the picker stays open so the report is not lost.
    expect(screen.getByLabelText("What should it have said?")).toBeTruthy();
  });

  it("tells a shell that asked, after the correction is stored and never before", async () => {
    fakeGateway(200, { accepted: true, code: "feedback-recorded", reason: "Recorded.", verdictId: "tv-panel-1" });
    const told: string[] = [];
    render(
      <VerdictPanel
        sessionId={SID}
        session={session(verdict())}
        onReported={(v, word) => told.push(`${v.verdictId}:${word}`)}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "This is wrong" }));
    fireEvent.change(screen.getByLabelText("What should it have said?"), { target: { value: "finished" } });
    expect(told).toEqual([]);

    fireEvent.click(screen.getByRole("button", { name: "Send the correction" }));

    await waitFor(() => expect(told).toEqual(["tv-panel-1:finished"]));
  });

  it("clears a half-written report when the stop changes", () => {
    const { rerender } = render(<VerdictPanel sessionId={SID} session={session(verdict())} />);
    fireEvent.click(screen.getByRole("button", { name: "This is wrong" }));
    fireEvent.change(screen.getByLabelText("What should it have said?"), { target: { value: "needed-you" } });

    // A new stop is a new question, and a word chosen about the last one must never ride to the next.
    rerender(<VerdictPanel sessionId={SID} session={session(verdict({ verdictId: "tv-panel-2" }))} />);

    expect(screen.queryByLabelText("What should it have said?")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "This is wrong" }));
    expect((screen.getByLabelText("What should it have said?") as HTMLSelectElement).value).toBe("");
  });
});
