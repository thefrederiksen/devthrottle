// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, within, fireEvent, waitFor } from "@testing-library/react";

// WHAT A ROW SHOWS IS ITS OWN ROW'S, IN THE GATEWAY'S COLOUR (the review of pull request 3933, finding 1).
//
// The first test file draws the Gateway's example answers, and in those answers every machine has the same forecast
// sentence, every label equals its machine name, and nothing failed to read - so a page that showed the FIRST row's
// forecast on every row, called a machine by the wrong field, or dropped a read error passed all of it. These tests
// change the answers so that each of those would show:
//   - the two machines and the accounts have DIFFERENT forecast sentences, and each row is read for its own;
//   - one machine's label is not its machine name (two users of one computer), on the card, the schedule and the log;
//   - an account and a machine's line carry a read error;
//   - a pill's COLOUR is checked by the class the stylesheet reads, not by an attribute it does not;
//   - the first read of the switch log fails;
//   - nothing has answered yet.
const getLicenseOverview = vi.fn();
const getLicenseForecast = vi.fn();
const getLicenseLog = vi.fn();
const switchLicenseNow = vi.fn();
const setLicenseRenewalDate = vi.fn();
const getLicenseApprovers = vi.fn();
vi.mock("@devthrottle/client-core/licenses/licensesClient", () => ({
  getLicenseOverview: (...args: unknown[]) => getLicenseOverview(...args),
  getLicenseForecast: (...args: unknown[]) => getLicenseForecast(...args),
  getLicenseLog: (...args: unknown[]) => getLicenseLog(...args),
  switchLicenseNow: (...args: unknown[]) => switchLicenseNow(...args),
  setLicenseRenewalDate: (...args: unknown[]) => setLicenseRenewalDate(...args),
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

async function card(accountEmail: string): Promise<HTMLElement> {
  return await screen.findByRole("region", { name: accountEmail });
}

describe("LicensesView: a pill is drawn in the Gateway's colour", () => {
  it("gives each account's status the class of its own tone, which is what the stylesheet colours", async () => {
    render(<LicensesView />);

    const classOf = async (email: string) => within(await card(email)).getByTestId("lic-account-status").className;
    expect(await classOf(ACCOUNT_A)).toContain("lic-tone-good");
    expect(await classOf(ACCOUNT_B)).toContain("lic-tone-warning");
    expect(await classOf(ACCOUNT_C)).toContain("lic-tone-bad");
    expect(await classOf(ACCOUNT_C)).not.toContain("lic-tone-good");
  });

  it("gives each log row's result the class of its own tone", async () => {
    render(<LicensesView />);

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
    answers.overview.accounts[0].statusTone = "a-later-tone" as never;
    serve(answers);
    render(<LicensesView />);

    expect(within(await card(ACCOUNT_A)).getByTestId("lic-account-status").className).toContain("lic-tone-neutral");
  });
});

describe("LicensesView: each row shows its own forecast", () => {
  it("gives each machine in the schedule its own next account and sentence", async () => {
    render(<LicensesView />);

    const rows = within(await screen.findByTestId("lic-schedule")).getAllByTestId("lic-schedule-row");
    const mac = rows.find((row) => row.textContent?.includes("the-mac")) as HTMLElement;
    const windows = rows.find((row) => row.textContent?.includes(WINDOWS_LABEL)) as HTMLElement;
    await waitFor(() => expect(mac.textContent).toContain("Next: account-b@example.com, in about 2 minutes."));
    expect(mac.textContent).not.toContain("in about 3 hours");
    expect(windows.textContent).toContain("Next: account-c@example.com, in about 3 hours.");
    expect(windows.textContent).not.toContain("in about 2 minutes");
  });

  it("draws a held machine's sentence whole, exactly as the Gateway gave it, in that machine's row and in no other", async () => {
    // The Gateway decides that a machine is held, why and until when. The time below is long past and the sentence
    // names a wait that fits no clock: a page that compared the time, or wrote a sentence of its own, would show here.
    const held = "Held out of the automatic switch for 4 minutes, until 2026-10-11 03:16 UTC: its switch from "
      + "account-a@example.com to account-b@example.com has failed more than once in a row (the exchange failed and the "
      + "machine went back to the account it was on). Switch now still sends it at once.";
    const answers = distinctAnswers();
    const windows = answers.overview.machines.find((m) => m.machineId === answers.windowsId);
    if (windows === undefined) throw new Error("the example overview has no the-windows-machine");
    windows.heldUntilUtc = "2001-01-01T00:00:00Z";
    windows.heldText = held;
    serve(answers);
    render(<LicensesView />);

    const rows = within(await screen.findByTestId("lic-schedule")).getAllByTestId("lic-schedule-row");
    const mac = rows.find((row) => row.textContent?.includes("the-mac")) as HTMLElement;
    const windowsRow = rows.find((row) => row.textContent?.includes(WINDOWS_LABEL)) as HTMLElement;
    expect(within(windowsRow).getByTestId("lic-machine-held").textContent).toBe(held);
    expect(within(mac).queryByTestId("lic-machine-held")).toBeNull();
    expect(screen.getAllByTestId("lic-machine-held")).toHaveLength(1);
  });

  // READINESS (phase 6): each machine's line shows the Gateway's readiness sentence as given, in the Gateway's tone,
  // and the two rows carry DIFFERENT sentences, so a page that drew the first row's sentence on every row, or wrote
  // a sentence of its own from the state word, would show here. A machine on no account draws no readiness line.
  it("shows each machine's readiness sentence verbatim, in its own tone, on its own row", async () => {
    const notReady = "Not ready: holds no sign-in for account-b@example.com; the last sign-in failed at 10:42 UTC "
      + "(The approval in the browser did not complete); trying again at 10:47 UTC.";
    const answers = distinctAnswers();
    const windows = answers.overview.machines.find((m) => m.machineId === answers.windowsId);
    const mac = answers.overview.machines.find((m) => m.machineId !== answers.windowsId);
    if (windows === undefined || mac === undefined) throw new Error("the example overview has not got its two machines");
    windows.readiness = "not-ready";
    windows.readinessText = notReady;
    windows.readinessTone = "warning";
    mac.readiness = "ready";
    mac.readinessText = "Ready for account-b@example.com.";
    mac.readinessTone = "good";
    serve(answers);
    render(<LicensesView />);

    const rows = within(await screen.findByTestId("lic-schedule")).getAllByTestId("lic-schedule-row");
    const macRow = rows.find((row) => row.textContent?.includes("the-mac")) as HTMLElement;
    const windowsRow = rows.find((row) => row.textContent?.includes(WINDOWS_LABEL)) as HTMLElement;
    const windowsLine = within(windowsRow).getByTestId("lic-machine-readiness");
    const macLine = within(macRow).getByTestId("lic-machine-readiness");
    expect(windowsLine.textContent).toBe(notReady);
    expect(windowsLine.className).toContain("lic-tone-warning");
    expect(macLine.textContent).toBe("Ready for account-b@example.com.");
    expect(macLine.className).toContain("lic-tone-good");
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

    await screen.findByTestId("lic-schedule");
    expect(screen.queryAllByTestId("lic-machine-readiness")).toHaveLength(0);
  });

  it("draws nothing for a hold on a machine the Gateway does not call held, whatever time it carries", async () => {
    const answers = distinctAnswers();
    for (const machine of answers.overview.machines) {
      machine.heldUntilUtc = "2999-01-01T00:00:00Z";
      machine.heldText = null;
    }
    serve(answers);
    render(<LicensesView />);

    await screen.findByTestId("lic-schedule");
    expect(screen.queryByTestId("lic-machine-held")).toBeNull();
  });

  it("gives each account's card its own expected sentence", async () => {
    const answers = distinctAnswers();
    render(<LicensesView />);

    for (const account of answers.forecast.accounts) {
      const drawn = await card(account.accountEmail);
      await waitFor(() => expect(within(drawn).getByTestId("lic-account-expected").textContent).toBe(account.expectedText));
    }
    // The three sentences differ, so a card that showed another account's would fail above.
    expect(new Set(answers.forecast.accounts.map((a) => a.expectedText)).size).toBe(3);
  });
});

describe("LicensesView: a machine is called by the Gateway's label", () => {
  it("on the cards, in the schedule and in the log - never by its machine name alone", async () => {
    render(<LicensesView />);

    const holdings = within(await card(ACCOUNT_B)).getAllByTestId("lic-holding");
    expect(holdings.some((row) => row.textContent?.includes(WINDOWS_LABEL))).toBe(true);

    const schedule = within(screen.getByTestId("lic-schedule")).getAllByTestId("lic-schedule-row");
    expect(schedule.filter((row) => row.textContent?.includes(WINDOWS_LABEL)).length).toBe(1);

    const log = within(await screen.findByTestId("lic-log")).getAllByTestId("lic-log-row");
    expect(log.length).toBeGreaterThan(0);
    expect(log.every((row) => (row.querySelectorAll("td")[2].textContent ?? "") === WINDOWS_LABEL)).toBe(true);
  });
});

describe("LicensesView: a read that failed on a machine", () => {
  it("shows the account's read error and the machine's, as the Gateway worded them, with no colour of the page's own", async () => {
    const answers = distinctAnswers();
    const c = answers.overview.accounts.find((a) => a.accountEmail === ACCOUNT_C);
    if (c === undefined) throw new Error("the example overview has no account C");
    c.readError = "network";
    c.readErrorText = "The last read of this account failed: the provider did not answer.";
    const windows = answers.overview.machines.find((m) => m.machineId === answers.windowsId);
    const spare = windows?.spares.find((h) => h.accountEmail === ACCOUNT_C);
    if (spare === undefined) throw new Error("the example overview has no spare for account C on the-windows-machine");
    spare.readError = "network";
    spare.readErrorText = "The machine could not reach the provider.";
    serve(answers);
    render(<LicensesView />);

    const drawn = await card(ACCOUNT_C);
    const onAccount = within(drawn).getByTestId("lic-account-read-error");
    expect(onAccount.textContent).toBe("The last read of this account failed: the provider did not answer.");
    const onMachine = within(drawn).getByTestId("lic-holding-read-error");
    expect(onMachine.textContent).toBe("The machine could not reach the provider.");
    // The Gateway sends no tone for a read error, so the page gives it none.
    expect(onAccount.className).not.toContain("lic-tone-");
    expect(onMachine.className).not.toContain("lic-tone-");
    // And nothing of the kind where nothing failed.
    expect(within(await card(ACCOUNT_A)).queryByTestId("lic-account-read-error")).toBeNull();
    expect(within(await card(ACCOUNT_A)).queryByTestId("lic-holding-read-error")).toBeNull();
  });

  it("says how old each account's numbers are, in the Gateway's sentence", async () => {
    const answers = distinctAnswers();
    render(<LicensesView />);

    for (const account of answers.overview.accounts) {
      expect(within(await card(account.accountEmail)).getByTestId("lic-account-read-age").textContent).toBe(account.readAgeText);
    }
    expect(new Set(answers.overview.accounts.map((a) => a.readAgeText)).size).toBeGreaterThan(1);
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
    expect(screen.queryByTestId("lic-account")).toBeNull();
  });

  it("shows Loading under the switch log while only the log has not answered", async () => {
    getLicenseLog.mockReturnValue(new Promise<never>(() => undefined));
    render(<LicensesView />);

    await card(ACCOUNT_A);
    expect(screen.getByRole("heading", { name: "Switch log" })).toBeTruthy();
    expect(screen.getByText("Loading...")).toBeTruthy();
    expect(screen.queryByTestId("lic-log")).toBeNull();
  });

  it("says so when the first read of the switch log fails, and keeps everything else on screen", async () => {
    getLicenseLog.mockRejectedValue(new Error("The switch log could not be read."));
    render(<LicensesView />);

    await card(ACCOUNT_A);
    const banner = await screen.findByRole("alert");
    expect(banner.textContent).toContain("The switch log could not be read.");
    expect(screen.getByTestId("lic-schedule")).toBeTruthy();
    expect(screen.queryByTestId("lic-log")).toBeNull();
    expect(screen.queryByText("Loading...")).toBeNull();
  });
});

describe("LicensesView: Switch now with no target chosen by the Gateway", () => {
  it("selects nothing and never calls Switch now with no target: a call that names none would be refused", async () => {
    // A state the shapes document names: Switch now is offered, and no target is chosen automatically. The page's
    // default action exists only when the Gateway chose a target; here the person picks one, and that one is sent.
    const answers = distinctAnswers();
    const a = answers.overview.accounts[0];
    a.switchNowTargetAccountEmail = null;
    a.switchNowText = "No target is chosen automatically; choose one by hand.";
    for (const option of a.switchTargets) option.isAutomaticChoice = false;
    serve(answers);
    switchLicenseNow.mockResolvedValue({ targetAccountEmail: ACCOUNT_B, text: "", switches: [], skipped: [] });
    render(<LicensesView />);
    fireEvent.click(within(await card(ACCOUNT_A)).getByRole("button", { name: "Switch now" }));
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
