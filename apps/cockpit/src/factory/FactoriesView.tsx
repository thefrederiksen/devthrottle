import { useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { getFactoriesList, type FactoriesListView } from "@devthrottle/client-core/factory/factoriesScreenClient";
import { EmptyState, ErrorBanner, LoadingState, PageHeader } from "../components";
import { ActivityTab, ReportsTab, useView } from "./FactoryActivityTabs";
import { OwnerActionButton, StatusWord, TalkButton } from "./FactoryParts";
import {
  FACTORY_COLUMN_KEYS,
  FACTORY_SORT_KEYS,
  clickHeading,
  directionLabel,
  headingArrow,
  loadFactorySort,
  saveFactorySort,
  sortFactoryRows,
  sortFooter,
  type FactorySortOrder,
} from "./factoriesSort";
import "./factory.css";

// Factories (Factories screen mission, mockups 1 and 4): three tabs - Factories, Activity and Reports. The Factories
// tab is one row per factory - name, what is waiting on the owner, status, and the CEO's Talk button. Every word and
// tone is the Gateway's (FactoriesScreenFold), rendered verbatim (rule 7). The ORDER is the owner's: a "Sort by"
// control and clickable column headings (factoriesSort.ts), remembered in this browser, Name A to Z the first time.
// The page keeps the chosen tab in the address. At phone width the same rows become cards (factory.css).

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
      {frame.data !== null && tab === "factories" && <FactoriesList view={frame.data} onChanged={frame.reload} />}
      {tab === "activity" && <ActivityTab />}
      {tab === "reports" && <ReportsTab />}
    </div>
  );
}

function FactoriesList({ view, onChanged }: { view: FactoriesListView; onChanged: () => void }) {
  const navigate = useNavigate();
  // Showing the archived factories is a layout choice; which are archived, and every word, are the Gateway's.
  const [showArchived, setShowArchived] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [order, setOrder] = useState<FactorySortOrder>(loadFactorySort);
  const pick = (next: FactorySortOrder) => {
    setOrder(next);
    saveFactorySort(next);
  };
  const rows = sortFactoryRows(view.rows, order);
  const archived = (
    <div className="fa-archived-section">
      <button
        type="button"
        className="fa-link-button"
        aria-expanded={showArchived}
        data-testid="fa-show-archived"
        onClick={() => setShowArchived((v) => !v)}
      >
        {showArchived ? view.hideArchivedLabel : view.showArchivedLabel}
      </button>
      {notice !== null && (
        <div className="fa-notice" role="status" data-testid="fa-notice">
          {notice}
        </div>
      )}
      {showArchived && (
        <div className="fa-archived-list" data-testid="fa-archived-list">
          {view.archivedEmptyText !== null && <p className="fa-dim">{view.archivedEmptyText}</p>}
          {view.archivedRows.map((row) => (
            <div key={row.id} className="fa-archived-row" data-testid={`fa-archived-${row.id}`}>
              <Link to={row.href}>{row.title}</Link>
              <span className="fa-dim">{row.archivedText}</span>
              <OwnerActionButton
                action={row.restore}
                onDone={(text) => {
                  setNotice(text);
                  onChanged();
                }}
              />
            </div>
          ))}
        </div>
      )}
    </div>
  );
  if (view.emptyText !== null)
    return (
      <div className="fa-tab-body">
        <EmptyState message={view.emptyText} />
        {archived}
        <OutsideAnyFactory view={view} />
      </div>
    );
  return (
    <div className="fa-tab-body">
      {view.truncatedText !== null && <div className="fa-warn">{view.truncatedText}</div>}
      <div className="fa-sortbar" data-testid="fa-sortbar">
        <span className="fa-sortbar-label" id="fa-sortbar-label">
          Sort by
        </span>
        <div className="fa-sortbar-seg" role="group" aria-labelledby="fa-sortbar-label">
          {FACTORY_SORT_KEYS.map((k) => (
            <button
              key={k.key}
              type="button"
              className={`fa-sortbar-choice${order.key === k.key ? " active" : ""}`}
              aria-pressed={order.key === k.key}
              data-testid={`fa-sort-${k.key}`}
              onClick={() => pick(order.key === k.key ? order : { key: k.key, reversed: false })}
            >
              {k.label}
            </button>
          ))}
        </div>
        <button
          type="button"
          className="fa-sortbar-dir"
          data-testid="fa-sort-direction"
          title="Reverse the order"
          aria-label={`Reverse the order, now ${directionLabel(order)}`}
          onClick={() => pick({ key: order.key, reversed: !order.reversed })}
        >
          {directionLabel(order)}
        </button>
        <span className="fa-sortbar-count">{rows.length === 1 ? "1 factory" : `${rows.length} factories`}</span>
      </div>
      <div className="fa-flist" role="table" aria-label={view.title} data-testid="fa-factories-list">
        <div className="fa-flist-head" role="row">
          {view.columns.map((c) => {
            const key = FACTORY_COLUMN_KEYS[c];
            if (key === undefined)
              return (
                <span key={c} role="columnheader">
                  {c}
                </span>
              );
            const active = order.key === key;
            const arrow = headingArrow(order);
            return (
              <span
                key={c}
                role="columnheader"
                aria-sort={active ? (arrow === "^" ? "ascending" : "descending") : "none"}
              >
                <button
                  type="button"
                  className={`fa-sort-heading${active ? " active" : ""}`}
                  data-testid={`fa-sort-heading-${key}`}
                  onClick={() => pick(clickHeading(order, key))}
                >
                  {c}
                  {active && (
                    <span className="fa-sort-arrow" aria-hidden="true">
                      {" " + arrow}
                    </span>
                  )}
                </button>
              </span>
            );
          })}
          <span role="columnheader" aria-label="Talk" />
        </div>
        {rows.map((row) => (
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
      <p className="fa-footnote" data-testid="fa-sort-footer">
        {sortFooter(order)}
      </p>
      {archived}
      <OutsideAnyFactory view={view} />
    </div>
  );
}

// Schedules outside any factory (issue #3650): every enabled schedule that is no seat of a factory, always shown, so
// a stray one is seen the day it appears. Which schedules, the order and every word are the Gateway's.
function OutsideAnyFactory({ view }: { view: FactoriesListView }) {
  return (
    <section className="fa-outside" aria-label={view.outsideTitle} data-testid="fa-outside">
      <h3 className="fa-outside-title">{view.outsideTitle}</h3>
      <p className="fa-dim">{view.outsideText}</p>
      {view.outsideEmptyText !== null && <p className="fa-dim">{view.outsideEmptyText}</p>}
      {view.outsideRows.map((row) => (
        <div key={row.id} className="fa-outside-row" data-testid={`fa-outside-${row.id}`}>
          <span className="fa-outside-name">{row.name}</span>
          <span className="fa-dim">{row.id}</span>
          <span>{row.whenText}</span>
          <span className="fa-dim">{row.machine}</span>
          <span className="fa-outside-reason">{row.reason}</span>
        </div>
      ))}
    </section>
  );
}
