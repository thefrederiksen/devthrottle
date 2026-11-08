import { useCallback, useEffect, useRef, useState } from "react";
import {
  getMachineResources,
  type MachineDriveHistory,
  type MachineResourceHistory,
} from "@devthrottle/client-core/fleet/machinesClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { ErrorBanner, LoadingState } from "../components";
import { CHART, chartGeometry, type ChartGeometry, type ChartResource } from "./machineResourcesChart";
import { toneClass } from "./machinesFormat";

// One machine expanded on Fleet -> Machines (Mission: Resource Monitor, section 5k): 24 hours of five-minute rows -
// the average line shaded up to the peak, each amber or red crossing marked - then the cost per registered factory.
// Everything here is the Gateway's GET /machines/{machine}/resources fold, drawn as sent: the crossing words, the
// tones, the factory labels and the empty message are never re-worded or re-derived.

const SURFACE = "cockpit-machine-resources";

const CHART_TITLE: Record<ChartResource, string> = { CPU: "Processor", Memory: "Memory" };

function clock(ms: number): string {
  return new Date(ms).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
}

function stamp(iso: string): string {
  const at = new Date(iso);
  return Number.isNaN(at.getTime())
    ? iso
    : at.toLocaleString(undefined, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
}

/** One resource's 24-hour chart. */
export function ResourceChart({ geometry }: { geometry: ChartGeometry }) {
  const title = CHART_TITLE[geometry.resource];
  return (
    <figure className="fmr-chart" data-testid={`fmr-chart-${geometry.resource}`}>
      <figcaption className="fmr-chart-title">
        {title} <span className="ddim">average, shaded up to the peak</span>
      </figcaption>
      <svg
        className="fmr-svg"
        viewBox={`0 0 ${CHART.width} ${CHART.height}`}
        width="100%"
        role="img"
        aria-label={`${title} use over the last day, average and peak for each five minutes`}
      >
        {geometry.grid.map((g) => (
          <g key={g.percent}>
            <line className="fmr-grid" x1={CHART.left} x2={CHART.right} y1={g.y} y2={g.y} />
            <text className="fmr-axis" x={CHART.left - 4} y={g.y + 3} textAnchor="end">{`${g.percent}%`}</text>
          </g>
        ))}
        {geometry.ticks.map((t, i) => (
          <text
            key={t.atMs}
            className="fmr-axis"
            x={t.x}
            y={CHART.height - 6}
            textAnchor={i === 0 ? "start" : i === geometry.ticks.length - 1 ? "end" : "middle"}
          >
            {clock(t.atMs)}
          </text>
        ))}

        {geometry.stripes.map((s) => (
          <rect
            key={`${s.kind}-${s.startUtc}`}
            data-testid={`fmr-stripe-${s.kind}`}
            x={s.x}
            y={CHART.top}
            // The mark may be wider than the stretch so it stays visible; the title reports the stretch's own span.
            width={Math.max(s.width, 3)}
            height={CHART.bottom - CHART.top}
            className={`fmr-stripe fmr-stripe-${s.kind} ${toneClass(s.tone)}`}
          >
            <title>{`${stamp(s.startUtc)} to ${stamp(s.endUtc)}: ${s.label}`}</title>
          </rect>
        ))}

        {geometry.runs.map((r, i) => (
          <g key={i} data-testid="fmr-run">
            <path className="fmr-band" d={r.bandPath} />
            <path className="fmr-average" d={r.averagePath} />
          </g>
        ))}

        {geometry.marks.map((m, i) => (
          <g key={`${m.startedUtc}-${i}`} className={toneClass(m.tone)} data-testid="fmr-mark">
            <line className="fmr-mark-line" x1={m.x} x2={m.x} y1={CHART.top} y2={CHART.bottom} />
            <path className="fmr-mark-head" d={`M${m.x - 4},${CHART.top - 8} L${m.x + 4},${CHART.top - 8} L${m.x},${CHART.top} Z`} />
            <title>{`${stamp(m.startedUtc)}: ${m.label}`}</title>
          </g>
        ))}
      </svg>
    </figure>
  );
}

const SPARK = { width: 160, height: 28 };

/**
 * A drive's short history: how full it was at its fullest in each window, laid across the Gateway's span so its right
 * edge is the Gateway's now. Layout only - the points are the Gateway's.
 */
function DriveSpark({ drive, fromUtc, toUtc }: { drive: MachineDriveHistory; fromUtc: string; toUtc: string }) {
  const from = Date.parse(fromUtc);
  const to = Date.parse(toUtc);
  const span = to - from;
  const points = drive.points
    .map((p) => ({ at: Date.parse(p.windowStartUtc), used: p.usedPercent }))
    .filter((p) => Number.isFinite(p.at) && span > 0);
  if (points.length === 0) return null;
  const xy = points.map((p) => {
    const x = Math.min(SPARK.width, Math.max(0, ((p.at - from) / span) * SPARK.width));
    const y = SPARK.height - (Math.min(100, Math.max(0, p.used)) / 100) * SPARK.height;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  });
  return (
    <svg
      className="fmr-drive-spark"
      viewBox={`0 0 ${SPARK.width} ${SPARK.height}`}
      width={SPARK.width}
      height={SPARK.height}
      role="img"
      aria-label={`Drive ${drive.name}: how full it was over the last day`}
    >
      <polyline className={`fmr-drive-line ${toneClass(drive.tone)}`} points={xy.join(" ")} fill="none" />
    </svg>
  );
}

/** Each fixed drive's free space, as the Gateway worded it (Disk Space on the Meter, 7 November 2026). Exported for tests. */
export function MachineDrives({ history }: { history: MachineResourceHistory }) {
  return (
    <div className="fmr-drives" data-testid="fmr-drives">
      <div className="fmr-factories-title">Disk space</div>
      {history.noDrivesMessage != null ? (
        <div className="fmr-empty" data-testid="fmr-no-drives">{history.noDrivesMessage}</div>
      ) : (
        <table className="dtbl fmr-drive-table">
          <tbody>
            {history.drives.map((d) => (
              <tr key={d.name} data-testid={`fmr-drive-${d.name}`}>
                <td className="dmono">{d.name}</td>
                <td>{d.headline}</td>
                <td className={toneClass(d.tone)}>{d.levelLabel}</td>
                <td className="ddim">{d.trendLabel ?? ""}</td>
                <td><DriveSpark drive={d} fromUtc={history.fromUtc} toUtc={history.toUtc} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

/** The history and the factory table, once loaded. Exported for tests. */
export function MachineResourcesBody({ history }: { history: MachineResourceHistory }) {
  if (history.emptyMessage != null) {
    return <div className="fmr-empty" data-testid="fmr-empty">{history.emptyMessage}</div>;
  }
  const cpu = chartGeometry(history, "CPU");
  const memory = chartGeometry(history, "Memory");
  if (cpu === null || memory === null) {
    // The Gateway sends an EmptyMessage whenever there are no rows. Rows with no readable times and no message is a
    // broken answer, and an empty chart would read as an idle machine - so say what arrived instead.
    return (
      <div className="dpage-error" data-testid="fmr-broken">
        The Gateway sent this machine's history with no readable five-minute rows and no reason.
      </div>
    );
  }
  // The event list is the Gateway's - what is in it, its order, its words and its tones.
  const events = history.events;

  return (
    <div className="fmr-body">
      <div className="fmr-charts">
        <ResourceChart geometry={cpu} />
        <ResourceChart geometry={memory} />
      </div>

      <MachineDrives history={history} />

      {events.length > 0 && (
        <ul className="fmr-events" data-testid="fmr-events">
          {events.map((e, i) => (
            <li key={`${e.atUtc}-${i}`} className={toneClass(e.tone)}>
              <span className="ddim">{stamp(e.atUtc)}</span> {e.label}
            </li>
          ))}
        </ul>
      )}

      <div className="fmr-factories-title">Cost per factory, last {history.hours} hours</div>
      {history.noFactoriesMessage != null ? (
        <div className="fmr-empty" data-testid="fmr-no-factories">{history.noFactoriesMessage}</div>
      ) : history.factories.length > 0 && (
        <table className="dtbl fmr-factories" data-testid="fmr-factories">
          <thead>
            <tr>
              <th>Factory</th>
              <th className="fm-num">Sessions</th>
              <th>CPU average / peak</th>
              <th>Memory average / peak</th>
            </tr>
          </thead>
          <tbody>
            {history.factories.map((f) => (
              <tr key={f.factory}>
                <td>{f.factory}</td>
                <td className="fm-num">{f.sessions}</td>
                <td className="dmono">{f.processorLabel}</td>
                <td className="dmono">{f.memoryLabel}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

/**
 * Reads GET /machines/{machine}/resources when shown, and draws it. It reads again whenever {@link refreshToken} changes -
 * the Machines table passes its own view, so the expanded chart is re-read on the table's refresh and never sits stale
 * under a live row. The chart already shown stays up while the next read is in flight.
 *
 * A read in flight is never aborted by the next refresh - that refresh is skipped instead - so a Gateway slower than
 * the table's poll still delivers a new chart rather than having every read cut off. Only closing the panel aborts.
 */
export function MachineResourcesPanel({ machine, refreshToken }: { machine: string; refreshToken?: unknown }) {
  const [history, setHistory] = useState<MachineResourceHistory | null>(null);
  const [error, setError] = useState<string | null>(null);
  const inFlight = useRef(false);
  const closed = useRef(new AbortController());

  const load = useCallback(async () => {
    if (inFlight.current) return;
    inFlight.current = true;
    const signal = closed.current.signal;
    try {
      const view = await getMachineResources(machine, signal);
      if (signal.aborted) return;
      setHistory(view);
      setError(null);
    } catch (err) {
      if (signal.aborted) return;
      setError(describeAndReport(SURFACE, "read the machine's resource history", err));
    } finally {
      inFlight.current = false;
    }
  }, [machine]);

  // Abort only when the panel closes.
  useEffect(() => {
    const controller = new AbortController();
    closed.current = controller;
    return () => {
      controller.abort();
      // The aborted read no longer counts as in flight, so a remount (React's development double-mount) reads again.
      inFlight.current = false;
    };
  }, []);

  useEffect(() => {
    void load();
  }, [load, refreshToken]);

  return (
    <div className="fmr-panel" data-testid="fmr-panel">
      {error !== null && <ErrorBanner message={error} onRetry={() => void load()} />}
      {history === null ? error === null && <LoadingState message="Loading the last 24 hours..." /> : <MachineResourcesBody history={history} />}
    </div>
  );
}
