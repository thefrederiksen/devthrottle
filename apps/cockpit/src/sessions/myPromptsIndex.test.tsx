// @vitest-environment jsdom
// THE INDEX OF YOUR OWN PROMPTS, as a way of finding your place - not as a way of reading.
//
// The job is "I asked something a long way up and I cannot find it". So the index shows your turns, and
// every one of them is a way BACK INTO THE THREAD: clicking one takes the filter off and scrolls to that
// turn in the full conversation. A filter you have to remember to switch off again would have left the
// reader somewhere they did not ask to be, looking at a conversation with every answer missing.
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import type { RenderedBubble } from "@devthrottle/client-core/history/chatView";
import type { HistoryBubbleFilter } from "@devthrottle/client-core/history/bubbleMapper";

const bubble = (kind: string, speaker: string, body: string, timestamp?: string): RenderedBubble => ({
  bubble: { speaker, body, kind, isRawText: false, ...(timestamp ? { timestamp } : {}) },
  html: `<p>${body}</p>`,
  links: [],
});

// What the shared model hands back when "my prompts only" is ON: the reader's turns and nothing else.
const MINE: RenderedBubble[] = [
  bubble("user", "You", "I like option B better.", "2026-09-20T09:12:00Z"),
  bubble("user", "You", "So to your two answers, I like the solid teal.", "2026-09-20T09:31:00Z"),
];
const EVERYTHING: RenderedBubble[] = [
  MINE[0],
  bubble("assistant", "Assistant", "Good - B it is."),
  MINE[1],
];

let filter: HistoryBubbleFilter = {
  showToolCalls: false,
  showToolResults: false,
  showThinking: false,
  myPromptsOnly: false,
};
const setFilter = vi.fn((next: HistoryBubbleFilter) => {
  filter = next;
});

vi.mock("@devthrottle/client-core/history/useSessionChat", () => ({
  useSessionChat: () => ({
    bubbles: filter.myPromptsOnly ? MINE : EVERYTHING,
    emptyText: "Waiting for the conversation to start...",
    staleNotice: null,
    loadFailed: false,
    loadError: null,
    filter,
    setFilter,
  }),
}));
vi.mock("../components/FileViewerModal", () => ({ FileViewerModal: () => <div /> }));

import { ChatTab } from "./ChatTab";

const SID = "5b8e1a40-0000-4000-8000-0000000000ab";

beforeEach(() => {
  filter = { showToolCalls: false, showToolResults: false, showThinking: false, myPromptsOnly: false };
  setFilter.mockClear();
  // jsdom does not implement scrolling; the jump only has to ASK for it.
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(cleanup);

describe("my prompts only, on the Chat tab", () => {
  it("offers the choice as a real checkbox, apart from the three machinery toggles", () => {
    render(<ChatTab sessionId={SID} />);

    const mine = screen.getByLabelText("My prompts only");
    expect(mine).toBeInstanceOf(HTMLInputElement);
    expect((mine as HTMLInputElement).type).toBe("checkbox");
    // Its own class, so the stylesheet can set it apart from the three that ADD content back.
    expect(mine.closest(".chat-filter-mine")).not.toBeNull();
  });

  it("turns on through the shared filter, like every other choice on this row", () => {
    render(<ChatTab sessionId={SID} />);

    fireEvent.click(screen.getByLabelText("My prompts only"));

    expect(setFilter).toHaveBeenCalledWith({
      showToolCalls: false,
      showToolResults: false,
      showThinking: false,
      myPromptsOnly: true,
    });
  });

  it("says how many prompts it is showing, and that they can be clicked", () => {
    filter = { ...filter, myPromptsOnly: true };
    render(<ChatTab sessionId={SID} />);

    expect(screen.getByText("2 prompts of yours in this conversation. Click one to go back to it.")).toBeTruthy();
  });

  it("says it in the singular when there is one", () => {
    filter = { ...filter, myPromptsOnly: true };
    MINE.length = 1;
    render(<ChatTab sessionId={SID} />);

    expect(screen.getByText("1 prompt of yours in this conversation. Click it to go back to it.")).toBeTruthy();
    MINE.push(bubble("user", "You", "So to your two answers, I like the solid teal.", "2026-09-20T09:31:00Z"));
  });

  it("draws no index lead and no way-back buttons in the ordinary conversation", () => {
    render(<ChatTab sessionId={SID} />);

    expect(screen.queryByText(/prompts of yours in this conversation/)).toBeNull();
    expect(screen.queryAllByLabelText("Go back to this prompt in the conversation")).toHaveLength(0);
  });

  it("gives every prompt a way back, and taking one puts the whole conversation back", () => {
    filter = { ...filter, myPromptsOnly: true };
    render(<ChatTab sessionId={SID} />);

    const ways = screen.getAllByLabelText("Go back to this prompt in the conversation");
    expect(ways).toHaveLength(2);

    fireEvent.click(ways[1]);

    // The filter comes OFF. The reader asked to find their place, not to stay in a filtered list.
    expect(setFilter).toHaveBeenCalledWith(expect.objectContaining({ myPromptsOnly: false }));
  });

  it("marks each turn with a handle that survives the list being rebuilt", () => {
    // The index into a FILTERED list cannot find a turn once the filter is off, so the jump is keyed on
    // something belonging to the turn itself.
    filter = { ...filter, myPromptsOnly: true };
    const { container } = render(<ChatTab sessionId={SID} />);

    const keys = [...container.querySelectorAll("[data-turn-key]")].map((n) => n.getAttribute("data-turn-key"));
    expect(keys).toEqual(["2026-09-20T09:12:00Z", "2026-09-20T09:31:00Z"]);
  });

  it("shows the clock on each prompt in the index only", () => {
    filter = { ...filter, myPromptsOnly: true };
    const { container } = render(<ChatTab sessionId={SID} />);
    expect(container.querySelectorAll(".chat-index-clock").length).toBe(2);

    cleanup();
    filter = { ...filter, myPromptsOnly: false };
    const plain = render(<ChatTab sessionId={SID} />);
    expect(plain.container.querySelectorAll(".chat-index-clock").length).toBe(0);
  });
});
