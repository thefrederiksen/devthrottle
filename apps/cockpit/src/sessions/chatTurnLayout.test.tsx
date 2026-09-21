// @vitest-environment jsdom
// THE TURN LAYOUT, LOCKED. The Chat tab had no test of its own at all - every other test on this page
// mocks it away - so the thing the owner actually looks at was the one thing nothing held in place.
//
// What is asserted here is the RULING, not the pixels: your words sit on the right in their own bubble,
// the agent's answer takes the column with no card around it, NEITHER carries a name label, and a tool
// result keeps both its card and its label because "Tool result" is the one fact the layout cannot say.
// A name label creeping back onto a conversation turn is exactly the regression this exists to catch.
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import type { RenderedBubble } from "@devthrottle/client-core/history/chatView";
import type { HistoryBubbleFilter } from "@devthrottle/client-core/history/bubbleMapper";

const bubble = (kind: string, speaker: string, body: string): RenderedBubble => ({
  bubble: { speaker, body, kind, isRawText: false },
  html: `<p>${body}</p>`,
  links: [],
});

const BUBBLES: RenderedBubble[] = [
  bubble("user", "You", "I like option B better."),
  bubble("assistant", "Assistant", "Good - B it is, pinned as a real button."),
  bubble("tool", "Tool result", "exit code 0"),
];

let filter: HistoryBubbleFilter = { showToolCalls: false, showToolResults: false, showThinking: false, myPromptsOnly: false };
const setFilter = vi.fn((next: HistoryBubbleFilter) => {
  filter = next;
});

vi.mock("@devthrottle/client-core/history/useSessionChat", () => ({
  useSessionChat: () => ({
    bubbles: BUBBLES,
    emptyText: "Waiting for the conversation to start...",
    staleNotice: null,
    loadFailed: false,
    loadError: null,
    filter,
    setFilter,
  }),
}));

// The viewer opens a real file over the network and is not the subject here.
vi.mock("../components/FileViewerModal", () => ({ FileViewerModal: () => <div /> }));

import { ChatTab } from "./ChatTab";

const SID = "5b8e1a40-0000-4000-8000-0000000000aa";

function turnFor(text: string): HTMLElement {
  const node = screen.getByText(text).closest(".chat-bubble");
  expect(node, `no turn found around "${text}"`).not.toBeNull();
  return node as HTMLElement;
}

beforeEach(() => {
  filter = { showToolCalls: false, showToolResults: false, showThinking: false, myPromptsOnly: false };
  setFilter.mockClear();
});
afterEach(cleanup);

describe("the Chat tab's turn layout", () => {
  it("draws no name label on either side of the conversation", () => {
    render(<ChatTab sessionId={SID} />);

    // The two labels that used to sit above every turn, in blue and green. Both are gone for good.
    expect(screen.queryByText("You")).toBeNull();
    expect(screen.queryByText("Assistant")).toBeNull();

    // And nothing is drawing a speaker line on a conversation turn by some other name either.
    expect(turnFor("I like option B better.").querySelector(".chat-speaker")).toBeNull();
    expect(turnFor("Good - B it is, pinned as a real button.").querySelector(".chat-speaker")).toBeNull();
  });

  it("keeps the speaker line on a tool result, which the layout cannot name on its own", () => {
    render(<ChatTab sessionId={SID} />);

    const tool = turnFor("exit code 0");
    const speaker = tool.querySelector(".chat-speaker");
    expect(speaker).not.toBeNull();
    expect(speaker?.textContent).toBe("Tool result");
  });

  it("marks each turn with the kind its side and its chrome are styled from", () => {
    render(<ChatTab sessionId={SID} />);

    expect(turnFor("I like option B better.").className).toContain("user");
    expect(turnFor("Good - B it is, pinned as a real button.").className).toContain("assistant");
    expect(turnFor("exit code 0").className).toContain("tool");
  });

  it("puts every turn in the one centred thread column", () => {
    const { container } = render(<ChatTab sessionId={SID} />);

    const thread = container.querySelector(".chat-scroll > .chat-thread");
    expect(thread).not.toBeNull();
    expect(thread?.querySelectorAll(".chat-bubble")).toHaveLength(3);
  });

  it("copies the message itself, not only the links inside it", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });

    render(<ChatTab sessionId={SID} />);

    const copies = screen.getAllByLabelText("Copy this message");
    expect(copies).toHaveLength(3);

    fireEvent.click(copies[1]);
    await waitFor(() => expect(writeText).toHaveBeenCalledWith("Good - B it is, pinned as a real button."));
    // The confirmation lands on the turn that was pressed and on no other.
    await waitFor(() => expect(copies[1].textContent).toBe("Copied"));
    expect(copies[0].textContent).toBe("Copy");
    expect(copies[2].textContent).toBe("Copy");
  });

  // The three "Show:" checkboxes became ONE switch with three positions (owner ruling, 2026-09-20),
  // which also drives the session cards in the rail. The checkboxes asked WHICH machinery; the switch
  // asks HOW MUCH, which is the question a person actually has.
  it("offers one detail switch in place of the three machinery checkboxes", () => {
    render(<ChatTab sessionId={SID} />);

    expect(screen.queryByLabelText("Thinking")).toBeNull();
    expect(screen.queryByLabelText("Tool calls")).toBeNull();
    expect(document.querySelectorAll(".chat-density-btn").length).toBe(3);
    // "My prompts only" was never one of the three - it takes the conversation away rather than adding
    // machinery to it - so the switch does not swallow it.
    expect(screen.getByLabelText("My prompts only")).toBeInstanceOf(HTMLInputElement);
  });

  it("asks for every kind of machinery when the switch is moved to Everything", () => {
    render(<ChatTab sessionId={SID} />);

    fireEvent.click(screen.getByRole("button", { name: "Everything" }));

    expect(setFilter).toHaveBeenCalledWith({
      showToolCalls: true,
      showToolResults: true,
      showThinking: true,
      myPromptsOnly: false,
    });
  });
});
