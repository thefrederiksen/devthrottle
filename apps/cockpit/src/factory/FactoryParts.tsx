import { useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import {
  downloadFactoryCsv,
  saveFactoryReport,
  setFactoryPaused,
  type FactoryActivityLine,
  type FactoryAgentRow,
  type FactoryFilters,
  type FactoryNumber,
  type FactoryPause,
  type FactoryQuery,
  type FactoryTone,
  type FactoryWindow,
  type SavedFactoryReport,
} from "@devthrottle/client-core/factory/factoryAgentsClient";
import { Button, ConfirmDialog } from "../components";

// The building blocks the Factory Agents pages share. Rule 7: every word, number and tone arrives finished from the
// Gateway; these components lay it out and nothing else. The one mapping here is tone -> colour class.

export function toneClass(tone: FactoryTone): string {
  return `fa-tone-${tone}`;
}

/** A status or outcome word as a small chip, coloured by the tone the Gateway chose. */
export function ToneChip({ word, tone, title }: { word: string; tone: FactoryTone; title?: string }) {
  return (
    <span className={`fa-chip ${toneClass(tone)}`} title={title}>
      {word}
    </span>
  );
}

/** One number from the Gateway, linked where the Gateway says it opens. */
export function NumberPill({ n }: { n: FactoryNumber }) {
  const body = <span className={`fa-number ${toneClass(n.tone)}`}>{n.text}</span>;
  return n.href ? (
    <Link className="fa-number-link" to={n.href}>
      {body}
    </Link>
  ) : (
    body
  );
}

/** The Activity lines, as the Gateway folded them (empty checks already collapsed). */
export function ActivityTable({ rows, showFactory }: { rows: FactoryActivityLine[]; showFactory: boolean }) {
  return (
    <table className="fa-table" data-testid="fa-activity-table">
      <thead>
        <tr>
          <th style={{ width: "120px" }}>Time</th>
          {showFactory && <th style={{ width: "160px" }}>Factory</th>}
          <th style={{ width: "150px" }}>Factory agent</th>
          <th>What happened</th>
          <th style={{ width: "130px" }}>Outcome</th>
          <th style={{ width: "100px" }}>Session</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <tr key={r.key} className={r.collapsed ? "fa-row-collapsed" : undefined}>
            <td className="mono">{r.time}</td>
            {showFactory && <td>{r.factoryTitle}</td>}
            <td>{r.who}</td>
            <td>
              <div>
                {r.what}
                {r.subject !== null && <span className="fa-subject"> - {r.subject}</span>}
                {r.link !== null && (
                  <>
                    {" "}
                    <a className="fa-link" href={r.link} target="_blank" rel="noopener noreferrer">
                      Open
                    </a>
                  </>
                )}
              </div>
              {r.note !== null && <div className="fa-note">{r.note}</div>}
            </td>
            <td>
              <ToneChip word={r.outcomeWord} tone={r.outcomeTone} />
            </td>
            <td className="mono">
              {r.sessionId !== null && r.sessionLabel !== null ? (
                <Link to={`/session/${encodeURIComponent(r.sessionId)}`}>{r.sessionLabel}</Link>
              ) : (
                "-"
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** The red fault lines ("No checks ran"): shown above everything else, never folded into a quiet view. */
export function Faults({ faults }: { faults: string[] }) {
  if (faults.length === 0) return null;
  return (
    <div className="fa-faults" role="alert" data-testid="fa-faults">
      {faults.map((f) => (
        <div key={f} className="fa-fault">
          {f}
        </div>
      ))}
    </div>
  );
}

/**
 * The filter bar the Activity and Reports tabs share. The choices and the chosen window come from the Gateway; the
 * bar only hands the chosen values back as a query.
 */
export function FilterBar({
  filters,
  window,
  onChange,
}: {
  filters: FactoryFilters;
  window: FactoryWindow;
  onChange: (q: FactoryQuery) => void;
}) {
  const [from, setFrom] = useState(window.fromLocal);
  const [to, setTo] = useState(window.toLocal);
  const base: FactoryQuery = {
    factory: filters.factory ?? "",
    agent: filters.agent ?? "",
    outcome: filters.outcome ?? "",
    window: window.key,
    from: window.key === "custom" ? window.fromLocal : "",
    to: window.key === "custom" ? window.toLocal : "",
  };
  return (
    <div className="fa-filters" data-testid="fa-filters">
      <label>
        Factory
        <select
          value={filters.factory ?? ""}
          onChange={(e) => onChange({ ...base, factory: e.target.value, agent: "" })}
        >
          {filters.factoryChoices.map((c) => (
            <option key={c.value} value={c.value}>
              {c.label}
            </option>
          ))}
        </select>
      </label>
      <label>
        Factory agent
        <select value={filters.agent ?? ""} onChange={(e) => onChange({ ...base, agent: e.target.value })}>
          {filters.agentChoices.map((c) => (
            <option key={c.value} value={c.value}>
              {c.label}
            </option>
          ))}
        </select>
      </label>
      <label>
        Outcome
        <select value={filters.outcome ?? ""} onChange={(e) => onChange({ ...base, outcome: e.target.value })}>
          {filters.outcomeChoices.map((c) => (
            <option key={c.value} value={c.value}>
              {c.label}
            </option>
          ))}
        </select>
      </label>
      <label>
        When
        <select
          value={window.key}
          onChange={(e) =>
            onChange({
              ...base,
              window: e.target.value,
              from: e.target.value === "custom" ? window.fromLocal : "",
              to: e.target.value === "custom" ? window.toLocal : "",
            })
          }
        >
          {window.choices.map((c) => (
            <option key={c.value} value={c.value}>
              {c.label}
            </option>
          ))}
        </select>
      </label>
      {window.key === "custom" && (
        <>
          <label>
            From
            <input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} />
          </label>
          <label>
            To
            <input type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} />
          </label>
          <Button onClick={() => onChange({ ...base, from, to })}>Show</Button>
        </>
      )}
      <span className="fa-window-label" data-testid="fa-window-label">
        {window.label}
      </span>
    </div>
  );
}

/** Export CSV: the Gateway builds the file (every row, never the collapsed lines); this only saves it. */
export function ExportCsvButton({ href }: { href: string }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <span className="fa-inline-action">
      <Button
        disabled={busy}
        onClick={async () => {
          setBusy(true);
          setError(null);
          try {
            await downloadFactoryCsv(href);
          } catch (err) {
            setError(gatewayErrorMessage(err, "export the CSV"));
          } finally {
            setBusy(false);
          }
        }}
      >
        {busy ? "Exporting..." : "Export CSV"}
      </Button>
      {error !== null && <span className="fa-error">{error}</span>}
    </span>
  );
}

/** "Make a report from this": keeps the current filter as a saved report under a name. */
export function SaveReportControl({
  label,
  query,
  onSaved,
}: {
  label: string;
  query: FactoryQuery;
  onSaved: (report: SavedFactoryReport) => void;
}) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!open) return <Button onClick={() => setOpen(true)}>{label}</Button>;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const saved = await saveFactoryReport({
        name,
        factory: query.factory || undefined,
        agent: query.agent || undefined,
        outcome: query.outcome || undefined,
        window: query.window || undefined,
        fromUtc: query.window === "custom" ? query.from : undefined,
        toUtc: query.window === "custom" ? query.to : undefined,
      });
      setOpen(false);
      setName("");
      onSaved(saved);
    } catch (err) {
      setError(gatewayErrorMessage(err, "save the report"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="fa-save-form" onSubmit={submit} data-testid="fa-save-form">
      <input
        type="text"
        placeholder="Report name"
        value={name}
        maxLength={120}
        onChange={(e) => setName(e.target.value)}
        aria-label="Report name"
        autoFocus
      />
      <Button type="submit" variant="primary" disabled={busy}>
        {busy ? "Saving..." : "Save"}
      </Button>
      <Button onClick={() => setOpen(false)} disabled={busy}>
        Cancel
      </Button>
      {error !== null && <span className="fa-error">{error}</span>}
    </form>
  );
}

/** Pause or Resume, exactly as the Gateway offers it; a confirm when the Gateway supplies the question. */
export function PauseButton({
  pause,
  factory,
  agent,
  onDone,
}: {
  pause: FactoryPause;
  factory: string;
  agent?: string;
  onDone: () => void;
}) {
  const [asking, setAsking] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      await setFactoryPaused(pause.action, factory, agent);
      onDone();
    } catch (err) {
      setError(gatewayErrorMessage(err, pause.label.toLowerCase()));
    } finally {
      setBusy(false);
    }
  };

  return (
    <span className="fa-inline-action">
      <Button
        disabled={busy}
        data-testid="fa-pause"
        onClick={() => (pause.confirm !== null ? setAsking(true) : void run())}
      >
        {busy ? pause.busyLabel : pause.label}
      </Button>
      {error !== null && <span className="fa-error">{error}</span>}
      {pause.confirm !== null && (
        <ConfirmDialog
          open={asking}
          title={`${pause.label}?`}
          message={pause.confirm}
          confirmLabel={pause.label}
          busyLabel={pause.busyLabel}
          danger={false}
          onConfirm={async () => {
            await setFactoryPaused(pause.action, factory, agent);
            onDone();
          }}
          onClose={() => setAsking(false)}
        />
      )}
    </span>
  );
}

/** Factory agents as rows: on a factory card, the All factory agents tab and a factory's Agents tab. */
export function AgentTable({ rows, showFactory }: { rows: FactoryAgentRow[]; showFactory: boolean }) {
  return (
    <table className="fa-table">
      <thead>
        <tr>
          <th>Factory agent</th>
          {showFactory && <th>Factory</th>}
          <th>Woken by</th>
          <th>Last run</th>
          <th style={{ width: "110px" }}>Status</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((a) => (
          <tr key={`${a.factoryId}/${a.agentId}`}>
            <td>
              <Link to={a.href}>{a.name}</Link>
            </td>
            {showFactory && <td>{a.factoryTitle}</td>}
            <td>{a.wokenBy}</td>
            <td>{a.lastRun}</td>
            <td>
              <ToneChip word={a.statusWord} tone={a.statusTone} />
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
