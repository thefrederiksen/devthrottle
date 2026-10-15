// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { GatewayError } from "@devthrottle/client-core/api/client";

// One seat's page (Factory Seats mission, phase 1). What is held down:
//   * a seat with NO schedule opens and shows every panel - who it is, where its brief is, how it starts, the goal and
//     what it has left behind - which is the thing the owner could not do on 13 October 2026;
//   * every word is the Gateway's: the fixtures carry "(fixture)" where the page could otherwise pass with words of
//     its own;
//   * a schedule the Gateway no longer has shows its sentence and offers nothing to press;
//   * "Take off this seat" asks first in the Gateway's sentences, sends the schedule the confirm named, shows what the
//     Gateway said it did, and a refusal stays on screen in the Gateway's words with nothing reloaded.

const screenClient = vi.hoisted(() => ({
  getFactorySeatPage: vi.fn(),
  takeScheduleOffSeat: vi.fn(),
  startFactoryTalk: vi.fn(),
  runFactoryOwnerAction: vi.fn(),
}));
vi.mock("@devthrottle/client-core/factory/factoriesScreenClient", () => screenClient);

const agentsClient = vi.hoisted(() => ({
  setFactoryPaused: vi.fn(),
  saveFactoryReport: vi.fn(),
  downloadFactoryCsv: vi.fn(),
  getFactoryActivity: vi.fn(),
  getFactoryReports: vi.fn(),
}));
vi.mock("@devthrottle/client-core/factory/factoryAgentsClient", () => agentsClient);

const cronClient = vi.hoisted(() => ({
  getCronJob: vi.fn(),
  createCronJob: vi.fn(),
  updateCronJob: vi.fn(),
  setCronJobEnabled: vi.fn(),
  getCronPause: vi.fn(),
  setCronPause: vi.fn(),
  getSeatChoices: vi.fn(async () => ({ noneLabel: "No factory (Personal)", factories: [] })),
}));
vi.mock("@devthrottle/client-core/schedule/scheduleClient", () => cronClient);

vi.mock("@devthrottle/client-core/fleet/fleetClient", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@devthrottle/client-core/fleet/fleetClient")>()),
  getFleetDirectors: vi.fn(async () => []),
  getSessionsEnvelope: vi.fn(async () => ({ sessions: [], machineErrors: [], directors: [] })),
}));

import { FactorySeatView } from "./FactorySeatView";
import { SEAT_PAGE_MISSING, SEAT_PAGE_SCHEDULED, SEAT_PAGE_TALK_ONLY } from "./fixtures";

const SCHEDULER_RUNNING = { paused: false, sinceUtc: null, banner: null, actionLabel: "Pause everything", actionTitle: "Stop every schedule (fixture)." };

function Where() {
  const loc = useLocation();
  return <div data-testid="where">{loc.pathname + loc.search}</div>;
}

function renderSeat(seat: string) {
  return render(
    <MemoryRouter initialEntries={[`/factories/warmforward/seats/${seat}`]}>
      <Routes>
        <Route path="/factories/:factory/seats/:seat" element={<FactorySeatView />} />
        <Route path="/factories/:factory/:tab" element={<Where />} />
        <Route path="/session/:id" element={<Where />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  screenClient.getFactorySeatPage.mockResolvedValue(SEAT_PAGE_SCHEDULED);
  cronClient.getCronPause.mockResolvedValue(SCHEDULER_RUNNING);
});

describe("A seat with nothing scheduled", () => {
  beforeEach(() => screenClient.getFactorySeatPage.mockResolvedValue(SEAT_PAGE_TALK_ONLY));

  it("opens, asks the Gateway for that factory and seat, and reads talk only in the Gateway's words", async () => {
    renderSeat("boss");

    await screen.findByTestId("factory-seat-page");
    expect(screenClient.getFactorySeatPage).toHaveBeenCalledWith("warmforward", "boss", expect.anything());
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Boss Talk only (fixture)");
    expect(screen.getByText("Talk only (fixture)").className).toContain("fa-tone-idle");
    expect(screen.getByTestId("fa-seat-talk-only").textContent).toBe("Talk only. Nothing is scheduled for this seat. (fixture)");
    expect(screen.getByTestId("fa-seat-facts").textContent).toBe("BossWarmForwardruns on SOREN_NORTH");
  });

  it("shows every panel all the same: the brief's place, the goal and number, and what it has left behind", async () => {
    renderSeat("boss");
    await screen.findByTestId("factory-seat-page");

    const brief = screen.getByTestId("fa-seat-brief");
    expect(within(brief).getByRole("heading").textContent).toBe("What this seat does");
    expect(screen.getByTestId("fa-seat-brief-path").textContent).toBe("/home/soren/factories/warmforward/agents/boss.yaml");
    expect(within(brief).getByText("The brief is a file on SOREN_NORTH. It is not held on the Gateway yet. (fixture)")).toBeTruthy();

    const goal = screen.getByTestId("fa-seat-goal");
    expect(within(goal).getByText("Cut the propane bill by a third this season.")).toBeTruthy();
    expect(within(goal).getByText("Litres saved: 212")).toBeTruthy();

    expect(screen.getByTestId("fa-seat-last-talk").textContent).toBe("None yet. (fixture)");
    expect(screen.getByTestId("fa-seat-last-run").textContent).toBe("Not run yet");
    // A seat with no schedule has no runs to judge, so it carries no "closes itself" verdict.
    expect(screen.queryByTestId("fa-seat-closing")).toBeNull();
    expect(screen.getByText("Nothing recorded in the last 7 days. (fixture)")).toBeTruthy();
    expect(within(screen.getByTestId("fa-seat-memory")).getByRole("link", { name: "Open Memory" }).getAttribute("href")).toBe(
      "/factories/warmforward/memory",
    );
  });

  it("offers Talk and nothing that needs a schedule", async () => {
    renderSeat("boss");
    await screen.findByTestId("factory-seat-page");

    // Talk, and the brief's own button - which needs no schedule either.
    expect(screen.getAllByRole("button").map((b) => b.textContent)).toEqual(["Talk to the boss", "Write the brief here (fixture)"]);
  });

  it("Talk starts the talk for this seat and opens the session", async () => {
    screenClient.startFactoryTalk.mockResolvedValue({ href: "/session/s-9" });
    renderSeat("boss");

    fireEvent.click(await screen.findByRole("button", { name: "Talk to the boss" }));

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/session/s-9"));
    expect(screenClient.startFactoryTalk).toHaveBeenCalledWith(SEAT_PAGE_TALK_ONLY.talk);
  });
});

describe("A seat with a schedule", () => {
  it("shows the schedule with its name, shift, Edit, Pause and Take off this seat, and links back up", async () => {
    renderSeat("scout");
    await screen.findByTestId("factory-seat-page");

    const row = screen.getByTestId("fa-seat-schedule-cj_scout");
    expect(within(row).getByText("Daily 06:15")).toBeTruthy();
    expect(within(row).getByText("WarmForward Factory - Scout - daily scan")).toBeTruthy();
    expect(within(row).getByText("Night shift")).toBeTruthy();
    expect(within(row).getAllByRole("button").map((b) => b.textContent)).toEqual(["Edit schedule", "Pause", "Take off this seat"]);
    expect(screen.queryByTestId("fa-seat-talk-only")).toBeNull();
    expect(screen.getByTestId("fa-seat-closing").textContent).toBe("closes itself - its last 7 runs (fixture)");

    const crumbs = screen.getByRole("navigation", { name: "Breadcrumb" });
    expect(crumbs.textContent).toBe("Factories / WarmForward / Seats / Scout");
    expect(within(crumbs).getAllByRole("link").map((a) => a.getAttribute("href"))).toEqual([
      "/factories",
      "/factories/warmforward",
      "/factories/warmforward/seats",
    ]);
  });

  it("Pause pauses that schedule - the target is what the button said - and reloads the page", async () => {
    cronClient.setCronJobEnabled.mockResolvedValue({ id: "cj_scout", loadWarning: null });
    renderSeat("scout");

    fireEvent.click(await screen.findByTestId("fa-seat-toggle-cj_scout"));

    await waitFor(() => expect(cronClient.setCronJobEnabled).toHaveBeenCalledWith("cj_scout", false));
    await waitFor(() => expect(screenClient.getFactorySeatPage).toHaveBeenCalledTimes(2));
  });

  it("a paused schedule says Paused and its button resumes it", async () => {
    const paused = {
      ...SEAT_PAGE_SCHEDULED,
      starts: {
        ...SEAT_PAGE_SCHEDULED.starts,
        schedules: [{ ...SEAT_PAGE_SCHEDULED.starts.schedules[0], paused: true, pausedText: "Paused (fixture)", toggleLabel: "Resume" }],
      },
    };
    screenClient.getFactorySeatPage.mockResolvedValue(paused);
    cronClient.setCronJobEnabled.mockResolvedValue({ id: "cj_scout", loadWarning: null });
    renderSeat("scout");

    expect((await screen.findByText("Paused (fixture)")).className).toContain("fa-tone-paused");
    fireEvent.click(screen.getByRole("button", { name: "Resume" }));
    await waitFor(() => expect(cronClient.setCronJobEnabled).toHaveBeenCalledWith("cj_scout", true));
  });

  it("lists a trigger that starts the seat, with the Gateway's Pause for it", async () => {
    agentsClient.setFactoryPaused.mockResolvedValue(undefined);
    screenClient.getFactorySeatPage.mockResolvedValue({
      ...SEAT_PAGE_SCHEDULED,
      starts: {
        ...SEAT_PAGE_SCHEDULED.starts,
        triggers: [
          { triggerId: "t1", text: "Trigger: New business mail, every 5 min", statusWord: "FAULT", statusTone: "red", statusText: "check failed: exit code 1", lastCheck: "22 Sep 07:55 - failed" },
        ],
        triggersPause: { action: "pause", label: "Pause triggers", busyLabel: "Pausing...", confirm: null },
      },
    });
    renderSeat("scout");

    const trigger = await screen.findByTestId("fa-seat-trigger-t1");
    expect(within(trigger).getByText("FAULT").className).toContain("fa-tone-red");
    expect(within(trigger).getByText("check failed: exit code 1")).toBeTruthy();
    expect(within(trigger).getByText("Last check: 22 Sep 07:55 - failed")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Pause triggers" }));
    await waitFor(() => expect(agentsClient.setFactoryPaused).toHaveBeenCalledWith("pause", "warmforward", "scout"));
  });
});

describe("A seat of a factory with a definition", () => {
  it("shows no Edit and no Take off, the Gateway's sentence why, and the way to the Definition tab", async () => {
    const [schedule] = SEAT_PAGE_SCHEDULED.starts.schedules;
    screenClient.getFactorySeatPage.mockResolvedValue({
      ...SEAT_PAGE_SCHEDULED,
      starts: {
        ...SEAT_PAGE_SCHEDULED.starts,
        schedules: [{ ...schedule, editLabel: null, unlink: null }],
        definedText: "WarmForward has a definition, and this seat's schedules come from it. (fixture)",
        definitionLabel: "Open the Definition tab",
        definitionHref: "/factories/warmforward/definition",
      },
    });
    renderSeat("scout");

    const defined = await screen.findByTestId("fa-seat-defined");
    expect(defined.textContent).toContain("WarmForward has a definition, and this seat's schedules come from it. (fixture)");
    expect(within(defined).getByRole("link", { name: "Open the Definition tab" }).getAttribute("href")).toBe(
      "/factories/warmforward/definition",
    );
    const row = screen.getByTestId(`fa-seat-schedule-${schedule.jobId}`);
    expect(within(row).queryByRole("button", { name: "Edit schedule" })).toBeNull();
    expect(within(row).queryByRole("button", { name: "Take off this seat" })).toBeNull();
    expect(within(row).getByRole("button", { name: "Pause" })).toBeTruthy();
  });
});

describe("A seat whose schedule the Gateway no longer has", () => {
  it("says so in the Gateway's sentence and offers nothing to press on it", async () => {
    screenClient.getFactorySeatPage.mockResolvedValue(SEAT_PAGE_MISSING);
    renderSeat("scout");
    await screen.findByTestId("factory-seat-page");

    const row = screen.getByTestId("fa-seat-schedule-cj_gone");
    expect(within(row).getByText("Schedule cj_gone is missing")).toBeTruthy();
    expect(within(row).getByText("This seat names a schedule the Gateway does not have, so nothing runs from it. (fixture)")).toBeTruthy();
    expect(within(row).queryAllByRole("button")).toEqual([]);
    expect(screen.getByText("Schedule missing (fixture)").className).toContain("fa-tone-amber");
    // It is not talk only: something was set to run it.
    expect(screen.queryByTestId("fa-seat-talk-only")).toBeNull();
  });
});

describe("Take off this seat", () => {
  it("asks first, in the Gateway's title and sentences, and sends nothing until it is confirmed", async () => {
    renderSeat("scout");

    fireEvent.click(await screen.findByTestId("fa-seat-unlink-cj_scout"));

    const dialog = await screen.findByRole("alertdialog");
    expect(within(dialog).getByText("Take this schedule off Scout? (fixture)")).toBeTruthy();
    expect(Array.from(within(dialog).getByTestId("fa-confirm-lines").querySelectorAll("p")).map((p) => p.textContent)).toEqual(
      SEAT_PAGE_SCHEDULED.starts.schedules[0].unlink!.confirmLines,
    );
    expect(screenClient.takeScheduleOffSeat).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull());
    expect(screenClient.takeScheduleOffSeat).not.toHaveBeenCalled();
  });

  it("confirmed: sends the schedule the confirm named, shows what the Gateway said it did, and reloads the page", async () => {
    screenClient.takeScheduleOffSeat.mockResolvedValue({
      text: "Taken off Scout of WarmForward. It is still switched on. (fixture)",
      factory: "warmforward",
      seat: "scout",
      job: { id: "cj_scout" },
    });
    renderSeat("scout");
    fireEvent.click(await screen.findByTestId("fa-seat-unlink-cj_scout"));
    // After it is taken off, the Gateway's page for this seat is the talk-only one.
    screenClient.getFactorySeatPage.mockResolvedValue({ ...SEAT_PAGE_TALK_ONLY, seatId: "scout", name: "Scout" });

    fireEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Take it off (fixture)" }));

    await waitFor(() => expect(screenClient.takeScheduleOffSeat).toHaveBeenCalledWith("cj_scout"));
    expect((await screen.findByTestId("fa-notice")).textContent).toBe("Taken off Scout of WarmForward. It is still switched on. (fixture)");
    await waitFor(() => expect(screen.getByTestId("fa-seat-talk-only")).toBeTruthy());
    expect(screen.queryByTestId("fa-seat-schedule-cj_scout")).toBeNull();
    expect(screenClient.getFactorySeatPage).toHaveBeenCalledTimes(2);
  });

  it("refused: the Gateway's own sentence stays on screen, no success is shown and the page is not reloaded", async () => {
    const refusal = "this schedule belongs to factory 'warmforward', so only a person or a session of 'warmforward' may change or run it";
    // As GatewayError.from builds it from the Gateway's { error }: the reason is the Gateway's own sentence.
    screenClient.takeScheduleOffSeat.mockRejectedValue(new GatewayError(403, refusal, { reason: refusal }));
    renderSeat("scout");
    fireEvent.click(await screen.findByTestId("fa-seat-unlink-cj_scout"));

    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Take it off (fixture)" }));

    await waitFor(() => expect(dialog.querySelector(".ui-confirm-error")?.textContent ?? "").toContain(refusal));
    // The confirm stays open on a refusal, so the sentence is read where the button was pressed.
    expect(screen.getByRole("alertdialog")).toBeTruthy();
    expect(screen.queryByTestId("fa-notice")).toBeNull();
    expect(screen.getByTestId("fa-seat-schedule-cj_scout")).toBeTruthy();
    expect(screenClient.getFactorySeatPage).toHaveBeenCalledTimes(1);
  });
});

describe("A seat that cannot be loaded", () => {
  it("shows the Gateway's sentence for a seat the factory does not have, never a blank page", async () => {
    const sentence = "WarmForward has no seat 'owner-session'. Its seats are: boss, scout. (fixture)";
    screenClient.getFactorySeatPage.mockRejectedValue(new GatewayError(404, sentence, { reason: sentence }));
    renderSeat("owner-session");

    expect((await screen.findByRole("alert")).textContent).toContain("WarmForward has no seat 'owner-session'. Its seats are: boss, scout. (fixture)");
    expect(screen.queryByTestId("factory-seat-page")).toBeNull();
  });
});
