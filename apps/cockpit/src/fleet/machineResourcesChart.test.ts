import { describe, expect, it } from "vitest";
import { CHART, chartGeometry, chartSpan } from "./machineResourcesChart";
import { SPAN_FROM, SPAN_TO, history, window } from "./machineCapacityFixtures";

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

  // Review of pull request 4646, finding 1: the span was anchored to the newest window, so a machine silent for ten
  // hours drew a chart that looked current. It is now exactly the span the Gateway read, ending at the Gateway's now.
  it("ChartSpan_IsExactlyTheSpanTheGatewayRead_NotTheNewestWindow", () => {
    const span = chartSpan(history([window(T0, [1, 2], [1, 2]), window(T1, [1, 2], [1, 2])]))!;

    expect(span.startMs).toBe(Date.parse(SPAN_FROM));
    expect(span.endMs).toBe(Date.parse(SPAN_TO));
  });

  it("ChartSpan_WithoutTheGatewaysSpan_IsNull_NeverAnInventedClock", () => {
    expect(chartSpan(history([window(T0, [1, 2], [1, 2])], [], { fromUtc: "", toUtc: "" }))).toBeNull();
  });

  it("ChartGeometry_TheNewestWindowSitsWhereTimePutsIt_NotAtTheRightEdge", () => {
    // T1 is two hours before the span's end: its window ends well short of the right edge.
    const g = chartGeometry(history([window(T0, [10, 20], [10, 20]), window(T1, [10, 20], [10, 20])]), "CPU")!;

    const xs = [...g.runs[0].averagePath.matchAll(/[ML](-?[\d.]+),/g)].map((m) => Number(m[1]));
    expect(Math.max(...xs)).toBeLessThan(CHART.right - 50);
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

    expect(g.stripes).toEqual([
      expect.objectContaining({ kind: "note", startUtc: T1, label: "Processor not read: the processor counters did not answer", tone: "idle" }),
    ]);
    expect(g.runs).toHaveLength(2);
    // Nothing is drawn at 0%: the line never dips to the axis for the window that was not read.
    for (const run of g.runs) expect(Math.max(...ys(run.averagePath))).toBeLessThan(CHART.bottom - 1);
  });

  it("ChartGeometry_AverageWithoutPeak_IsAStripe_NeverHalfAFigure", () => {
    const note = { label: "Processor not read: the peak was not kept", tone: "idle" as const };
    const halfRead = { ...window(T0, [40, 50], [50, 55]), processorPeakPercent: null, processorNote: note };

    const g = chartGeometry(history([halfRead]), "CPU")!;

    expect(g.runs).toEqual([]);
    expect(g.stripes).toEqual([expect.objectContaining({ kind: "note", label: note.label })]);
  });

  // Second review, finding 3: a window with neither a figure nor a note is never worded by the client.
  it("ChartGeometry_AWindowWithNoFigureAndNoNote_Throws_NeverWordsItself", () => {
    const broken = { ...window(T0, [40, 50], [50, 55]), processorPeakPercent: null };

    expect(() => chartGeometry(history([broken]), "CPU")).toThrow(/no CPU figure and no note/);
  });

  // Finding 3: the words and the tone of a refused window are the Gateway's note, drawn as sent.
  it("ChartGeometry_RefusedWindow_IsAStripeWithTheGatewaysNoteAndTone", () => {
    const note = { label: "Figures refused: its processor peak was 400%, above 100", tone: "bad" as const };
    const refused = { ...window(T1, null, null), refusedReason: "its processor peak was 400%, above 100", processorNote: note, memoryNote: note };

    const g = chartGeometry(history([window(T0, [10, 20], [30, 40]), refused]), "Memory")!;

    expect(g.stripes).toEqual([expect.objectContaining({ kind: "note", startUtc: T1, label: note.label, tone: "bad" })]);
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

  // Finding 2: a stretch with no window was blank chart. It is now the Gateway's gap, drawn as a stripe on both charts.
  it("ChartGeometry_StretchWithNoWindow_BreaksTheLine_AndDrawsTheGatewaysGap", () => {
    const gap = { startUtc: T1, endUtc: T3, label: "No resource summary arrived for 10 minutes", tone: "idle" as const };
    const h = history([window(T0, [10, 20], [10, 20]), window(T3, [10, 20], [10, 20])], [], { gaps: [gap] });

    for (const resource of ["CPU", "Memory"] as const) {
      const g = chartGeometry(h, resource)!;
      expect(g.runs).toHaveLength(2);
      expect(g.stripes).toEqual([expect.objectContaining({ kind: "gap", startUtc: T1, endUtc: T3, label: gap.label, tone: "idle" })]);
    }
  });

  it("ChartGeometry_AGapRunningPastTheSpan_IsClippedToIt", () => {
    const gap = { startUtc: "2026-11-06T11:00:00Z", endUtc: "2026-11-06T13:00:00Z", label: "No resource summary has arrived for 1 hour", tone: "idle" as const };

    const g = chartGeometry(history([window(T0, [10, 20], [10, 20])], [], { gaps: [gap] }), "CPU")!;

    const stripe = g.stripes[0];
    expect(stripe.x + stripe.width).toBeCloseTo(CHART.right, 0);
    // The hover still reports the gap's own span.
    expect(stripe.endUtc).toBe(gap.endUtc);
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
