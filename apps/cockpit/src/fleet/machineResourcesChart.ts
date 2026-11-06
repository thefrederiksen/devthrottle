import type { MachineResourceHistory, MachineResourceWindow } from "@devthrottle/client-core/fleet/machinesClient";

// The geometry of one machine's 24-hour chart (Mission: Resource Monitor, section 5k). It decides WHERE a figure the
// Gateway sent is drawn and nothing about what it means: every figure, crossing word and tone arrives folded by the
// Gateway's MachineResourceHistoryFold.
//
// The span is the Gateway's too: the chart lays out exactly [fromUtc, toUtc], so its right edge is the Gateway's now
// and a machine that went silent shows the silence. The client never invents a clock.
//
// THE FIVE THINGS THIS FILE MUST NEVER DO, because each one turns a fault into a believable picture:
//  - draw a figure that was not read as zero, or as an empty band - an empty band reads as an idle machine;
//  - clamp a figure to fit the axis - a per-core 400% squeezed to a tidy 100% hides the defect the contract catches.
//    The axis grows to the figure instead;
//  - draw the average without the peak, or the peak without the average - both travel, always, together;
//  - join two windows across a stretch with no window - the line would invent the readings in between;
//  - leave a stretch the Gateway says no summary arrived in as blank chart - blank reads as a quiet machine.

/** The length of one summary window: the five-minute logging contract (mission section 6). */
export const WINDOW_MS = 5 * 60 * 1000;

/** Two windows further apart than this are not neighbours, and the line breaks between them. */
const NEIGHBOUR_MS = WINDOW_MS * 1.5;

export const CHART = { width: 720, height: 150, left: 40, right: 712, top: 14, bottom: 128 } as const;

export type ChartResource = "CPU" | "Memory";

/** One unbroken run of read windows: the average line and the band shaded from it up to the peak. */
export interface ChartRun {
  /** The SVG path of the average line, as steps: each window is flat across its own five minutes. */
  averagePath: string;
  /** The closed SVG path of the band from the average up to the peak. */
  bandPath: string;
  windows: number;
}

/**
 * A stretch drawn as a stripe instead of a figure: a window with no figure for this resource ("note"), or a stretch with
 * no window at all ("gap"). The kind decides only the shape; what it means - the words and the tone - is the Gateway's.
 */
export interface ChartStripe {
  x: number;
  width: number;
  /** The stretch's own span. A hover reports this, never the drawn width. */
  startUtc: string;
  endUtc: string;
  kind: "note" | "gap";
  label: string;
  tone: string;
}

export interface ChartMark {
  x: number;
  startedUtc: string;
  label: string;
  tone: string;
}

export interface ChartTick {
  x: number;
  atMs: number;
}

export interface ChartGridLine {
  y: number;
  percent: number;
}

export interface ChartGeometry {
  resource: ChartResource;
  startMs: number;
  endMs: number;
  /** The top of the axis: 100, or higher when a figure above 100 arrived. Never lower than the highest figure. */
  yMax: number;
  /** The bottom of the axis: 0, or lower when a figure below 0 arrived. */
  yMin: number;
  runs: ChartRun[];
  stripes: ChartStripe[];
  marks: ChartMark[];
  ticks: ChartTick[];
  grid: ChartGridLine[];
}

function figuresOf(w: MachineResourceWindow, resource: ChartResource): [number, number] | null {
  const average = resource === "CPU" ? w.processorAveragePercent : w.memoryAveragePercent;
  const peak = resource === "CPU" ? w.processorPeakPercent : w.memoryPeakPercent;
  // Average and peak travel together: a window holding only one of them cannot be drawn honestly, so it is a stripe.
  if (typeof average !== "number" || typeof peak !== "number") return null;
  if (!Number.isFinite(average) || !Number.isFinite(peak)) return null;
  return [average, peak];
}

const round = (n: number) => Math.round(n * 10) / 10;

/** The span the chart covers: exactly the one the Gateway read. Null when there are no rows or no readable span. */
export function chartSpan(history: MachineResourceHistory): { startMs: number; endMs: number } | null {
  const startMs = Date.parse(history.fromUtc);
  const endMs = Date.parse(history.toUtc);
  if (!Number.isFinite(startMs) || !Number.isFinite(endMs) || endMs <= startMs) return null;
  if (!history.windows.some((w) => Number.isFinite(Date.parse(w.windowStartUtc)))) return null;
  return { startMs, endMs };
}

const iso = (ms: number) => new Date(ms).toISOString();

/** Lay out one resource's chart. Null when there are no rows to draw - the caller shows the Gateway's EmptyMessage. */
export function chartGeometry(history: MachineResourceHistory, resource: ChartResource): ChartGeometry | null {
  const span = chartSpan(history);
  if (span === null) return null;
  const { startMs, endMs } = span;
  const windows = history.windows
    .map((w) => ({ w, at: Date.parse(w.windowStartUtc) }))
    .filter((e) => Number.isFinite(e.at))
    .sort((a, b) => a.at - b.at);

  // The axis grows to the figures - it never squeezes them.
  let yMax = 100;
  let yMin = 0;
  for (const { w } of windows) {
    const f = w.refusedReason == null ? figuresOf(w, resource) : null;
    if (f === null) continue;
    yMax = Math.max(yMax, ...f);
    yMin = Math.min(yMin, ...f);
  }
  if (yMax > 100) yMax = Math.ceil(yMax / 10) * 10;
  if (yMin < 0) yMin = Math.floor(yMin / 10) * 10;

  const x = (ms: number) => round(CHART.left + ((ms - startMs) / (endMs - startMs)) * (CHART.right - CHART.left));
  const y = (percent: number) =>
    round(CHART.bottom - ((percent - yMin) / (yMax - yMin)) * (CHART.bottom - CHART.top));

  const runs: ChartRun[] = [];
  const stripes: ChartStripe[] = [];
  let current: { at: number; average: number; peak: number }[] = [];

  const closeRun = () => {
    if (current.length === 0) return;
    const averagePoints: string[] = [];
    const peakPoints: string[] = [];
    for (const p of current) {
      const x0 = x(p.at);
      const x1 = x(p.at + WINDOW_MS);
      averagePoints.push(`${x0},${y(p.average)}`, `${x1},${y(p.average)}`);
      peakPoints.push(`${x0},${y(p.peak)}`, `${x1},${y(p.peak)}`);
    }
    runs.push({
      averagePath: `M${averagePoints.join(" L")}`,
      bandPath: `M${peakPoints.join(" L")} L${[...averagePoints].reverse().join(" L")} Z`,
      windows: current.length,
    });
    current = [];
  };

  let previousAt: number | null = null;
  for (const { w, at } of windows) {
    if (previousAt !== null && at - previousAt > NEIGHBOUR_MS) closeRun();
    previousAt = at;
    const figures = w.refusedReason == null ? figuresOf(w, resource) : null;
    if (figures === null) {
      closeRun();
      const note = resource === "CPU" ? w.processorNote : w.memoryNote;
      // getMachineResources refuses a history with a window that has neither a figure nor a note, so this is never
      // reached from the wire; a caller that skipped that check is a programming error, not a state to word.
      if (note == null) throw new Error(`chartGeometry: the window at ${w.windowStartUtc} has no ${resource} figure and no note`);
      stripes.push({
        x: x(at),
        width: round(x(at + WINDOW_MS) - x(at)),
        startUtc: w.windowStartUtc,
        endUtc: iso(at + WINDOW_MS),
        kind: "note",
        label: note.label,
        tone: note.tone,
      });
      continue;
    }
    current.push({ at, average: figures[0], peak: figures[1] });
  }
  closeRun();

  // The Gateway's gaps, clipped to the span: the same stretch on both charts, because no summary means neither figure.
  for (const gap of history.gaps) {
    const from = Math.max(Date.parse(gap.startUtc), startMs);
    const to = Math.min(Date.parse(gap.endUtc), endMs);
    if (!Number.isFinite(from) || !Number.isFinite(to) || to <= from) continue;
    stripes.push({
      x: x(from),
      width: round(x(to) - x(from)),
      startUtc: gap.startUtc,
      endUtc: gap.endUtc,
      kind: "gap",
      label: gap.label,
      tone: gap.tone,
    });
  }

  const marks: ChartMark[] = [];
  for (const { w } of windows) {
    for (const c of w.crossings) {
      if (c.resource !== resource) continue;
      const at = Date.parse(c.startedUtc);
      if (!Number.isFinite(at)) continue;
      marks.push({ x: x(at), startedUtc: c.startedUtc, label: c.label, tone: c.tone });
    }
  }

  const ticks: ChartTick[] = [];
  for (let at = endMs; at >= startMs; at -= 6 * 3_600_000) ticks.push({ x: x(at), atMs: at });
  ticks.reverse();

  const gridPercents = [0, 50, 100];
  if (yMax > 100) gridPercents.push(yMax);
  if (yMin < 0) gridPercents.unshift(yMin);
  const grid = gridPercents.map((percent) => ({ y: y(percent), percent }));

  return { resource, startMs, endMs, yMax, yMin, runs, stripes, marks, ticks, grid };
}
