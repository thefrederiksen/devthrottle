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

  it("renders nothing for a row that carries no judged verdict", () => {
    const { container, rerender } = render(<VerdictPanel sessionId={SID} session={session(null)} />);
    expect(container.innerHTML).toBe("");
    // A failed or reading row may carry a record; only "judged" is an answer to show.
    rerender(<VerdictPanel sessionId={SID} session={session(verdict(), "failed")} />);
    expect(container.innerHTML).toBe("");
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

  it("shows the this-is-wrong action, unpressable until slice G wires it", () => {
    const { rerender } = render(<VerdictPanel sessionId={SID} session={session(verdict())} />);
    expect((screen.getByRole("button", { name: "This is wrong" }) as HTMLButtonElement).disabled).toBe(true);

    const reported: string[] = [];
    rerender(<VerdictPanel sessionId={SID} session={session(verdict())} onReportWrong={(v) => reported.push(v.verdictId)} />);
    fireEvent.click(screen.getByRole("button", { name: "This is wrong" }));
    expect(reported).toEqual(["tv-panel-1"]);
  });
});
