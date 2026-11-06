import { useCallback, useEffect, useState } from "react";
import {
  getMachineResources,
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
            key={`${s.kind}-${s.windowStartUtc}`}
            data-testid={`fmr-stripe-${s.kind}`}
            x={s.x}
            y={CHART.top}
            width={Math.max(s.width, 3)}
            height={CHART.bottom - CHART.top}
            className={`fmr-stripe-${s.kind}`}
          >
            <title>
              {s.kind === "refused"
                ? `${stamp(s.windowStartUtc)}: the Gateway refused this window's figures because ${s.reason}`
                : `${stamp(s.windowStartUtc)}: no ${title.toLowerCase()} figure for this window`}
            </title>
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
  // The crossings and the refused windows as one list, oldest first: the order is layout, every word is the Gateway's.
  const events = [
    ...history.windows.flatMap((w) =>
      w.crossings.map((c) => ({ at: c.startedUtc, tone: c.tone, text: c.label })),
    ),
    ...history.windows
      .filter((w) => w.refusedReason != null)
      .map((w) => ({ at: w.windowStartUtc, tone: "bad", text: `Figures refused: ${w.refusedReason}` })),
  ].sort((a, b) => Date.parse(a.at) - Date.parse(b.at));

  return (
    <div className="fmr-body">
      <div className="fmr-charts">
        <ResourceChart geometry={cpu} />
        <ResourceChart geometry={memory} />
      </div>

      {events.length > 0 && (
        <ul className="fmr-events" data-testid="fmr-events">
          {events.map((e, i) => (
            <li key={`${e.at}-${i}`} className={toneClass(e.tone)}>
              <span className="ddim">{stamp(e.at)}</span> {e.text}
            </li>
          ))}
        </ul>
      )}

      <div className="fmr-factories-title">Cost per factory, last {history.hours} hours</div>
      {history.factories.length === 0 ? (
        <div className="fmr-empty" data-testid="fmr-no-factories">No sessions were recorded on this machine in this span.</div>
      ) : (
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

/** Reads GET /machines/{machine}/resources when shown, and draws it. */
export function MachineResourcesPanel({ machine }: { machine: string }) {
  const [history, setHistory] = useState<MachineResourceHistory | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const view = await getMachineResources(machine, signal);
        if (signal?.aborted) return;
        setHistory(view);
        setError(null);
      } catch (err) {
        if (signal?.aborted) return;
        setError(describeAndReport(SURFACE, "read the machine's resource history", err));
      }
    },
    [machine],
  );

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  return (
    <div className="fmr-panel" data-testid="fmr-panel">
      {error !== null && <ErrorBanner message={error} onRetry={() => void load()} />}
      {history === null ? error === null && <LoadingState message="Loading the last 24 hours..." /> : <MachineResourcesBody history={history} />}
    </div>
  );
}
