// @vitest-environment jsdom
// The schedule editor's factory and seat picker (the owner, 2026-10-10: link a schedule to its factory seat from the
// page). The choices come from the Gateway's GET /cron/seat-choices; the save carries the pair, which the Gateway
// checks against the same registry.
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { GatewayError } from "@devthrottle/client-core/api/client";

const cronClient = vi.hoisted(() => ({
  createCronJob: vi.fn(),
  updateCronJob: vi.fn(),
  getSeatChoices: vi.fn(),
  getFolderChoices: vi.fn(),
}));
vi.mock("@devthrottle/client-core/schedule/scheduleClient", () => cronClient);

// The editor reads the machines for its machine picker as it opens; only the last describe block is about that picker.
const fleet = vi.hoisted(() => ({ getFleetDirectors: vi.fn() }));
vi.mock("@devthrottle/client-core/fleet/fleetClient", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@devthrottle/client-core/fleet/fleetClient")>()),
  getFleetDirectors: fleet.getFleetDirectors,
  getSessionsEnvelope: vi.fn(async () => ({ sessions: [], machineErrors: [], directors: [] })),
}));

// The real describeAndReport, watched: what the editor shows is its sentence, and the test can see what was reported.
const reported = vi.hoisted(() => ({ describeAndReport: vi.fn() }));
vi.mock("@devthrottle/client-core/errors/reportClientError", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@devthrottle/client-core/errors/reportClientError")>();
  reported.describeAndReport.mockImplementation(actual.describeAndReport);
  return { ...actual, describeAndReport: reported.describeAndReport };
});

import { ScheduleEditor } from "./ScheduleEditor";
import type { CronJob } from "@devthrottle/client-core/schedule/scheduleClient";

const CHOICES = {
  noneLabel: "No factory (Personal)",
  factories: [
    {
      factory: "warmforward",
      title: "WarmForward Factory",
      seats: [
        { id: "ceo", label: "Nora Hale (ceo)" },
        { id: "value-hunter", label: "Value Hunter (value-hunter)" },
      ],
    },
  ],
};

const personal: CronJob = {
  id: "cj_54663c",
  name: "WarmForward Factory - Nora Hale - morning run",
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
  factory: null,
  seat: null,
};

function openOn(job: CronJob) {
  const onSaved = vi.fn();
  render(<ScheduleEditor request={{ kind: "edit", job }} onClose={vi.fn()} onSaved={onSaved} />);
  return onSaved;
}

async function factorySelect(): Promise<HTMLSelectElement> {
  const select = (await screen.findByLabelText("Factory")) as HTMLSelectElement;
  await waitFor(() => expect(select.disabled).toBe(false));
  return select;
}

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  cronClient.getSeatChoices.mockResolvedValue(CHOICES);
  cronClient.getFolderChoices.mockImplementation(async (machine: string) => ({
    machine, os: "", osName: "Unknown", online: true, placeholder: "", note: null, repositories: [],
  }));
  fleet.getFleetDirectors.mockResolvedValue([]);
});

describe("ScheduleEditor factory and seat picker", () => {
  it("links a Personal schedule to a factory seat, and saves the pair", async () => {
    cronClient.updateCronJob.mockResolvedValue({ ...personal, factory: "warmforward", seat: "ceo" });
    const onSaved = openOn(personal);

    const factory = await factorySelect();
    expect(factory.value).toBe("");
    // No seat is asked for until a factory is chosen.
    expect(screen.queryByLabelText("Seat")).toBeNull();

    fireEvent.change(factory, { target: { value: "warmforward" } });
    const seat = screen.getByLabelText("Seat") as HTMLSelectElement;
    expect(within(seat).getAllByRole("option").map((o) => o.textContent)).toEqual([
      "Choose a seat...",
      "Nora Hale (ceo)",
      "Value Hunter (value-hunter)",
    ]);
    // A factory without a seat cannot be saved; the Gateway would refuse it.
    expect(screen.getByText("Choose the seat this schedule runs as.")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(seat, { target: { value: "ceo" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(cronClient.updateCronJob).toHaveBeenCalledWith(
      "cj_54663c",
      expect.objectContaining({ factory: "warmforward", seat: "ceo", cronExpression: "15 6 * * *" }),
    );
  });

  it("leaves a Personal schedule in no factory when the picker is not touched", async () => {
    cronClient.updateCronJob.mockResolvedValue(personal);
    const onSaved = openOn(personal);
    await factorySelect();

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(cronClient.updateCronJob).toHaveBeenCalledWith("cj_54663c", expect.objectContaining({ factory: null, seat: null }));
  });

  it("offers no factory to a work-list schedule, which the Gateway would refuse", async () => {
    openOn({ ...personal, action: { ...personal.action, seed: "", workListName: "Tonight" } });
    await waitFor(() => expect(cronClient.getSeatChoices).toHaveBeenCalled());

    expect(screen.queryByLabelText("Factory")).toBeNull();
  });

  it("shows the Gateway's refusal of a pair in the form", async () => {
    cronClient.updateCronJob.mockRejectedValue(
      new GatewayError(400, "PUT /cron/jobs/cj_54663c failed", {
        reason: "'ceo' is not a seat of WarmForward Factory (warmforward); its seats are: value-hunter.",
      }),
    );
    openOn({ ...personal, factory: "warmforward", seat: "ceo" });
    await factorySelect();

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect((await screen.findByText(/is not a seat of WarmForward Factory/)).textContent).toContain("value-hunter");
  });

  it("says so when the factories cannot be read, and the schedule can still be saved as it is", async () => {
    cronClient.getSeatChoices.mockRejectedValue(new GatewayError(500, "GET /cron/seat-choices failed", { reason: "the registry could not be read" }));
    cronClient.updateCronJob.mockResolvedValue(personal);
    const onSaved = openOn(personal);

    expect((await screen.findByText(/Could not read the factories/)).textContent).toContain("the registry could not be read");
    expect(reported.describeAndReport).toHaveBeenCalledWith("cockpit-schedule-editor", "read the factories", expect.any(GatewayError));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
  });
});

// The machine picker is filled from the Gateway's Directors and sessions as the editor opens. When that read fails,
// the picker says why instead of "no machines known", and the failure is reported (the step 3 rulings, R12) - it is
// never swallowed.
describe("ScheduleEditor machine picker", () => {
  it("loadDirectors_ReadFails_PickerSaysWhyAndTheFailureIsReported", async () => {
    const failure = new GatewayError(500, "GET /fleet/directors failed", { reason: "the Gateway fell over (fake)" });
    fleet.getFleetDirectors.mockRejectedValue(failure);
    render(<ScheduleEditor request={{ kind: "create", timeZone: "UTC" }} onClose={vi.fn()} onSaved={vi.fn()} />);

    fireEvent.click(await screen.findByText("Choose..."));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("the Gateway fell over (fake)");
    expect(screen.queryByText("No machines known to this Gateway yet.")).toBeNull();
    expect(reported.describeAndReport).toHaveBeenCalledWith("cockpit-schedule-editor", "read the machines for the picker", failure);
  });
});

// Issue #4421: the folder box offers the chosen machine's repositories and an example in that machine's own shape, both
// from the Gateway (GET /cron/folder-choices). A person on a Mac is no longer shown a Windows path to copy.
describe("ScheduleEditor folder choices", () => {
  const onTheMac: CronJob = { ...personal, target: { machine: "STUDIO_MAC" }, action: { ...personal.action, repoPath: "" } };

  it("asks the Gateway about the chosen machine, and shows its placeholder instead of a Windows path", async () => {
    cronClient.getFolderChoices.mockResolvedValue({
      machine: "STUDIO_MAC", os: "macos", osName: "Mac", online: true,
      placeholder: "/Users/<you>/Documents/my-project", note: null, repositories: [],
    });
    openOn(onTheMac);

    const box = screen.getByLabelText("Repository path") as HTMLInputElement;
    await waitFor(() => expect(box.placeholder).toBe("/Users/<you>/Documents/my-project"));
    expect(cronClient.getFolderChoices).toHaveBeenCalledWith("STUDIO_MAC", expect.anything());
    expect(box.placeholder).not.toContain("\\");
  });

  it("offers the machine's repositories, and picking one fills the folder that is saved", async () => {
    cronClient.getFolderChoices.mockResolvedValue({
      machine: "STUDIO_MAC", os: "macos", osName: "Mac", online: true, placeholder: "/Users/<you>/Documents/my-project", note: null,
      repositories: [
        { name: "project", path: "/Users/pat/Documents/project", lastUsed: null, neverOpened: false },
        { name: "notes", path: "/Users/pat/notes", lastUsed: null, neverOpened: true },
      ],
    });
    cronClient.updateCronJob.mockResolvedValue({ ...onTheMac, action: { ...onTheMac.action, repoPath: "/Users/pat/notes" } });
    const onSaved = openOn(onTheMac);

    const pick = (await screen.findByLabelText("STUDIO_MAC's repositories")) as HTMLSelectElement;
    // In the Gateway's order, never re-sorted here.
    expect(within(pick).getAllByRole("option").map((o) => o.textContent)).toEqual([
      "Choose one of STUDIO_MAC's repositories...",
      "project - /Users/pat/Documents/project",
      "notes - /Users/pat/notes",
    ]);
    fireEvent.change(pick, { target: { value: "/Users/pat/notes" } });
    expect((screen.getByLabelText("Repository path") as HTMLInputElement).value).toBe("/Users/pat/notes");

    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(cronClient.updateCronJob).toHaveBeenCalledWith(
      "cj_54663c",
      expect.objectContaining({ action: expect.objectContaining({ repoPath: "/Users/pat/notes" }) }),
    );
  });

  it("asks the Gateway for the empty box's words before a machine is chosen (review, finding 5)", async () => {
    cronClient.getFolderChoices.mockResolvedValue({
      machine: "", os: "", osName: "Unknown", online: false, placeholder: "Choose the machine first", note: null, repositories: [],
    });
    render(<ScheduleEditor request={{ kind: "create", timeZone: "America/Toronto" }} onClose={vi.fn()} onSaved={vi.fn()} />);

    const box = screen.getByLabelText("Repository path") as HTMLInputElement;
    await waitFor(() => expect(box.placeholder).toBe("Choose the machine first"));
    expect(cronClient.getFolderChoices).toHaveBeenCalledWith("", expect.anything());
  });

  it("shows the Gateway's own sentence when the folder choices cannot be read, with nothing added by the form", async () => {
    cronClient.getFolderChoices.mockRejectedValue(new GatewayError(503, "GET /cron/folder-choices failed", {
      reason: "Known repository storage is not available.",
    }));
    openOn(onTheMac);

    const shown = await screen.findByText(/Known repository storage is not available\./);
    // The shared sentence alone: the Gateway's reason, and the retry hint the shared helper gives a 503.
    expect(shown.textContent).toBe("Known repository storage is not available. Try again.");
  });

  it("shows the Gateway's note when the machine is offline", async () => {
    cronClient.getFolderChoices.mockResolvedValue({
      machine: "STUDIO_MAC", os: "macos", osName: "Mac", online: false, placeholder: "/Users/<you>/Documents/my-project",
      note: "STUDIO_MAC is offline, so the folder cannot be checked when you save. It is checked when the schedule first runs.",
      repositories: [],
    });
    openOn(onTheMac);

    expect(await screen.findByText("STUDIO_MAC is offline, so the folder cannot be checked when you save. It is checked when the schedule first runs.")).toBeTruthy();
  });

  it("shows the Gateway's refusal of a Windows path for a Mac inline, and keeps the editor open", async () => {
    cronClient.updateCronJob.mockRejectedValue(new GatewayError(400, "PUT /cron/jobs/cj_54663c failed", {
      reason: "'\\\\documents\\\\devthrottle' is a Windows path, but STUDIO_MAC is a Mac. Use a path like /Users/<you>/Documents/devthrottle.",
    }));
    const onSaved = openOn({ ...onTheMac, action: { ...onTheMac.action, repoPath: "\\\\documents\\\\devthrottle" } });

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText(/is a Windows path, but STUDIO_MAC is a Mac/)).toBeTruthy();
    expect(onSaved).not.toHaveBeenCalled();
  });
});

// Issue #4436: a failed run tells its owner by default.
describe("ScheduleEditor notifications", () => {
  it("starts a new schedule on notify-when-a-run-fails", async () => {
    render(<ScheduleEditor request={{ kind: "create", timeZone: "UTC" }} onClose={vi.fn()} onSaved={vi.fn()} />);

    expect(await screen.findByDisplayValue("When a run fails (the default)")).toBeTruthy();
  });

  it("keeps an owner's choice of off", async () => {
    openOn({ ...personal, notifyOn: "none" });

    expect(await screen.findByDisplayValue("Off (no notification)")).toBeTruthy();
  });
});
