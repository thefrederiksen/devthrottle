// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { GatewayError } from "@devthrottle/client-core/api/client";

// Factories screen round 2 (the owner's feedback on the live list). What is held down:
//   * under every status the Gateway explains, its one line is rendered verbatim, and RUNNING has none;
//   * the status word links where the Gateway says (the failures, the waiting items), and so does the waiting count -
//     without the row's own click taking over;
//   * the factory page header carries the same line, and the failures card renders each item in the Gateway's words,
//     with Handled only where the Gateway offers it;
//   * Handled posts for that factory and that row, then reloads the page from the Gateway.

const screenClient = vi.hoisted(() => ({
  getFactoriesList: vi.fn(),
  getFactoryPage: vi.fn(),
  getFactorySeats: vi.fn(),
  startFactoryTalk: vi.fn(),
  markFactoryFailureHandled: vi.fn(),
}));
vi.mock("@devthrottle/client-core/factory/factoriesScreenClient", () => screenClient);

const agentsClient = vi.hoisted(() => ({
  getFactoryActivity: vi.fn(),
  getFactoryReports: vi.fn(),
  getFactoryWaiting: vi.fn(),
  getFactoryAgent: vi.fn(),
  markFactoryItemHandled: vi.fn(),
  setFactoryPaused: vi.fn(),
  saveFactoryReport: vi.fn(),
  downloadFactoryCsv: vi.fn(),
}));
vi.mock("@devthrottle/client-core/factory/factoryAgentsClient", () => agentsClient);

import { FactoriesView } from "./FactoriesView";
import { FactoryView } from "./FactoryView";
import { FACTORY_LIST, FACTORY_PAGE } from "./fixtures";

function Where() {
  const loc = useLocation();
  return <div data-testid="where">{loc.pathname + loc.search + loc.hash}</div>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/factories" element={<FactoriesView />} />
        <Route path="/factories/:factory" element={<FactoryView />} />
        <Route path="/factories/:factory/:tab" element={<FactoryView />} />
      </Routes>
      <Where />
    </MemoryRouter>,
  );
}

const where = () => screen.getByTestId("where").textContent;

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  // These tests read the TABLE's rows; the list opens on Cards since 8 Oct 2026 (FactoriesCards.test.tsx), so the
  // table is asked for the way the owner would pick it: remembered in this browser.
  window.localStorage.clear();
  window.localStorage.setItem("cockpit.factoriesView", JSON.stringify("table"));
  screenClient.getFactoriesList.mockResolvedValue(FACTORY_LIST);
  screenClient.getFactoryPage.mockResolvedValue(FACTORY_PAGE);
});

describe("Factories round 2 - every status explains itself", () => {
  it("renders the Gateway's line under each status but RUNNING, verbatim", async () => {
    renderAt("/factories");

    const failing = await screen.findByTestId("fa-factory-mindzie-web");
    expect(within(failing).getByTestId("fa-status-line").textContent).toBe("Sender: 4 failures, newest today 12:02 (fixture)");
    expect(within(screen.getByTestId("fa-factory-warmforward")).getByTestId("fa-status-line").textContent).toBe(
      "Nora Hale, today 06:20: Is the bunkie meant to be at 20 C? (fixture)",
    );
    const running = screen.getByTestId("fa-factory-devthrottle");
    expect(within(running).queryByTestId("fa-status-line")).toBeNull();
    expect(within(running).queryByTestId("fa-status-link")).toBeNull();
  });

  it("links the status word and the waiting count where the Gateway says, not to the row's page", async () => {
    renderAt("/factories");

    const failing = await screen.findByTestId("fa-factory-mindzie-web");
    expect(within(failing).getByTestId("fa-status-link").getAttribute("href")).toBe("/factories/mindzie-web#failing");
    expect(within(failing).queryByTestId("fa-waiting-link")).toBeNull();

    const warm = screen.getByTestId("fa-factory-warmforward");
    const count = within(warm).getByTestId("fa-waiting-link");
    expect(count.textContent).toBe("1 question (fixture)");
    expect(count.getAttribute("href")).toBe("/factories/warmforward#waiting");

    fireEvent.click(within(failing).getByText("FAILING"));
    await waitFor(() => expect(where()).toBe("/factories/mindzie-web#failing"));
  });

  it("puts the line under the status on the factory's page, linked to its items", async () => {
    renderAt("/factories/warmforward");

    const line = await screen.findByTestId("fa-page-status-line");
    expect(line.textContent).toBe("Nora Hale, today 06:20: Is the bunkie meant to be at 20 C? (fixture)");
    expect(within(line).getByRole("link").getAttribute("href")).toBe("/factories/warmforward#waiting");
    expect(screen.getByTestId("fa-page-waiting").id).toBe("waiting");
  });

  it("shows no line and no failures card when the Gateway sends none", async () => {
    screenClient.getFactoryPage.mockResolvedValue({ ...FACTORY_PAGE, statusLine: null, statusHref: null, failures: null });
    renderAt("/factories/warmforward");

    await screen.findByTestId("fa-overview");
    expect(screen.queryByTestId("fa-page-status-line")).toBeNull();
    expect(screen.queryByTestId("fa-page-failing")).toBeNull();
  });
});

describe("Factories round 2 - the failures card", () => {
  it("renders each failure in the Gateway's words, with Handled only where it is offered", async () => {
    renderAt("/factories/warmforward#failing");

    const card = await screen.findByTestId("fa-page-failing");
    expect(card.id).toBe("failing");
    expect(within(card).getByText("Failing (fixture)")).toBeTruthy();
    expect(within(card).getByText("A failure stops counting when the same seat later succeeds at the same thing (fixture).")).toBeTruthy();

    const row = within(card).getByTestId("fa-failure-f1");
    expect(within(row).getByText("All Types Fence & Deck")).toBeTruthy();
    expect(within(row).getByText(/answers 404 after 20 min/)).toBeTruthy();
    expect(within(row).getByText("Sender, today 12:02")).toBeTruthy();
    expect(within(row).getByText("171aef53 (fixture)").getAttribute("href")).toBe("/session/171aef53-9bf8-45be-93dd-b9e57f832fc4");
    expect(within(row).getByRole("button", { name: "Handled (fixture)" })).toBeTruthy();

    const schedule = within(card).getByTestId("fa-failure-schedule");
    expect(within(schedule).getByText("This clears when the schedule next starts its run (fixture).")).toBeTruthy();
    expect(within(schedule).queryByRole("button")).toBeNull();
  });

  it("Handled posts for this factory and this row, shows its busy words, and reloads the page", async () => {
    let finish: () => void = () => {};
    screenClient.markFactoryFailureHandled.mockReturnValue(new Promise<void>((r) => (finish = r)));
    renderAt("/factories/warmforward");

    fireEvent.click(await screen.findByRole("button", { name: "Handled (fixture)" }));
    expect(await screen.findByRole("button", { name: "Marking it handled (fixture)..." })).toBeTruthy();
    expect(screenClient.markFactoryFailureHandled).toHaveBeenCalledWith("warmforward", "f1");
    const loadsBefore = screenClient.getFactoryPage.mock.calls.length;
    finish();
    await waitFor(() => expect(screenClient.getFactoryPage.mock.calls.length).toBeGreaterThan(loadsBefore));
  });

  it("shows the Gateway's sentence when Handled is refused", async () => {
    const sentence = "This failure is already handled.";
    screenClient.markFactoryFailureHandled.mockRejectedValue(new GatewayError(400, sentence, { reason: sentence }));
    renderAt("/factories/warmforward");

    fireEvent.click(await screen.findByRole("button", { name: "Handled (fixture)" }));
    expect(await screen.findByText(/This failure is already handled\./)).toBeTruthy();
  });
});
