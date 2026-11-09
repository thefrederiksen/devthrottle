// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, within, fireEvent, waitFor } from "@testing-library/react";

// WHAT A ROW SHOWS IS ITS OWN ROW'S, IN THE GATEWAY'S COLOUR (the review of pull request 3933, finding 1).
//
// The first test file draws the Gateway's example answers, and in those answers every machine has the same forecast
// sentence, every label equals its machine name, and nothing failed to read - so a page that showed the FIRST row's
// forecast on every row, called a machine by the wrong field, or dropped a read error passed all of it. These tests
// change the answers so that each of those would show:
//   - the two machines have DIFFERENT next licenses and sentences, and each line and drawer is read for its own;
//   - one machine's label is not its machine name (two users of one computer), in the table, its drawer and the log;
//   - a sign-in on a machine carries a read error;
//   - a pill's COLOUR is checked by the class the stylesheet reads, not by an attribute it does not;
//   - the first read of the switch log fails;
//   - nothing has answered yet.
const getLicenseOverview = vi.fn();
const getLicenseForecast = vi.fn();
const getLicenseLog = vi.fn();
const switchLicenseNow = vi.fn();
const getLicenseApprovers = vi.fn();
vi.mock("@devthrottle/client-core/licenses/licensesClient", () => ({
  getLicenseAlarms: async () => ({ count: 0, summary: null, linkText: null, alarms: [], tone: null }),
  getLicenseOverview: (...args: unknown[]) => getLicenseOverview(...args),
  getLicenseForecast: (...args: unknown[]) => getLicenseForecast(...args),
  getLicenseLog: (...args: unknown[]) => getLicenseLog(...args),
  switchLicenseNow: (...args: unknown[]) => switchLicenseNow(...args),
  getLicenseApprovers: (...args: unknown[]) => getLicenseApprovers(...args),
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  describeAndReport: (_surface: string, _action: string, err: unknown) => (err instanceof Error ? err.message : String(err)),
  backgroundRecovered: () => undefined,
}));

import { LicensesView } from "./LicensesView";
import { APPROVERS_EMPTY, FORECAST, LOG_PAGE, OVERVIEW, copyOf } from "./fixtures";

const ACCOUNT_A = "account-a@example.com";
const ACCOUNT_B = "account-b@example.com";
const ACCOUNT_C = "account-c@example.com";
// Two users of one computer are two machines; the Gateway tells them apart in the label.
const WINDOWS_LABEL = "the-windows-machine (owner)";

/** The Gateway's example answers, changed so that every row differs from its neighbour. */
function distinctAnswers() {
  const overview = copyOf(OVERVIEW);
  const forecast = copyOf(FORECAST);
  const log = copyOf(LOG_PAGE);
  const windows = overview.machines.find((m) => m.machineName === "the-windows-machine");
  if (windows === undefined) throw new Error("the example overview has no the-windows-machine");
  windows.label = WINDOWS_LABEL;
  for (const entry of log.entries) {
    if (entry.machineId === windows.machineId) entry.label = WINDOWS_LABEL;
  }
  const next = forecast.machines.find((m) => m.machineId === windows.machineId);
  if (next === undefined) throw new Error("the example forecast has no the-windows-machine");
  next.label = WINDOWS_LABEL;
  next.nextAccountEmail = ACCOUNT_C;
  next.text = "Next: account-c@example.com, in about 3 hours.";
  return { overview, forecast, log, windowsId: windows.machineId };
}

function serve(answers: ReturnType<typeof distinctAnswers>) {
  getLicenseOverview.mockResolvedValue(answers.overview);
  getLicenseForecast.mockResolvedValue(answers.forecast);
  getLicenseLog.mockResolvedValue(answers.log);
  getLicenseApprovers.mockResolvedValue(copyOf(APPROVERS_EMPTY));
}

beforeEach(() => serve(distinctAnswers()));

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

/** The line of one machine in the Machines table. */
async function machineLine(label: string): Promise<HTMLElement> {
  const rows = within(await screen.findByTestId("lic-machines")).getAllByTestId("lic-machine-row");
  const row = rows.find((r) => r.querySelector(".lic-strong")?.textContent === label);
  if (row === undefined) throw new Error(`no line for ${label}`);
  return row;
}

/** The drawer of one machine, opened from its line. */
async function drawer(label: string): Promise<HTMLElement> {
  fireEvent.click(within(await machineLine(label)).getByRole("button", { name: `More about ${label}` }));
  return screen.getByTestId("lic-drawer");
}

describe("LicensesView: a word is drawn in the Gateway's colour", () => {
  it("gives each machine's ready-for-next words the class of its own tone, which is what the stylesheet colours", async () => {
    const answers = distinctAnswers();
    const windows = answers.overview.machines.find((m) => m.machineId === answers.windowsId)!;
    windows.readyForNext = null;
    windows.readyForNextText = "No other license has room";
    windows.readyForNextTone = "neutral";
    serve(answers);
    render(<LicensesView />);

    expect(within(await machineLine("the-mac")).getByTestId("lic-machine-ready").className).toContain("lic-tone-good");
    const other = within(await machineLine(WINDOWS_LABEL)).getByTestId("lic-machine-ready");
    expect(other.className).toContain("lic-tone-neutral");
    expect(other.className).not.toContain("lic-tone-good");
  });

  it("gives each log row's result the class of its own tone", async () => {
    render(<LicensesView initialTab="history" />);

    const rows = within(await screen.findByTestId("lic-log")).getAllByTestId("lic-log-row");
    const pill = (state: string) =>
      within(rows.find((row) => row.getAttribute("data-state") === state) as HTMLElement).getByTestId("lic-log-state").className;
    expect(pill("failed")).toContain("lic-tone-bad");
    expect(pill("failed")).not.toContain("lic-tone-good");
    expect(pill("switched")).toContain("lic-tone-good");
    expect(pill("recorded")).toContain("lic-tone-neutral");
  });

  it("draws a tone word this build does not know as neutral", async () => {
    const answers = distinctAnswers();
    answers.overview.machines[0].readyForNextTone = "a-later-tone" as never;
    serve(answers);
    render(<LicensesView />);

    expect(within(await machineLine("the-mac")).getByTestId("lic-machine-ready").className).toContain("lic-tone-neutral");
  });
});

describe("LicensesView: each machine shows its own line and its own drawer", () => {
  it("gives each machine in the table its own next license", async () => {
    const answers = distinctAnswers();
    answers.overview.machines.find((m) => m.machineId === answers.windowsId)!.readinessTargetAccountEmail = ACCOUNT_C;
    serve(answers);
    render(<LicensesView />);

    expect((await machineLine("the-mac")).textContent).toContain(ACCOUNT_B);
    expect((await machineLine("the-mac")).textContent).not.toContain(ACCOUNT_C);
    expect((await machineLine(WINDOWS_LABEL)).textContent).toContain(ACCOUNT_C);
  });

  it("draws a held machine's sentence whole, exactly as the Gateway gave it, in that machine's drawer and in no other", async () => {
    // The Gateway decides that a machine is held, why and until when. The time below is long past and the sentence
    // names a wait that fits no clock: a page that compared the time, or wrote a sentence of its own, would show here.
    const held = "Held out of the automatic switch for 4 minutes, until 2026-10-11 03:16 UTC: its switch from "
      + "account-a@example.com to account-b@example.com has failed more than once in a row (the exchange failed and the "
      + "machine went back to the account it was on). Switch now still sends it at once.";
    const answers = distinctAnswers();
    const windows = answers.overview.machines.find((m) => m.machineId === answers.windowsId)!;
    windows.heldUntilUtc = "2001-01-01T00:00:00Z";
    windows.heldText = held;
    serve(answers);
    render(<LicensesView />);

    expect(within(await drawer(WINDOWS_LABEL)).getByText(held)).toBeTruthy();
    fireEvent.click(within(screen.getByTestId("lic-drawer")).getByRole("button", { name: "Close" }));
    expect(within(await drawer("the-mac")).queryByText(held)).toBeNull();
  });

  // READINESS (phase 6): each machine's drawer shows the Gateway's readiness sentence as given, in the Gateway's tone, and
  // the two machines carry DIFFERENT sentences, so a page that drew one machine's sentence for another would show here.
  it("shows each machine's readiness sentence verbatim, in its own tone, in its own drawer", async () => {
    const notReadyText = "Not ready: holds no sign-in for account-b@example.com; the last sign-in failed at 10:42 UTC "
      + "(The approval in the browser did not complete); trying again at 10:47 UTC.";
    const answers = distinctAnswers();
    const windows = answers.overview.machines.find((m) => m.machineId === answers.windowsId)!;
    windows.readiness = "not-ready";
    windows.readinessText = notReadyText;
    windows.readinessTone = "warning";
    serve(answers);
    render(<LicensesView />);

    const line = within(await drawer(WINDOWS_LABEL)).getByTestId("lic-drawer-readiness");
    expect(line.textContent).toBe(notReadyText);
    expect(line.className).toContain("lic-tone-warning");
    fireEvent.click(within(screen.getByTestId("lic-drawer")).getByRole("button", { name: "Close" }));
    const mac = within(await drawer("the-mac")).getByTestId("lic-drawer-readiness");
    expect(mac.textContent).toBe("Ready for account-b@example.com.");
    expect(mac.className).toContain("lic-tone-good");
  });

  it("draws no readiness line for a machine the Gateway gives none", async () => {
    const answers = distinctAnswers();
    for (const machine of answers.overview.machines) {
      machine.readiness = null;
      machine.readinessTargetAccountEmail = null;
      machine.readinessText = null;
      machine.readinessTone = null;
    }
    serve(answers);
    render(<LicensesView />);

    expect(within(await drawer("the-mac")).queryByTestId("lic-drawer-readiness")).toBeNull();
  });

  it("draws a refused machine's status and its sentence exactly as the Gateway gave them, with no last report time", async () => {
    const refused = "Its report was refused: it holds account-a@example.com twice (once in use and once as a spare). It is "
      + "not switched until a report is accepted. Its Director is still sending reports: 412 reports refused since "
      + "2026-10-16 01:02 UTC, the latest 1 minute ago.";
    const answers = distinctAnswers();
    const windows = answers.overview.machines.find((m) => m.machineId === answers.windowsId)!;
    windows.status = "report-refused";
    windows.statusText = "Report refused";
    windows.statusTone = "bad";
    windows.statusDetail = refused;
    windows.lastReportAtUtc = null;
    windows.lastReportText = "No report from it has been accepted yet";
    serve(answers);
    render(<LicensesView />);

    const opened = await drawer(WINDOWS_LABEL);
    expect(within(opened).getByText(refused)).toBeTruthy();
    expect(opened.textContent).toContain("Report refused");
    expect(opened.textContent).toContain("No report from it has been accepted yet");
  });

  it("draws nothing for a hold on a machine the Gateway does not call held, whatever time it carries", async () => {
    const answers = distinctAnswers();
    for (const machine of answers.overview.machines) {
      machine.heldUntilUtc = "2999-01-01T00:00:00Z";
      machine.heldText = null;
    }
    serve(answers);
    render(<LicensesView />);

    expect((await drawer("the-mac")).textContent).not.toContain("2999");
  });
});

describe("LicensesView: a machine is called by the Gateway's label", () => {
  it("in the Machines table, in its drawer and in the log - never by its machine name alone", async () => {
    render(<LicensesView />);

    expect(within(await drawer(WINDOWS_LABEL)).getByRole("heading", { name: WINDOWS_LABEL })).toBeTruthy();
    fireEvent.click(within(screen.getByTestId("lic-drawer")).getByRole("button", { name: "Close" }));

    fireEvent.click(screen.getByRole("tab", { name: "History" }));
    const log = within(await screen.findByTestId("lic-log")).getAllByTestId("lic-log-row");
    expect(log.length).toBeGreaterThan(0);
    expect(log.every((row) => (row.querySelectorAll("td")[2].textContent ?? "") === WINDOWS_LABEL)).toBe(true);
  });
});

describe("LicensesView: a read that failed on a machine", () => {
  it("shows a sign-in's read error in the machine's drawer, as the Gateway worded it, with no colour of the page's own", async () => {
    const answers = distinctAnswers();
    const windows = answers.overview.machines.find((m) => m.machineId === answers.windowsId)!;
    const spare = windows.spares.find((h) => h.accountEmail === ACCOUNT_C);
    if (spare === undefined) throw new Error("the example overview has no spare for account C on the-windows-machine");
    spare.readError = "network";
    spare.readErrorText = "The machine could not reach the provider.";
    serve(answers);
    render(<LicensesView />);

    const line = within(await drawer(WINDOWS_LABEL)).getByText("The machine could not reach the provider.");
    expect(line.className).not.toContain("lic-tone-");
  });
});

describe("LicensesView: before an answer, and when the log cannot be read", () => {
  it("shows Loading at once, before the Gateway has answered anything", async () => {
    const never = new Promise<never>(() => undefined);
    getLicenseOverview.mockReturnValue(never);
    getLicenseForecast.mockReturnValue(never);
    getLicenseLog.mockReturnValue(never);
    render(<LicensesView />);

    expect(screen.getByRole("heading", { name: "Licenses" })).toBeTruthy();
    expect(screen.getByText("Loading...")).toBeTruthy();
    expect(screen.queryByTestId("lic-status")).toBeNull();
  });

  it("shows Loading on the History tab while only the log has not answered", async () => {
    getLicenseLog.mockReturnValue(new Promise<never>(() => undefined));
    render(<LicensesView />);

    await screen.findByTestId("lic-status");
    fireEvent.click(screen.getByRole("tab", { name: "History" }));
    expect(screen.getByText("Loading...")).toBeTruthy();
    expect(screen.queryByTestId("lic-log")).toBeNull();
  });

  it("says so on the History tab when the first read of the switch log fails, and keeps the Overview whole", async () => {
    getLicenseLog.mockRejectedValue(new Error("The switch log could not be read."));
    render(<LicensesView initialTab="history" />);

    const banner = await screen.findByRole("alert");
    expect(banner.textContent).toContain("The switch log could not be read.");
    expect(screen.queryByTestId("lic-log")).toBeNull();
    expect(screen.queryByText("Loading...")).toBeNull();
    fireEvent.click(screen.getByRole("tab", { name: "Overview" }));
    expect(await screen.findByTestId("lic-status")).toBeTruthy();
    expect(screen.getByTestId("lic-machines")).toBeTruthy();
  });
});

describe("LicensesView: Switch now with no target chosen by the Gateway", () => {
  it("selects nothing and never calls Switch now with no target: a call that names none would be refused", async () => {
    const answers = distinctAnswers();
    const a = answers.overview.accounts[0];
    a.switchNowTargetAccountEmail = null;
    a.switchNowText = "No target is chosen automatically; choose one by hand.";
    for (const option of a.switchTargets) option.isAutomaticChoice = false;
    serve(answers);
    switchLicenseNow.mockResolvedValue({ targetAccountEmail: ACCOUNT_B, text: "", switches: [], skipped: [] });
    render(<LicensesView />);
    fireEvent.click(within(await screen.findByTestId("lic-status")).getByRole("button", { name: "Switch now" }));
    const dialog = screen.getByRole("alertdialog");

    expect(within(dialog).getByText("No target is chosen automatically; choose one by hand.")).toBeTruthy();
    expect((within(dialog).getAllByRole("radio") as HTMLInputElement[]).some((radio) => radio.checked)).toBe(false);
    const confirm = within(dialog).getByRole("button", { name: "Switch now" }) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    fireEvent.click(confirm);
    expect(switchLicenseNow).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole("radio", { name: /account-b@example\.com/ }));
    fireEvent.click(confirm);

    await waitFor(() => expect(switchLicenseNow).toHaveBeenCalledTimes(1));
    expect(switchLicenseNow).toHaveBeenCalledWith(ACCOUNT_A, ACCOUNT_B);
  });
});
