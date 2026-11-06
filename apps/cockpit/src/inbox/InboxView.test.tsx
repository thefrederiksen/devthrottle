// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { oddList, oddThread } from "@devthrottle/client-core/factoryinbox/factoryInboxFixture.testkit";
import { oddYourCount, oddYourList, oddYourMessage } from "@devthrottle/client-core/yourinbox/yourInboxFixture.testkit";

// The Cockpit's Inbox (Factory Inbox mission, phase 4; mockup D): the shared list on the left and the open factory's
// thread as the reading pane. The screens themselves are tested in client-core; here it is the Cockpit's half - the two
// columns, which factory is open from the address, and where the composer sits.

vi.mock("@devthrottle/client-core/factoryinbox/factoryInboxClient", () => ({
  getFactoryInboxList: vi.fn(async () => oddList()),
  getFactoryInboxThread: vi.fn(async () => oddThread()),
  getFactoryInboxSummary: vi.fn(),
  sendFactoryInboxMessage: vi.fn(),
}));
vi.mock("@devthrottle/client-core/factoryinbox/factoryInboxVoice", () => ({
  listFactoryRecordings: vi.fn(async () => []),
  saveFactoryRecording: vi.fn(),
  deleteFactoryRecording: vi.fn(),
  sendFactoryRecording: vi.fn(),
}));

vi.mock("@devthrottle/client-core/yourinbox/yourInboxClient", () => ({
  getYourInbox: vi.fn(async () => oddYourList()),
  getYourInboxMessage: vi.fn(async () => oddYourMessage({ unread: false, state: "read" })),
  setYourInboxState: vi.fn(),
  replyYourInbox: vi.fn(),
  getYourInboxCount: vi.fn(async () => oddYourCount()),
}));

import { resetYourInboxCount } from "@devthrottle/client-core/yourinbox/useYourInboxCount";
import { cockpitInboxHref, InboxView } from "./InboxView";

function Where() {
  const at = useLocation();
  return <span data-testid="where">{at.pathname + at.search}</span>;
}

function mountAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/inbox" element={<><InboxView /><Where /></>} />
        <Route path="/inbox/:factory" element={<><InboxView /><Where /></>} />
      </Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  resetYourInboxCount();
});

describe("InboxView", () => {
  it("InboxView_NoFactoryOpen_ShowsTheListWithItsComposerAndAnEmptyReadingPane", async () => {
    mountAt("/inbox");

    expect(await screen.findAllByTestId("fi-row")).toHaveLength(2);
    expect(within(screen.getByTestId("fi-list")).getByTestId("fi-composer")).toBeTruthy();
    expect(screen.queryByTestId("fi-thread")).toBeNull();
    expect(screen.getByRole("heading", { level: 1, name: "Inbox" })).toBeTruthy();
  });

  it("InboxView_RowPressed_OpensItsThreadInTheReadingPaneAtItsOwnAddress", async () => {
    mountAt("/inbox");

    fireEvent.click((await screen.findAllByTestId("fi-row"))[0]);

    expect(screen.getByTestId("where").textContent).toBe("/inbox/zeta-works");
    const pane = screen.getByTestId("inbox-reading-pane");
    expect(await within(pane).findAllByTestId("fi-message")).toHaveLength(2);
  });

  it("InboxView_FactoryOpen_MarksItsRowAndMovesTheComposerIntoThePane", async () => {
    mountAt("/inbox/zeta-works");

    const rows = await screen.findAllByTestId("fi-row");
    expect(rows[0].getAttribute("aria-current")).toBe("true");
    expect(rows[1].getAttribute("aria-current")).toBeNull();
    expect(within(screen.getByTestId("fi-list")).queryByTestId("fi-composer")).toBeNull();
    expect(within(screen.getByTestId("inbox-reading-pane")).getByTestId("fi-composer")).toBeTruthy();
  });

  // Your inbox (Factory Inbox mission, step C): the same list-and-pane frame on its own tab.
  it("InboxView_YourInboxTab_ShowsItsListWithTheGatewaysCountOnTheTabAndAnEmptyPane", async () => {
    mountAt("/inbox?tab=yours");

    expect(await screen.findAllByTestId("yi-row")).toHaveLength(2);
    expect(screen.getByRole("tab", { name: /Your inbox/ }).getAttribute("aria-selected")).toBe("true");
    expect((await screen.findByTestId("yi-tab-count")).textContent).toBe("three fresh ones (gateway)");
    expect(screen.queryByTestId("yi-message")).toBeNull();
    expect(screen.queryByTestId("fi-list")).toBeNull();
  });

  it("InboxView_YourInboxRowPressed_OpensTheMessageInTheReadingPaneAtItsOwnAddress", async () => {
    mountAt("/inbox?tab=yours");

    fireEvent.click((await screen.findAllByTestId("yi-row"))[0]);

    expect(screen.getByTestId("where").textContent).toBe("/inbox?tab=yours&message=y-1");
    const pane = screen.getByTestId("inbox-reading-pane");
    expect((await within(pane).findByTestId("yi-body")).textContent).toContain("The quokka notes are filed.");
    expect((await screen.findAllByTestId("yi-row"))[0].getAttribute("aria-current")).toBe("true");
  });

  it("InboxView_TabPressed_MovesBetweenTheTwoInboxes", async () => {
    mountAt("/inbox");
    await screen.findAllByTestId("fi-row");

    fireEvent.click(screen.getByRole("tab", { name: /Your inbox/ }));
    expect(screen.getByTestId("where").textContent).toBe("/inbox?tab=yours");
    fireEvent.click(screen.getByRole("tab", { name: "Factory inbox" }));
    expect(screen.getByTestId("where").textContent).toBe("/inbox");
  });

  it("cockpitInboxHref_EachKind_IsTheCockpitsOwnAddress", () => {
    expect(cockpitInboxHref({ kind: "session", label: "", target: "s 1" })).toBe("/sessions/s%201");
    expect(cockpitInboxHref({ kind: "report", label: "", target: "r1" })).toBe("/report/r1");
    expect(cockpitInboxHref({ kind: "factory", label: "", target: "zeta-works" })).toBe("/inbox/zeta-works");
    expect(cockpitInboxHref({ kind: "unknown-kind", label: "", target: "x" })).toBeNull();
  });
});
