import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";

// The license triangle (Web UI Polish, 7 November 2026): what the shared piece does whichever shell mounts it. The
// shells' own tests prove WHERE it sits (the Cockpit's rail head, every phone app bar); these prove how it behaves - the
// Cockpit's hover list opens from the keyboard as well as the pointer, lets the pointer move onto it, closes on Escape,
// stays inside the window, and is drawn in the Gateway's tone; and the provider announces an alarm to a screen reader.

const { getLicenseAlarms } = vi.hoisted(() => ({ getLicenseAlarms: vi.fn() }));
vi.mock("./licensesClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("./licensesClient");
  return { ...actual, getLicenseAlarms };
});
vi.mock("../errors/reportClientError", () => ({ describeAndReport: () => "", backgroundRecovered: () => undefined }));

import { LicenseAlarmIndicatorView, LicenseAlarmsProvider } from "./LicenseAlarmIndicator";
import type { LicenseAlarms } from "./licensesClient";

const FIRST = "Renewing the sign-in for dev@example.com on machine-a failed. Put right what the reason says.";
const SECOND = "machine-b holds no spare. Sign machine-b in to a second account.";

function answer(tone: "warning" | "critical"): LicenseAlarms {
  return {
    asOfUtc: "2026-11-07T08:00:00Z",
    count: 2,
    summary: "2 license problems",
    linkText: "Open Licenses",
    tone,
    alarms: [
      { alarmId: "a1", kind: "not-ready-at-80", machineName: "machine-a", accountEmail: null, text: FIRST, openedAtUtc: "2026-11-07T07:58:00Z", tone },
      { alarmId: "a2", kind: "not-ready-at-80", machineName: "machine-b", accountEmail: null, text: SECOND, openedAtUtc: "2026-11-07T07:58:00Z", tone },
    ],
  } as LicenseAlarms;
}

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.restoreAllMocks();
  getLicenseAlarms.mockReset();
});

describe("the Cockpit's hover list", () => {
  it("Focus_OpensTheListInTheGatewaysWords_AndBlurClosesIt", () => {
    render(<LicenseAlarmIndicatorView alarms={answer("warning")} href="/licenses" details />);
    const triangle = screen.getByTestId("license-alarm-indicator");

    fireEvent.focus(triangle);
    const card = screen.getByTestId("license-alarm-card");
    expect(card.getAttribute("role")).toBe("tooltip");
    expect(triangle.getAttribute("aria-describedby")).toBe(card.id);
    expect(screen.getAllByTestId("license-alarm-card-line").map((line) => line.textContent)).toEqual([FIRST, SECOND]);

    fireEvent.blur(triangle);
    expect(screen.queryByTestId("license-alarm-card")).toBeNull();
    expect(triangle.getAttribute("aria-describedby")).toBeNull();
  });

  it("Escape_ClosesTheList", () => {
    render(<LicenseAlarmIndicatorView alarms={answer("warning")} href="/licenses" details />);
    const triangle = screen.getByTestId("license-alarm-indicator");

    fireEvent.focus(triangle);
    expect(screen.getByTestId("license-alarm-card")).toBeTruthy();
    fireEvent.keyDown(triangle, { key: "Escape" });
    expect(screen.queryByTestId("license-alarm-card")).toBeNull();
  });

  it("ThePointerCanCrossOntoTheList_AndTheListClosesOnceItLeavesBoth", () => {
    vi.useFakeTimers();
    render(<LicenseAlarmIndicatorView alarms={answer("warning")} href="/licenses" details />);
    const triangle = screen.getByTestId("license-alarm-indicator");

    fireEvent.mouseEnter(triangle);
    fireEvent.mouseLeave(triangle);
    fireEvent.mouseEnter(screen.getByTestId("license-alarm-card"));
    act(() => vi.advanceTimersByTime(1000));
    expect(screen.getByTestId("license-alarm-card")).toBeTruthy();

    fireEvent.mouseLeave(screen.getByTestId("license-alarm-card"));
    act(() => vi.advanceTimersByTime(1000));
    expect(screen.queryByTestId("license-alarm-card")).toBeNull();
  });

  it("NearTheRightEdge_TheListMovesLeftToStayInTheWindow", () => {
    vi.spyOn(window, "innerWidth", "get").mockReturnValue(1024);
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      const width = this.getAttribute("data-testid") === "license-alarm-card" ? 420 : 32;
      return { left: 900, right: 900 + width, top: 10, bottom: 42, width, height: 32, x: 900, y: 10, toJSON: () => ({}) } as DOMRect;
    });
    render(<LicenseAlarmIndicatorView alarms={answer("warning")} href="/licenses" details />);

    fireEvent.mouseEnter(screen.getByTestId("license-alarm-indicator"));
    // 1024 wide, less the 8px edge, less the card's 420: its left edge comes to 596, not the triangle's 900.
    expect(screen.getByTestId("license-alarm-card").style.left).toBe("596px");
  });

  it.each([["warning"], ["critical"]] as const)("TheListIsDrawnInTheGatewaysTone: %s", (tone) => {
    render(<LicenseAlarmIndicatorView alarms={answer(tone)} href="/licenses" details />);

    fireEvent.focus(screen.getByTestId("license-alarm-indicator"));
    expect(screen.getByTestId("license-alarm-card").classList.contains(`license-alarm-card--${tone}`)).toBe(true);
  });

  it("WithoutDetails_ThePhoneGetsNoList_ButANativeTooltip", () => {
    render(<LicenseAlarmIndicatorView alarms={answer("warning")} href="/licenses" />);
    const triangle = screen.getByTestId("license-alarm-indicator");

    fireEvent.focus(triangle);
    fireEvent.mouseEnter(triangle);
    expect(screen.queryByTestId("license-alarm-card")).toBeNull();
    expect(triangle.getAttribute("title")).toBe("2 license problems - Open Licenses");
  });
});

describe("the announcement for a screen reader", () => {
  it("ProviderAnnouncesTheGatewaysSummary_InOneAlertLine", async () => {
    getLicenseAlarms.mockResolvedValue(answer("warning"));
    render(
      <LicenseAlarmsProvider enabled>
        <p>page</p>
      </LicenseAlarmsProvider>,
    );

    const line = await screen.findByTestId("license-alarm-announce");
    expect(line.getAttribute("role")).toBe("alert");
    expect(line.textContent).toBe("2 license problems");
    expect(screen.getAllByRole("alert")).toHaveLength(1);
  });

  it("WithNoAlarmOpen_NothingIsAnnounced_AndNothingIsDrawn", async () => {
    getLicenseAlarms.mockResolvedValue({ asOfUtc: "2026-11-07T08:00:00Z", count: 0, summary: null, linkText: null, tone: null, alarms: [] });
    render(
      <LicenseAlarmsProvider enabled>
        <p>page</p>
      </LicenseAlarmsProvider>,
    );

    await waitFor(() => expect(getLicenseAlarms).toHaveBeenCalled());
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByTestId("license-alarm-announce")).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
