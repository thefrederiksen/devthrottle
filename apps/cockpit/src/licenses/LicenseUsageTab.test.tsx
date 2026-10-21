// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";

// WHAT IS USING IT: the second tab of the Licenses page. The Gateway's answers are faked with invented sessions and
// accounts. What these pin:
//   - every number is the Gateway's, and one it could not measure is "not measured", never a zero;
//   - the verdict and the suggestion are the planner's own sentences;
//   - ticking a session or a top-level session ASKS the planner again with those ids in `stop`, and shows its
//     what-if verdict: the tab never subtracts a rate itself;
//   - "Suggest what to stop" ticks exactly the sessions the planner suggested; picking a target asks for it;
//   - the price is saved through the price route.
const getLicenseUsage = vi.fn();
const getLicensePlan = vi.fn();
const setLicenseMonthlyPrice = vi.fn();
vi.mock("@devthrottle/client-core/licenses/licenseUsageClient", () => ({
  getLicenseUsage: (...args: unknown[]) => getLicenseUsage(...args),
  getLicensePlan: (...args: unknown[]) => getLicensePlan(...args),
  setLicenseMonthlyPrice: (...args: unknown[]) => setLicenseMonthlyPrice(...args),
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  describeAndReport: (_surface: string, _action: string, err: unknown) => (err instanceof Error ? err.message : String(err)),
  backgroundRecovered: () => undefined,
}));

import { LicenseUsageTab, pct } from "./LicenseUsageTab";

const A = "account-a@example.com";

function session(id: string, number: number, name: string, state: "working" | "waiting", rateNow: number | null, top = id) {
  return {
    sessionId: id, number, name, missionId: null, missionName: "Build the thing", topLevelSessionId: top, isLive: true,
    state, stateText: state === "working" ? "Working" : "Waiting", machineName: "machine-one", model: "opus",
    agentKind: "claude", rateNowPercentPerHour: rateNow, last4HoursPercent: 0.8, soFarPercent: rateNow === null ? null : 1.25,
    runningHours: 2.5, averagePercentPerHour: 0.4, dollarsSoFar: rateNow === null ? null : 0.58, weightedLastHour: 1,
    weightedLast4Hours: 1, weightedSoFar: 1, startedAtUtc: "2026-10-20T10:00:00Z", lastSeenAtUtc: "2026-10-20T12:30:00Z",
  };
}

const USAGE = {
  asOfUtc: "2026-10-20T12:30:00Z",
  accountEmail: A,
  accountInUse: true,
  accounts: [{ accountEmail: A, inUse: true, label: `${A} (in use)` }],
  emptyText: null,
  calibration: { isMeasured: true, percentPerMillionWeighted: 1, fromUtc: null, toUtc: null, risePercent: 2, weighted: 2e6,
    text: "Measured between this week's first kept reading and its latest, 5 hours apart." },
  unitText: ["One unit for everything: percent of the weekly limit."],
  weekUsedPercent: 80,
  weekResetsAtUtc: "2026-10-21T12:00:00Z",
  burnPercentPerHour: 2.5,
  burnText: "All sessions together are using about 2.5% of the weekly limit an hour.",
  monthlyPriceUsd: 200,
  dollarsPerPercent: 0.46,
  sessions: [
    session("lead", 101, "Lead", "working", 1.5),
    session("dev", 102, "Developer one", "waiting", 1.0, "lead"),
    session("quiet", 103, "Quiet one", "waiting", null),
  ],
  topLevel: [
    { id: "lead", number: 101, label: "#101 Lead", sessionIds: ["lead", "dev"], liveSessionIds: ["lead", "dev"],
      rateNowPercentPerHour: 2.5, last4HoursPercent: 1.6, soFarPercent: 2.5, dollarsSoFar: 1.16 },
    { id: "quiet", number: 103, label: "#103 Quiet one", sessionIds: ["quiet"], liveSessionIds: ["quiet"],
      rateNowPercentPerHour: null, last4HoursPercent: null, soFarPercent: null, dollarsSoFar: null },
  ],
  missions: [],
};

const PLAN = {
  asOfUtc: "2026-10-20T12:30:00Z",
  accountEmail: A,
  accountInUse: true,
  emptyText: null,
  targets: [
    { id: "room:account-b@example.com", label: "account-b@example.com has room again", atUtc: "2026-10-20T18:00:00Z" },
    { id: "reset", label: "this account's own weekly reset", atUtc: "2026-10-21T12:00:00Z" },
  ],
  targetId: "room:account-b@example.com",
  targetAtUtc: "2026-10-20T18:00:00Z",
  targetText: "until account-b@example.com has room again",
  weekUsedPercent: 80,
  leftPercent: 17,
  hoursToGo: 5.5,
  allowedPercentPerHour: 3.09,
  burnPercentPerHour: 3,
  burnText: "3% an hour over the last 6 hours.",
  shortfallPercentPerHour: 0,
  runsOutAtUtc: null,
  lasts: false,
  verdict: "At 3% an hour it reaches 97% in about 6 hours, 1 hour too early.",
  verdictTone: "bad",
  suggestion: { sessionIds: ["dev"], savesPercentPerHour: 1, text: "Stop 1 session (1 waiting, 0 working) to save about 1% an hour." },
  choices: [{ topLevelSessionId: "lead", number: 101, label: "#101 Lead", sessionIds: ["lead", "dev"], rateNowPercentPerHour: 2.5, working: 1, waiting: 1 }],
  sessions: [],
  whatIf: null,
};

function whatIf(stopped: string[], verdict: string) {
  return { ...PLAN, whatIf: { stoppedSessionIds: stopped, notFound: [], savesPercentPerHour: 1, burnPercentPerHour: 2,
    runsOutAtUtc: null, lasts: true, verdict, verdictTone: "good" } };
}

beforeEach(() => {
  getLicenseUsage.mockResolvedValue(structuredClone(USAGE));
  getLicensePlan.mockImplementation(async (question: { stop?: string[] }) =>
    question.stop && question.stop.length > 0 ? whatIf(question.stop, `Stopping ${question.stop.join("+")} lasts.`) : structuredClone(PLAN));
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function lastPlanQuestion(): { account?: string | null; until?: string | null; stop?: string[] } {
  return getLicensePlan.mock.calls[getLicensePlan.mock.calls.length - 1][0];
}

describe("LicenseUsageTab", () => {
  it("lists each session in percent of the weekly limit, and says 'not measured' rather than zero", async () => {
    render(<LicenseUsageTab />);

    const table = await screen.findByTestId("lic-usage-sessions");
    const lead = within(table).getByText("Lead").closest("tr") as HTMLElement;
    expect(lead.textContent).toContain("1.50%/h");
    expect(lead.textContent).toContain("1.25% - $0.58");
    const quiet = within(table).getByText("Quiet one").closest("tr") as HTMLElement;
    expect(quiet.textContent).toContain("not measured");
    expect(quiet.textContent).not.toContain("0.000%/h");
    expect(screen.getByTestId("lic-usage-calibration").textContent).toBe(USAGE.calibration.text);
  });

  it("shows the planner's verdict and suggestion word for word, in its tone", async () => {
    render(<LicenseUsageTab />);

    const verdict = await screen.findByTestId("lic-usage-verdict");
    expect(verdict.textContent).toBe(PLAN.verdict);
    expect(verdict.className).toContain("lic-tone-bad");
    expect(screen.getByTestId("lic-usage-suggestion").textContent).toBe(PLAN.suggestion.text);
  });

  it("asks the planner again when a session is ticked, and shows its what-if verdict", async () => {
    render(<LicenseUsageTab />);
    const table = await screen.findByTestId("lic-usage-sessions");

    fireEvent.click(within(table).getByLabelText("What if Developer one stopped"));

    await waitFor(() => expect(screen.getByTestId("lic-usage-verdict").textContent).toBe("Stopping dev lasts."));
    expect(lastPlanQuestion().stop).toEqual(["dev"]);
    expect(screen.getByTestId("lic-usage-verdict").className).toContain("lic-tone-good");
  });

  it("unticking a top-level session asks about every live session under it", async () => {
    render(<LicenseUsageTab />);
    const groups = await screen.findByTestId("lic-usage-groups");

    fireEvent.click(within(groups).getByRole("checkbox"));

    await waitFor(() => expect(lastPlanQuestion().stop).toEqual(["dev", "lead"]));
  });

  it("Suggest what to stop ticks exactly the sessions the planner suggested, and Clear unticks them", async () => {
    render(<LicenseUsageTab />);
    await screen.findByTestId("lic-usage-verdict");

    fireEvent.click(screen.getByRole("button", { name: "Suggest what to stop" }));
    await waitFor(() => expect(lastPlanQuestion().stop).toEqual(["dev"]));
    const table = screen.getByTestId("lic-usage-sessions");
    expect((within(table).getByLabelText("What if Developer one stopped") as HTMLInputElement).checked).toBe(true);
    expect((within(table).getByLabelText("What if Lead stopped") as HTMLInputElement).checked).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Clear" }));
    await waitFor(() => expect(lastPlanQuestion().stop).toEqual([]));
  });

  it("picking a target asks the planner for it", async () => {
    render(<LicenseUsageTab />);
    await screen.findByTestId("lic-usage-verdict");

    fireEvent.click(screen.getByText(PLAN.targets[1].label).closest("label")!.querySelector("input")!);

    await waitFor(() => expect(lastPlanQuestion().until).toBe("reset"));
  });

  it("a picked time is asked as an ISO time", async () => {
    render(<LicenseUsageTab />);
    await screen.findByTestId("lic-usage-verdict");

    fireEvent.change(screen.getByTestId("lic-usage-custom-time"), { target: { value: "2026-10-21T08:00" } });

    await waitFor(() => expect(lastPlanQuestion().until).toBe(new Date("2026-10-21T08:00").toISOString()));
  });

  it("saves the plan price through the price route, and reads the usage again", async () => {
    setLicenseMonthlyPrice.mockResolvedValue({ accountEmail: A, monthlyPriceUsd: 100 });
    render(<LicenseUsageTab />);
    const input = await screen.findByTestId("lic-usage-price");
    const readsBefore = getLicenseUsage.mock.calls.length;

    fireEvent.change(input, { target: { value: "100" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(setLicenseMonthlyPrice).toHaveBeenCalledWith(A, 100));
    await waitFor(() => expect(getLicenseUsage.mock.calls.length).toBeGreaterThan(readsBefore));
  });

  it("shows the Gateway's sentence when no account has reported", async () => {
    getLicenseUsage.mockResolvedValue({ ...structuredClone(USAGE), accountEmail: null, accounts: [], sessions: [], topLevel: [],
      emptyText: "No machine has reported a Claude account yet, so there is nothing to measure." });
    render(<LicenseUsageTab />);

    expect(await screen.findByText("No machine has reported a Claude account yet, so there is nothing to measure.")).toBeTruthy();
    expect(screen.queryByTestId("lic-usage-plan")).toBeNull();
  });

  it("formats a percent with more places below a tenth, and null as not measured", () => {
    expect([pct(2.5), pct(0.05), pct(null)]).toEqual(["2.50%", "0.050%", "not measured"]);
  });
});
