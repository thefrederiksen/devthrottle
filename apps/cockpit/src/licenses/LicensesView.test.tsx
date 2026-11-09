// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, waitFor, within, fireEvent } from "@testing-library/react";
import type { LicenseLogEntry, LicenseOverview } from "@devthrottle/client-core/licenses/licensesClient";

// THE LICENSES PAGE ON ONE SCREEN (the owner's redesign, 8 November 2026) SHOWS WHAT THE GATEWAY RULED AND RULES NOTHING
// ITSELF (CLAUDE.md rule 7). The fixtures are the Gateway's own example answers (fixtures.ts). What these pin:
//   - an account whose machines never reported shows the Gateway's sentence, and nothing else;
//   - today's shape (every machine covered) is one green card, and nothing on the page is orange or red;
//   - the one not-ready case is exactly one card in the alarm's tone, naming the machine, with one fix button;
//   - a tile, a machine line and a to-do are drawn as the Gateway worded them;
//   - a grey to-do never changes the count beside the bell (the alarms answer is the only thing that draws it);
//   - the switch log is on the History tab; Switch now is on the status card and asks first.
const getLicenseAlarms = vi.fn();
const getLicenseOverview = vi.fn();
const getLicenseForecast = vi.fn();
const getLicenseLog = vi.fn();
const switchLicenseNow = vi.fn();
const getLicenseApprovers = vi.fn();
const addLicenseSpare = vi.fn();
vi.mock("@devthrottle/client-core/licenses/licensesClient", () => ({
  getLicenseAlarms: (...args: unknown[]) => getLicenseAlarms(...args),
  getLicenseOverview: (...args: unknown[]) => getLicenseOverview(...args),
  getLicenseForecast: (...args: unknown[]) => getLicenseForecast(...args),
  getLicenseLog: (...args: unknown[]) => getLicenseLog(...args),
  switchLicenseNow: (...args: unknown[]) => switchLicenseNow(...args),
  getLicenseApprovers: (...args: unknown[]) => getLicenseApprovers(...args),
  addLicenseSpare: (...args: unknown[]) => addLicenseSpare(...args),
}));
// describeAndReport is the one way an error reaches the screen (and the Gateway); here it only has to say the error.
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  describeAndReport: (_surface: string, _action: string, err: unknown) => (err instanceof Error ? err.message : String(err)),
  backgroundRecovered: () => undefined,
}));

import { LicensesView, foldNewestLogPage, mergeLog } from "./LicensesView";
import { LicenseAlarmIndicator, LicenseAlarmsProvider } from "@devthrottle/client-core/licenses/LicenseAlarmIndicator";
import { APPROVERS_EMPTY, EMPTY_OVERVIEW, FORECAST, LOG_PAGE, OVERVIEW, copyOf } from "./fixtures";

/** The page as the Cockpit mounts it: under the shell's one reader of the license alarms, with the rail's triangle. */
function InShell() {
  return (
    <LicenseAlarmsProvider enabled>
      <LicenseAlarmIndicator href="/licenses" />
      <LicensesView />
    </LicenseAlarmsProvider>
  );
}

const ACCOUNT_A = "account-a@example.com";
const ACCOUNT_B = "account-b@example.com";
const ACCOUNT_C = "account-c@example.com";

const LOUD = ".lic-tone-bad, .lic-tone-warning";

beforeEach(() => {
  getLicenseAlarms.mockResolvedValue({ count: 0, summary: null, linkText: null, tone: null, alarms: [] });
  getLicenseOverview.mockResolvedValue(copyOf(OVERVIEW));
  getLicenseForecast.mockResolvedValue(copyOf(FORECAST));
  getLicenseLog.mockResolvedValue(copyOf(LOG_PAGE));
  getLicenseApprovers.mockResolvedValue(copyOf(APPROVERS_EMPTY));
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

/** The overview with the one not-ready case: the-windows-machine at 85% holds no sign-in for its next license. */
function notReady(): LicenseOverview {
  const overview = copyOf(OVERVIEW);
  const windows = overview.machines.find((m) => m.label === "the-windows-machine")!;
  windows.readyForNext = false;
  windows.readyForNextText = "Not signed in";
  windows.readyForNextTone = "bad";
  overview.verdict = {
    state: "not-ready",
    tone: "bad",
    headline: "the-windows-machine has no next license ready.",
    detail: "the-windows-machine is on account-a@example.com, which is at 98% of its weekly limit, and it holds no sign-in for account-b@example.com: it needs account-b@example.com as its next license.",
    actionText: "Set an approver for account-b@example.com on the Licenses page.",
    machine: { machineId: windows.machineId, machineName: windows.machineName, osUser: windows.osUser, label: windows.label },
    fix: { label: "Fix: sign the-windows-machine in", action: "add-spare", accountEmail: ACCOUNT_B, machineId: windows.machineId },
    switchNowAccountEmail: null,
  };
  return overview;
}

async function statusCard(): Promise<HTMLElement> {
  return await screen.findByTestId("lic-status");
}

function openTab(name: string): void {
  fireEvent.click(screen.getByRole("tab", { name }));
}

describe("LicensesView: an account whose machines have never reported", () => {
  it("shows the Gateway's sentence in place of every list", async () => {
    getLicenseOverview.mockResolvedValue(copyOf(EMPTY_OVERVIEW));
    render(<InShell />);

    expect(await screen.findByText(EMPTY_OVERVIEW.emptyText as string)).toBeTruthy();
    expect(screen.queryByTestId("lic-status")).toBeNull();
    expect(screen.queryByTestId("lic-tiles")).toBeNull();
    expect(screen.queryByTestId("lic-machines")).toBeNull();
    expect(screen.queryByRole("button", { name: "Switch now" })).toBeNull();
  });
});

describe("LicensesView: the status card", () => {
  it("is one green card when every machine is covered, and nothing on the page is orange or red", async () => {
    const { container } = render(<InShell />);

    const card = await statusCard();
    expect(screen.getAllByTestId("lic-status").length).toBe(1);
    expect(card.getAttribute("data-state")).toBe("covered");
    expect(card.className).toContain("lic-tone-good");
    expect(within(card).getByTestId("lic-status-headline").textContent).toBe("All 2 machines are covered.");
    expect(within(card).getByTestId("lic-status-detail").textContent).toBe(OVERVIEW.verdict!.detail);
    expect(within(card).queryByTestId("lic-status-fix")).toBeNull();
    expect(container.querySelectorAll(LOUD).length).toBe(0);
  });

  it("is exactly one card in the alarm's tone when a machine is not ready, naming that machine, with its one fix", async () => {
    getLicenseOverview.mockResolvedValue(notReady());
    const { container } = render(<InShell />);

    const card = await statusCard();
    expect(screen.getAllByTestId("lic-status").length).toBe(1);
    expect(card.getAttribute("data-state")).toBe("not-ready");
    expect(card.className).toContain("lic-tone-bad");
    expect(within(card).getByTestId("lic-status-headline").textContent).toBe("the-windows-machine has no next license ready.");
    expect(within(card).getByText("Set an approver for account-b@example.com on the Licenses page.")).toBeTruthy();
    expect(within(card).getByTestId("lic-status-fix").textContent).toBe("Fix: sign the-windows-machine in");
    // Red is only on the card and on the one machine the card names.
    const loud = [...container.querySelectorAll(LOUD)].filter((el) => !card.contains(el) && el !== card);
    expect(loud.length).toBe(1);
    expect(loud[0].textContent).toBe("Not signed in");
  });

  it("opens Add a spare on the named machine with the named license when the fix is pressed, and sends nothing yet", async () => {
    getLicenseOverview.mockResolvedValue(notReady());
    render(<InShell />);

    fireEvent.click(within(await statusCard()).getByTestId("lic-status-fix"));

    const dialog = screen.getByRole("alertdialog", { name: "Add a spare on the-windows-machine?" });
    expect((within(dialog).getByRole("textbox") as HTMLInputElement).value).toBe(ACCOUNT_B);
    expect(addLicenseSpare).not.toHaveBeenCalled();
  });
});

describe("LicensesView: the tiles, the Machines table and the to-dos", () => {
  it("draws one tile per license with the Gateway's role and sentence", async () => {
    render(<InShell />);

    const tiles = within(await screen.findByTestId("lic-tiles")).getAllByTestId("lic-tile");
    expect(tiles.map((t) => t.getAttribute("data-role"))).toEqual(["in-use", "next", "resting"]);
    expect(within(tiles[0]).getByTestId("lic-tile-role").textContent).toBe("In use - 2 machines");
    expect(within(tiles[0]).getByTestId("lic-tile-text").textContent).toContain("Switches at 97% in about 7 hours 49 minutes.");
    // The moment is shown as a clock time in the viewer's own zone, beside the sentence.
    expect(tiles[0].querySelector("time")?.getAttribute("dateTime")).toBe("2026-10-11T11:01:00Z");
    expect(within(tiles[2]).getByTestId("lic-tile-text").textContent).toBe("Not needed now.");
  });

  it("draws one line per machine, by the Gateway's label, ending in its ready-for-next words", async () => {
    render(<InShell />);

    const rows = within(await screen.findByTestId("lic-machines")).getAllByTestId("lic-machine-row");
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain("the-mac");
    expect(rows[0].textContent).toContain(ACCOUNT_A);
    expect(rows[0].textContent).toContain(ACCOUNT_B);
    expect(within(rows[0]).getByTestId("lic-machine-ready").textContent).toBe("Ready");
    expect(within(rows[0]).getByTestId("lic-machine-ready").getAttribute("data-tone")).toBe("good");
  });

  it("lists the grey to-dos, and a to-do never changes the count beside the bell", async () => {
    const overview = copyOf(OVERVIEW);
    overview.todos = [
      { kind: "approver", text: "Sign in to claude.ai once in the 3 approver browsers on the-mac, so new sign-ins can be approved.", mattersByUtc: "2026-10-13T08:24:00Z", opens: "approvers", machineId: null, actionLabel: "Show me how" },
      { kind: "renewal-failed", text: "2 sign-ins on the-windows-machine could not be renewed.", mattersByUtc: "2026-10-14T08:24:00Z", opens: "machine", machineId: overview.machines[1].machineId, actionLabel: "Details" },
    ];
    getLicenseOverview.mockResolvedValue(overview);
    const { container } = render(<InShell />);

    const todos = within(await screen.findByTestId("lic-todos")).getAllByTestId("lic-todo");
    expect(todos.map((t) => t.getAttribute("data-kind"))).toEqual(["approver", "renewal-failed"]);
    expect(todos[0].textContent).toContain("Sign in to claude.ai once in the 3 approver browsers on the-mac");
    expect(container.querySelectorAll(LOUD).length).toBe(0);
    // The triangle is drawn from the alarms answer alone: with no alarm open there is none, whatever the to-dos say.
    await waitFor(() => expect(getLicenseAlarms).toHaveBeenCalled());
    expect(screen.queryByTestId("license-alarm-indicator")).toBeNull();
  });

  it("opens the Approvers & spares tab from an approver to-do, and the machine's drawer from a machine to-do", async () => {
    const overview = copyOf(OVERVIEW);
    overview.todos = [
      { kind: "approver", text: "Approver to-do.", mattersByUtc: null, opens: "approvers", machineId: null, actionLabel: "Show me how" },
      { kind: "not-reporting", text: "Machine to-do.", mattersByUtc: null, opens: "machine", machineId: overview.machines[1].machineId, actionLabel: "Details" },
    ];
    getLicenseOverview.mockResolvedValue(overview);
    render(<InShell />);

    fireEvent.click(within(await screen.findByTestId("lic-todos")).getByRole("button", { name: "Details" }));
    expect(within(screen.getByTestId("lic-drawer")).getByRole("heading", { name: "the-windows-machine" })).toBeTruthy();
    fireEvent.click(within(screen.getByTestId("lic-drawer")).getByRole("button", { name: "Close" }));
    expect(screen.queryByTestId("lic-drawer")).toBeNull();

    fireEvent.click(within(screen.getByTestId("lic-todos")).getByRole("button", { name: "Show me how" }));
    expect(screen.getByRole("tab", { name: "Approvers & spares" }).getAttribute("aria-selected")).toBe("true");
  });
});

describe("LicensesView: the switch log, on the History tab", () => {
  it("lists a failed switch with the Gateway's state, tone, sentence and reason", async () => {
    render(<InShell />);
    await statusCard();
    openTab("History");

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
    expect(failed.textContent).not.toContain("usage read");
  });

  it("still lists a row whose kind and state this build does not know, in the Gateway's words", async () => {
    const log = copyOf(LOG_PAGE);
    log.entries[0] = { ...log.entries[0], kind: "a-later-kind", kindText: "Note", state: "a-later-state", stateText: "a-later-state", summary: "Something a later build wrote." };
    getLicenseLog.mockResolvedValue(log);
    render(<InShell />);
    openTab("History");

    const rows = within(await screen.findByTestId("lic-log")).getAllByTestId("lic-log-row");
    expect(rows[0].textContent).toContain("Note");
    expect(rows[0].textContent).toContain("Something a later build wrote.");
  });

  it("shows the Gateway's sentence when the log is empty", async () => {
    getLicenseLog.mockResolvedValue({ asOfUtc: LOG_PAGE.asOfUtc, isEmpty: true, emptyText: "Nothing has been switched yet.", entries: [], hasMore: false, nextCursor: null });
    render(<InShell />);
    openTab("History");

    expect(await screen.findByText("Nothing has been switched yet.")).toBeTruthy();
    expect(screen.queryByTestId("lic-log")).toBeNull();
  });

  it("reads older rows with the cursor exactly as the Gateway gave it, and stops offering when there are none", async () => {
    render(<InShell />);
    openTab("History");
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

    const fresh = foldNewestLogPage(foldNewestLogPage(null, LOG_PAGE), moved);
    expect(fresh.entries.map((entry) => entry.entryId)).toEqual(["brand-new", first.entryId, second.entryId]);
    expect(fresh.cursor).toBe("a-newer-cursor");

    const walked = { ...foldNewestLogPage(null, LOG_PAGE), walked: true, cursor: "the-walk's-cursor" };
    const kept = foldNewestLogPage(walked, moved);
    expect(kept.entries.map((entry) => entry.entryId)).toEqual(["brand-new", first.entryId, second.entryId, third.entryId, fourth.entryId]);
    expect(kept.cursor).toBe("the-walk's-cursor");
  });

  it("lays a newer page on top of the rows already read without losing or repeating one", () => {
    const [first, second, third] = LOG_PAGE.entries;
    const brandNew: LicenseLogEntry = { ...first, entryId: "brand-new" };
    const merged = mergeLog([brandNew, first, second], [first, second, third]);
    expect(merged.map((entry) => entry.entryId)).toEqual(["brand-new", first.entryId, second.entryId, third.entryId]);
  });
});

describe("LicensesView: Switch now, on the status card", () => {
  it("is offered only where the Gateway names a license to switch", async () => {
    const overview = copyOf(OVERVIEW);
    overview.verdict!.switchNowAccountEmail = null;
    getLicenseOverview.mockResolvedValue(overview);
    render(<InShell />);

    expect(within(await statusCard()).queryByRole("button", { name: "Switch now" })).toBeNull();
  });

  it("asks first, and sends nothing when the confirmation is cancelled", async () => {
    render(<InShell />);
    fireEvent.click(within(await statusCard()).getByRole("button", { name: "Switch now" }));

    const dialog = screen.getByRole("alertdialog", { name: "Switch account-a@example.com now?" });
    expect(within(dialog).getByText("Moves the-mac and the-windows-machine to account-b@example.com.")).toBeTruthy();
    const choosable = within(dialog).getByRole("radio", { name: /account-b@example\.com/ }) as HTMLInputElement;
    const barred = within(dialog).getByRole("radio", { name: /account-c@example\.com/ }) as HTMLInputElement;
    expect(choosable.checked).toBe(true);
    expect(barred.disabled).toBe(true);
    expect(within(dialog).getByText("account-c@example.com has no room: it is at 99% of its 5-hour window.")).toBeTruthy();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(switchLicenseNow).not.toHaveBeenCalled();
  });

  it("on confirmation of the Gateway's own choice sends no target, shows the Gateway's answer, and reads again", async () => {
    switchLicenseNow.mockResolvedValue({
      targetAccountEmail: ACCOUNT_B,
      text: "the-mac is now on account-b@example.com. the-windows-machine is not switched: its last report is too old.",
      switches: [
        { switchId: "s1", entryId: "e1", machineName: "the-mac", osUser: "owner", label: "the-mac", state: "switched", stateText: "Switched", stateTone: "good", summary: "the-mac switched from account-a@example.com to account-b@example.com." },
      ],
      skipped: [{ machineName: "the-windows-machine", osUser: "owner", label: "the-windows-machine", why: "the-windows-machine is not switched: its last report is too old." }],
    });
    render(<InShell />);
    fireEvent.click(within(await statusCard()).getByRole("button", { name: "Switch now" }));
    const readsBefore = getLicenseOverview.mock.calls.length;

    fireEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Switch now" }));

    const answer = await screen.findByTestId("lic-switch-answer");
    expect(switchLicenseNow).toHaveBeenCalledTimes(1);
    expect(switchLicenseNow).toHaveBeenCalledWith(ACCOUNT_A, null);
    expect(answer.textContent).toContain("the-mac is now on account-b@example.com.");
    expect(within(answer).getByTestId("lic-switch-answer-skipped").textContent).toBe("the-windows-machine is not switched: its last report is too old.");
    await waitFor(() => expect(getLicenseOverview.mock.calls.length).toBeGreaterThan(readsBefore));
  });

  it("sends the target picked by hand", async () => {
    const overview = copyOf(OVERVIEW);
    const toC = overview.accounts[0].switchTargets[1];
    toC.canChoose = true;
    toC.unavailableText = null;
    getLicenseOverview.mockResolvedValue(overview);
    switchLicenseNow.mockResolvedValue({ targetAccountEmail: ACCOUNT_C, text: "", switches: [], skipped: [] });
    render(<InShell />);
    fireEvent.click(within(await statusCard()).getByRole("button", { name: "Switch now" }));
    const dialog = screen.getByRole("alertdialog");

    fireEvent.click(within(dialog).getByRole("radio", { name: /account-c@example\.com/ }));
    expect(within(dialog).queryByText("Moves the-mac and the-windows-machine to account-b@example.com.")).toBeNull();
    fireEvent.click(within(dialog).getByRole("button", { name: "Switch now" }));

    await waitFor(() => expect(switchLicenseNow).toHaveBeenCalledWith(ACCOUNT_A, ACCOUNT_C));
  });

  it("keeps the confirmation open on a refusal and shows the Gateway's own sentence", async () => {
    switchLicenseNow.mockRejectedValue(new Error("account-b@example.com is at its limit until its 5-hour window resets."));
    render(<InShell />);
    fireEvent.click(within(await statusCard()).getByRole("button", { name: "Switch now" }));

    fireEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Switch now" }));

    const dialog = screen.getByRole("alertdialog");
    expect(await within(dialog).findByText("account-b@example.com is at its limit until its 5-hour window resets.")).toBeTruthy();
    expect(screen.queryByTestId("lic-switch-answer")).toBeNull();
  });

  it("is offered on the tile of every other license the Gateway says can switch now, and only there", async () => {
    const overview = copyOf(OVERVIEW);
    overview.accounts.find((a) => a.accountEmail === ACCOUNT_B)!.canSwitchNow = true;
    getLicenseOverview.mockResolvedValue(overview);
    render(<InShell />);
    await statusCard();

    const tiles = screen.getAllByTestId("lic-tile");
    const tileOf = (email: string) => tiles.find((t) => within(t).queryByText(email) !== null)!;
    // The card already carries account A's; its tile does not repeat it, and a license that cannot switch has none.
    expect(within(tileOf(ACCOUNT_A)).queryByRole("button", { name: "Switch now" })).toBeNull();
    expect(within(tileOf(ACCOUNT_C)).queryByRole("button", { name: "Switch now" })).toBeNull();

    fireEvent.click(within(tileOf(ACCOUNT_B)).getByRole("button", { name: "Switch now" }));
    expect(screen.getByRole("alertdialog", { name: `Switch ${ACCOUNT_B} now?` })).toBeTruthy();
  });
});

describe("LicensesView: opened in a background tab", () => {
  it("reads and draws the page while the tab is still hidden, never sitting on Loading until it is shown", async () => {
    const hidden = vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");
    try {
      render(<InShell />);

      expect(await statusCard()).toBeTruthy();
      expect(getLicenseOverview).toHaveBeenCalledTimes(1);
      expect(screen.queryByText("Loading...")).toBeNull();
    } finally {
      hidden.mockRestore();
    }
  });
});

describe("LicensesView: a read that fails", () => {
  it("says so in a banner, and never shows an empty page as if there were no accounts", async () => {
    getLicenseOverview.mockRejectedValue(new Error("The Gateway took too long to answer."));
    render(<InShell />);

    const banner = await screen.findByRole("alert");
    expect(banner.textContent).toContain("The Gateway took too long to answer.");
    expect(screen.queryByTestId("lic-status")).toBeNull();
    expect(screen.queryByText(EMPTY_OVERVIEW.emptyText as string)).toBeNull();
  });
});
