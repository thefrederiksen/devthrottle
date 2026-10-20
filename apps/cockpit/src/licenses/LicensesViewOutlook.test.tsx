// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, within } from "@testing-library/react";

// THE NEXT SEVEN DAYS ARE THE GATEWAY'S (CLAUDE.md rule 7). FORECAST_GAP is the Gateway's own answer for a picture in
// which the account in use runs out while no other account has room (cockpit-api.md, section 3). What these pin:
//   - the alert is drawn only when the Gateway says isAlert, in its words; otherwise its calm line;
//   - the three tiles show the Gateway's value, clock time and detail;
//   - every span is drawn with the kind the Gateway sent, in its order, placed linearly on the axis it sent - a kind
//     is never worked out again, and one this build does not know is drawn as not known;
//   - each gap and renewal mark is drawn with the Gateway's sentence;
//   - each card's Forecast box shows the Gateway's rows in their tones, and the weekly bar's projected part is drawn
//     only where the Gateway sent one.
const getLicenseAlarms = vi.fn();
const getLicenseOverview = vi.fn();
const getLicenseForecast = vi.fn();
const getLicenseLog = vi.fn();
const getLicenseApprovers = vi.fn();
vi.mock("@devthrottle/client-core/licenses/licensesClient", () => ({
  getLicenseAlarms: (...args: unknown[]) => getLicenseAlarms(...args),
  getLicenseOverview: (...args: unknown[]) => getLicenseOverview(...args),
  getLicenseForecast: (...args: unknown[]) => getLicenseForecast(...args),
  getLicenseLog: (...args: unknown[]) => getLicenseLog(...args),
  getLicenseApprovers: (...args: unknown[]) => getLicenseApprovers(...args),
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  describeAndReport: (_surface: string, _action: string, err: unknown) => (err instanceof Error ? err.message : String(err)),
  backgroundRecovered: () => undefined,
}));

import { LicensesView } from "./LicensesView";
import { localMidnights } from "./LicenseOutlook";
import { APPROVERS_EMPTY, FORECAST, FORECAST_GAP, LOG_PAGE, OVERVIEW, copyOf } from "./fixtures";

const ACCOUNT_A = "account-a@example.com";
const ACCOUNT_B = "account-b@example.com";

beforeEach(() => {
  getLicenseAlarms.mockResolvedValue({ count: 0, summary: null, linkText: null, alarms: [] });
  getLicenseOverview.mockResolvedValue(copyOf(OVERVIEW));
  getLicenseForecast.mockResolvedValue(copyOf(FORECAST_GAP));
  getLicenseLog.mockResolvedValue(copyOf(LOG_PAGE));
  getLicenseApprovers.mockResolvedValue(copyOf(APPROVERS_EMPTY));
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

async function card(accountEmail: string): Promise<HTMLElement> {
  return await screen.findByRole("region", { name: accountEmail });
}

describe("LicensesView: the alert and the tiles", () => {
  it("shows the Gateway's alert, word for word, when it says there is a gap", async () => {
    render(<LicensesView />);

    const alert = await screen.findByTestId("lic-outlook-alert");
    expect(alert.getAttribute("role")).toBe("alert");
    expect(alert.textContent).toBe(`${FORECAST_GAP.outlook.headline.text}${FORECAST_GAP.outlook.headline.detail}`);
    expect(alert.className).toContain("lic-tone-bad");
    expect(screen.queryByTestId("lic-outlook-headline")).toBeNull();
  });

  it("shows only the calm line when the Gateway says there is no gap", async () => {
    getLicenseForecast.mockResolvedValue(copyOf(FORECAST));
    render(<LicensesView />);

    expect((await screen.findByTestId("lic-outlook-headline")).textContent).toBe(FORECAST.outlook.headline.text);
    expect(screen.queryByTestId("lic-outlook-alert")).toBeNull();
  });

  it("draws an alert from the flag alone: a calm sentence the Gateway marks as an alert is an alert", async () => {
    const forecast = copyOf(FORECAST);
    forecast.outlook.headline.isAlert = true;
    getLicenseForecast.mockResolvedValue(forecast);
    render(<LicensesView />);

    expect((await screen.findByTestId("lic-outlook-alert")).textContent).toBe(FORECAST.outlook.headline.text);
  });

  it("shows each tile's label, value, clock time and detail as the Gateway sent them", async () => {
    render(<LicensesView />);

    const runsOut = await screen.findByTestId("lic-tile-runs-out");
    const tile = FORECAST_GAP.outlook.runsOut;
    expect(within(runsOut).getByText(tile.label)).toBeTruthy();
    expect(within(runsOut).getByText(tile.valueText).className).toContain(`lic-tone-${tile.tone}`);
    expect(within(runsOut).getByText(tile.detailText)).toBeTruthy();
    expect(runsOut.querySelector("time")?.getAttribute("dateTime")).toBe(tile.atUtc);

    const renewal = screen.getByTestId("lic-tile-next-renewal");
    expect(within(renewal).getByText(FORECAST_GAP.outlook.nextRenewal.valueText)).toBeTruthy();
    // A tile with no moment shows no clock time.
    expect(renewal.querySelector("time")).toBeNull();
  });
});

describe("LicensesView: the seven-day timeline", () => {
  it("draws every account's spans with the Gateway's kinds, in its order", async () => {
    render(<LicensesView />);

    const rows = await screen.findAllByTestId("lic-tl-row");
    expect(rows.map((row) => row.getAttribute("data-account"))).toEqual(FORECAST_GAP.outlook.rows.map((row) => row.accountEmail));
    rows.forEach((row, index) => {
      const kinds = within(row).getAllByTestId("lic-tl-span").map((span) => span.getAttribute("data-kind"));
      expect(kinds).toEqual(FORECAST_GAP.outlook.rows[index].spans.map((span) => span.kind));
    });
    // The forecast span is striped, the at-limit one red, the unknown one grey: one class per kind.
    const a = rows[0].querySelectorAll("rect");
    expect([...a].map((rect) => rect.getAttribute("class"))).toEqual(["lic-tl-room", "lic-tl-forecast", "lic-tl-at-limit", "lic-tl-room"]);
  });

  it("places a span linearly between the axis start and end the Gateway sent", async () => {
    render(<LicensesView />);

    // B is at its limit from now until its week resets: the bar starts at the chart's left edge and ends at the
    // share of the axis that reset is.
    const row = (await screen.findAllByTestId("lic-tl-row"))[1];
    const rect = row.querySelector("rect") as SVGRectElement;
    const outlook = FORECAST_GAP.outlook;
    const span = outlook.rows[1].spans[0];
    const share = (Date.parse(span.endUtc) - Date.parse(outlook.axisStartUtc)) / (Date.parse(outlook.axisEndUtc) - Date.parse(outlook.axisStartUtc));
    expect(Number(rect.getAttribute("x"))).toBe(180);
    expect(Number(rect.getAttribute("width"))).toBeCloseTo(share * 630, 6);
  });

  it("draws a kind as sent and never works one out: a span the Gateway calls room is green whatever else is true", async () => {
    const forecast = copyOf(FORECAST_GAP);
    forecast.outlook.rows[1].spans[0].kind = "room";
    forecast.outlook.rows[2].spans[0].kind = "a-kind-from-a-later-build" as never;
    getLicenseForecast.mockResolvedValue(forecast);
    render(<LicensesView />);

    const rows = await screen.findAllByTestId("lic-tl-row");
    expect(rows[1].querySelector("rect")?.getAttribute("class")).toBe("lic-tl-room");
    expect(rows[2].querySelector("rect")?.getAttribute("class")).toBe("lic-tl-unknown");
  });

  it("draws each gap and each renewal mark with the Gateway's sentence, and the legend", async () => {
    render(<LicensesView />);

    const gaps = await screen.findAllByTestId("lic-tl-gap");
    expect(gaps.length).toBe(FORECAST_GAP.outlook.gaps.length);
    expect(gaps[0].textContent).toContain(FORECAST_GAP.outlook.gaps[0].text);
    const marks = within(screen.getAllByTestId("lic-tl-row")[1]).getAllByTestId("lic-tl-mark");
    expect(marks.map((mark) => mark.textContent)).toEqual(FORECAST_GAP.outlook.rows[1].marks.map((mark) => mark.text));
    expect(screen.getByTestId("lic-outlook-legend").textContent).toBe(FORECAST_GAP.outlook.legendText);
  });

  it("draws a day line at every midnight in the viewer's time zone inside the axis", async () => {
    render(<LicensesView />);

    const days = await screen.findAllByTestId("lic-tl-day");
    expect(days.length).toBe(
      localMidnights(new Date(FORECAST_GAP.outlook.axisStartUtc), new Date(FORECAST_GAP.outlook.axisEndUtc)).length,
    );
    expect(days.length).toBe(7);
  });
});

describe("LicensesView: a card's Forecast and Subscription boxes", () => {
  it("shows the Gateway's forecast rows in their tones, and its expected sentence", async () => {
    render(<LicensesView />);

    const box = within(await card(ACCOUNT_A)).getByTestId("lic-account-forecast");
    const sent = FORECAST_GAP.accounts.find((account) => account.accountEmail === ACCOUNT_A)!;
    const rows = within(box).getAllByTestId("lic-forecast-row");
    expect(rows.map((row) => row.firstChild?.textContent)).toEqual(sent.rows.map((row) => row.label));
    rows.forEach((row, index) => {
      const value = row.lastElementChild as HTMLElement;
      expect(value.textContent?.startsWith(sent.rows[index].text)).toBe(true);
      expect(value.className).toContain(`lic-tone-${sent.rows[index].tone}`);
    });
    expect(within(box).getByTestId("lic-account-expected").textContent).toBe(sent.expectedText);
  });

  it("draws the weekly bar's projected part only where the Gateway sent one", async () => {
    render(<LicensesView />);

    const projected = within(within(await card(ACCOUNT_A)).getByTestId("lic-window-seven-day")).getByTestId("lic-bar-projected");
    expect(projected.style.width).toBe("97%");
    expect(within(await card(ACCOUNT_B)).queryByTestId("lic-bar-projected")).toBeNull();
  });

  it("shows the plan and the renewal sentence in the Subscription box, with Set renewal date", async () => {
    render(<LicensesView />);

    const box = within(await card(ACCOUNT_B)).getByTestId("lic-account-subscription");
    const account = OVERVIEW.accounts.find((a) => a.accountEmail === ACCOUNT_B)!;
    expect(within(box).getByTestId("lic-account-plan").textContent).toBe(account.planText);
    expect(within(box).getByTestId("lic-account-renewal").textContent).toBe(account.renewsText);
    expect(within(box).getByRole("button", { name: "Set renewal date" })).toBeTruthy();
  });
});
