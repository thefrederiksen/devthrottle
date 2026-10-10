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
