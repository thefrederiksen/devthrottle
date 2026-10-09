import { Fragment, useState } from "react";
import { Link, useParams } from "react-router-dom";
import {
  getFactoryPage,
  getFactorySeats,
  type FactoryPageView,
  type FactorySeatsView,
} from "@devthrottle/client-core/factory/factoriesScreenClient";
import { EmptyState, ErrorBanner, LoadingState } from "../components";
import { ActivityTab, ReportsTab, useView } from "./FactoryActivityTabs";
import { FailuresCard, useScrollToHash } from "./FactoryFailures";
import { FactoryFloorPanel } from "./FactoryFloor";
import { FactoryMemoryTab } from "./FactoryMemoryTab";
import { OwnerActionButton, TalkButton, ToneChip } from "./FactoryParts";
import { WaitingItem } from "./FactoryWaitingView";
import "./factory.css";

// One factory's page (Factories screen mission, mockups 2 and 3): the header - name, status, the boss, seat count, the
// computer it runs on, and Talk to the boss - then the tabs the Gateway offers: Overview, Seats (n), Activity,
// Reports, Memory and Documents. Each tab has its own address (/factories/<id>/<tab>; the Overview is the page's own
// address), and a tab the Gateway did not offer opens the Overview.
//
// Rule 7: every word and tone is the Gateway's (FactoriesScreenFold).

export function factoryTabHref(factory: string, key: string): string {
  const page = `/factories/${encodeURIComponent(factory)}`;
  return key === "overview" ? page : `${page}/${encodeURIComponent(key)}`;
}

export function FactoryView() {
  const { factory = "", tab: requestedTab = "overview" } = useParams();
  const view = useView<FactoryPageView>((s) => getFactoryPage(factory, s), factory, "load this factory");
  // What the owner's last action did, in the Gateway's words (round 2); it stays until the next one.
  const [notice, setNotice] = useState<string | null>(null);
  const done = (text: string) => {
    setNotice(text);
    view.reload();
  };
  // The Seats tab is its own Gateway view; it is read only while that tab is open.
  const onSeats = requestedTab === "seats";
  const seats = useView<FactorySeatsView | null>(
    (s) => (onSeats ? getFactorySeats(factory, s) : Promise.resolve(null)),
    `${factory}|${onSeats}`,
    "load this factory's seats",
  );

  useScrollToHash(view.data !== null);

  if (view.error !== null) return <ErrorBanner message={view.error} onRetry={view.reload} />;
  if (view.data === null) return <LoadingState />;
  const page = view.data;
  const tab = page.tabs.some((t) => t.key === requestedTab) ? requestedTab : "overview";

  return (
    <div className="fa-page" data-testid="factory-page">
      {tab === "seats" && seats.data !== null ? (
        <Crumbs crumb={seats.data.crumb} hrefs={[page.crumbHref, factoryTabHref(page.id, "overview")]} />
      ) : (
        <Crumbs crumb={page.crumb} hrefs={[page.crumbHref]} />
      )}
      <header className="fa-factory-head">
        <div className="fa-factory-id">
          <h1 className="ui-page-title">
            {page.title} <ToneChip word={page.statusWord} tone={page.statusTone} title={page.statusReason} />
          </h1>
          {page.statusLine !== null && (
            <p className="fa-status-line dt-private" data-testid="fa-page-status-line">
              {page.statusHref !== null ? <Link to={page.statusHref}>{page.statusLine}</Link> : page.statusLine}
            </p>
          )}
          <div className="fa-factory-facts" data-testid="fa-factory-facts">
            <span>{page.bossText}</span>
            <span>{page.seatCountText}</span>
            <span>{page.computerText}</span>
          </div>
        </div>
        <div className="fa-head-actions">
          {page.talk !== null && <TalkButton talk={page.talk} />}
          {page.archive !== null && <OwnerActionButton action={page.archive} onDone={done} />}
        </div>
      </header>

      {notice !== null && (
        <div className="fa-notice" role="status" data-testid="fa-notice">
          {notice}
        </div>
      )}
      {page.archivedText !== null && (
        <div className="fa-archived" data-testid="fa-archived">
          <span>{page.archivedText}</span>
          {page.restore !== null && <OwnerActionButton action={page.restore} onDone={done} variant="primary" />}
        </div>
      )}

      <nav className="fa-tabs" aria-label={`${page.title} view`}>
        {page.tabs.map((t) => (
          <Link
            key={t.key}
            to={factoryTabHref(page.id, t.key)}
            className={`fa-tab${tab === t.key ? " active" : ""}`}
            aria-current={tab === t.key ? "page" : undefined}
          >
            {t.label}
          </Link>
        ))}
      </nav>

      {page.truncatedText !== null && <div className="fa-warn">{page.truncatedText}</div>}
      {tab === "overview" && <FactoryFloorPanel factory={page.id} />}
      {tab === "overview" && <Overview page={page} onChanged={view.reload} onDone={done} />}
      {tab === "floor" && <FactoryFloorPanel factory={page.id} full />}
      {tab === "seats" &&
        (seats.error !== null ? (
          <ErrorBanner message={seats.error} onRetry={seats.reload} />
        ) : seats.data === null ? (
          <LoadingState />
        ) : (
          <SeatsTab view={seats.data} />
        ))}
      {tab === "activity" && <ActivityTab fixedFactory={page.id} />}
      {tab === "reports" && <ReportsTab fixedFactory={page.id} />}
      {tab === "memory" && <FactoryMemoryTab factory={page.id} />}
      {tab === "documents" && <EmptyState message={page.documentsText} />}
    </div>
  );
}

/** "Factories / WarmForward / Seats": every part but the last links back up, to the address given for it. */
function Crumbs({ crumb, hrefs }: { crumb: string; hrefs: string[] }) {
  const parts = crumb.split(" / ");
  return (
    <nav className="fa-crumbs" aria-label="Breadcrumb">
      {parts.map((p, i) => (
        <Fragment key={i}>
          {i > 0 && " / "}
          {i < parts.length - 1 && hrefs[i] !== undefined ? <Link to={hrefs[i]}>{p}</Link> : <span>{p}</span>}
        </Fragment>
      ))}
    </nav>
  );
}

function Overview({
  page,
  onChanged,
  onDone,
}: {
  page: FactoryPageView;
  onChanged: () => void;
  onDone: (resultText: string) => void;
}) {
  const { goal, goalNumber, failures, waiting, bossLatest, lastTalk } = page;
  return (
    <div className="fa-overview" data-testid="fa-overview">
      {failures !== null && <FailuresCard factory={page.id} failures={failures} onChanged={onChanged} />}

      <section className="fa-panel" data-testid="fa-goal">
        <h2 className="fa-section-title">{goal.heading}</h2>
        {goal.text !== null && <p className="fa-goal-text dt-private">{goal.text}</p>}
        {goal.note !== null && <p className="fa-dim">{goal.note}</p>}
        {goal.emptyText !== null && <p className="fa-dim">{goal.emptyText}</p>}
      </section>

      <section className="fa-panel" data-testid="fa-goal-number">
        <h2 className="fa-section-title">{goalNumber.heading}</h2>
        {goalNumber.valueText !== null && <p className="fa-goal-value">{goalNumber.valueText}</p>}
        {goalNumber.asOfText !== null && (
          <p className="fa-dim">
            {goalNumber.asOfText}
            {goalNumber.linkHref !== null && (
              <>
                {" "}
                <a className="fa-link" href={goalNumber.linkHref} target="_blank" rel="noopener noreferrer">
                  {goalNumber.linkLabel}
                </a>
              </>
            )}
          </p>
        )}
        {goalNumber.emptyText !== null && <p className="fa-dim">{goalNumber.emptyText}</p>}
      </section>

      <section className="fa-panel" id="waiting" data-testid="fa-page-waiting">
        <h2 className="fa-section-title">{waiting.heading}</h2>
        {waiting.orderText !== null && <p className="fa-dim">{waiting.orderText}</p>}
        {waiting.bulkHandled !== null && (
          <div className="fa-bulk">
            <OwnerActionButton action={waiting.bulkHandled} onDone={onDone} />
          </div>
        )}
        {waiting.bulkHandledNote !== null && <p className="fa-dim">{waiting.bulkHandledNote}</p>}
        {waiting.emptyText !== null ? (
          <p className="fa-dim">{waiting.emptyText}</p>
        ) : (
          <div className="fa-waiting">
            {waiting.items.map((item) => (
              <WaitingItem key={item.id} item={item} onHandled={onChanged} />
            ))}
          </div>
        )}
      </section>

      <section className="fa-panel" data-testid="fa-boss-latest">
        <h2 className="fa-section-title">{bossLatest.heading}</h2>
        {bossLatest.emptyText !== null && <p className="fa-dim">{bossLatest.emptyText}</p>}
        {bossLatest.lines.length > 0 && (
          <ul className="fa-lines dt-private">
            {bossLatest.lines.map((line, i) => (
              <li key={i}>{line}</li>
            ))}
          </ul>
        )}
        {bossLatest.allHref !== null && (
          <Link className="fa-link" to={bossLatest.allHref}>
            {bossLatest.allLabel}
          </Link>
        )}
      </section>

      <section className="fa-panel" data-testid="fa-last-talk">
        <h2 className="fa-section-title">{lastTalk.heading}</h2>
        <p className="dt-private">{lastTalk.text}</p>
      </section>
    </div>
  );
}

function SeatsTab({ view: d }: { view: FactorySeatsView }) {
  return (
    <div className="fa-tab-body">
      <div className="fa-scroll">
        <table className="fa-table fa-seats" data-testid="fa-seats-table">
          <thead>
            <tr>
              {d.columns.map((c) => (
                <th key={c}>{c}</th>
              ))}
              <th aria-label="Talk" />
            </tr>
          </thead>
          <tbody>
            {d.rows.map((seat) => (
              <tr key={seat.seatId} data-testid={`fa-seat-${seat.seatId}`}>
                <td>
                  <div className="fa-seat-name">{seat.name}</div>
                  <div className="fa-dim">{seat.role}</div>
                </td>
                <td>{seat.whenText}</td>
                <td>
                  <span className={`fa-lastrun fa-tone-${seat.lastRunTone}`}>{seat.lastRunText}</span>
                </td>
                <td>{seat.computerText}</td>
                <td className="fa-seat-talk">
                  <TalkButton talk={seat.talk} variant="secondary" />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="fa-footnote">{d.note}</p>
    </div>
  );
}
