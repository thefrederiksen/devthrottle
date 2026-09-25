// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

// The Factory Agents tabs (Screens 1, 3 and 5) render the Gateway's fold VERBATIM (rule 7): the words, numbers,
// links and tones come from FactoryAgentsFold, and the page composes none of its own. The Gateway client is mocked
// with fixtures in the fold's exact shape.

const client = vi.hoisted(() => ({
  getFactories: vi.fn(),
  getFactoryActivity: vi.fn(),
  getFactoryReports: vi.fn(),
  setFactoryPaused: vi.fn(),
  saveFactoryReport: vi.fn(),
  downloadFactoryCsv: vi.fn(),
}));

vi.mock("@devthrottle/client-core/factory/factoryAgentsClient", () => client);

import { FactoryAgentsView } from "./FactoryAgentsView";
import { ACTIVITY, ACTIVITY_NO_CHECKS, FACTORIES, REPORT } from "./fixtures";

function renderAt(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Routes>
        <Route path="/factory-agents" element={<FactoryAgentsView />} />
        <Route path="/factory-agents/waiting" element={<div>waiting page</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("Factory Agents - Factories tab (Screen 1)", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    client.getFactories.mockResolvedValue(FACTORIES);
  });

  it("renders the tabs, each card's status, numbers and factory agents in the Gateway's words", async () => {
    renderAt("/factory-agents");

    const card = await screen.findByTestId("fa-card-website-business");
    expect(screen.getAllByRole("tab").map((t) => t.textContent)).toEqual([
      "Factories",
      "All factory agents (2)",
      "Activity",
      "Reports",
    ]);
    expect(within(card).getByText("RUNNING-X")).toBeTruthy();
    expect(screen.getByTestId("fa-map-website-business").getAttribute("href")).toBe("/factory-agents/website-business");
    expect(screen.getByTestId("fa-map-silent-factory").getAttribute("href")).toBe("/factory-agents/silent-factory");
    expect(within(card).getByText("2 factory agents, 1 trigger")).toBeTruthy();
    const number = within(card).getByText("7 asked, waiting for you (fixture)");
    expect(number.closest("a")?.getAttribute("href")).toBe("/factory-agents/waiting?factory=website-business");
    expect(within(card).getByText("188 empty checks").closest("a")?.getAttribute("href")).toBe(
      "/factory-agents?tab=activity&factory=website-business&outcome=nothing-to-do&window=last-24h",
    );
    expect(within(card).getByText("05:32 - 2 asked, 1 escalated")).toBeTruthy();
    expect(within(card).getByText("Front Desk v3").closest("a")?.getAttribute("href")).toBe(
      "/factory-agents/website-business/front-desk",
    );
    expect(within(card).getByText("WORKING").className).toContain("fa-tone-working");
  });

  it("shows the red no-checks fault a factory carries, and offers the Gateway's Resume", async () => {
    renderAt("/factory-agents");

    const card = await screen.findByTestId("fa-card-silent-factory");
    const fault = within(card).getByRole("alert");
    expect(fault.textContent).toBe("No checks ran in the last 24 hours. That is a fault, not a quiet night.");
    expect(within(card).getByText("FAULT").className).toContain("fa-tone-red");
    expect(within(card).getByRole("button", { name: "Resume factory" })).toBeTruthy();
  });

  it("asks before pausing when the Gateway supplies the question, then posts pause", async () => {
    client.setFactoryPaused.mockResolvedValue(undefined);
    renderAt("/factory-agents");

    const card = await screen.findByTestId("fa-card-website-business");
    fireEvent.click(within(card).getByRole("button", { name: "Pause factory" }));
    expect(await screen.findByText("Pause the factory Website Business? Its trigger keeps checking.")).toBeTruthy();
    expect(client.setFactoryPaused).not.toHaveBeenCalled();
    const dialogButtons = screen.getAllByRole("button", { name: "Pause factory" });
    fireEvent.click(dialogButtons[dialogButtons.length - 1]);
    await waitFor(() => expect(client.setFactoryPaused).toHaveBeenCalledWith("pause", "website-business", undefined));
  });

  it("lists every factory agent on the All factory agents tab", async () => {
    renderAt("/factory-agents?tab=agents");

    expect(await screen.findByText("Scout v2")).toBeTruthy();
    expect(screen.getByText("No trigger names it")).toBeTruthy();
    expect(screen.getAllByText("Website Business").length).toBeGreaterThan(0);
  });

  it("says what the Gateway says when there is no factory", async () => {
    client.getFactories.mockResolvedValue({ ...FACTORIES, factories: [], allAgents: [], emptyText: "No factory yet (fixture)." });
    renderAt("/factory-agents");

    expect(await screen.findByText("No factory yet (fixture).")).toBeTruthy();
  });
});

describe("Factory Agents - Activity tab (Screen 3)", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    client.getFactories.mockResolvedValue(FACTORIES);
    client.getFactoryActivity.mockResolvedValue(ACTIVITY);
  });

  it("renders the lines as folded - the collapsed empty checks as one grey line - with the footnote", async () => {
    renderAt("/factory-agents?tab=activity&factory=website-business");

    const table = await screen.findByTestId("fa-activity-table");
    const collapsed = within(table).getByText("Checked for new business mail 25 times - nothing to do").closest("tr");
    expect(collapsed?.className).toBe("fa-row-collapsed");
    expect(within(collapsed as HTMLElement).getByText("02:05-04:05")).toBeTruthy();
    expect(within(table).getByText("BLOCKED").className).toContain("fa-tone-red");
    expect(within(table).getByText("#135").closest("a")?.getAttribute("href")).toBe("/session/135");
    expect(within(table).getByText("Corrected 22 Sep 08:12: Marked handled by owner (device:abc): money question")).toBeTruthy();
    expect(screen.getByText("These rows are permanent. Nothing here can be edited or deleted, by anyone.")).toBeTruthy();
    expect(client.getFactoryActivity).toHaveBeenCalledWith(
      expect.objectContaining({ factory: "website-business" }),
      expect.anything(),
    );
  });

  it("shows the no-checks fault in red above an empty list", async () => {
    client.getFactoryActivity.mockResolvedValue(ACTIVITY_NO_CHECKS);
    renderAt("/factory-agents?tab=activity");

    const faults = await screen.findByTestId("fa-faults");
    expect(faults.textContent).toContain("Silent Factory: No checks ran in the last 24 hours.");
    expect(screen.getByText("Nothing matches these filters in the last 24 hours.")).toBeTruthy();
  });

  it("changing a filter asks the Gateway again with it", async () => {
    renderAt("/factory-agents?tab=activity&factory=website-business");
    await screen.findByTestId("fa-activity-table");

    fireEvent.change(screen.getByLabelText("Outcome"), { target: { value: "blocked" } });
    await waitFor(() =>
      expect(client.getFactoryActivity).toHaveBeenLastCalledWith(
        expect.objectContaining({ factory: "website-business", outcome: "blocked", window: "last-24h" }),
        expect.anything(),
      ),
    );
  });

  it("Export CSV hands the Gateway's own address to the download", async () => {
    client.downloadFactoryCsv.mockResolvedValue(undefined);
    renderAt("/factory-agents?tab=activity");
    await screen.findByTestId("fa-activity-table");

    fireEvent.click(screen.getByRole("button", { name: "Export CSV" }));
    await waitFor(() => expect(client.downloadFactoryCsv).toHaveBeenCalledWith(ACTIVITY.csvHref));
  });

  it("Make a report from this saves the filter the Gateway folded, under a name", async () => {
    client.saveFactoryReport.mockResolvedValue({ ...REPORT.saved[0], href: "/factory-agents?tab=reports&report=abc123" });
    client.getFactoryReports.mockResolvedValue(REPORT);
    renderAt("/factory-agents?tab=activity&factory=website-business");
    await screen.findByTestId("fa-activity-table");

    fireEvent.click(screen.getByRole("button", { name: "Make a report from this" }));
    fireEvent.change(screen.getByLabelText("Report name"), { target: { value: "Night watch" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(client.saveFactoryReport).toHaveBeenCalledWith({
        name: "Night watch",
        factory: "website-business",
        agent: undefined,
        outcome: undefined,
        window: "last-24h",
        fromUtc: undefined,
        toUtc: undefined,
      }),
    );
  });
});

describe("Factory Agents - Reports tab (Screen 5)", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    client.getFactories.mockResolvedValue(FACTORIES);
    client.getFactoryReports.mockResolvedValue(REPORT);
  });

  it("renders the summary table, the total and the saved reports as the Gateway folded them", async () => {
    renderAt("/factory-agents?tab=reports");

    const table = await screen.findByTestId("fa-report-table");
    expect(Array.from(table.querySelectorAll("th")).map((th) => th.textContent)).toEqual(REPORT.columns);
    const rows = Array.from(table.querySelectorAll("tbody tr")).map((tr) =>
      Array.from(tr.querySelectorAll("td")).map((td) => td.textContent),
    );
    expect(rows).toEqual([
      ["Front Desk", "14", "5", "4", "3", "0", "0", "0", "288"],
      ["Scout", "7", "7", "7", "0", "3", "0", "0", "0"],
      ["Total", "21", "12", "11", "3", "3", "0", "0", "288"],
    ]);
    expect(screen.getByText("Each factory agent, in the last 7 days")).toBeTruthy();
    const saved = screen.getByTestId("fa-saved-reports");
    expect(within(saved).getByText("Weekly blocks").closest("a")?.getAttribute("href")).toBe(
      "/factory-agents?tab=reports&report=abc123",
    );
  });

  it("opening a saved report asks the Gateway for it by id and shows its name", async () => {
    client.getFactoryReports.mockResolvedValue({ ...REPORT, openedReportName: "Weekly blocks" });
    renderAt("/factory-agents?tab=reports&report=abc123");

    expect(await screen.findByRole("heading", { name: "Weekly blocks" })).toBeTruthy();
    expect(client.getFactoryReports).toHaveBeenCalledWith(expect.objectContaining({ report: "abc123" }), expect.anything());
  });

  it("offers neither Ask the record nor a scheduled report - they are not this build", async () => {
    renderAt("/factory-agents?tab=reports");
    await screen.findByTestId("fa-report-table");

    expect(screen.queryByText(/ask the record/i)).toBeNull();
    expect(screen.queryByText(/schedule/i)).toBeNull();
  });
});
