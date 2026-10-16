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
  getCronPause: vi.fn(async () => ({ paused: false, banner: null, actionLabel: "Pause everything", actionTitle: "" })),
  setCronPause: vi.fn(),
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

// Factory Control, step 3: the shift a schedule works in, the Gateway's warning when a run would end after its shift,
// and each run's shift. Every word is the Gateway's; the page renders it as given.
describe("Schedule page: shifts and the shift-end warning", () => {
  const WARNING =
    "Ends after its shift: the run on Sun 11 Oct at 15:45 starts in the morning shift and usually takes about 25 min, so it would still be going when the shift ends at 16:00. 7 of its 7 runs in the next 7 days do this.";
  const RULE = "The rule: a run belongs to the shift it starts in (fixture).";

  it("shows each schedule's shift, marks the warned one, and gives the warning and its rule in the detail", async () => {
    cronClient.getCronJobs.mockResolvedValue([
      job("cj_own", "Own morning sweep", { shiftText: "Morning shift", shiftWarning: WARNING, shiftRule: RULE }),
      job("cj_orphan", "Retired factory run", { factory: "retired", seat: "boss", factoryHref: null, shiftText: "Night shift" }),
    ]);
    cronClient.getCronRuns.mockResolvedValue([
      {
        scheduledUtc: "2026-10-10T19:45:00Z",
        firedUtc: "2026-10-10T19:45:02Z",
        machine: "SOREN_NORTH",
        targetDirectorId: "d1",
        infraStatus: "started",
        taskStatus: "succeeded",
        shiftText: "morning shift, Sat 10 Oct, 08:00-16:00",
      },
    ] as never);
    renderPage();

    const warned = await screen.findByTestId("sched-shift-cj_own");
    expect(warned.textContent).toBe("Morning shift");
    expect(warned.className).toContain("warn");
    expect(warned.getAttribute("title")).toBe(WARNING);
    const quiet = screen.getByTestId("sched-shift-cj_orphan");
    expect(quiet.textContent).toBe("Night shift");
    expect(quiet.className).not.toContain("warn");

    fireEvent.click(screen.getByText("Own morning sweep"));

    const banner = await screen.findByTestId("sched-shift-warning");
    expect(banner.textContent).toContain(WARNING);
    expect(banner.textContent).toContain(RULE);
    expect(await screen.findByText("morning shift, Sat 10 Oct, 08:00-16:00")).toBeTruthy();
  });

  it("shows a run whose start is not known in the Gateway's words and tone, never as did not run", async () => {
    cronClient.getCronJobs.mockResolvedValue([job("cj_own", "Own morning sweep", { shiftText: "Morning shift" })]);
    cronClient.getCronRuns.mockResolvedValue([
      {
        scheduledUtc: "2026-10-16T04:05:07Z",
        firedUtc: "2026-10-16T04:05:37Z",
        machine: "SOREN_NORTH",
        targetDirectorId: "d1",
        infraStatus: "start-unknown",
        infraTone: "warn",
        taskStatus: "unknown",
        ending: "start-unknown",
        endingText: "may have started - the Gateway did not learn whether its session started",
      },
    ] as never);
    renderPage();

    fireEvent.click(await screen.findByText("Own morning sweep"));

    const infra = await screen.findByTestId("sched-run-infra");
    expect(infra.textContent).toBe("start-unknown");
    expect(infra.className).toBe("sched-st warn");
    expect(screen.getByText("may have started - the Gateway did not learn whether its session started")).toBeTruthy();
    expect(screen.queryByText(/did not run/)).toBeNull();
  });

  it("shows no warning for a schedule the Gateway did not warn about", async () => {
    cronClient.getCronJobs.mockResolvedValue([job("cj_own", "Own morning sweep", { shiftText: "Morning shift" })]);
    renderPage();

    fireEvent.click(await screen.findByText("Own morning sweep"));

    await waitFor(() => expect(cronClient.getCronRuns).toHaveBeenCalledWith("cj_own"));
    expect(screen.queryByTestId("sched-shift-warning")).toBeNull();
  });
});
