// THE NOTE CONTROLS LIVE IN THE PANEL (issue #3077).
//
// They were a pill floating in the corner of the report, over the words, carrying a queued count the panel
// beside it was already showing. The owner's complaint was exactly that: "that's hovering over the report so
// I can't see the report - why is it not under the conversation?"
//
// What this file holds the panel to: it draws the note controls from what the PAGE says, never from what it
// asked for; it offers nothing that cannot reach the report; and it says the same thing on both surfaces,
// because there is one panel and both shells mount it.

import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DevReportConversation, type DevReportConversationModel } from "./DevReportConversation";
import type { DevReportNoteMode, DevReportNoteModeRequest } from "./protocol";

afterEach(cleanup);

function panel(over: Partial<DevReportConversationModel> = {}) {
  const setNoteMode = vi.fn<(mode: DevReportNoteModeRequest) => void>();
  const model: DevReportConversationModel = {
    queued: [],
    sent: [],
    replies: [],
    sending: false,
    sendError: null,
    send: () => {},
    noteMode: { picking: false, selectionQuote: null },
    setNoteMode,
    connected: true,
    ...over,
  };
  render(<DevReportConversation conversation={model} />);
  return setNoteMode;
}

const picking: DevReportNoteMode = { picking: true, selectionQuote: null };

describe("the note controls in the conversation panel", () => {
  it("offers Add a note, and asks the page to arm one", () => {
    const setNoteMode = panel();

    fireEvent.click(screen.getByTestId("dev-report-add-note"));

    expect(setNoteMode).toHaveBeenCalledWith("pick");
    // It does NOT draw itself as armed on its own say-so: the page answers, and the answer is what moves the
    // button. Picking also ends by itself when the reader clicks, and a panel that trusted its own click
    // would sit there saying "Cancel" afterwards.
    expect(screen.getByTestId("dev-report-add-note").textContent).toBe("Add a note");
    expect(screen.queryByTestId("dev-report-note-hint")).toBeNull();
  });

  it("becomes the way out of picking, and says what to do, once the page says it is armed", () => {
    const setNoteMode = panel({ noteMode: picking });

    const button = screen.getByTestId("dev-report-add-note");
    expect(button.textContent).toBe("Cancel");
    expect(screen.getByTestId("dev-report-note-hint").textContent).toContain("Click the paragraph");

    fireEvent.click(button);
    expect(setNoteMode).toHaveBeenCalledWith("off");
  });

  it("offers a note on the text the reader selected, in their own words, shortened when it is long", () => {
    const setNoteMode = panel({ noteMode: { picking: false, selectionQuote: "the newco and the intellectual-property assignment" } });

    const button = screen.getByTestId("dev-report-note-selection");
    expect(button.textContent).toBe('Note on "the newco and the intellectual..."');

    fireEvent.click(button);
    expect(setNoteMode).toHaveBeenCalledWith("selection");
  });

  it("offers the selection only when there is one, and never while picking", () => {
    panel();
    expect(screen.queryByTestId("dev-report-note-selection")).toBeNull();
    cleanup();

    // Armed, the reader is being asked to click something - offering the old selection at the same moment is
    // two different answers to one question.
    panel({ noteMode: { picking: true, selectionQuote: "A paragraph." } });
    expect(screen.queryByTestId("dev-report-note-selection")).toBeNull();
  });

  it("offers nothing to press when no page is connected to take it", () => {
    panel({ connected: false });

    expect((screen.getByTestId("dev-report-add-note") as HTMLButtonElement).disabled).toBe(true);
  });

  it("no longer sends the reader into the report to find a control that is not there any more", () => {
    panel();

    // The empty-queue sentence used to say "Add notes and answers in the report", which was where the button
    // was. The button is here now, and the report still holds the questions.
    const empty = screen.getByText(/Nothing queued/);
    expect(empty.textContent).toContain("Add a note above");
    expect(empty.textContent).not.toContain("Add notes and answers in the report");
  });
});
