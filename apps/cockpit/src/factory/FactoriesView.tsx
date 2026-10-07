import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { getFactoriesList, type FactoriesListView } from "@devthrottle/client-core/factory/factoriesScreenClient";
import { EmptyState, ErrorBanner, LoadingState, PageHeader } from "../components";
import { ActivityTab, ReportsTab, useView } from "./FactoryActivityTabs";
import { StatusWord, TalkButton } from "./FactoryParts";
import "./factory.css";

// Factories (Factories screen mission, mockups 1 and 4): three tabs - Factories, Activity and Reports. The Factories
// tab is one row per factory - name, what is waiting on the owner, status, and the CEO's Talk button - worst first.
// Every word, tone and the order are the Gateway's (FactoriesScreenFold), rendered verbatim (rule 7); the page only
// lays them out and keeps the chosen tab in the address. At phone width the same rows become cards (factory.css).

type TabKey = "factories" | "activity" | "reports";

const TAB_KEYS: ReadonlyArray<TabKey> = ["factories", "activity", "reports"];

export function FactoriesView() {
  const [params, setParams] = useSearchParams();
  const raw = params.get("tab");
  const tab: TabKey = TAB_KEYS.includes(raw as TabKey) ? (raw as TabKey) : "factories";

  // The frame (title, subtitle, tab labels) is the list fold's; it is read whichever tab is open.
  const frame = useView<FactoriesListView>((s) => getFactoriesList(s), "frame", "load the factories");

  const selectTab = (t: TabKey) => setParams(t === "factories" ? {} : { tab: t });

  return (
    <div className="fa-page" data-testid="factories-page">
      <PageHeader title={frame.data?.title ?? "Factories"} subtitle={frame.data?.subtitle} />
      {frame.data !== null && (
        <div className="fa-tabs" role="tablist" aria-label="Factories view">
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
      {frame.data !== null && tab === "factories" && <FactoriesList view={frame.data} />}
      {tab === "activity" && <ActivityTab />}
      {tab === "reports" && <ReportsTab />}
    </div>
  );
}

function FactoriesList({ view }: { view: FactoriesListView }) {
  const navigate = useNavigate();
  if (view.emptyText !== null) return <EmptyState message={view.emptyText} />;
  return (
    <div className="fa-tab-body">
      {view.truncatedText !== null && <div className="fa-warn">{view.truncatedText}</div>}
      <div className="fa-flist" role="table" aria-label={view.title} data-testid="fa-factories-list">
        <div className="fa-flist-head" role="row">
          {view.columns.map((c) => (
            <span key={c} role="columnheader">
              {c}
            </span>
          ))}
          <span role="columnheader" aria-label="Talk" />
        </div>
        {view.rows.map((row) => (
          <div
            key={row.id}
            className="fa-flist-row"
            role="row"
            data-testid={`fa-factory-${row.id}`}
            onClick={() => navigate(row.href)}
          >
            <span className="fa-flist-name" role="cell">
              <Link to={row.href} onClick={(e) => e.stopPropagation()}>
                {row.title}
              </Link>
            </span>
            <span className="fa-flist-waiting" role="cell">
              {row.waitingHref !== null ? (
                <Link to={row.waitingHref} onClick={(e) => e.stopPropagation()} data-testid="fa-waiting-link">
                  {row.waitingText}
                </Link>
              ) : (
                row.waitingText
              )}
            </span>
            <span className="fa-flist-status" role="cell">
              <StatusWord word={row.statusWord} tone={row.statusTone} reason={row.statusReason} line={row.statusLine} href={row.statusHref} />
            </span>
            <span className="fa-flist-talk" role="cell">
              {row.talk !== null ? (
                <TalkButton talk={row.talk} variant="secondary" />
              ) : (
                <span className="fa-dim">{row.noCeoText}</span>
              )}
            </span>
          </div>
        ))}
      </div>
      {view.footerText !== null && <p className="fa-footnote">{view.footerText}</p>}
    </div>
  );
}
