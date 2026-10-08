import { useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import {
  getFactoriesList,
  type FactoriesListView,
  type FactoryListRow,
} from "@devthrottle/client-core/factory/factoriesScreenClient";
import { EmptyState, ErrorBanner, LoadingState, PageHeader } from "../components";
import { ActivityTab, ReportsTab, useView } from "./FactoryActivityTabs";
import { OwnerActionButton, StatusWord, TalkButton, ToneChip } from "./FactoryParts";
import {
  FACTORY_VIEW_MODES,
  countFactoryRows,
  groupFactoryRows,
  initialsOf,
  loadFactoriesView,
  saveFactoriesView,
  shortTalkLabel,
  type FactoriesViewMode,
  type FactoryGroup,
} from "./factoriesCards";
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
//
// Since 8 Oct 2026 (owner decision, mockup B) the tab opens as CARDS grouped by state - "Needs fixing", "Paused",
// "Running on its own" - under a strip of counts, with a Cards / Table switch at the right of the toolbar that this
// browser remembers (factoriesCards.ts). Table is the row list above, unchanged. Each card: the name (a link), the
// status pill, the registry's one-line purpose when set, the Gateway's status line, and the head with Talk.

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
  const [mode, setMode] = useState<FactoriesViewMode>(loadFactoriesView);
  const pickMode = (next: FactoriesViewMode) => {
    setMode(next);
    saveFactoriesView(next);
  };
  const rows = sortFactoryRows(view.rows, order);
  const counts = countFactoryRows(view.rows);
  const groups = groupFactoryRows(view.rows, order);
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
      <div className="fa-strip" data-testid="fa-strip" aria-label="Factory counts">
        <StripNumber testId="fa-strip-total" n={counts.total} label={counts.total === 1 ? "factory" : "factories"} tone="" />
        <StripNumber testId="fa-strip-running" n={counts.running} label="running" tone="ok" />
        <StripNumber testId="fa-strip-fixing" n={counts.needsFixing} label={counts.needsFixing === 1 ? "needs fixing" : "need fixing"} tone="red" />
        <StripNumber testId="fa-strip-paused" n={counts.paused} label="paused" tone="amber" />
        <StripNumber testId="fa-strip-waiting" n={counts.waiting} label="waiting on you" tone="" />
      </div>
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
        <div className="fa-sortbar-seg fa-views" role="group" aria-label="Show as" data-testid="fa-views">
          {FACTORY_VIEW_MODES.map((m) => (
            <button
              key={m.key}
              type="button"
              className={`fa-sortbar-choice${mode === m.key ? " active" : ""}`}
              aria-pressed={mode === m.key}
              data-testid={`fa-view-${m.key}`}
              onClick={() => pickMode(m.key)}
            >
              {m.label}
            </button>
          ))}
        </div>
      </div>
      {mode === "cards" && (
        <div className="fa-groups" data-testid="fa-factories-cards">
          {groups.map((g) => (
            <FactoryCardGroup key={g.key} group={g} />
          ))}
        </div>
      )}
      {mode === "table" && (
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
            <span className="fa-flist-waiting dt-private" role="cell">
              {row.waitingHref !== null ? (
                <Link to={row.waitingHref} onClick={(e) => e.stopPropagation()} data-testid="fa-waiting-link">
                  {row.waitingText}
                </Link>
              ) : (
                row.waitingText
              )}
            </span>
            <span className="fa-flist-status dt-private" role="cell">
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
      )}
      <p className="fa-footnote" data-testid="fa-sort-footer">
        {sortFooter(order)}
      </p>
      {archived}
      <OutsideAnyFactory view={view} />
    </div>
  );
}

/** One number of the count strip, coloured by its tone (green running, red needs fixing, amber paused). */
function StripNumber({ testId, n, label, tone }: { testId: string; n: number; label: string; tone: "" | "ok" | "red" | "amber" }) {
  return (
    <div className={`fa-strip-item${tone === "" ? "" : ` fa-tone-${tone}`}`} data-testid={testId}>
      <b className="fa-strip-num">{n}</b>
      <span className="fa-strip-label">{label}</span>
    </div>
  );
}

/** One group of cards under its heading, with the group's count. Only a group with a card in it is rendered. */
function FactoryCardGroup({ group }: { group: FactoryGroup }) {
  return (
    <section className="fa-group" aria-label={group.heading} data-testid={`fa-group-${group.key}`}>
      <h4 className={`fa-group-heading fa-group-${group.key}`} data-testid={`fa-group-heading-${group.key}`}>
        {group.heading} <span className="fa-group-count">({group.rows.length})</span>
      </h4>
      <div className="fa-cards">
        {group.rows.map((row) => (
          <FactoryCard key={row.id} row={row} groupKey={group.key} />
        ))}
      </div>
    </section>
  );
}

/**
 * One factory as a card (mockup B). Every word is the Gateway's: the name, the status word and its reason, the status
 * line, the purpose, the head's name and the Talk button's words - the card only shortens "Talk to Nora Hale" to
 * "Talk to Nora" and draws the head's initials. A RUNNING row has no status line from the Gateway and the card shows
 * none. The status line is cut to three lines here; the full line is on the factory's page, which the name opens.
 * The purpose, the status (word, reason and line) and the waiting text carry the dt-private class on the card and in
 * the table (owner ruling, 8 Oct 2026), so a demo mode can blank them.
 */
function FactoryCard({ row, groupKey }: { row: FactoryListRow; groupKey: FactoryGroup["key"] }) {
  const pill = <ToneChip word={row.statusWord} tone={row.statusTone} title={row.statusReason} />;
  return (
    <article className={`fa-card fa-card-${groupKey}`} data-testid={`fa-card-${row.id}`}>
      <div className="fa-card-top">
        <Link className="fa-card-name" to={row.href} data-testid="fa-card-name">
          {row.title}
        </Link>
        <span className="dt-private">
          {row.statusHref !== null ? (
            <Link className="fa-status-link" to={row.statusHref} data-testid="fa-card-status-link">
              {pill}
            </Link>
          ) : (
            pill
          )}
        </span>
      </div>
      {row.purpose !== null && (
        <p className="fa-card-purpose dt-private" data-testid="fa-card-purpose">
          {row.purpose}
        </p>
      )}
      {row.statusLine !== null && (
        <p className="fa-card-line dt-private" data-testid="fa-card-line" title={row.statusLine}>
          {row.statusLine}
        </p>
      )}
      {row.waitingCount > 0 && row.waitingHref !== null && (
        <Link className="fa-card-waiting dt-private" to={row.waitingHref} data-testid="fa-card-waiting">
          {row.waitingText} waiting on you
        </Link>
      )}
      <div className="fa-card-bottom">
        {row.headName !== null && row.talk !== null ? (
          <>
            <div className="fa-card-head">
              <span className="fa-avatar" aria-hidden="true">
                {initialsOf(row.headName)}
              </span>
              <span className="fa-card-head-text">
                <b data-testid="fa-card-head-name">{row.headName}</b>
                <span className="fa-dim">runs it</span>
              </span>
            </div>
            <TalkButton talk={{ ...row.talk, label: shortTalkLabel(row.talk.label, row.headName) }} variant="secondary" />
          </>
        ) : (
          <div className="fa-card-head">
            <span className="fa-avatar fa-avatar-none" aria-hidden="true">
              -
            </span>
            <span className="fa-dim" data-testid="fa-card-no-head">
              {row.noCeoText}
            </span>
          </div>
        )}
      </div>
    </article>
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
