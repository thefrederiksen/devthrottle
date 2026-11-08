// @vitest-environment jsdom
// THE SESSION HEADER'S CHIP AND MISSION BUTTON ON THE CHAT FILTER ROW (Web UI Polish, fix 6). The session view hands
// them to ChatTab as `toolbarEnd`; they must land INSIDE the filter row, before LIVE, so the tab strip above keeps the
// top row to itself. Without the slot, the row draws nothing extra.
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";

vi.mock("@devthrottle/client-core/history/useSessionChat", () => ({
  useSessionChat: () => ({
    bubbles: [],
    emptyText: "Waiting for the conversation to start...",
    staleNotice: null,
    loadFailed: false,
    loadError: null,
    filter: { showToolCalls: false, showToolResults: false, showThinking: false, myPromptsOnly: false },
    setFilter: vi.fn(),
  }),
}));
vi.mock("../components/FileViewerModal", () => ({ FileViewerModal: () => <div /> }));

import { ChatTab } from "./ChatTab";

const SID = "5b8e1a40-0000-4000-8000-0000000000ac";

afterEach(cleanup);

describe("ChatTab toolbarEnd", () => {
  it("ChatTab_ToolbarEndGiven_DrawsItInsideTheFilterRowBeforeLive", () => {
    const { container } = render(<ChatTab sessionId={SID} toolbarEnd={<span data-testid="header-pair">pair</span>} />);

    const pair = screen.getByTestId("header-pair");
    expect(pair.closest(".chat-filter")).not.toBeNull();
    expect(pair.closest(".chat-filter-end")).not.toBeNull();
    const live = container.querySelector(".chat-filter .chat-live");
    expect(live).not.toBeNull();
    expect(pair.compareDocumentPosition(live as Node) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("ChatTab_NoToolbarEnd_DrawsNoEndSlot", () => {
    const { container } = render(<ChatTab sessionId={SID} />);
    expect(container.querySelector(".chat-filter-end")).toBeNull();
  });
});
