import { useMemo, useState } from "react";
import {
  formatPercent,
  type ThrottleFigure,
  type SystemStat,
  type MachineStat,
} from "@devthrottle/client-core/stats/statsClient";

// The "Systems" tab of Your Throttle (owner ask, 2026-10-15): where the driving actually runs - how the turns
// split across Windows, Linux and Mac, with the machines that make up each system listed under it.
//
// Same figure, same window and same turns as the Agents tab: the Gateway folds the submission ledger by the
// operating system each session's Director reported, and finishes every share and card. This file only lays
// them out. A session whose system is not known is its own "Unknown" row, so the rows add up to the Overview.

type Metric = "turns" | "sessions" | "agents";
const METRIC_LABEL: Record<Metric, string> = { turns: "Turns", sessions: "Sessions", agents: "From agents" };
const METRIC_WORD: Record<Metric, string> = { turns: "turns", sessions: "sessions", agents: "turns agents drove" };

type Row = SystemStat | MachineStat;

function metricValue(r: Row, metric: Metric): number {
  return metric === "turns" ? r.turns : metric === "sessions" ? r.sessions : r.agentDrivenTurns;
}

function metricPercent(r: Row, metric: Metric): number | null {
  return metric === "turns" ? r.turnPercent : metric === "sessions" ? r.sessionPercent : r.agentDrivenPercent;
}

function plural(n: number, word: string): string {
  return `${n.toLocaleString()} ${word}${n === 1 ? "" : "s"}`;
}

function HeadlineCard({ label, value, sub }: { label: string; value: string; sub: string }) {
  return (
    <div className="thr-stat">
      <div className="thr-stat-value">{value}</div>
      <div className="thr-stat-label">{label}</div>
      <div className="thr-stat-label thr-stat-sub">{sub}</div>
    </div>
  );
}

/** The bar: on the turns view it splits into the spoken and typed parts by the Gateway's voice share; on the
 * other views it is one accent. Its length against the longest system row is layout, not a ratio. */
function Bar({ row, metric, max }: { row: Row; metric: Metric; max: number }) {
  const value = metricValue(row, metric);
  const width = max > 0 ? (value / max) * 100 : 0;
  const voice = row.voiceShare ?? 0;
  return (
    <div className="repo-bar-track" title={`${value.toLocaleString()} ${METRIC_WORD[metric]}`}>
      {metric === "turns" && row.turns > 0 ? (
        <>
          <div className="repo-bar-voice" style={{ width: `${width * voice}%` }} />
          <div className="repo-bar-typed" style={{ width: `${width * (1 - voice)}%` }} />
        </>
      ) : (
        <div className="repo-bar-voice" style={{ width: `${width}%` }} />
      )}
    </div>
  );
}

function fromAgents(n: number): string {
  return n > 0 ? ` - ${n.toLocaleString()} from agents` : "";
}

function SystemRow({ rank, system, metric, max }: { rank: number; system: SystemStat; metric: Metric; max: number }) {
  const meta =
    `${plural(system.machines.filter((m) => m.machineName !== "").length, "machine")} - ` +
    `${plural(system.sessions, "session")} - ${formatPercent(system.voicePercent)} voice${fromAgents(system.agentDrivenTurns)}`;
  return (
    <div className="repo-row">
      <div className="repo-rank">{rank}</div>
      <div className="repo-main">
        <div className="repo-top">
          <span className={`sys-dot sys-dot-${system.os || "unknown"}`} aria-hidden="true" />
          <span className="repo-name">{system.osName}</span>
          {system.os === "" && (
            <span className="repo-path">sessions whose Director has not reported its system</span>
          )}
        </div>
        <div className="repo-meta">{meta}</div>
        <Bar row={system} metric={metric} max={max} />
      </div>
      <div className="repo-val">
        <div className="repo-val-num">{metricValue(system, metric).toLocaleString()}</div>
        <div className="repo-val-share">{formatPercent(metricPercent(system, metric))}</div>
      </div>
    </div>
  );
}

function MachineRow({ machine, metric, max }: { machine: MachineStat; metric: Metric; max: number }) {
  const meta = `${plural(machine.sessions, "session")} - ${formatPercent(machine.voicePercent)} voice${fromAgents(machine.agentDrivenTurns)}`;
  return (
    <div className="sys-machine-row">
      <div className="repo-rank" />
      <div className="repo-main">
        <div className="repo-top">
          <span className="sys-machine-name">{machine.displayName}</span>
        </div>
        <div className="repo-meta">{meta}</div>
        <Bar row={machine} metric={metric} max={max} />
      </div>
      <div className="repo-val">
        <div className="sys-machine-num">{metricValue(machine, metric).toLocaleString()}</div>
        <div className="sys-machine-share">{formatPercent(metricPercent(machine, metric))}</div>
      </div>
    </div>
  );
}

export function SystemsTab({ figure }: { figure: ThrottleFigure }) {
  const [metric, setMetric] = useState<Metric>("turns");
  const summary = figure.systemsSummary;

  // Re-sort by the active metric so switching views re-orders honestly; machines re-sort inside their system.
  const ranked = useMemo(
    () =>
      [...figure.systems]
        .sort((a, b) => metricValue(b, metric) - metricValue(a, metric))
        .map((s) => ({ ...s, machines: [...s.machines].sort((a, b) => metricValue(b, metric) - metricValue(a, metric)) })),
    [figure.systems, metric],
  );
  const max = ranked.length > 0 ? metricValue(ranked[0], metric) : 0;

  if (summary === null) {
    return (
      <div className="thr-empty">
        This Gateway does not report operating systems yet. The tab fills in once the Gateway is updated.
      </div>
    );
  }

  if (!summary.hasData) {
    return (
      <div className="thr-empty">
        No turn counted in this window. Drive a session on any of your machines and the split by operating
        system will appear here, ranked.
      </div>
    );
  }

  return (
    <>
      <div className="repos-private" title="This tab is only ever shown to you">
        <span className="repos-lock" aria-hidden="true" />
        Private - only you
      </div>

      <p className="repos-intro">
        Where your driving actually runs: how your turns split across the operating systems your Directors run
        on, with each machine under its system, ranked by {METRIC_WORD[metric]}. {figure.window.label}, the same
        turns as the rings on the Overview tab - desktop typing included.
      </p>

      <div className="thr-stats repos-cards">
        <HeadlineCard
          label="Operating systems"
          value={String(summary.systemCount)}
          sub={`across ${plural(summary.machineCount, "machine")}`}
        />
        <HeadlineCard
          label="Turns counted"
          value={summary.totalTurns.toLocaleString()}
          sub={figure.window.label.toLowerCase()}
        />
        <HeadlineCard
          label="Most used"
          value={formatPercent(summary.topPercent)}
          sub={summary.topSystemName !== null ? `${summary.topSystemName} leads` : "of your turns"}
        />
        <HeadlineCard
          label="Busiest machine"
          value={summary.topMachineTurns !== null ? summary.topMachineTurns.toLocaleString() : "-"}
          sub={
            summary.topMachineName !== null
              ? `${summary.topMachineName}${summary.topMachineOsName !== null ? ` (${summary.topMachineOsName})` : ""}`
              : "no machine recorded"
          }
        />
        {summary.agentDrivenTurns > 0 && (
          <HeadlineCard
            label="Turns agents drove"
            value={summary.agentDrivenTurns.toLocaleString()}
            sub={
              summary.topAgentDrivenSystemName !== null
                ? `${formatPercent(summary.topAgentDrivenPercent)} on ${summary.topAgentDrivenSystemName}`
                : "on no known system"
            }
          />
        )}
      </div>

      <div className="repos-controls">
        <span className="repos-controls-label">Rank by</span>
        <div className="repos-seg" role="tablist" aria-label="Rank operating systems by">
          {(["turns", "sessions", "agents"] as Metric[]).map((m) => (
            <button
              key={m}
              type="button"
              role="tab"
              aria-selected={metric === m}
              className={metric === m ? "repos-seg-btn on" : "repos-seg-btn"}
              onClick={() => setMetric(m)}
            >
              {METRIC_LABEL[m]}
            </button>
          ))}
        </div>
      </div>

      <div className="thr-panel">
        <div className="thr-panel-head">
          <h2>By operating system</h2>
        </div>
        <div className="repo-rows">
          {ranked.map((s, i) => (
            <div key={s.os || "unknown"} className="sys-group">
              <SystemRow rank={i + 1} system={s} metric={metric} max={max} />
              {s.machines.map((m) => (
                <MachineRow key={m.machineName || "unrecorded"} machine={m} metric={metric} max={max} />
              ))}
            </div>
          ))}
        </div>
        {summary.unknownTurns > 0 && (
          <p className="sys-unknown-note">
            {plural(summary.unknownTurns, "turn")} ran in sessions whose system is not known yet. They are shown
            as Unknown, not guessed.
          </p>
        )}
      </div>

      <div className="thr-caveats">
        <h2>What this does and doesn&apos;t count</h2>
        <ul>
          <li>
            The system is the one the session&apos;s Director runs on, as the Director reported it when it
            connected. A machine reports once and its earlier sessions take that answer too, so the split covers
            the whole window, not only the days since the report began.
          </li>
          <li>
            A session on a machine whose Director has never reported its system - a build older than this tab -
            is counted under Unknown rather than dropped. It moves to its system once that machine&apos;s
            Director is updated.
          </li>
          <li>
            On the turns view, each bar splits the turns: the blue part is the share you spoke, the grey part
            the share you typed. Machine bars are drawn on the same scale as the system bars above them.
          </li>
          <li>
            Turns you drove and turns agents drove into other sessions are counted separately and never added
            together. &quot;From agents&quot; is the fleet driving itself, attributed to the machine that
            session ran on.
          </li>
        </ul>
      </div>
    </>
  );
}
