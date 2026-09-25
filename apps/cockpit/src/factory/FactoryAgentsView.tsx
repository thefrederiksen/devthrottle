import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import {
  getFactories,
  getFactoryActivity,
  getFactoryReports,
  type FactoriesView,
  type FactoryActivityView,
  type FactoryCard,
  type FactoryQuery,
  type FactoryReportView,
} from "@devthrottle/client-core/factory/factoryAgentsClient";
import { EmptyState, ErrorBanner, LoadingState, PageHeader } from "../components";
import {
  ActivityTable,
  AgentTable,
  ExportCsvButton,
  Faults,
  FilterBar,
  NumberPill,
  PauseButton,
  SaveReportControl,
  ToneChip,
} from "./FactoryParts";
import "./factory.css";

// The Factory Agents area (Website Business Factory, product track, Screens 1, 3 and 5): four tabs - Factories,
// All factory agents, Activity and Reports. Read-only. Everything shown is the Gateway's fold, rendered verbatim
// (rule 7); the page only lays it out and keeps the chosen tab and filter in the address.

type TabKey = "factories" | "agents" | "activity" | "reports";

const TAB_KEYS: ReadonlyArray<TabKey> = ["factories", "agents", "activity", "reports"];

function queryFrom(params: URLSearchParams): FactoryQuery {
  return {
    factory: params.get("factory") ?? "",
    agent: params.get("agent") ?? "",
    outcome: params.get("outcome") ?? "",
    window: params.get("window") ?? "",
    from: params.get("from") ?? "",
    to: params.get("to") ?? "",
    report: params.get("report") ?? "",
  };
}

/** Load a Gateway view, keeping the last good answer and the error apart. */
function useView<T>(load: (signal: AbortSignal) => Promise<T>, key: string, what: string) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  useEffect(() => {
    const ctrl = new AbortController();
    setError(null);
    load(ctrl.signal).then(
      (d) => setData(d),
      (err: unknown) => {
        if (!ctrl.signal.aborted) setError(gatewayErrorMessage(err, what));
      },
    );
    return () => ctrl.abort();
    // `key` stands for everything `load` reads: the view reloads when the query changes, not on every render.
  }, [key, nonce]);
  const reload = useCallback(() => setNonce((n) => n + 1), []);
  return { data, error, reload };
}

export function FactoryAgentsView() {
  const [params, setParams] = useSearchParams();
  const raw = params.get("tab");
  const tab: TabKey = TAB_KEYS.includes(raw as TabKey) ? (raw as TabKey) : "factories";

  // The frame (title, subtitle, tab labels) is the Factories fold's; it is read whichever tab is open.
  const frame = useView<FactoriesView>((s) => getFactories({}, s), "frame", "load Factory Agents");

  const selectTab = (t: TabKey) => setParams(t === "factories" ? {} : { tab: t });

  return (
    <div className="fa-page" data-testid="factory-agents-page">
      <PageHeader title={frame.data?.title ?? "Factory Agents"} subtitle={frame.data?.subtitle} />
      {frame.data !== null && (
        <div className="fa-tabs" role="tablist" aria-label="Factory Agents view">
          {frame.data.tabs.map((t) => (
            <button
              key={t.key}
              type="button"
              role="tab"
              aria-selected={tab === t.key}
              className={`fa-tab${tab === t.key ? " active" : ""}`}
              onClick={() => selectTab(t.key as TabKey)}
            >
              {t.label}
            </button>
          ))}
        </div>
      )}
      {frame.error !== null && <ErrorBanner message={frame.error} onRetry={frame.reload} />}
      {frame.data === null && frame.error === null && <LoadingState />}
      {frame.data !== null && tab === "factories" && <FactoriesTab view={frame.data} onChanged={frame.reload} />}
      {frame.data !== null && tab === "agents" && <AgentsTab view={frame.data} />}
      {tab === "activity" && <ActivityTab />}
      {tab === "reports" && <ReportsTab />}
    </div>
  );
}

function FactoriesTab({ view, onChanged }: { view: FactoriesView; onChanged: () => void }) {
  if (view.emptyText !== null) return <EmptyState message={view.emptyText} />;
  return (
    <div className="fa-cards">
      <div className="fa-window-note">{view.window.label}</div>
      {view.factories.map((card) => (
        <FactoryCardView key={card.id} card={card} onChanged={onChanged} />
      ))}
    </div>
  );
}

function FactoryCardView({ card, onChanged }: { card: FactoryCard; onChanged: () => void }) {
  return (
    <section className="fa-card" data-testid={`fa-card-${card.id}`}>
      <header className="fa-card-head">
        <div>
          <h2 className="fa-card-title">
            {card.title} <ToneChip word={card.statusWord} tone={card.statusTone} />
          </h2>
          <div className="fa-card-sub">{card.subtitle}</div>
        </div>
        <div className="fa-card-actions">
          <Link className="ui-btn ui-btn-secondary fa-map-link" to={card.mapHref} data-testid={`fa-map-${card.id}`}>
            Map
          </Link>
          <Link className="fa-link" to={card.waitingHref}>
            Waiting for you
          </Link>
          {card.pause !== null && <PauseButton pause={card.pause} factory={card.id} onDone={onChanged} />}
        </div>
      </header>
      {card.faultText !== null && (
        <div className="fa-fault" role="alert">
          {card.faultText}
        </div>
      )}
      {card.numbers.length > 0 && (
        <div className="fa-numbers">
          {card.numbers.map((n) => (
            <NumberPill key={n.text} n={n} />
          ))}
        </div>
      )}
      <AgentTable rows={card.agents} showFactory={false} />
    </section>
  );
}

function AgentsTab({ view }: { view: FactoriesView }) {
  if (view.emptyText !== null) return <EmptyState message={view.emptyText} />;
  return <AgentTable rows={view.allAgents} showFactory />;
}

function ActivityTab() {
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const q = queryFrom(params);
  const key = JSON.stringify(q);
  const view = useView<FactoryActivityView>((s) => getFactoryActivity(q, s), key, "load the activity");
  const apply = (next: FactoryQuery) => setParams(withTab("activity", next));

  if (view.error !== null) return <ErrorBanner message={view.error} onRetry={view.reload} />;
  if (view.data === null) return <LoadingState />;
  const d = view.data;
  return (
    <div className="fa-tab-body">
      <FilterBar key={key} filters={d.filters} window={d.window} onChange={apply} />
      <div className="fa-toolbar">
        <ExportCsvButton href={d.csvHref} />
        <SaveReportControl
          label={d.saveLabel}
          query={queryFromView(d.filters, d.window)}
          onSaved={(r) => navigate(r.href)}
        />
      </div>
      <Faults faults={d.faults} />
      {d.truncatedText !== null && <div className="fa-warn">{d.truncatedText}</div>}
      {d.emptyText !== null ? (
        <EmptyState message={d.emptyText} />
      ) : (
        <ActivityTable rows={d.rows} showFactory={d.filters.factory === null} />
      )}
      <p className="fa-footnote">{d.footnote}</p>
    </div>
  );
}

function ReportsTab() {
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const q = queryFrom(params);
  const key = JSON.stringify(q);
  const view = useView<FactoryReportView>((s) => getFactoryReports(q, s), key, "load the report");
  const apply = (next: FactoryQuery) => setParams(withTab("reports", next));

  if (view.error !== null) return <ErrorBanner message={view.error} onRetry={view.reload} />;
  if (view.data === null) return <LoadingState />;
  const d = view.data;
  return (
    <div className="fa-tab-body">
      {d.openedReportName !== null && <h2 className="fa-section-title">{d.openedReportName}</h2>}
      <FilterBar key={key} filters={d.filters} window={d.window} onChange={apply} />
      <div className="fa-toolbar">
        <ExportCsvButton href={d.csvHref} />
        <SaveReportControl
          label={d.saveLabel}
          query={queryFromView(d.filters, d.window)}
          onSaved={(r) => navigate(r.href)}
        />
      </div>
      <Faults faults={d.faults} />
      {d.truncatedText !== null && <div className="fa-warn">{d.truncatedText}</div>}
      <h3 className="fa-section-title">{d.tableTitle}</h3>
      {d.emptyText !== null ? (
        <EmptyState message={d.emptyText} />
      ) : (
        <table className="fa-table fa-report" data-testid="fa-report-table">
          <thead>
            <tr>
              {d.columns.map((c, i) => (
                <th key={c} className={i === 0 ? undefined : "fa-num"}>
                  {c}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {d.rows.map((r) => (
              <tr key={r.key}>
                <td>{r.name}</td>
                {r.cells.map((c, i) => (
                  <td key={i} className="fa-num">
                    {c}
                  </td>
                ))}
              </tr>
            ))}
            {d.total !== null && (
              <tr className="fa-total">
                <td>{d.total.name}</td>
                {d.total.cells.map((c, i) => (
                  <td key={i} className="fa-num">
                    {c}
                  </td>
                ))}
              </tr>
            )}
          </tbody>
        </table>
      )}
      <h3 className="fa-section-title">{d.savedTitle}</h3>
      {d.savedEmptyText !== null ? (
        <p className="fa-dim">{d.savedEmptyText}</p>
      ) : (
        <ul className="fa-saved" data-testid="fa-saved-reports">
          {d.saved.map((s) => (
            <li key={s.id}>
              <Link to={s.href}>{s.name}</Link>
              <span className="fa-dim"> - {s.description}. {s.savedText}.</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

// The query the view was folded for, as the save form sends it back: the Gateway's own answer, not the address.
function queryFromView(filters: FactoryActivityView["filters"], window: FactoryActivityView["window"]): FactoryQuery {
  return {
    factory: filters.factory ?? "",
    agent: filters.agent ?? "",
    outcome: filters.outcome ?? "",
    window: window.key,
    from: window.key === "custom" ? window.fromLocal : "",
    to: window.key === "custom" ? window.toLocal : "",
  };
}

function withTab(tab: TabKey, q: FactoryQuery): Record<string, string> {
  const out: Record<string, string> = { tab };
  for (const [k, v] of Object.entries(q)) {
    if (k !== "report" && typeof v === "string" && v.length > 0) out[k] = v;
  }
  return out;
}
