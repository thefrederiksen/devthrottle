// @vitest-environment jsdom
// The Schedule page's switch (the owner, 2026-10-09): your own jobs by default, and "Show factory schedules" brings the
// factories' ones back, in every list. A factory schedule is changed in its factory, so its Edit goes there. Which
// schedule is a factory's is the Gateway's ruling, stamped as factoryHref: one whose factory is not registered stays
// the owner's own, with its own Edit and Delete.
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { CronJob } from "@devthrottle/client-core/schedule/scheduleClient";

const cronClient = vi.hoisted(() => ({
  getCronJobs: vi.fn(),
  getCronLoad: vi.fn(),
  getCronRuns: vi.fn(async () => []),
  runCronJobNow: vi.fn(),
  updateCronJob: vi.fn(),
  deleteCronJob: vi.fn(),
  createCronJob: vi.fn(),
  getSeatChoices: vi.fn(async () => ({ noneLabel: "No factory (Personal)", factories: [] })),
}));
vi.mock("@devthrottle/client-core/schedule/scheduleClient", () => cronClient);
vi.mock("@devthrottle/client-core/settings/settingsClient", () => ({
  getGatewaySettings: vi.fn(async () => ({ timeZone: "America/Toronto" })),
}));

import { ScheduleView } from "./ScheduleView";

const NEXT = new Date(Date.now() + 60 * 60 * 1000).toISOString();

function job(id: string, name: string, extra: Partial<CronJob> = {}): CronJob {
  return {
    id,
    name,
    enabled: true,
    scheduleKind: "recurring",
    cronExpression: "0 7 * * *",
    runAt: null,
    timeZoneId: "America/Toronto",
    target: { machine: "SOREN_NORTH" },
    action: { repoPath: "D:\\ReposFred\\repo", seed: "/help", workListName: null },
    preventOverlap: true,
    notifyOn: "none",
    notifyWebhookUrl: null,
    nextRunUtc: NEXT,
    lifecycle: "active",
    ...extra,
  } as CronJob;
}

const JOBS: CronJob[] = [
  job("cj_own", "Own morning sweep"),
  job("cj_fac", "Funnel builder run", {
    factory: "clickfunnels",
    seat: "builder",
    factoryTitle: "ClickFunnels",
    factoryHref: "/factories/clickfunnels/seats",
  }),
  job("cj_fac_paused", "Funnel tidy run", {
    enabled: false,
    lifecycle: "paused",
    factory: "clickfunnels",
    seat: "builder",
    factoryTitle: "ClickFunnels",
    factoryHref: "/factories/clickfunnels/seats",
  }),
  // Its factory is not registered, so the Gateway stamps no address: it stays the owner's own.
  job("cj_orphan", "Retired factory run", { factory: "retired", seat: "boss", factoryHref: null }),
];

function renderPage() {
  render(
    <MemoryRouter initialEntries={["/schedule"]}>
      <ScheduleView />
    </MemoryRouter>,
  );
}

function factorySwitch(): HTMLInputElement {
  return screen.getByRole("checkbox", { name: /Show factory schedules/ }) as HTMLInputElement;
}

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  window.localStorage.clear();
  cronClient.getCronJobs.mockResolvedValue(JOBS);
  cronClient.getCronLoad.mockResolvedValue({ generatedUtc: new Date().toISOString(), capacity: 6, machines: [] });
});

describe("Schedule page: your own jobs first, factory schedules behind a switch", () => {
  it("shows only your own jobs by default, and says how many are in factories", async () => {
    renderPage();

    expect(await screen.findByText("Own morning sweep")).toBeTruthy();
    expect(screen.getByText("Retired factory run")).toBeTruthy();
    expect(screen.queryByText("Funnel builder run")).toBeNull();
    expect(factorySwitch().checked).toBe(false);
    expect(factorySwitch().parentElement?.textContent).toContain("Show factory schedules (1)");
    expect(screen.getByText("2 active jobs of your own, plus 1 in factories.")).toBeTruthy();
  });

  it("brings the factory schedules back when switched on, remembers it, and sends their Edit to the factory", async () => {
    renderPage();
    await screen.findByText("Own morning sweep");

    fireEvent.click(factorySwitch());

    expect(await screen.findByText(/Funnel builder run|builder run/)).toBeTruthy();
    expect(window.localStorage.getItem("schedule.showFactorySchedules")).toBe("1");
    const toFactory = screen.getAllByRole("link", { name: "Edit in factory" });
    expect(toFactory.length).toBe(1);
    expect(toFactory[0].getAttribute("href")).toBe("/factories/clickfunnels/seats");
  });

  it("keeps a schedule whose factory is not registered as your own, with its own Edit and Delete", async () => {
    renderPage();
    await screen.findByText("Own morning sweep");

    // Two own jobs, each with Edit and Delete; no link to a factory.
    expect(screen.getAllByRole("button", { name: "Edit" }).length).toBe(2);
    expect(screen.getAllByRole("button", { name: "Delete" }).length).toBe(2);
    expect(screen.queryByRole("link", { name: "Edit in factory" })).toBeNull();
  });

  it("hides factory schedules from Paused too, until the switch is on", async () => {
    renderPage();
    await screen.findByText("Own morning sweep");

    fireEvent.click(screen.getByRole("tab", { name: /Paused/ }));
    await waitFor(() => expect(screen.queryByText("Own morning sweep")).toBeNull());
    expect(screen.queryByText(/tidy run/)).toBeNull();

    fireEvent.click(factorySwitch());
    expect(await screen.findByText(/tidy run/)).toBeTruthy();
  });

  it("opens with the switch on when it was left on", async () => {
    window.localStorage.setItem("schedule.showFactorySchedules", "1");
    renderPage();

    expect(await screen.findByText(/builder run/)).toBeTruthy();
    expect(factorySwitch().checked).toBe(true);
  });
});
