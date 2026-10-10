// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { GatewayError } from "@devthrottle/client-core/api/client";

// The Factories screen (Factories screen mission, phase D). What is held down:
//   * the list renders the Gateway's rows verbatim - name, waiting text, status chip, and the boss's Talk button or the
//     "No boss named" text - in the order the owner picked (Name A to Z the first time), and the "All factory agents"
//     tab is gone;
//   * a row opens its factory's page;
//   * the factory page shows the header, the Overview cards and the tabs exactly as folded, and its computer is
//     just the computer's name - no "change - coming" label;
//   * the Seats tab lists the seats with their own Talk buttons, and each seat's schedule has an Edit that opens the
//     Schedule page's own editor on it (the owner, 2026-10-09: a factory's schedules are changed in the factory);
//   * Talk shows a busy state at once, opens the session the Gateway started, and shows the Gateway's own sentence
//     when it refuses - never a button that does nothing silently;
//   * every old /factory-agents address lands on its new equivalent;
//   * at phone width the list is cards, from the stylesheet, not a horizontally scrolling table.

const screenClient = vi.hoisted(() => ({
  getFactoriesList: vi.fn(),
  getFactoryPage: vi.fn(),
  getFactorySeats: vi.fn(),
  getFactoryFloor: vi.fn(() => new Promise(() => {})),
  startFactoryTalk: vi.fn(),
  runFactoryOwnerAction: vi.fn(),
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

const cronClient = vi.hoisted(() => ({
  getCronJob: vi.fn(),
  createCronJob: vi.fn(),
  updateCronJob: vi.fn(),
}));
vi.mock("@devthrottle/client-core/schedule/scheduleClient", () => cronClient);

// The editor reads the machines for its picker as it opens; nothing here is about the picker.
vi.mock("@devthrottle/client-core/fleet/fleetClient", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@devthrottle/client-core/fleet/fleetClient")>()),
  getFleetDirectors: vi.fn(async () => []),
  getSessionsEnvelope: vi.fn(async () => ({ sessions: [], machineErrors: [], directors: [] })),
}));

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
  window.localStorage.clear();
  // These tests are about the TABLE (rows, column headings, the row click); the list opens on Cards since 8 Oct 2026
  // (FactoriesCards.test.tsx), so the table is asked for the way the owner would pick it: remembered in this browser.
  window.localStorage.setItem("cockpit.factoriesView", JSON.stringify("table"));
  screenClient.getFactoriesList.mockResolvedValue(FACTORY_LIST);
  screenClient.getFactoryPage.mockResolvedValue(FACTORY_PAGE);
  screenClient.getFactorySeats.mockResolvedValue(FACTORY_SEATS);
  agentsClient.getFactoryActivity.mockResolvedValue(ACTIVITY);
  agentsClient.getFactoryReports.mockResolvedValue(REPORT);
});

describe("Factories - the list (mockup 1)", () => {
  it("renders every row verbatim, by name A to Z the first time, with the boss's Talk button or No boss named", async () => {
    renderAt("/factories");

    const list = await screen.findByTestId("fa-factories-list");
    const rows = within(list).getAllByRole("row").slice(1);
    expect(rows.map((r) => r.getAttribute("data-testid"))).toEqual([
      "fa-factory-devthrottle",
      "fa-factory-mindzie-web",
      "fa-factory-warmforward",
    ]);
    const warm = rows[2];
    expect(within(warm).getByText("WarmForward").closest("a")?.getAttribute("href")).toBe("/factories/warmforward");
    expect(within(warm).getByText("1 question (fixture)")).toBeTruthy();
    const chip = within(warm).getByText("NEEDS YOU-X");
    expect(chip.className).toContain("fa-tone-amber");
    expect(chip.getAttribute("title")).toBe("1 question waiting on you.");
    expect(within(warm).getByRole("button", { name: "Talk to the boss" })).toBeTruthy();
    expect(within(rows[1]).getByText("No boss named")).toBeTruthy();
    expect(within(rows[1]).queryByRole("button")).toBeNull();
    expect(Array.from(list.querySelectorAll("[role=columnheader]")).map((h) => h.textContent)).toEqual([
      "Factory ^",
      "Waiting on you",
      "Status",
      "",
    ]);
    // The Cockpit states the order it shows; the Gateway's old "Worst first" line is no longer displayed.
    expect(screen.getByTestId("fa-sort-footer").textContent).toBe("Sorted by name, A to Z.");
    expect(screen.queryByText("Worst first: failing, then needs you, then paused, then running.")).toBeNull();
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

  it("shows the schedules outside any factory, every word verbatim and in the Gateway's order (issue #3650)", async () => {
    renderAt("/factories");

    const outside = await screen.findByTestId("fa-outside");
    expect(within(outside).getByRole("heading", { name: "Schedules outside any factory (fixture)" })).toBeTruthy();
    expect(within(outside).getByText("These run on a schedule but are no seat of any factory. (fixture)")).toBeTruthy();
    const rows = Array.from(outside.querySelectorAll("[data-testid^='fa-outside-cj_']"));
    expect(rows.map((r) => r.getAttribute("data-testid"))).toEqual(["fa-outside-cj_job001", "fa-outside-cj_money1"]);
    const money = within(rows[1] as HTMLElement);
    expect(money.getByText("Money Saver - daily")).toBeTruthy();
    expect(money.getByText("Every day at 06:00 (fixture)")).toBeTruthy();
    expect(money.getByText("Names the factory 'money-saver', which is not registered (fixture)")).toBeTruthy();
  });

  it("says so when every enabled schedule is a seat, and shows the list even with no factory registered", async () => {
    screenClient.getFactoriesList.mockResolvedValue({
      ...FACTORY_LIST,
      rows: [],
      emptyText: "No factory yet (fixture).",
      outsideRows: [],
      outsideEmptyText: "Every enabled schedule is a seat (fixture).",
    });
    renderAt("/factories");

    const outside = await screen.findByTestId("fa-outside");
    expect(within(outside).getByText("Every enabled schedule is a seat (fixture).")).toBeTruthy();
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
  // Every boss's button says the same words, so the WarmForward row is found by its row, not by the label.
  const warmTalk = async () =>
    within(await screen.findByTestId("fa-factory-warmforward")).getByRole("button", { name: "Talk to the boss" });

  it("shows it is working at once, then opens the session the Gateway started", async () => {
    let finish: (v: unknown) => void = () => {};
    screenClient.startFactoryTalk.mockReturnValue(new Promise((r) => (finish = r)));
    renderAt("/factories");

    fireEvent.click(await warmTalk());
    // The busy words are the Gateway's, like the button's own label.
    const busy = await screen.findByRole("button", { name: "Starting the talk with the boss (fixture)..." });
    expect((busy as HTMLButtonElement).disabled).toBe(true);
    expect(busy.getAttribute("aria-busy")).toBe("true");
    expect(screenClient.startFactoryTalk).toHaveBeenCalledWith({
      label: "Talk to the boss",
      busyLabel: "Starting the talk with the boss (fixture)...",
      factoryId: "warmforward",
      seatId: "nora-hale",
    });
    // The Talk button sits in a clickable row: pressing it did not also open the factory.
    expect(where()).toBe("/factories");

    finish({ sessionId: "abc-123", sessionName: "WarmForward - Boss - talk with the owner", href: "/session/abc-123" });
    await waitFor(() => expect(where()).toBe("/session/abc-123"));
  });

  it("shows the Gateway's own sentence when it refuses, and the button can be pressed again", async () => {
    const sentence = "No Director is running on SOREN_NORTH, the computer Boss runs on, so the talk was not started.";
    screenClient.startFactoryTalk.mockRejectedValue(new GatewayError(409, sentence, { reason: sentence }));
    renderAt("/factories");

    fireEvent.click(await warmTalk());
    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain(sentence);
    expect(((await warmTalk()) as HTMLButtonElement).disabled).toBe(false);
    expect(where()).toBe("/factories");
  });
});

describe("A factory's page (mockup 2)", () => {
  it("shows the header and the Overview as folded, with the computer as just its name", async () => {
    renderAt("/factories/warmforward");

    await screen.findByTestId("fa-overview");
    expect(screen.getByRole("navigation", { name: "Breadcrumb" }).textContent).toBe("Factories / WarmForward");
    expect(screen.getByRole("link", { name: "Factories" }).getAttribute("href")).toBe("/factories");
    expect(screen.getByTestId("fa-factory-facts").textContent).toContain("Boss");
    expect(screen.getByTestId("fa-factory-facts").textContent).toContain("4 seats");
    expect(screen.getByTestId("fa-factory-facts").textContent).toContain("runs on SOREN_NORTH");
    expect(screen.getByTestId("fa-factory-facts").textContent).not.toContain("change - coming");
    expect(screen.queryByTestId("fa-coming")).toBeNull();
    expect(screen.getByRole("button", { name: "Talk to the boss" })).toBeTruthy();

    expect(screen.getByText("A cash engine of $15,000-$40,000 a year that runs without your time.")).toBeTruthy();
    expect(screen.getByText("Only you change the goal. Approved 4 Oct 2026.")).toBeTruthy();
    expect(screen.getByText("Goal number - posted by Boss, today 06:20")).toBeTruthy();
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
      ["Floor", "/factories/warmforward/floor"],
      ["Seats (4)", "/factories/warmforward/seats"],
      ["Activity", "/factories/warmforward/activity"],
      ["Reports", "/factories/warmforward/reports"],
      ["Memory", "/factories/warmforward/memory"],
      ["Documents", "/factories/warmforward/documents"],
    ]);
    expect(tabs[0].getAttribute("aria-current")).toBe("page");
  });

  it("says no goal and no number in the Gateway's words when there are none, and has no Talk without a boss", async () => {
    screenClient.getFactoryPage.mockResolvedValue({
      ...FACTORY_PAGE,
      talk: null,
      bossText: "No boss named",
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
    // The boss has no name of its own: the row says Boss as its name and Boss as its role.
    expect(within(nora).getAllByText("Boss")).toHaveLength(2);
    expect(within(nora).getByText("Daily 06:15")).toBeTruthy();
    expect(within(nora).getByText("Today 06:20 - succeeded").className).toContain("fa-tone-ok");
    expect(within(nora).getByText("SOREN_NORTH").tagName).toBe("TD");
    expect(within(table).queryByText(/change - coming/)).toBeNull();
    expect(within(within(table).getByTestId("fa-seat-value-hunter")).getByText("Not run yet")).toBeTruthy();
    expect(screen.getByText("Only seats the boss hired are listed.")).toBeTruthy();
    expect(screenClient.getFactorySeats).toHaveBeenCalledWith("warmforward", expect.anything());
  });

  it("edits a seat's schedule in the Schedule page's own editor, and shows the seats again after the save", async () => {
    const boss = {
      id: "cj_boss",
      name: "WarmForward Factory - Nora Hale",
      enabled: true,
      scheduleKind: "recurring",
      cronExpression: "15 6 * * *",
      runAt: null,
      timeZoneId: "America/Toronto",
      target: { machine: "SOREN_NORTH" },
      action: { repoPath: "D:\\ReposFred\\warmforward-factory", seed: "You are Nora Hale.", workListName: null },
      preventOverlap: true,
      notifyOn: "none",
      notifyWebhookUrl: null,
      factory: "warmforward",
      seat: "nora-hale",
    };
    cronClient.getCronJob.mockResolvedValue(boss);
    cronClient.updateCronJob.mockResolvedValue({ ...boss, loadWarning: "SOREN_NORTH has 7 sessions open at 06:00." });
    renderAt("/factories/warmforward/seats");

    const nora = await screen.findByTestId("fa-seat-nora-hale");
    // A schedule that no longer exists is still told, and offers no Edit.
    const hunter = screen.getByTestId("fa-seat-value-hunter");
    expect(within(hunter).getByText("Schedule cj_value is missing")).toBeTruthy();
    expect(within(hunter).queryByRole("button", { name: "Edit schedule" })).toBeNull();
    fireEvent.click(within(nora).getByRole("button", { name: "Edit schedule" }));

    const dialog = await screen.findByRole("dialog", { name: "Edit cron job" });
    expect(cronClient.getCronJob).toHaveBeenCalledWith("cj_boss");
    expect((within(dialog).getByDisplayValue("15 6 * * *") as HTMLInputElement).value).toBe("15 6 * * *");
    fireEvent.change(within(dialog).getByDisplayValue("15 6 * * *"), { target: { value: "30 6 * * *" } });
    const seatReads = screenClient.getFactorySeats.mock.calls.length;
    fireEvent.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Edit cron job" })).toBeNull());
    expect(cronClient.updateCronJob).toHaveBeenCalledWith(
      "cj_boss",
      expect.objectContaining({ cronExpression: "30 6 * * *", name: boss.name, enabled: true }),
    );
    await waitFor(() => expect(screenClient.getFactorySeats.mock.calls.length).toBeGreaterThan(seatReads));
    // The Gateway's warning is shown here too, as on the Schedule page.
    expect((await screen.findByText(/Saved, but:/)).textContent).toContain("SOREN_NORTH has 7 sessions open at 06:00.");
  });

  it("says so when the schedule cannot be read, instead of a button that does nothing", async () => {
    cronClient.getCronJob.mockRejectedValue(new GatewayError(404, "GET /cron/jobs/cj_boss failed: no such cron job"));
    renderAt("/factories/warmforward/seats");

    fireEvent.click(within(await screen.findByTestId("fa-seat-nora-hale")).getByRole("button", { name: "Edit schedule" }));

    expect((await screen.findByRole("alert")).textContent).toContain("Could not open the schedule");
    expect(screen.queryByRole("dialog")).toBeNull();
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

describe("The owner's actions (round 2)", () => {
  it("the bulk clear's confirm shows the Gateway's title, sentences and count verbatim, and sends back what it showed", async () => {
    screenClient.runFactoryOwnerAction.mockResolvedValue({ text: "Marked 61 items handled. (fixture)", marked: 61, schedulesSwitched: [] });
    renderAt("/factories/warmforward");

    await screen.findByTestId("fa-overview");
    expect(screen.getByText("Decisions first, then questions; newest first in each. (fixture)")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Mark everything older than 7 days as handled (fixture)" }));

    const dialog = await screen.findByRole("alertdialog", { name: "Mark 61 items handled? (fixture)" });
    expect(within(dialog).getAllByText(/\(fixture\)$/).map((p) => p.textContent)).toEqual([
      "Mark 61 items handled? (fixture)",
      "This marks 61 items waiting on you from WarmForward as handled: every one from before 29 Sep 23:50, more than 7 days ago. (fixture)",
      "Nothing is deleted. (fixture)",
      "Mark 61 handled (fixture)",
    ]);
    fireEvent.click(within(dialog).getByRole("button", { name: "Mark 61 handled (fixture)" }));

    await waitFor(() => expect(screenClient.runFactoryOwnerAction).toHaveBeenCalledWith(FACTORY_PAGE.waiting.bulkHandled));
    expect((await screen.findByTestId("fa-notice")).textContent).toBe("Marked 61 items handled. (fixture)");
    await waitFor(() => expect(screenClient.getFactoryPage).toHaveBeenCalledTimes(2));
  });

  it("the archive confirm lists exactly what happens, in the Gateway's words, and a refusal stays in the confirm verbatim", async () => {
    screenClient.runFactoryOwnerAction.mockRejectedValue(
      new GatewayError(409, "The schedules changed after the confirm was shown. Nothing was done. (fixture)", {
        reason: "The schedules changed after the confirm was shown. Nothing was done. (fixture)",
      }),
    );
    renderAt("/factories/warmforward");

    fireEvent.click(await screen.findByRole("button", { name: "Archive factory (fixture)" }));
    const dialog = await screen.findByRole("alertdialog", { name: "Archive WarmForward? (fixture)" });
    expect(Array.from(within(dialog).getByTestId("fa-confirm-lines").querySelectorAll("p")).map((p) => p.textContent)).toEqual(
      FACTORY_PAGE.archive!.confirmLines,
    );
    expect(within(dialog).getByRole("button", { name: "Archive WarmForward (fixture)" }).className).toContain("ui-btn-danger");
    fireEvent.click(within(dialog).getByRole("button", { name: "Archive WarmForward (fixture)" }));

    expect(await within(dialog).findByText(/The schedules changed after the confirm was shown\. Nothing was done\. \(fixture\)/)).toBeTruthy();
    expect(screenClient.runFactoryOwnerAction).toHaveBeenCalledWith(FACTORY_PAGE.archive);
    expect(screen.queryByTestId("fa-notice")).toBeNull();
  });

  it("draws the confirm's button as the Gateway says, never by the action's kind", async () => {
    screenClient.getFactoryPage.mockResolvedValue({ ...FACTORY_PAGE, archive: { ...FACTORY_PAGE.archive!, danger: false } });
    renderAt("/factories/warmforward");

    fireEvent.click(await screen.findByRole("button", { name: "Archive factory (fixture)" }));
    const dialog = await screen.findByRole("alertdialog", { name: "Archive WarmForward? (fixture)" });
    expect(within(dialog).getByRole("button", { name: "Archive WarmForward (fixture)" }).className).not.toContain("ui-btn-danger");
  });

  it("an archived factory's page says so and offers Restore instead of Archive", async () => {
    screenClient.getFactoryPage.mockResolvedValue({
      ...FACTORY_PAGE,
      archive: null,
      archivedText: "Archived 6 Oct 23:50 by the owner. It is not on the Factories list. (fixture)",
      restore: FACTORY_LIST.archivedRows[0].restore,
    });
    renderAt("/factories/warmforward");

    const banner = await screen.findByTestId("fa-archived");
    expect(within(banner).getByText("Archived 6 Oct 23:50 by the owner. It is not on the Factories list. (fixture)")).toBeTruthy();
    expect(within(banner).getByRole("button", { name: "Restore (fixture)" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Archive factory (fixture)" })).toBeNull();
  });

  it("Show archived opens the archived factories, and Restore confirms in the Gateway's words", async () => {
    screenClient.runFactoryOwnerAction.mockResolvedValue({ text: "Tallyhand is back on the Factories list. (fixture)", marked: 0, schedulesSwitched: [] });
    renderAt("/factories");

    await screen.findByTestId("fa-factories-list");
    expect(screen.queryByTestId("fa-archived-list")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Show archived (1) (fixture)" }));

    const archived = screen.getByTestId("fa-archived-tallyhand");
    expect(within(archived).getByText("Tallyhand").closest("a")?.getAttribute("href")).toBe("/factories/tallyhand");
    expect(within(archived).getByText("Archived 6 Oct 23:50 by the owner (fixture)")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Hide archived (fixture)" })).toBeTruthy();

    fireEvent.click(within(archived).getByRole("button", { name: "Restore (fixture)" }));
    const dialog = await screen.findByRole("alertdialog", { name: "Restore Tallyhand? (fixture)" });
    expect(Array.from(within(dialog).getByTestId("fa-confirm-lines").querySelectorAll("p")).map((p) => p.textContent)).toEqual(
      FACTORY_LIST.archivedRows[0].restore.confirmLines,
    );
    expect(within(dialog).getByRole("button", { name: "Restore Tallyhand (fixture)" }).className).not.toContain("ui-btn-danger");
    fireEvent.click(within(dialog).getByRole("button", { name: "Restore Tallyhand (fixture)" }));

    expect((await screen.findByTestId("fa-notice")).textContent).toBe("Tallyhand is back on the Factories list. (fixture)");
    expect(screenClient.runFactoryOwnerAction).toHaveBeenCalledWith(FACTORY_LIST.archivedRows[0].restore);
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

describe("Factories - the owner's sort order (mockups A and B, 8 Oct 2026)", () => {
  function order(): string[] {
    const list = screen.getByTestId("fa-factories-list");
    return within(list)
      .getAllByRole("row")
      .slice(1)
      .map((r) => (r.getAttribute("data-testid") ?? "").replace("fa-factory-", ""));
  }

  it("opens on Name, A to Z, with the Sort by control showing it", async () => {
    renderAt("/factories");

    await screen.findByTestId("fa-factories-list");
    expect(order()).toEqual(["devthrottle", "mindzie-web", "warmforward"]);
    expect(screen.getByTestId("fa-sort-name").getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByTestId("fa-sort-status").getAttribute("aria-pressed")).toBe("false");
    expect(screen.getByTestId("fa-sort-direction").textContent).toBe("A to Z");
    expect(screen.getByText("3 factories")).toBeTruthy();
  });

  it("sorts by status worst first from the control, and the direction toggle reverses it to best first", async () => {
    renderAt("/factories");

    fireEvent.click(await screen.findByTestId("fa-sort-status"));
    expect(order()).toEqual(["mindzie-web", "warmforward", "devthrottle"]);
    expect(screen.getByTestId("fa-sort-direction").textContent).toBe("Worst first");
    expect(screen.getByTestId("fa-sort-footer").textContent).toContain("worst first");
    expect(screen.getByTestId("fa-sort-heading-status").textContent).toBe("Status v");

    fireEvent.click(screen.getByTestId("fa-sort-direction"));
    expect(order()).toEqual(["devthrottle", "warmforward", "mindzie-web"]);
    expect(screen.getByTestId("fa-sort-direction").textContent).toBe("Best first");
    expect(screen.getByTestId("fa-sort-heading-status").textContent).toBe("Status ^");
  });

  it("sorts by what is waiting on you, most first", async () => {
    renderAt("/factories");

    fireEvent.click(await screen.findByTestId("fa-sort-waiting"));
    // WarmForward has one question; the two with none fall back to worse status first (FAILING before RUNNING).
    expect(order()).toEqual(["warmforward", "mindzie-web", "devthrottle"]);
    expect(screen.getByTestId("fa-sort-footer").textContent).toBe(
      "Sorted by what is waiting on you, most first. Same count: worse status first, then by name.",
    );
  });

  it("sorts by a column heading, and clicking the active heading again reverses it", async () => {
    renderAt("/factories");

    await screen.findByTestId("fa-factories-list");
    fireEvent.click(screen.getByTestId("fa-sort-heading-name"));
    expect(order()).toEqual(["warmforward", "mindzie-web", "devthrottle"]);
    expect(screen.getByTestId("fa-sort-heading-name").textContent).toBe("Factory v");
    expect(screen.getByTestId("fa-sort-footer").textContent).toBe("Sorted by name, Z to A.");

    fireEvent.click(screen.getByTestId("fa-sort-heading-status"));
    expect(order()).toEqual(["mindzie-web", "warmforward", "devthrottle"]);
    expect(screen.getByTestId("fa-sort-status").getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByTestId("fa-sort-heading-name").textContent).toBe("Factory");
  });

  it("tells assistive technology which column is sorted and which way", async () => {
    renderAt("/factories");

    const list = await screen.findByTestId("fa-factories-list");
    const sortOf = () => Array.from(list.querySelectorAll("[role=columnheader]")).map((h) => h.getAttribute("aria-sort"));
    expect(sortOf()).toEqual(["ascending", "none", "none", null]);
    fireEvent.click(screen.getByTestId("fa-sort-heading-status"));
    expect(sortOf()).toEqual(["none", "none", "descending", null]);
    expect(screen.getByTestId("fa-sort-direction").getAttribute("aria-label")).toBe("Reverse the order, now Worst first");
  });

  it("matches headings to keys by their words, so a column the Gateway moves still sorts by its own key", async () => {
    screenClient.getFactoriesList.mockResolvedValue({ ...FACTORY_LIST, columns: ["Status", "Owner", "Factory"] });
    renderAt("/factories");

    await screen.findByTestId("fa-factories-list");
    fireEvent.click(screen.getByTestId("fa-sort-heading-status"));
    expect(order()).toEqual(["mindzie-web", "warmforward", "devthrottle"]);
    // A heading the Cockpit has no key for is plain text, not a button that sorts by something else.
    expect(screen.getByText("Owner").closest("button")).toBeNull();
  });

  it("remembers the last order picked in this browser and opens on it next time", async () => {
    renderAt("/factories");

    fireEvent.click(await screen.findByTestId("fa-sort-status"));
    fireEvent.click(screen.getByTestId("fa-sort-direction"));
    expect(JSON.parse(window.localStorage.getItem("cockpit.factoriesSort") ?? "null")).toEqual({ key: "status", reversed: true });

    cleanup();
    renderAt("/factories");
    await screen.findByTestId("fa-factories-list");
    expect(order()).toEqual(["devthrottle", "warmforward", "mindzie-web"]);
    expect(screen.getByTestId("fa-sort-direction").textContent).toBe("Best first");
  });
});
