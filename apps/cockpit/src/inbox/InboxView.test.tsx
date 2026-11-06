// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { oddList, oddThread } from "@devthrottle/client-core/factoryinbox/factoryInboxFixture.testkit";

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

import { InboxView } from "./InboxView";

function Where() {
  return <span data-testid="where">{useLocation().pathname}</span>;
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

afterEach(() => cleanup());

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
});
