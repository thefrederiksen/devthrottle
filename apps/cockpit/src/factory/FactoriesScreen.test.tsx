// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { GatewayError } from "@devthrottle/client-core/api/client";

// The Factories screen (Factories screen mission, phase D). What is held down:
//   * the list renders the Gateway's rows verbatim and in its order - name, waiting text, status chip, and the CEO's
//     Talk button or the "No CEO" text - and the "All factory agents" tab is gone;
//   * a row opens its factory's page;
//   * the factory page shows the header, the Overview cards and the tabs exactly as folded, and its computer's
//     "change - coming" is a label, never a control;
//   * the Seats tab lists the seats with their own Talk buttons;
//   * Talk shows a busy state at once, opens the session the Gateway started, and shows the Gateway's own sentence
//     when it refuses - never a button that does nothing silently;
//   * every old /factory-agents address lands on its new equivalent;
//   * at phone width the list is cards, from the stylesheet, not a horizontally scrolling table.

const screenClient = vi.hoisted(() => ({
  getFactoriesList: vi.fn(),
  getFactoryPage: vi.fn(),
  getFactorySeats: vi.fn(),
  startFactoryTalk: vi.fn(),
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
import {
  OldFactoriesListRedirect,
  OldFactoryAgentRedirect,
  OldFactoryPageRedirect,
  OldFactoryWaitingRedirect,
} from "./FactoryRedirects";
import { ACTIVITY, FACTORY_LIST, FACTORY_PAGE, FACTORY_SEATS, REPORT } from "./fixtures";

function Where() {
  const loc = useLocation();
  return <div data-testid="where">{loc.pathname + loc.search}</div>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/factories" element={<FactoriesView />} />
        <Route path="/factories/waiting" element={<Where />} />
        <Route path="/factories/:factory" element={<FactoryView />} />
        <Route path="/factories/:factory/:tab" element={<FactoryView />} />
        <Route path="/factories/:factory/agents/:agent" element={<Where />} />
        <Route path="/session/:id" element={<Where />} />
        <Route path="/factory-agents" element={<OldFactoriesListRedirect />} />
        <Route path="/factory-agents/waiting" element={<OldFactoryWaitingRedirect />} />
        <Route path="/factory-agents/:factory" element={<OldFactoryPageRedirect />} />
        <Route path="/factory-agents/:factory/:agent" element={<OldFactoryAgentRedirect />} />
      </Routes>
      <Where />
    </MemoryRouter>,
  );
}

function where(): string {
  const all = screen.getAllByTestId("where");
  return all[all.length - 1].textContent ?? "";
}

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  screenClient.getFactoriesList.mockResolvedValue(FACTORY_LIST);
  screenClient.getFactoryPage.mockResolvedValue(FACTORY_PAGE);
  screenClient.getFactorySeats.mockResolvedValue(FACTORY_SEATS);
  agentsClient.getFactoryActivity.mockResolvedValue(ACTIVITY);
  agentsClient.getFactoryReports.mockResolvedValue(REPORT);
});

describe("Factories - the list (mockup 1)", () => {
  it("renders every row verbatim, in the Gateway's order, with the CEO's Talk button or No CEO", async () => {
    renderAt("/factories");

    const list = await screen.findByTestId("fa-factories-list");
    const rows = within(list).getAllByRole("row").slice(1);
    expect(rows.map((r) => r.getAttribute("data-testid"))).toEqual([
      "fa-factory-mindzie-web",
      "fa-factory-warmforward",
      "fa-factory-devthrottle",
    ]);
    const warm = rows[1];
    expect(within(warm).getByText("WarmForward").closest("a")?.getAttribute("href")).toBe("/factories/warmforward");
    expect(within(warm).getByText("1 question (fixture)")).toBeTruthy();
    const chip = within(warm).getByText("NEEDS YOU-X");
    expect(chip.className).toContain("fa-tone-amber");
    expect(chip.getAttribute("title")).toBe("1 question waiting on you.");
    expect(within(warm).getByRole("button", { name: "Talk to Nora Hale" })).toBeTruthy();
    expect(within(rows[0]).getByText("No CEO")).toBeTruthy();
    expect(within(rows[0]).queryByRole("button")).toBeNull();
    expect(Array.from(list.querySelectorAll("[role=columnheader]")).map((h) => h.textContent)).toEqual([
      "Factory",
      "Waiting on you",
      "Status",
      "",
    ]);
    expect(screen.getByText("Worst first: failing, then needs you, then paused, then running.")).toBeTruthy();
  });

  it("has the Factories, Activity and Reports tabs, and no All factory agents tab", async () => {
    renderAt("/factories");

    await screen.findByTestId("fa-factories-list");
    expect(screen.getAllByRole("tab").map((t) => t.textContent)).toEqual(["Factories", "Activity", "Reports"]);
    expect(screen.queryByText(/All factory agents/)).toBeNull();
  });

  it("opens a factory's page when its row is clicked", async () => {
    renderAt("/factories");

    fireEvent.click(await screen.findByTestId("fa-factory-devthrottle"));
    await waitFor(() => expect(where()).toBe("/factories/devthrottle"));
  });

  it("says what the Gateway says when no factory is registered", async () => {
    screenClient.getFactoriesList.mockResolvedValue({ ...FACTORY_LIST, rows: [], emptyText: "No factory yet (fixture)." });
    renderAt("/factories");

    expect(await screen.findByText("No factory yet (fixture).")).toBeTruthy();
  });

  it("keeps the Activity tab over every factory, and a saved report opens on the list's Reports tab", async () => {
    renderAt("/factories?tab=activity");

    await screen.findByTestId("fa-activity-table");
    expect(agentsClient.getFactoryActivity).toHaveBeenCalledWith(expect.objectContaining({ factory: "" }), expect.anything());

    cleanup();
    renderAt("/factories?tab=reports&report=abc123");
    await screen.findByTestId("fa-report-table");
    expect(agentsClient.getFactoryReports).toHaveBeenCalledWith(expect.objectContaining({ report: "abc123" }), expect.anything());
    expect(within(screen.getByTestId("fa-saved-reports")).getByText("Weekly blocks").closest("a")?.getAttribute("href")).toBe(
      "/factories?tab=reports&report=abc123",
    );
  });
});

describe("Factories - Talk", () => {
  it("shows it is working at once, then opens the session the Gateway started", async () => {
    let finish: (v: unknown) => void = () => {};
    screenClient.startFactoryTalk.mockReturnValue(new Promise((r) => (finish = r)));
    renderAt("/factories");

    fireEvent.click(await screen.findByRole("button", { name: "Talk to Nora Hale" }));
    // The busy words are the Gateway's, like the button's own label.
    const busy = await screen.findByRole("button", { name: "Starting the talk with Nora Hale (fixture)..." });
    expect((busy as HTMLButtonElement).disabled).toBe(true);
    expect(busy.getAttribute("aria-busy")).toBe("true");
    expect(screenClient.startFactoryTalk).toHaveBeenCalledWith({
      label: "Talk to Nora Hale",
      busyLabel: "Starting the talk with Nora Hale (fixture)...",
      factoryId: "warmforward",
      seatId: "nora-hale",
    });
    // The Talk button sits in a clickable row: pressing it did not also open the factory.
    expect(where()).toBe("/factories");

    finish({ sessionId: "abc-123", sessionName: "WarmForward - Nora Hale - talk with the owner", href: "/session/abc-123" });
    await waitFor(() => expect(where()).toBe("/session/abc-123"));
  });

  it("shows the Gateway's own sentence when it refuses, and the button can be pressed again", async () => {
    const sentence = "No Director is running on SOREN_NORTH, the computer Nora Hale runs on, so the talk was not started.";
    screenClient.startFactoryTalk.mockRejectedValue(new GatewayError(409, sentence, { reason: sentence }));
    renderAt("/factories");

    fireEvent.click(await screen.findByRole("button", { name: "Talk to Nora Hale" }));
    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain(sentence);
    expect((screen.getByRole("button", { name: "Talk to Nora Hale" }) as HTMLButtonElement).disabled).toBe(false);
    expect(where()).toBe("/factories");
  });
});

describe("A factory's page (mockup 2)", () => {
  it("shows the header and the Overview as folded, with the computer's change as a label, not a control", async () => {
    renderAt("/factories/warmforward");

    await screen.findByTestId("fa-overview");
    expect(screen.getByRole("navigation", { name: "Breadcrumb" }).textContent).toBe("Factories / WarmForward");
    expect(screen.getByRole("link", { name: "Factories" }).getAttribute("href")).toBe("/factories");
    expect(screen.getByTestId("fa-factory-facts").textContent).toContain("CEO Nora Hale");
    expect(screen.getByTestId("fa-factory-facts").textContent).toContain("4 seats");
    expect(screen.getByTestId("fa-factory-facts").textContent).toContain("runs on SOREN_NORTH");
    const coming = screen.getByTestId("fa-coming");
    expect(coming.textContent).toBe("change - coming");
    expect(coming.tagName).toBe("SPAN");
    expect(coming.closest("a, button")).toBeNull();
    expect(screen.getByRole("button", { name: "Talk to Nora Hale" })).toBeTruthy();

    expect(screen.getByText("A cash engine of $15,000-$40,000 a year that runs without your time.")).toBeTruthy();
    expect(screen.getByText("Only you change the goal. Approved 4 Oct 2026.")).toBeTruthy();
    expect(screen.getByText("Goal number - posted by Nora Hale, today 06:20")).toBeTruthy();
    expect(screen.getByText("Propane saved this season: not yet proven")).toBeTruthy();
    expect(screen.getByText("How it is measured").getAttribute("href")).toBe("/fixture/how-it-is-measured");
    expect(screen.getByText("Is the Bunkie Bathroom meant to be at 20 C?")).toBeTruthy();
    expect(screen.getByText("Today 06:20 - Feed healthy. Found zone 8 holding 20 C; asked you.")).toBeTruthy();
    expect(screen.getByText("All reports").getAttribute("href")).toBe("/factories?tab=activity&factory=warmforward&agent=nora-hale");
    expect(within(screen.getByTestId("fa-last-talk")).getByText("None yet.")).toBeTruthy();
  });

  it("offers the Gateway's tabs, each at its own address", async () => {
    renderAt("/factories/warmforward");

    await screen.findByTestId("fa-overview");
    const tabs = within(screen.getByRole("navigation", { name: "WarmForward view" })).getAllByRole("link");
    expect(tabs.map((t) => [t.textContent, t.getAttribute("href")])).toEqual([
      ["Overview", "/factories/warmforward"],
      ["Seats (4)", "/factories/warmforward/seats"],
      ["Activity", "/factories/warmforward/activity"],
      ["Reports", "/factories/warmforward/reports"],
      ["Memory", "/factories/warmforward/memory"],
      ["Documents", "/factories/warmforward/documents"],
    ]);
    expect(tabs[0].getAttribute("aria-current")).toBe("page");
  });

  it("says no goal and no number in the Gateway's words when there are none, and has no Talk without a CEO", async () => {
    screenClient.getFactoryPage.mockResolvedValue({
      ...FACTORY_PAGE,
      talk: null,
      ceoText: "No CEO",
      goal: { heading: "Goal", text: null, note: null, emptyText: "No goal set yet" },
      goalNumber: { heading: "Goal number", valueText: null, asOfText: null, linkHref: null, linkLabel: null, emptyText: "No number posted yet" },
    });
    renderAt("/factories/warmforward");

    await screen.findByTestId("fa-overview");
    expect(screen.getByText("No goal set yet")).toBeTruthy();
    expect(screen.getByText("No number posted yet")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^Talk/ })).toBeNull();
  });

  it("shows the Documents tab's sentence and no documents", async () => {
    renderAt("/factories/warmforward/documents");

    expect(await screen.findByText("The factory's documents are not on the Gateway yet (fixture).")).toBeTruthy();
  });

  it("fixes the Activity tab to this factory", async () => {
    renderAt("/factories/warmforward/activity");

    await screen.findByTestId("fa-activity-table");
    expect(agentsClient.getFactoryActivity).toHaveBeenCalledWith(
      expect.objectContaining({ factory: "warmforward" }),
      expect.anything(),
    );
  });
});

describe("A factory's Seats tab (mockup 3)", () => {
  it("lists each seat - name, role, when it runs, last run, computer - with its own Talk", async () => {
    renderAt("/factories/warmforward/seats");

    const table = await screen.findByTestId("fa-seats-table");
    expect(screen.getByRole("navigation", { name: "Breadcrumb" }).textContent).toBe("Factories / WarmForward / Seats");
    expect(screen.getByRole("link", { name: "WarmForward" }).getAttribute("href")).toBe("/factories/warmforward");
    expect(Array.from(table.querySelectorAll("th")).map((th) => th.textContent)).toEqual([
      "Seat",
      "When it runs",
      "Last run",
      "Computer",
      "",
    ]);
    const nora = within(table).getByTestId("fa-seat-nora-hale");
    expect(within(nora).getByText("Nora Hale")).toBeTruthy();
    expect(within(nora).getByText("CEO")).toBeTruthy();
    expect(within(nora).getByText("Daily 06:15")).toBeTruthy();
    expect(within(nora).getByText("Today 06:20 - succeeded").className).toContain("fa-tone-ok");
    expect(within(nora).getByText("change - coming").closest("a, button")).toBeNull();
    expect(within(within(table).getByTestId("fa-seat-value-hunter")).getByText("Not run yet")).toBeTruthy();
    expect(screen.getByText("Only seats the CEO hired are listed.")).toBeTruthy();
    expect(screenClient.getFactorySeats).toHaveBeenCalledWith("warmforward", expect.anything());
  });

  it("starts a talk with that seat", async () => {
    screenClient.startFactoryTalk.mockResolvedValue({ sessionId: "s-9", href: "/session/s-9" });
    renderAt("/factories/warmforward/seats");

    const row = await screen.findByTestId("fa-seat-value-hunter");
    fireEvent.click(within(row).getByRole("button", { name: "Talk" }));
    await waitFor(() => expect(where()).toBe("/session/s-9"));
    expect(screenClient.startFactoryTalk).toHaveBeenCalledWith(FACTORY_SEATS.rows[1].talk);
  });
});

describe("The old Factory Agents addresses", () => {
  it.each([
    ["/factory-agents", "/factories"],
    ["/factory-agents?tab=agents", "/factories"],
    ["/factory-agents?tab=activity&factory=warmforward", "/factories?tab=activity&factory=warmforward"],
    ["/factory-agents?tab=reports&report=abc123", "/factories?tab=reports&report=abc123"],
    ["/factory-agents/waiting?factory=warmforward", "/factories/waiting?factory=warmforward"],
    ["/factory-agents/warmforward", "/factories/warmforward"],
    ["/factory-agents/warmforward?tab=map", "/factories/warmforward"],
    ["/factory-agents/warmforward?tab=agents", "/factories/warmforward/seats"],
    ["/factory-agents/warmforward?tab=memory", "/factories/warmforward/memory"],
    ["/factory-agents/warmforward/nora-hale", "/factories/warmforward/agents/nora-hale"],
  ])("%s lands on %s", async (from, to) => {
    renderAt(from);
    await waitFor(() => expect(where()).toBe(to));
  });
});

describe("Factories at phone width (mockup 4)", () => {
  // jsdom applies no media queries, so the proof is the stylesheet itself: under the phone width the list's rows
  // become cards and its heading row is hidden, so nothing scrolls sideways at 390px.
  const css = readFileSync(join(__dirname, "factory.css"), "utf8");

  it("turns the list's rows into cards under 640px", () => {
    const phone = /@media \(max-width: 640px\) \{([\s\S]*?)\n\}/.exec(css);
    expect(phone).not.toBeNull();
    const body = phone![1];
    expect(body).toMatch(/\.fa-flist-head\s*\{[^}]*display:\s*none/);
    expect(body).toMatch(/\.fa-flist-row\s*\{[^}]*grid-template-areas/);
    expect(body).toMatch(/\.fa-flist-row\s*\{[^}]*border-radius/);
  });

  it("turns the Seats tab's rows into cards under 640px, so Talk never scrolls out of sight", () => {
    const body = /@media \(max-width: 640px\) \{([\s\S]*?)\n\}/.exec(css)![1];
    expect(body).toMatch(/\.fa-seats thead\s*\{[^}]*display:\s*none/);
    expect(body).toMatch(/\.fa-seats tr\s*\{[^}]*border-radius/);
  });

  it("renders the rows with the classes the phone layout reads", async () => {
    renderAt("/factories");

    const row = await screen.findByTestId("fa-factory-warmforward");
    expect(row.className).toBe("fa-flist-row");
    expect(row.querySelector(".fa-flist-name")).not.toBeNull();
    expect(row.querySelector(".fa-flist-status")).not.toBeNull();
    expect(row.querySelector(".fa-flist-waiting")).not.toBeNull();
    expect(row.querySelector(".fa-flist-talk")).not.toBeNull();
  });
});
