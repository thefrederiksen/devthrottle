import { Fragment, useCallback, useState } from "react";
import { Link, useParams } from "react-router-dom";
import {
  getFactoryPage,
  getFactorySeats,
  type FactoryPageView,
  type FactorySeatsView,
} from "@devthrottle/client-core/factory/factoriesScreenClient";
import { getCronJob, setCronJobEnabled, type CronJob } from "@devthrottle/client-core/schedule/scheduleClient";
import { savedWarnings } from "@devthrottle/client-core/schedule/savedWarnings";
import { Button, ErrorBanner, LoadingState } from "../components";
import { ScheduleEditor, type ScheduleEditorRequest } from "../schedule/ScheduleEditor";
import { PausedBanner, useSchedulerPause } from "../schedule/SchedulerPause";
import { ActivityTab, ReportsTab, useView } from "./FactoryActivityTabs";
import { FailuresCard, useScrollToHash } from "./FactoryFailures";
import { ProblemsCard } from "./FactoryProblems";
import { EmailsCard } from "./FactoryEmails";
import { FactoryFloorPanel } from "./FactoryFloor";
import { FactoryMemoryTab } from "./FactoryMemoryTab";
import { FactoryDefinitionTab } from "./FactoryDefinitionTab";
import { FactoryRecordsTab } from "./FactoryRecordsTab";
import { OwnerActionButton, TalkButton, ToneChip } from "./FactoryParts";
import { WaitingItem } from "./FactoryWaitingView";
import "./factory.css";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-factory";

// One factory's page (Factories screen mission, mockups 2 and 3): the header - name, status, the boss, seat count, the
// computer it runs on, and Talk to the boss - then the tabs the Gateway offers: Overview, Seats (n), Activity,
// Reports, Memory, Records and Definition. Each tab has its own address (/factories/<id>/<tab>; the Overview is the page's own
// address), and a tab the Gateway did not offer opens the Overview. The Definition tab replaced the Documents tab
// (Factory Definition, phase 6), so the old /documents address opens it.
//
// Rule 7: every word and tone is the Gateway's (FactoriesScreenFold).

export function factoryTabHref(factory: string, key: string): string {
  const page = `/factories/${encodeURIComponent(factory)}`;
  return key === "overview" ? page : `${page}/${encodeURIComponent(key)}`;
}

export function FactoryView() {
  const { factory = "", tab: askedTab = "overview" } = useParams();
  const requestedTab = askedTab === "documents" ? "definition" : askedTab;
  const view = useView<FactoryPageView>((s) => getFactoryPage(factory, s), factory, "load this factory");
  // What the owner's last action did, in the Gateway's words (round 2); it stays until the next one.
  const [notice, setNotice] = useState<string | null>(null);
  const done = (text: string) => {
    setNotice(text);
    view.reload();
  };
  // The Seats tab is its own Gateway view; it is read only while that tab is open and has something to show (a factory
  // that is not published yet has no seats to read - its tab carries the Gateway's sentence instead).
  const onSeats =
    requestedTab === "seats" &&
    view.data !== null &&
    view.data.tabs.some((t) => t.key === "seats" && (t.notYetText ?? null) === null);
  const seats = useView<FactorySeatsView | null>(
    (s) => (onSeats ? getFactorySeats(factory, s) : Promise.resolve(null)),
    `${factory}|${onSeats}`,
    "load this factory's seats",
  );

  useScrollToHash(view.data !== null);

  // error-reported-by: useView
  if (view.error !== null) return <ErrorBanner message={view.error} onRetry={view.reload} />;
  if (view.data === null) return <LoadingState />;
  const page = view.data;
  const tab = page.tabs.some((t) => t.key === requestedTab) ? requestedTab : "overview";
  // A tab with nothing to show yet shows the Gateway's one sentence in place of its content.
  const notYet = page.tabs.find((t) => t.key === tab)?.notYetText ?? null;

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
          {page.leftOpenText !== null && page.leftOpenHref !== null && (
            <p className="fa-left-open" data-testid="fa-page-left-open">
              <Link to={page.leftOpenHref}>{page.leftOpenText}</Link>
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
      {notYet !== null && (
        <section className="fa-panel" data-testid="fa-tab-not-yet">
          <p className="fa-dim">{notYet}</p>
        </section>
      )}
      {notYet === null && tab === "overview" && <Overview page={page} onChanged={view.reload} onDone={done} />}
      {notYet === null && tab === "floor" && <FactoryFloorPanel factory={page.id} full />}
      {notYet === null && tab === "seats" &&
        (seats.error !== null ? (
          // error-reported-by: useView
          <ErrorBanner message={seats.error} onRetry={seats.reload} />
        ) : seats.data === null ? (
          <LoadingState />
        ) : (
          <SeatsTab view={seats.data} onChanged={seats.reload} />
        ))}
      {notYet === null && tab === "activity" && <ActivityTab fixedFactory={page.id} />}
      {notYet === null && tab === "reports" && <ReportsTab fixedFactory={page.id} />}
      {notYet === null && tab === "memory" && <FactoryMemoryTab factory={page.id} />}
      {notYet === null && tab === "records" && <FactoryRecordsTab factory={page.id} />}
      {notYet === null && tab === "definition" && <FactoryDefinitionTab factory={page.id} onChanged={view.reload} />}
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
  const { goal, goalNumber, failures, problems, emails, waiting, bossLatest, lastTalk } = page;
  return (
    <div className="fa-overview" data-testid="fa-overview">
      {problems !== null && <ProblemsCard problems={problems} onChanged={onChanged} />}
      {failures !== null && <FailuresCard factory={page.id} failures={failures} onChanged={onChanged} />}
      {emails !== null && <EmailsCard emails={emails} />}

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

// The Seats tab. Every seat row opens that seat's own page ("Open seat"), whether or not it has a schedule - the
// schedule's Edit used to be the only way into a seat, so a seat with none could not be opened (the owner,
// 13 October 2026). Each seat's schedules carry an Edit that opens the same editor as the Schedule page, because a
// factory's schedules are changed in the factory (the owner, 2026-10-09) - and, beside it, the one tap that pauses or
// resumes it, right where its state is read (issue #3910).
function SeatsTab({ view: d, onChanged }: { view: FactorySeatsView; onChanged: () => void }) {
  const [editor, setEditor] = useState<ScheduleEditorRequest | null>(null);
  // Pause everything (issue #3910): while it is on, no seat's schedule starts, and the tab says so.
  const schedulerPause = useSchedulerPause(SURFACE);
  // The schedule being fetched for the editor, so its button says so while it loads.
  const [opening, setOpening] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  // The Gateway's warning on a save that lands in a crowded hour; the schedule is saved either way.
  const [loadWarning, setLoadWarning] = useState<string | null>(null);

  const onSaved = useCallback(
    (saved: CronJob) => {
      setLoadWarning(savedWarnings(saved));
      onChanged();
    },
    [onChanged],
  );

  // The schedule being paused or resumed, so its button says so until the Gateway has answered.
  const [switching, setSwitching] = useState<string | null>(null);

  // One tap. The target is what the button said - a paused schedule is resumed, an active one paused - never "the
  // opposite of whatever is stored", so a tap on a row that changed underneath it cannot do the reverse.
  const toggleSchedule = useCallback(
    async (jobId: string, paused: boolean) => {
      setError(null);
      setSwitching(jobId);
      try {
        const saved = await setCronJobEnabled(jobId, paused);
        setLoadWarning(saved.loadWarning ?? null);
        onChanged();
      } catch (err) {
        setError(describeAndReport(SURFACE, paused ? "resume the schedule" : "pause the schedule", err));
      } finally {
        setSwitching(null);
      }
    },
    [onChanged],
  );

  const openEditor = useCallback(async (jobId: string) => {
    setError(null);
    setOpening(jobId);
    try {
      setEditor({ kind: "edit", job: await getCronJob(jobId) });
    } catch (err) {
      setError(describeAndReport(SURFACE, "open the schedule", err));
    } finally {
      setOpening(null);
    }
  }, []);

  return (
    <div className="fa-tab-body">
      <PausedBanner control={schedulerPause} />
      {error !== null && <ErrorBanner message={error} />}
      {loadWarning !== null && (
        <div className="fa-warn" role="status">
          Saved, but: {loadWarning}{" "}
          <Button variant="secondary" onClick={() => setLoadWarning(null)}>
            Dismiss
          </Button>
        </div>
      )}
      {d.definedText !== null && (
        <p className="fa-seat-defined" data-testid="fa-seats-defined">
          {d.definedText}{" "}
          {d.definitionHref !== null && d.definitionLabel !== null && (
            <Link className="fa-link" to={d.definitionHref}>
              {d.definitionLabel}
            </Link>
          )}
        </p>
      )}
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
          {/* Grouped by shift (Factory Control, step 3). The groups, their order and their headings are the
              Gateway's; a seat in two shifts arrives in both, each row with that shift's schedules only. */}
          {d.groups.map((group) => (
            <tbody key={group.shift} data-testid={`fa-shift-${group.shift}`}>
              <tr className="fa-shift-head">
                <th colSpan={d.columns.length + 1} scope="colgroup">
                  {group.title}
                </th>
              </tr>
              {group.rows.map((seat) => (
                <tr key={seat.seatId} data-testid={`fa-seat-${group.shift}-${seat.seatId}`}>
                  <td>
                    <div className="fa-seat-name">{seat.name}</div>
                    <div className="fa-dim">{seat.role}</div>
                    <div className="fa-dim fa-seat-shift">{seat.shiftText}</div>
                    {/* Every seat opens its own page, with or without a schedule (Factory Seats mission). */}
                    <Link className="fa-link fa-seat-open" to={seat.href} data-testid={`fa-seat-open-${group.shift}-${seat.seatId}`}>
                      {seat.openLabel}
                    </Link>
                  </td>
                  <td>
                    {seat.schedules.length === 0
                      ? seat.whenText
                      : seat.schedules.map((s) => (
                          <div className="fa-seat-schedule" key={s.jobId}>
                            <span>{s.whenText}</span>
                            {s.editLabel !== null && (
                              <Button
                                variant="secondary"
                                disabled={opening !== null}
                                onClick={() => void openEditor(s.jobId)}
                              >
                                {opening === s.jobId ? "Opening..." : s.editLabel}
                              </Button>
                            )}
                            {s.toggleLabel !== null && (
                              <Button
                                variant="secondary"
                                data-testid={`fa-seat-toggle-${s.jobId}`}
                                disabled={switching !== null}
                                onClick={() => void toggleSchedule(s.jobId, s.paused)}
                              >
                                {switching === s.jobId ? "Saving..." : s.toggleLabel}
                              </Button>
                            )}
                          </div>
                        ))}
                  </td>
                  <td>
                    <span className={`fa-lastrun fa-tone-${seat.lastRunTone}`}>{seat.lastRunText}</span>
                    <div className={`fa-lastrun fa-closing fa-tone-${seat.closingTone}`} data-testid="fa-seat-closing">
                      {seat.closingText}
                    </div>
                  </td>
                  <td>{seat.computerText}</td>
                  <td className="fa-seat-talk">
                    <TalkButton talk={seat.talk} variant="secondary" />
                  </td>
                </tr>
              ))}
            </tbody>
          ))}
        </table>
      </div>
      <p className="fa-footnote">{d.note}</p>
      {editor !== null && <ScheduleEditor request={editor} onClose={() => setEditor(null)} onSaved={onSaved} />}
    </div>
  );
}
