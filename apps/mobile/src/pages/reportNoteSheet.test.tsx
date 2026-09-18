// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import type { DevReportConversationModel } from "@devthrottle/client-core/devreports/DevReportConversation";

// THE SHEET GETS OUT OF THE WAY WHEN A NOTE IS ARMED (issue #3077).
//
// The note controls moved out of the report and into the app's panel. On a phone that panel is a sheet over
// the report, so pressing "Add a note" in it would leave the reader looking at the panel they now have to
// click THROUGH to reach the paragraph they meant. The desktop has no such problem - its panel sits beside
// the report - which is why this belongs to the phone's frame rather than to the shared panel.

vi.mock("@devthrottle/client-core/api/client", () => ({
  authHeaders: () => ({}),
  gatewayErrorMessage: (e: unknown) => String(e),
  listSessions: () => Promise.resolve([]),
  GatewayError: class GatewayError extends Error {},
}));

// The viewer is client-core's. Here it hands the page a conversation whose note state the test drives, which
// is exactly what the real one does from the page over its port.
const model = vi.hoisted(() => ({ picking: false }));
vi.mock("@devthrottle/client-core/devreports/DevReportViewer", () => ({
  DevReportViewer: ({ renderConversation }: { renderConversation: (c: DevReportConversationModel) => unknown }) => (
    <div data-testid="fake-report-viewer">
      {renderConversation({
        queued: [],
        sent: [],
        replies: [],
        sending: false,
        sendError: null,
        send: () => {},
        noteMode: { picking: model.picking, selectionQuote: null },
        setNoteMode: () => {},
        connected: true,
      }) as never}
    </div>
  ),
}));
vi.mock("@devthrottle/client-core/devreports/DevReportList", () => ({ DevReportList: () => <div /> }));
vi.mock("@devthrottle/client-core/devreports/DevReportConversation", () => ({
  DevReportConversation: () => <div data-testid="fake-conversation" />,
}));
vi.mock("../components/useSessionManage", () => ({ useSessionManage: () => ({}) }));
vi.mock("../components/SessionAppBar", () => ({ SessionAppBar: () => <div /> }));

import { DEV_REPORT_ROUTES } from "./reportRoutes";

const SID = "7d2f9c10-0000-4000-8000-000000000031";
const REPORT = "b41e77a2-0000-4000-8000-0000000000aa";

afterEach(() => {
  cleanup();
  model.picking = false;
});

function mountReport() {
  return render(
    <RouterProvider
      router={createMemoryRouter(DEV_REPORT_ROUTES, { initialEntries: [`/session/${SID}/reports/${REPORT}`] })}
    />,
  );
}

describe("the phone's conversation sheet while a note is being placed", () => {
  it("opens over the report, as it always did", () => {
    mountReport();

    fireEvent.click(screen.getByTestId("report-conversation-open"));

    expect(screen.getByTestId("report-conversation-sheet")).toBeTruthy();
  });

  it("closes itself the moment the page says a note is armed", () => {
    const { rerender } = mountReport();
    fireEvent.click(screen.getByTestId("report-conversation-open"));
    expect(screen.queryByTestId("report-conversation-sheet")).toBeTruthy();

    // The page answers the app's "Add a note": it is now waiting for a click on what the note is about.
    model.picking = true;
    rerender(
      <RouterProvider
        router={createMemoryRouter(DEV_REPORT_ROUTES, { initialEntries: [`/session/${SID}/reports/${REPORT}`] })}
      />,
    );

    expect(screen.queryByTestId("report-conversation-sheet")).toBeNull();
  });

  it("says what to do on the strip, which is all that is left on screen beside the report", () => {
    model.picking = true;
    mountReport();

    expect(screen.getByTestId("report-conversation-open").textContent).toContain("Tap the paragraph");
  });

  it("goes back to counting the conversation once the note has been placed", () => {
    mountReport();

    expect(screen.getByTestId("report-conversation-open").textContent).toContain("Conversation");
  });
});
