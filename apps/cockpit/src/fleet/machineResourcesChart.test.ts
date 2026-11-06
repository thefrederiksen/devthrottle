import { describe, expect, it } from "vitest";
import { CHART, WINDOW_MS, chartGeometry, chartSpan } from "./machineResourcesChart";
import { history, window } from "./machineCapacityFixtures";

// The 24-hour chart's geometry (Mission: Resource Monitor, section 5k). What these pin is the defect class this
// mission keeps producing: a PLAUSIBLE picture of a fault. An unread figure is never drawn as zero, an impossible figure
// is never clamped to fit, and the line never joins across a stretch with no window.

const T0 = "2026-11-06T10:00:00Z";
const T1 = "2026-11-06T10:05:00Z";
const T2 = "2026-11-06T10:10:00Z";
const T3 = "2026-11-06T10:15:00Z";

/** Every y value in an SVG path. */
function ys(path: string): number[] {
  return [...path.matchAll(/[ML](-?[\d.]+),(-?[\d.]+)/g)].map((m) => Number(m[2]));
}

describe("chartGeometry", () => {
  it("ChartGeometry_NoRows_IsNull", () => {
    expect(chartGeometry(history([]), "CPU")).toBeNull();
  });

  it("ChartSpan_EndsWhereTheNewestWindowEnds_AndCoversTheGatewaysHours", () => {
    const span = chartSpan(history([window(T0, [1, 2], [1, 2]), window(T1, [1, 2], [1, 2])]))!;

    expect(span.endMs).toBe(Date.parse(T1) + WINDOW_MS);
    expect(span.endMs - span.startMs).toBe(24 * 3_600_000);
  });

  it("ChartGeometry_ConsecutiveWindows_DrawOneRunWithAverageAndPeak", () => {
    const g = chartGeometry(history([window(T0, [20, 60], [50, 55]), window(T1, [30, 90], [50, 55])]), "CPU")!;

    expect(g.runs).toHaveLength(1);
    expect(g.runs[0].windows).toBe(2);
    expect(g.stripes).toEqual([]);
    // The band reaches the peak (90%) and comes back down to the average (20%).
    const band = ys(g.runs[0].bandPath);
    const y90 = CHART.bottom - 0.9 * (CHART.bottom - CHART.top);
    const y20 = CHART.bottom - 0.2 * (CHART.bottom - CHART.top);
    expect(Math.min(...band)).toBeCloseTo(y90, 0);
    expect(Math.max(...band)).toBeCloseTo(y20, 0);
  });

  it("ChartGeometry_UnreadWindow_IsAStripe_NeverAZeroOnTheLine", () => {
    const g = chartGeometry(history([window(T0, [40, 50], [50, 55]), window(T1, null, [50, 55]), window(T2, [40, 50], [50, 55])]), "CPU")!;

    expect(g.stripes).toEqual([expect.objectContaining({ kind: "unread", windowStartUtc: T1, reason: null })]);
    expect(g.runs).toHaveLength(2);
    // Nothing is drawn at 0%: the line never dips to the axis for the window that was not read.
    for (const run of g.runs) expect(Math.max(...ys(run.averagePath))).toBeLessThan(CHART.bottom - 1);
  });

  it("ChartGeometry_AverageWithoutPeak_IsAStripe_NeverHalfAFigure", () => {
    const halfRead = { ...window(T0, [40, 50], [50, 55]), processorPeakPercent: null };

    const g = chartGeometry(history([halfRead]), "CPU")!;

    expect(g.runs).toEqual([]);
    expect(g.stripes.map((s) => s.kind)).toEqual(["unread"]);
  });

  it("ChartGeometry_RefusedWindow_IsAStripeCarryingTheGatewaysReason", () => {
    const refused = { ...window(T1, null, null), refusedReason: "its processor peak was 400%, above 100" };

    const g = chartGeometry(history([window(T0, [10, 20], [30, 40]), refused]), "Memory")!;

    expect(g.stripes).toEqual([
      expect.objectContaining({ kind: "refused", windowStartUtc: T1, reason: "its processor peak was 400%, above 100" }),
    ]);
  });

  it("ChartGeometry_FigureAbove100_GrowsTheAxis_NeverClampsTheFigure", () => {
    const g = chartGeometry(history([window(T0, [150, 400], [50, 55])]), "CPU")!;

    expect(g.yMax).toBe(400);
    // The peak sits at the top of the plot: it is drawn at 400, not squeezed to a tidy 100.
    expect(Math.min(...ys(g.runs[0].bandPath))).toBe(CHART.top);
    expect(g.grid.map((l) => l.percent)).toContain(400);
  });

  it("ChartGeometry_FiguresWithinRange_KeepTheAxisAt0To100", () => {
    const g = chartGeometry(history([window(T0, [5, 9], [50, 55])]), "CPU")!;

    expect(g.yMin).toBe(0);
    expect(g.yMax).toBe(100);
  });

  it("ChartGeometry_StretchWithNoWindow_BreaksTheLine", () => {
    const g = chartGeometry(history([window(T0, [10, 20], [10, 20]), window(T3, [10, 20], [10, 20])]), "CPU")!;

    expect(g.runs).toHaveLength(2);
    expect(g.stripes).toEqual([]);
  });

  it("ChartGeometry_Crossings_AreMarkedOnTheirOwnResourceOnly_WithTheGatewaysWords", () => {
    const w = {
      ...window(T0, [90, 99], [50, 55]),
      crossings: [
        { startedUtc: "2026-11-06T10:01:30Z", resource: "CPU", label: "CPU red for 90 seconds, peak 99%", tone: "bad" as const },
        { startedUtc: "2026-11-06T10:02:00Z", resource: "Memory", label: "Memory amber for 60 seconds", tone: "warn" as const },
      ],
    };

    const cpu = chartGeometry(history([w]), "CPU")!;
    const memory = chartGeometry(history([w]), "Memory")!;

    expect(cpu.marks.map((m) => [m.label, m.tone])).toEqual([["CPU red for 90 seconds, peak 99%", "bad"]]);
    expect(memory.marks.map((m) => [m.label, m.tone])).toEqual([["Memory amber for 60 seconds", "warn"]]);
  });
});
