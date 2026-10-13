// @vitest-environment jsdom
// Scheduler quick pause (issue #3910). The owner's symptom, on the Schedule page and in the editor:
//   * there was no way to pause the whole scheduler with one tap and get back to the same state - now a control at
//     the top pauses everything, a banner says so in the Gateway's words, and the same control resumes;
//   * the status chip said Enabled / Disabled while the tabs said Paused - now every surface says Active or Paused,
//     the word the Gateway stamped, and one tap on it pauses or resumes that schedule;
//   * the editor had no on/off control - now it has one, opens on the schedule's own state, and saves what it shows,
//     so editing a paused schedule never resumes it unless the person picks Active.
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { GatewayError } from "@devthrottle/client-core/api/client";
import type { CronJob, CronPause } from "@devthrottle/client-core/schedule/scheduleClient";

const cronClient = vi.hoisted(() => ({
  getCronJobs: vi.fn(),
  getCronLoad: vi.fn(),
  getCronRuns: vi.fn(async () => []),
  runCronJobNow: vi.fn(),
  updateCronJob: vi.fn(),
  deleteCronJob: vi.fn(),
  createCronJob: vi.fn(),
  getCronPause: vi.fn(),
  setCronPause: vi.fn(),
  getSeatChoices: vi.fn(async () => ({ noneLabel: "No factory (Personal)", factories: [] })),
}));
// The write body is the real one: what a tap sends is part of what is proven here.
vi.mock("@devthrottle/client-core/schedule/scheduleClient", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@devthrottle/client-core/schedule/scheduleClient")>()),
  ...cronClient,
}));
vi.mock("@devthrottle/client-core/settings/settingsClient", () => ({
  getGatewaySettings: vi.fn(async () => ({ timeZone: "America/Toronto" })),
}));
vi.mock("@devthrottle/client-core/fleet/fleetClient", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@devthrottle/client-core/fleet/fleetClient")>()),
  getFleetDirectors: vi.fn(async () => []),
  getSessionsEnvelope: vi.fn(async () => ({ sessions: [], machineErrors: [], directors: [] })),
}));

import { ScheduleView } from "./ScheduleView";
import { ScheduleEditor } from "./ScheduleEditor";

const RUNNING: CronPause = {
  paused: false,
  sinceUtc: null,
  banner: null,
  actionLabel: "Pause everything",
  actionTitle: "Stop every schedule from starting (fixture).",
};
const PAUSED: CronPause = {
  paused: true,
  sinceUtc: "2026-10-12T14:00:00Z",
  banner: "Everything is paused. No schedule will start until you resume (fixture).",
  actionLabel: "Resume everything",
  actionTitle: "Let schedules start again (fixture).",
};

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
    nextRunUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    lifecycle: "active",
    stateLabel: "Active",
    toggleLabel: "Pause",
    ...extra,
  } as CronJob;
}

const ACTIVE = job("cj_on", "Morning sweep");
const SWITCHED_OFF = job("cj_off", "Evening tidy", { enabled: false, lifecycle: "paused", stateLabel: "Paused", toggleLabel: "Resume" });

function renderPage() {
  render(
    <MemoryRouter initialEntries={["/schedule"]}>
      <ScheduleView />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  window.localStorage.clear();
  cronClient.getCronJobs.mockResolvedValue([ACTIVE, SWITCHED_OFF]);
  cronClient.getCronLoad.mockResolvedValue({ generatedUtc: new Date().toISOString(), capacity: 6, machines: [] });
  cronClient.getCronPause.mockResolvedValue(RUNNING);
  cronClient.updateCronJob.mockImplementation(async (_id: string, body: CronJob) => body);
});

describe("Schedule page: pause everything", () => {
  it("pauses with one tap, says so in the Gateway's words, and resumes with one tap - changing no schedule", async () => {
    cronClient.setCronPause.mockImplementation(async (paused: boolean) => (paused ? PAUSED : RUNNING));
    renderPage();
    await screen.findByText("Morning sweep");
    expect(screen.queryByTestId("schedules-paused-banner")).toBeNull();

    fireEvent.click(await screen.findByRole("button", { name: "Pause everything" }));

    const banner = await screen.findByTestId("schedules-paused-banner");
    expect(cronClient.setCronPause).toHaveBeenCalledWith(true);
    expect(banner.textContent).toContain("Everything is paused. No schedule will start until you resume (fixture).");
    expect(banner.getAttribute("role")).toBe("status");
    // The header control now offers the way back, and so does the banner.
    expect(screen.getAllByRole("button", { name: "Resume everything" })).toHaveLength(2);
    // The schedule that was active still reads Active: the switch sits beside the schedules.
    expect(screen.getByRole("button", { name: "Active" })).toBeTruthy();

    fireEvent.click(within(banner).getByRole("button", { name: "Resume everything" }));

    await waitFor(() => expect(screen.queryByTestId("schedules-paused-banner")).toBeNull());
    expect(cronClient.setCronPause).toHaveBeenLastCalledWith(false);
    expect(await screen.findByRole("button", { name: "Pause everything" })).toBeTruthy();
    // Pausing and resuming everything wrote no schedule.
    expect(cronClient.updateCronJob).not.toHaveBeenCalled();
  });

  it("opens already showing the banner when the Gateway says everything is paused", async () => {
    cronClient.getCronPause.mockResolvedValue(PAUSED);
    renderPage();

    expect((await screen.findByTestId("schedules-paused-banner")).textContent).toContain("Everything is paused.");
  });

  it("shows the Gateway's sentence when the tap is refused, and stays as it was", async () => {
    cronClient.setCronPause.mockRejectedValue(new GatewayError(403, "PUT /cron/pause failed: Pausing is the owner's control."));
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: "Pause everything" }));

    expect((await screen.findByTestId("schedules-pause-error")).textContent).toContain("Pause everything failed:");
    expect(screen.queryByTestId("schedules-paused-banner")).toBeNull();
    expect(screen.getByRole("button", { name: "Pause everything" })).toBeTruthy();
  });
});

describe("Schedule page: one word, Paused", () => {
  it("shows Active and Paused as the Gateway stamped them, and never Enabled or Disabled", async () => {
    renderPage();
    await screen.findByText("Morning sweep");

    const activeChip = screen.getByRole("button", { name: "Active" });
    expect(activeChip.getAttribute("title")).toBe("Pause this schedule");
    expect(document.body.textContent).not.toMatch(/Enabled|Disabled/);
    expect(document.body.innerHTML).not.toMatch(/title="[^"]*(Enabled|Disabled|enabled|disabled)[^"]*"/);

    fireEvent.click(screen.getByRole("tab", { name: /Paused/ }));
    const pausedChip = await screen.findByRole("button", { name: "Paused" });
    expect(pausedChip.getAttribute("title")).toBe("Resume this schedule");
    expect(document.body.textContent).not.toMatch(/Enabled|Disabled/);
  });

  it("pauses a schedule with one tap on its state, and resumes one the same way", async () => {
    renderPage();
    await screen.findByText("Morning sweep");

    fireEvent.click(screen.getByRole("button", { name: "Active" }));

    await waitFor(() =>
      expect(cronClient.updateCronJob).toHaveBeenCalledWith("cj_on", expect.objectContaining({ enabled: false, name: "Morning sweep" })),
    );

    fireEvent.click(screen.getByRole("tab", { name: /Paused/ }));
    fireEvent.click(await screen.findByRole("button", { name: "Paused" }));

    await waitFor(() =>
      expect(cronClient.updateCronJob).toHaveBeenCalledWith("cj_off", expect.objectContaining({ enabled: true, name: "Evening tidy" })),
    );
  });
});

describe("Schedule editor: the Active / Paused control", () => {
  function open(target: CronJob) {
    const onSaved = vi.fn();
    render(<ScheduleEditor request={{ kind: "edit", job: target }} onClose={vi.fn()} onSaved={onSaved} />);
    return onSaved;
  }
  const radio = (name: string) => screen.getByRole("radio", { name }) as HTMLButtonElement;

  it("opens a paused schedule as Paused, and an edit that does not touch the control never resumes it", async () => {
    const onSaved = open(SWITCHED_OFF);

    expect(radio("Paused").getAttribute("aria-checked")).toBe("true");
    expect(radio("Active").getAttribute("aria-checked")).toBe("false");
    fireEvent.change(screen.getByDisplayValue("Evening tidy"), { target: { value: "Evening tidy, renamed" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(cronClient.updateCronJob).toHaveBeenCalledWith(
      "cj_off",
      expect.objectContaining({ name: "Evening tidy, renamed", enabled: false }),
    );
  });

  it("resumes a paused schedule only when the person picks Active", async () => {
    const onSaved = open(SWITCHED_OFF);

    fireEvent.click(radio("Active"));
    expect(radio("Active").getAttribute("aria-checked")).toBe("true");
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(cronClient.updateCronJob).toHaveBeenCalledWith("cj_off", expect.objectContaining({ enabled: true }));
  });

  it("pauses an active schedule from the editor, so the editor is never a dead end", async () => {
    const onSaved = open(ACTIVE);

    expect(radio("Active").getAttribute("aria-checked")).toBe("true");
    fireEvent.click(radio("Paused"));
    expect(screen.getByText("Paused: it does not run until it is set to Active.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(cronClient.updateCronJob).toHaveBeenCalledWith("cj_on", expect.objectContaining({ enabled: false }));
  });

  it("starts a new schedule as Active", () => {
    render(<ScheduleEditor request={{ kind: "create", timeZone: "America/Toronto" }} onClose={vi.fn()} onSaved={vi.fn()} />);

    expect(radio("Active").getAttribute("aria-checked")).toBe("true");
  });
});
