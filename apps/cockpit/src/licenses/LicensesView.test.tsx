// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, waitFor, within, fireEvent } from "@testing-library/react";
import type { LicenseLogEntry } from "@devthrottle/client-core/licenses/licensesClient";

// THE LICENSES PAGE SHOWS WHAT THE GATEWAY RULED AND RULES NOTHING ITSELF (CLAUDE.md rule 7). The fixtures are the
// Gateway's own example answers (fixtures.ts), so these tests draw what the Gateway sends. What they pin:
//   - an account whose machines never reported shows the Gateway's sentence, and no table;
//   - a status, a tone and a window's level are drawn from the Gateway's words, never worked out from a number;
//   - a window that was not read is drawn hatched and is never a meter at zero;
//   - each machine's sign-in for an account shows when it expires, in the Gateway's tone;
//   - a failed switch is in the log with its reason;
//   - Switch now sends nothing until it is confirmed, sends the account and the picked target, and shows a
//     refusal in the Gateway's own sentence.
const getLicenseOverview = vi.fn();
const getLicenseForecast = vi.fn();
const getLicenseLog = vi.fn();
const switchLicenseNow = vi.fn();
vi.mock("@devthrottle/client-core/licenses/licensesClient", () => ({
  getLicenseOverview: (...args: unknown[]) => getLicenseOverview(...args),
  getLicenseForecast: (...args: unknown[]) => getLicenseForecast(...args),
  getLicenseLog: (...args: unknown[]) => getLicenseLog(...args),
  switchLicenseNow: (...args: unknown[]) => switchLicenseNow(...args),
}));
// describeAndReport is the one way an error reaches the screen (and the Gateway); here it only has to say the error.
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  describeAndReport: (_surface: string, _action: string, err: unknown) => (err instanceof Error ? err.message : String(err)),
  backgroundRecovered: () => undefined,
}));

import { LicensesView, foldNewestLogPage, mergeLog } from "./LicensesView";
import { EMPTY_OVERVIEW, FORECAST, LOG_PAGE, OVERVIEW, copyOf } from "./fixtures";

const ACCOUNT_A = "account-a@example.com";
const ACCOUNT_B = "account-b@example.com";
const ACCOUNT_C = "account-c@example.com";

beforeEach(() => {
  getLicenseOverview.mockResolvedValue(copyOf(OVERVIEW));
  getLicenseForecast.mockResolvedValue(copyOf(FORECAST));
  getLicenseLog.mockResolvedValue(copyOf(LOG_PAGE));
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

/** The card of one account, once the page has drawn it. */
async function card(accountEmail: string): Promise<HTMLElement> {
  return await screen.findByRole("region", { name: accountEmail });
}

describe("LicensesView: an account whose machines have never reported", () => {
  it("shows the Gateway's sentence in place of every list", async () => {
    getLicenseOverview.mockResolvedValue(copyOf(EMPTY_OVERVIEW));
    render(<LicensesView />);

    expect(await screen.findByText(EMPTY_OVERVIEW.emptyText as string)).toBeTruthy();
    expect(screen.queryByTestId("lic-account")).toBeNull();
    expect(screen.queryByTestId("lic-schedule")).toBeNull();
    expect(screen.queryByTestId("lic-log")).toBeNull();
    expect(screen.queryByRole("button", { name: "Switch now" })).toBeNull();
  });
});

describe("LicensesView: the accounts", () => {
  it("draws one card per account, with the Gateway's status word in the Gateway's tone", async () => {
    render(<LicensesView />);

    const a = await card(ACCOUNT_A);
    expect(screen.getAllByTestId("lic-account").length).toBe(3);
    const statusA = within(a).getByTestId("lic-account-status");
    expect(statusA.textContent).toBe("In use");
    expect(statusA.getAttribute("data-tone")).toBe("good");
    expect(within(a).getByText("In use on the-mac and the-windows-machine.")).toBeTruthy();

    const statusB = within(await card(ACCOUNT_B)).getByTestId("lic-account-status");
    expect(statusB.textContent).toBe("Sign-in expires soon");
    expect(statusB.getAttribute("data-tone")).toBe("warning");

    const statusC = within(await card(ACCOUNT_C)).getByTestId("lic-account-status");
    expect(statusC.textContent).toBe("At limit");
    expect(statusC.getAttribute("data-tone")).toBe("bad");
  });

  it("draws each window as a meter with the Gateway's number, text and reset sentence", async () => {
    render(<LicensesView />);

    const fiveHour = within(await card(ACCOUNT_A)).getByTestId("lic-window-five-hour");
    expect(fiveHour.textContent).toContain("96% - Resets in 1 hour 48 minutes");
    const meter = within(fiveHour).getByRole("meter");
    expect(meter.getAttribute("aria-valuenow")).toBe("96");
    expect(meter.querySelector(".lic-bar-normal")).not.toBeNull();

    const week = within(await card(ACCOUNT_A)).getByTestId("lic-window-seven-day");
    expect(week.textContent).toContain("61% - Resets in 4 days 5 hours");

    // The window the Gateway called at-limit is drawn as at-limit.
    const full = within(await card(ACCOUNT_C)).getByTestId("lic-window-five-hour");
    expect(within(full).getByRole("meter").querySelector(".lic-bar-at-limit")).not.toBeNull();
  });

  it("never works a verdict out of a number: 99% the Gateway calls normal is drawn normal", async () => {
    // The Gateway decides what a percentage means. If the page compared the number with a threshold of its own,
    // this window would turn red and this account would stop saying "Has room".
    const overview = copyOf(OVERVIEW);
    const a = overview.accounts[0];
    a.fiveHour = { ...a.fiveHour, utilization: 99, utilizationText: "99%", level: "normal" };
    a.status = "has-room";
    a.statusText = "Has room";
    a.statusTone = "good";
    getLicenseOverview.mockResolvedValue(overview);
    render(<LicensesView />);

    const drawn = await card(ACCOUNT_A);
    expect(within(drawn).getByTestId("lic-account-status").textContent).toBe("Has room");
    const meter = within(within(drawn).getByTestId("lic-window-five-hour")).getByRole("meter");
    expect(meter.querySelector(".lic-bar-normal")).not.toBeNull();
    expect(meter.querySelector(".lic-bar-at-limit")).toBeNull();
  });

  it("draws a window that was not read hatched, never as a meter at zero", async () => {
    const overview = copyOf(OVERVIEW);
    const a = overview.accounts[0];
    a.fiveHour = { utilization: null, utilizationText: "Not known", level: "not-known", resetsAtUtc: null, resetsText: null };
    // A number from before the window's last reset says nothing about now either.
    a.sevenDay = { ...a.sevenDay, utilization: 61, level: "reset-since" };
    // Nor does a number too old to trust: the Gateway still gives it, with the level that says so.
    const b = overview.accounts[1];
    b.fiveHour = { ...b.fiveHour, utilization: 4, level: "stale" };
    getLicenseOverview.mockResolvedValue(overview);
    render(<LicensesView />);

    const drawn = await card(ACCOUNT_A);
    const old = within(await card(ACCOUNT_B)).getByTestId("lic-window-five-hour");
    expect(within(old).queryByRole("meter")).toBeNull();
    expect(old.querySelector(".lic-bar-unknown")).not.toBeNull();
    expect(old.textContent).toContain("4%");
    for (const id of ["lic-window-five-hour", "lic-window-seven-day"]) {
      const window = within(drawn).getByTestId(id);
      expect(within(window).queryByRole("meter")).toBeNull();
      expect(window.querySelector(".lic-bar-unknown")).not.toBeNull();
      expect(window.querySelector(".lic-bar-fill")).toBeNull();
    }
    expect(within(drawn).getByTestId("lic-window-five-hour").textContent).toContain("Not known");
  });

  it("shows, per machine, when the sign-in it holds for the account expires, in the Gateway's tone", async () => {
    render(<LicensesView />);

    const holdings = within(await card(ACCOUNT_B)).getAllByTestId("lic-holding");
    expect(holdings.length).toBe(2);
    const onMac = holdings.find((row) => row.textContent?.includes("the-mac")) as HTMLElement;
    const onWindows = holdings.find((row) => row.textContent?.includes("the-windows-machine")) as HTMLElement;
    expect(within(onMac).getByText("Sign-in expires in 25 days").className).toContain("lic-tone-good");
    expect(within(onMac).getByText("Spare")).toBeTruthy();
    // The one that is close is flagged with the Gateway's warning tone.
    expect(within(onWindows).getByText("Sign-in expires in 2 days 5 hours").className).toContain("lic-tone-warning");
    expect(onWindows.getAttribute("data-verdict")).toBe("expires soon");

    // Account A is the active sign-in on both machines.
    const active = within(await card(ACCOUNT_A)).getAllByTestId("lic-holding");
    expect(active.every((row) => within(row).queryByText("Active") !== null)).toBe(true);
  });

  it("flags a sign-in whose last renewal failed, and the subscription's renewal date, when the Gateway sends them", async () => {
    const overview = copyOf(OVERVIEW);
    overview.accounts[1].renewsOn = "2026-10-17";
    overview.accounts[1].renewsText = "Renews 17 Oct";
    const spare = overview.machines[0].spares[0];
    spare.renewalFailedAtUtc = "2026-10-11T00:12:00Z";
    spare.renewalFailedText = "The last renewal of this sign-in failed 3 hours ago.";
    spare.renewalFailedTone = "bad";
    getLicenseOverview.mockResolvedValue(overview);
    render(<LicensesView />);

    const b = await card(ACCOUNT_B);
    expect(within(b).getByText("Max - Renews 17 Oct")).toBeTruthy();
    const failed = within(b).getAllByTestId("lic-holding-renewal-failed");
    expect(failed.length).toBe(1);
    expect(failed[0].textContent).toBe("The last renewal of this sign-in failed 3 hours ago.");
    expect(failed[0].className).toContain("lic-tone-bad");
    // Nothing is drawn for a sign-in whose last renewal worked: the Gateway sends null for it.
    expect(within(await card(ACCOUNT_A)).queryByTestId("lic-holding-renewal-failed")).toBeNull();
    // An account with no date set says so, in the Gateway's sentence - never a blank.
    expect(within(await card(ACCOUNT_A)).getByText("Max - Renewal date not set")).toBeTruthy();
  });
});

describe("LicensesView: the schedule", () => {
  it("shows the next switch and, per machine, the account it is on and the Gateway's sentence for what is next", async () => {
    render(<LicensesView />);

    expect((await screen.findByTestId("lic-next-switch")).textContent).toBe(FORECAST.nextSwitchText);
    const rows = within(screen.getByTestId("lic-schedule")).getAllByTestId("lic-schedule-row");
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain("the-mac");
    // The machine is called by the Gateway's label; the page does not build a name out of the user.
    expect(rows[0].textContent).not.toContain("owner");
    expect(rows[0].textContent).toContain("Reporting");
    expect(rows[0].textContent).toContain("Last report 1 minute ago");
    expect(rows[0].textContent).toContain(ACCOUNT_A);
    expect(rows[0].textContent).toContain("Next: account-b@example.com, in about 2 minutes.");
  });

  it("keeps the accounts on screen when the forecast cannot be read, and says so", async () => {
    getLicenseForecast.mockRejectedValue(new Error("The forecast could not be read."));
    render(<LicensesView />);

    expect(await card(ACCOUNT_A)).toBeTruthy();
    expect(await screen.findByText("The forecast could not be read.")).toBeTruthy();
    expect(screen.getByTestId("lic-schedule")).toBeTruthy();
  });
});

describe("LicensesView: the switch log", () => {
  it("lists a failed switch with the Gateway's state, tone, sentence and reason", async () => {
    render(<LicensesView />);

    const rows = within(await screen.findByTestId("lic-log")).getAllByTestId("lic-log-row");
    expect(rows.length).toBe(4);
    const failed = rows.find((row) => row.getAttribute("data-state") === "failed") as HTMLElement;
    const state = within(failed).getByTestId("lic-log-state");
    expect(state.textContent).toBe("Failed");
    expect(state.getAttribute("data-tone")).toBe("bad");
    expect(failed.textContent).toContain("By hand");
    expect(failed.textContent).toContain("the-windows-machine was not switched from account-a@example.com to account-c@example.com.");
    expect(failed.textContent).toContain("The machine holds no spare for the target.");

    const worked = rows.find((row) => row.getAttribute("data-state") === "switched") as HTMLElement;
    expect(worked.textContent).toContain("Threshold reached");
    expect(worked.textContent).toContain("Took 2.0 seconds on the machine (the exchange itself 41 milliseconds)");
    expect(worked.textContent).toContain("The new sign-in answered a usage read.");
    // A switch that was not made says nothing about a usage read.
    expect(failed.textContent).not.toContain("usage read");
  });

  it("still lists a row whose kind and state this build does not know, in the Gateway's words", async () => {
    const log = copyOf(LOG_PAGE);
    log.entries[0] = { ...log.entries[0], kind: "a-later-kind", kindText: "Note", state: "a-later-state", stateText: "a-later-state", summary: "Something a later build wrote." };
    getLicenseLog.mockResolvedValue(log);
    render(<LicensesView />);

    const rows = within(await screen.findByTestId("lic-log")).getAllByTestId("lic-log-row");
    expect(rows[0].textContent).toContain("Note");
    expect(rows[0].textContent).toContain("Something a later build wrote.");
  });

  it("shows the Gateway's sentence when the log is empty", async () => {
    getLicenseLog.mockResolvedValue({ asOfUtc: LOG_PAGE.asOfUtc, isEmpty: true, emptyText: "Nothing has been switched yet.", entries: [], hasMore: false, nextCursor: null });
    render(<LicensesView />);

    expect(await screen.findByText("Nothing has been switched yet.")).toBeTruthy();
    expect(screen.queryByTestId("lic-log")).toBeNull();
  });

  it("reads older rows with the cursor exactly as the Gateway gave it, and stops offering when there are none", async () => {
    render(<LicensesView />);
    const older = await screen.findByRole("button", { name: "Show older" });

    const olderRow: LicenseLogEntry = { ...copyOf(LOG_PAGE.entries[2]), entryId: "an-older-row", summary: "An older switch." };
    getLicenseLog.mockResolvedValueOnce({ ...copyOf(LOG_PAGE), entries: [olderRow], hasMore: false, nextCursor: null });
    fireEvent.click(older);

    await waitFor(() => expect(screen.getAllByTestId("lic-log-row").length).toBe(5));
    expect(getLicenseLog).toHaveBeenLastCalledWith(LOG_PAGE.nextCursor);
    expect(screen.getByText("An older switch.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Show older" })).toBeNull();
  });

  it("takes a new newest page whole until older rows were read, and after that keeps every row already on screen", () => {
    const [first, second, third, fourth] = LOG_PAGE.entries;
    const brandNew: LicenseLogEntry = { ...first, entryId: "brand-new" };
    const moved = { ...copyOf(LOG_PAGE), entries: [brandNew, first, second], nextCursor: "a-newer-cursor" };

    // Nobody read past the newest page: what is on screen is the new page, with its own cursor.
    const fresh = foldNewestLogPage(foldNewestLogPage(null, LOG_PAGE), moved);
    expect(fresh.entries.map((entry) => entry.entryId)).toEqual(["brand-new", first.entryId, second.entryId]);
    expect(fresh.cursor).toBe("a-newer-cursor");

    // Older rows were read: `third` and `fourth` fell off the newest page and stay, and the walk keeps its own cursor.
    const walked = { ...foldNewestLogPage(null, LOG_PAGE), walked: true, cursor: "the-walk's-cursor" };
    const kept = foldNewestLogPage(walked, moved);
    expect(kept.entries.map((entry) => entry.entryId)).toEqual(["brand-new", first.entryId, second.entryId, third.entryId, fourth.entryId]);
    expect(kept.cursor).toBe("the-walk's-cursor");
  });

  it("lays a newer page on top of the rows already read without losing or repeating one", () => {
    const [first, second, third] = LOG_PAGE.entries;
    const brandNew: LicenseLogEntry = { ...first, entryId: "brand-new" };
    // The newest page moved by one row: `third` fell off it, and it must stay on screen.
    const merged = mergeLog([brandNew, first, second], [first, second, third]);
    expect(merged.map((entry) => entry.entryId)).toEqual(["brand-new", first.entryId, second.entryId, third.entryId]);
  });
});

describe("LicensesView: Switch now", () => {
  it("offers the control only where the Gateway offers it, and always shows the Gateway's sentence", async () => {
    render(<LicensesView />);

    expect(within(await card(ACCOUNT_A)).getByRole("button", { name: "Switch now" })).toBeTruthy();
    const b = await card(ACCOUNT_B);
    expect(within(b).queryByRole("button", { name: "Switch now" })).toBeNull();
    expect(within(b).getByText("No machine on account-b@example.com can be switched now: none is on it.")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "Switch now" }).length).toBe(1);
  });

  it("asks first, and sends nothing when the confirmation is cancelled", async () => {
    render(<LicensesView />);
    fireEvent.click(within(await card(ACCOUNT_A)).getByRole("button", { name: "Switch now" }));

    const dialog = screen.getByRole("alertdialog", { name: "Switch account-a@example.com now?" });
    // What it would do, in the Gateway's sentence, and every target with the reason one cannot be chosen.
    expect(within(dialog).getByText("Moves the-mac and the-windows-machine to account-b@example.com.")).toBeTruthy();
    const choosable = within(dialog).getByRole("radio", { name: /account-b@example\.com/ }) as HTMLInputElement;
    const barred = within(dialog).getByRole("radio", { name: /account-c@example\.com/ }) as HTMLInputElement;
    expect(choosable.checked).toBe(true);
    expect(barred.disabled).toBe(true);
    expect(within(dialog).getByText("the-mac holds no usable spare for account-c@example.com.")).toBeTruthy();
    expect(switchLicenseNow).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(switchLicenseNow).not.toHaveBeenCalled();
  });

  it("on confirmation sends the account and the picked target, shows the Gateway's answer, and reads again", async () => {
    switchLicenseNow.mockResolvedValue({
      targetAccountEmail: ACCOUNT_B,
      switches: [
        { switchId: "s1", machineName: "the-mac", osUser: "owner", state: "switched", stateText: "Switched", stateTone: "good", summary: "the-mac switched from account-a@example.com to account-b@example.com." },
      ],
      skipped: [{ machineName: "the-windows-machine", why: "Its last report is too old." }],
    });
    render(<LicensesView />);
    fireEvent.click(within(await card(ACCOUNT_A)).getByRole("button", { name: "Switch now" }));
    const readsBefore = getLicenseOverview.mock.calls.length;

    fireEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Switch now" }));

    const answer = await screen.findByTestId("lic-switch-answer");
    expect(switchLicenseNow).toHaveBeenCalledTimes(1);
    expect(switchLicenseNow).toHaveBeenCalledWith(ACCOUNT_A, ACCOUNT_B);
    expect(answer.textContent).toContain("the-mac switched from account-a@example.com to account-b@example.com.");
    expect(answer.textContent).toContain("the-windows-machine: Its last report is too old.");
    expect(screen.queryByRole("alertdialog")).toBeNull();
    await waitFor(() => expect(getLicenseOverview.mock.calls.length).toBeGreaterThan(readsBefore));
  });

  it("sends the target picked by hand, and stops showing the sentence that describes the Gateway's own choice", async () => {
    const overview = copyOf(OVERVIEW);
    const toC = overview.accounts[0].switchTargets[1];
    toC.canChoose = true;
    toC.unavailableText = null;
    getLicenseOverview.mockResolvedValue(overview);
    switchLicenseNow.mockResolvedValue({ targetAccountEmail: ACCOUNT_C, switches: [], skipped: [] });
    render(<LicensesView />);
    fireEvent.click(within(await card(ACCOUNT_A)).getByRole("button", { name: "Switch now" }));
    const dialog = screen.getByRole("alertdialog");

    fireEvent.click(within(dialog).getByRole("radio", { name: /account-c@example\.com/ }));
    // The sentence names account B, which is no longer where the machines would go.
    expect(within(dialog).queryByText("Moves the-mac and the-windows-machine to account-b@example.com.")).toBeNull();
    fireEvent.click(within(dialog).getByRole("button", { name: "Switch now" }));

    await waitFor(() => expect(switchLicenseNow).toHaveBeenCalledWith(ACCOUNT_A, ACCOUNT_C));
  });

  it("keeps the confirmation open on a refusal and shows the Gateway's own sentence", async () => {
    switchLicenseNow.mockRejectedValue(new Error("account-b@example.com is at its limit until its 5-hour window resets."));
    render(<LicensesView />);
    fireEvent.click(within(await card(ACCOUNT_A)).getByRole("button", { name: "Switch now" }));

    fireEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Switch now" }));

    const dialog = screen.getByRole("alertdialog");
    expect(await within(dialog).findByText("account-b@example.com is at its limit until its 5-hour window resets.")).toBeTruthy();
    expect(screen.queryByTestId("lic-switch-answer")).toBeNull();
  });
});

describe("LicensesView: a read that fails", () => {
  it("says so in a banner, and never shows an empty page as if there were no accounts", async () => {
    getLicenseOverview.mockRejectedValue(new Error("The Gateway took too long to answer."));
    render(<LicensesView />);

    const banner = await screen.findByRole("alert");
    expect(banner.textContent).toContain("The Gateway took too long to answer.");
    expect(screen.queryByTestId("lic-account")).toBeNull();
    expect(screen.queryByText(EMPTY_OVERVIEW.emptyText as string)).toBeNull();
  });
});
