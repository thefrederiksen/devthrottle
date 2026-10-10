import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import {
  getFactoryActivity,
  getFactoryReports,
  type FactoryActivityView,
  type FactoryQuery,
  type FactoryReportView,
} from "@devthrottle/client-core/factory/factoryAgentsClient";
import { EmptyState, ErrorBanner, LoadingState } from "../components";
import { ActivityTable, ExportCsvButton, Faults, FilterBar, SaveReportControl } from "./FactoryParts";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-factory-activity";

// The Activity and Reports tabs (Screens 3 and 5). They appear twice: on the Factories list, over every factory, and
// on one factory's page, fixed to that factory. Everything shown is the Gateway's fold, rendered verbatim (rule 7);
// the tab only keeps the chosen filter in the address.

export type FactoryRecordTab = "activity" | "reports";

/** Load a Gateway view, keeping the last good answer and the error apart. */
export function useView<T>(load: (signal: AbortSignal) => Promise<T>, key: string, what: string) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  useEffect(() => {
    const ctrl = new AbortController();
    setError(null);
    load(ctrl.signal).then(
      (d) => setData(d),
      (err: unknown) => {
        if (!ctrl.signal.aborted) setError(describeAndReport(SURFACE, what, err));
      },
    );
    return () => ctrl.abort();
    // `key` stands for everything `load` reads: the view reloads when the query changes, not on every render.
  }, [key, nonce]);
  const reload = useCallback(() => setNonce((n) => n + 1), []);
  return { data, error, reload };
}

function queryFrom(params: URLSearchParams, fixedFactory: string | undefined): FactoryQuery {
  return {
    factory: fixedFactory ?? params.get("factory") ?? "",
    agent: params.get("agent") ?? "",
    outcome: params.get("outcome") ?? "",
    window: params.get("window") ?? "",
    from: params.get("from") ?? "",
    to: params.get("to") ?? "",
    report: params.get("report") ?? "",
  };
}

/**
 * Where a filter lives. On a factory's page the factory is in the path, so the query carries the rest; a filter that
 * leaves that factory (another factory, or every factory) goes to the Factories list's tab.
 */
export function recordAddress(tab: FactoryRecordTab, q: FactoryQuery, fixedFactory: string | undefined): string {
  const params = new URLSearchParams();
  const onPage = fixedFactory !== undefined && q.factory === fixedFactory;
  if (!onPage) params.set("tab", tab);
  for (const [k, v] of Object.entries(q)) {
    if (k === "report" || (onPage && k === "factory")) continue;
    if (typeof v === "string" && v.length > 0) params.set(k, v);
  }
  const s = params.toString();
  const path = onPage ? `/factories/${encodeURIComponent(fixedFactory)}/${tab}` : "/factories";
  return s.length === 0 ? path : `${path}?${s}`;
}

export function ActivityTab({ fixedFactory }: { fixedFactory?: string }) {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const q = queryFrom(params, fixedFactory);
  const key = JSON.stringify(q);
  const view = useView<FactoryActivityView>((s) => getFactoryActivity(q, s), key, "load the activity");
  const apply = (next: FactoryQuery) => navigate(recordAddress("activity", next, fixedFactory));

  // error-reported-by: useView
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
        <div className="fa-scroll">
          <ActivityTable rows={d.rows} showFactory={d.filters.factory === null} />
        </div>
      )}
      <p className="fa-footnote">{d.footnote}</p>
    </div>
  );
}

export function ReportsTab({ fixedFactory }: { fixedFactory?: string }) {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const q = queryFrom(params, fixedFactory);
  const key = JSON.stringify(q);
  const view = useView<FactoryReportView>((s) => getFactoryReports(q, s), key, "load the report");
  const apply = (next: FactoryQuery) => navigate(recordAddress("reports", next, fixedFactory));

  // error-reported-by: useView
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
        <div className="fa-scroll">
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
        </div>
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
