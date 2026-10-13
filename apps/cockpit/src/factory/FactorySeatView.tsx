import { Fragment, useCallback, useState } from "react";
import { Link, useParams } from "react-router-dom";
import {
  getFactorySeatPage,
  takeScheduleOffSeat,
  type FactorySeatPageSchedule,
  type FactorySeatPageView,
  type FactorySeatUnlink,
} from "@devthrottle/client-core/factory/factoriesScreenClient";
import { getCronJob, setCronJobEnabled, type CronJob } from "@devthrottle/client-core/schedule/scheduleClient";
import { savedWarnings } from "@devthrottle/client-core/schedule/savedWarnings";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button, ConfirmDialog, ErrorBanner, LoadingState } from "../components";
import { ScheduleEditor, type ScheduleEditorRequest } from "../schedule/ScheduleEditor";
import { PausedBanner, useSchedulerPause } from "../schedule/SchedulerPause";
import { useView } from "./FactoryActivityTabs";
import { ActivityTable, NumberPill, PauseButton, TalkButton, ToneChip } from "./FactoryParts";
import "./factory.css";

const SURFACE = "cockpit-factory-seat";

// One seat's page (Factory Seats mission, phase 1): /factories/<factory>/seats/<seat>. Every seat has one, whether
// or not anything is scheduled for it - before this the only way into a seat was the Edit button of its schedule,
// so a seat with no schedule could not be opened at all. The panels, in the Gateway's order: who the seat is, what
// it does (its brief), how it starts, the factory's goal and number, and what it has left behind.
//
// Rule 7: every word, tone and order is the Gateway's (FactorySeatPageFold). This file lays it out and sends back
// what a button named; it never decides what "talk only" or "paused" means.
export function FactorySeatView() {
  const { factory = "", seat = "" } = useParams();
  const view = useView<FactorySeatPageView>((s) => getFactorySeatPage(factory, seat, s), `${factory}|${seat}`, "load this seat");
  // What the last action did, in the Gateway's words; it stays until the next one.
  const [notice, setNotice] = useState<string | null>(null);

  // error-reported-by: useView
  if (view.error !== null) return <ErrorBanner message={view.error} onRetry={view.reload} />;
  if (view.data === null) return <LoadingState />;
  const page = view.data;
  const crumbs = page.crumb.split(" / ");

  return (
    <div className="fa-page" data-testid="factory-seat-page">
      <nav className="fa-crumbs" aria-label="Breadcrumb">
        {crumbs.map((part, i) => (
          <Fragment key={i}>
            {i > 0 && " / "}
            {i < crumbs.length - 1 && page.crumbHrefs[i] !== undefined ? (
              <Link to={page.crumbHrefs[i]}>{part}</Link>
            ) : (
              <span>{part}</span>
            )}
          </Fragment>
        ))}
      </nav>

      <header className="fa-factory-head">
        <div className="fa-factory-id">
          <h1 className="ui-page-title">
            {page.name} <ToneChip word={page.startsWord} tone={page.startsTone} />
          </h1>
          <div className="fa-factory-facts" data-testid="fa-seat-facts">
            <span>{page.role}</span>
            <span>{page.factoryTitle}</span>
            <span>{page.computerText}</span>
          </div>
        </div>
        <div className="fa-head-actions">
          <TalkButton talk={page.talk} />
        </div>
      </header>

      {notice !== null && (
        <div className="fa-notice" role="status" data-testid="fa-notice">
          {notice}
        </div>
      )}
      {page.truncatedText !== null && <div className="fa-warn">{page.truncatedText}</div>}

      <div className="fa-overview">
        <section className="fa-panel" data-testid="fa-seat-brief">
          <h2 className="fa-section-title">{page.brief.heading}</h2>
          <dl className="fa-seat-facts">
            <dt>{page.brief.pathLabel}</dt>
            <dd className="mono dt-private" data-testid="fa-seat-brief-path">
              {page.brief.pathText}
            </dd>
            <dt>{page.brief.computerLabel}</dt>
            <dd>{page.brief.computerText}</dd>
          </dl>
          <p className="fa-dim">{page.brief.note}</p>
        </section>

        <StartsPanel
          page={page}
          onChanged={view.reload}
          onDone={(text) => {
            setNotice(text);
            view.reload();
          }}
        />

        <section className="fa-panel" data-testid="fa-seat-goal">
          <h2 className="fa-section-title">{page.goal.heading}</h2>
          {page.goal.text !== null && <p className="fa-goal-text dt-private">{page.goal.text}</p>}
          {page.goal.note !== null && <p className="fa-dim">{page.goal.note}</p>}
          {page.goal.emptyText !== null && <p className="fa-dim">{page.goal.emptyText}</p>}
          <h3 className="fa-seat-subtitle">{page.goalNumber.heading}</h3>
          {page.goalNumber.valueText !== null && <p className="fa-goal-value">{page.goalNumber.valueText}</p>}
          {page.goalNumber.asOfText !== null && (
            <p className="fa-dim">
              {page.goalNumber.asOfText}
              {page.goalNumber.linkHref !== null && (
                <>
                  {" "}
                  <a className="fa-link" href={page.goalNumber.linkHref} target="_blank" rel="noopener noreferrer">
                    {page.goalNumber.linkLabel}
                  </a>
                </>
              )}
            </p>
          )}
          {page.goalNumber.emptyText !== null && <p className="fa-dim">{page.goalNumber.emptyText}</p>}
        </section>

        <LeftBehindPanel page={page} />
      </div>
    </div>
  );
}

// "How it starts": talk only, or each schedule with its Edit, its Pause or Resume and "Take off this seat", then each
// trigger. The schedule buttons are the Seats tab's own - the same editor, the same one-tap pause.
function StartsPanel({
  page,
  onChanged,
  onDone,
}: {
  page: FactorySeatPageView;
  onChanged: () => void;
  onDone: (resultText: string) => void;
}) {
  const { starts } = page;
  const [editor, setEditor] = useState<ScheduleEditorRequest | null>(null);
  // Pause everything: while it is on, no schedule starts, and the panel says so.
  const schedulerPause = useSchedulerPause(SURFACE);
  const [opening, setOpening] = useState<string | null>(null);
  const [switching, setSwitching] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loadWarning, setLoadWarning] = useState<string | null>(null);

  const onSaved = useCallback(
    (saved: CronJob) => {
      setLoadWarning(savedWarnings(saved));
      onChanged();
    },
    [onChanged],
  );

  // The target is what the button said - a paused schedule is resumed, an active one paused - never "the opposite of
  // whatever is stored", so a tap on a row that changed underneath it cannot do the reverse.
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
    <section className="fa-panel" data-testid="fa-seat-starts">
      <h2 className="fa-section-title">{starts.heading}</h2>
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
      {starts.talkOnlyText !== null && (
        <p className="fa-seat-talk-only" data-testid="fa-seat-talk-only">
          {starts.talkOnlyText}
        </p>
      )}
      {starts.schedules.map((s) => (
        <ScheduleRow
          key={s.jobId}
          schedule={s}
          opening={opening}
          switching={switching}
          onEdit={openEditor}
          onToggle={toggleSchedule}
          onUnlinked={onDone}
        />
      ))}
      {starts.triggers.map((w) => (
        <div key={w.triggerId} className="fa-woken" data-testid={`fa-seat-trigger-${w.triggerId}`}>
          <div>
            {w.text} <ToneChip word={w.statusWord} tone={w.statusTone} />
          </div>
          {w.statusText !== null && <div className="fa-fault">{w.statusText}</div>}
          <div className="fa-dim">Last check: {w.lastCheck}</div>
        </div>
      ))}
      {starts.triggersPause !== null && (
        <div className="fa-seat-trigger-actions">
          <PauseButton pause={starts.triggersPause} factory={page.factoryId} agent={page.seatId} onDone={onChanged} />
        </div>
      )}
      {editor !== null && <ScheduleEditor request={editor} onClose={() => setEditor(null)} onSaved={onSaved} />}
    </section>
  );
}

function ScheduleRow({
  schedule: s,
  opening,
  switching,
  onEdit,
  onToggle,
  onUnlinked,
}: {
  schedule: FactorySeatPageSchedule;
  opening: string | null;
  switching: string | null;
  onEdit: (jobId: string) => Promise<void>;
  onToggle: (jobId: string, paused: boolean) => Promise<void>;
  onUnlinked: (resultText: string) => void;
}) {
  return (
    <div className="fa-seat-start" data-testid={`fa-seat-schedule-${s.jobId}`}>
      <div className="fa-seat-start-words">
        <div className="fa-seat-start-when">
          {s.whenText}
          {s.pausedText !== null && (
            <>
              {" "}
              <ToneChip word={s.pausedText} tone="paused" />
            </>
          )}
        </div>
        {s.name !== null && <div className="fa-dim dt-private">{s.name}</div>}
        <div className="fa-dim">{s.shiftText}</div>
        {s.missingText !== null && <div className="fa-fault">{s.missingText}</div>}
      </div>
      <div className="fa-seat-start-actions">
        {s.editLabel !== null && (
          <Button variant="secondary" disabled={opening !== null} onClick={() => void onEdit(s.jobId)}>
            {opening === s.jobId ? "Opening..." : s.editLabel}
          </Button>
        )}
        {s.toggleLabel !== null && (
          <Button
            variant="secondary"
            data-testid={`fa-seat-toggle-${s.jobId}`}
            disabled={switching !== null}
            onClick={() => void onToggle(s.jobId, s.paused)}
          >
            {switching === s.jobId ? "Saving..." : s.toggleLabel}
          </Button>
        )}
        {s.unlink !== null && <UnlinkButton unlink={s.unlink} onDone={onUnlinked} />}
      </div>
    </div>
  );
}

/**
 * "Take off this seat": the button opens the shared confirm with the Gateway's own title, sentences and label,
 * verbatim. Confirming sends the schedule the confirm named. On success the Gateway's sentence saying what was done
 * goes to the page, which shows it and reloads. A refusal stays in the confirm, in the Gateway's words.
 */
function UnlinkButton({ unlink, onDone }: { unlink: FactorySeatUnlink; onDone: (resultText: string) => void }) {
  const [open, setOpen] = useState(false);
  return (
    <span className="fa-inline-action">
      <Button variant="secondary" data-testid={`fa-seat-unlink-${unlink.jobId}`} onClick={() => setOpen(true)}>
        {unlink.label}
      </Button>
      <ConfirmDialog
        open={open}
        title={unlink.confirmTitle}
        message={
          <div className="fa-confirm-lines" data-testid="fa-confirm-lines">
            {unlink.confirmLines.map((line, i) => (
              <p key={i}>{line}</p>
            ))}
          </div>
        }
        confirmLabel={unlink.confirmLabel}
        busyLabel={unlink.busyLabel}
        danger={false}
        action={unlink.label.toLowerCase()}
        onConfirm={async () => {
          const result = await takeScheduleOffSeat(unlink.jobId);
          onDone(result.text);
        }}
        onClose={() => setOpen(false)}
      />
    </span>
  );
}

function LeftBehindPanel({ page }: { page: FactorySeatPageView }) {
  const left = page.leftBehind;
  return (
    <section className="fa-panel" data-testid="fa-seat-left-behind">
      <h2 className="fa-section-title">{left.heading}</h2>
      <dl className="fa-seat-facts">
        <dt>{left.lastTalkLabel}</dt>
        <dd className="dt-private" data-testid="fa-seat-last-talk">
          {left.lastTalkText}
        </dd>
        <dt>{left.lastRunLabel}</dt>
        <dd data-testid="fa-seat-last-run">
          <span className={`fa-lastrun fa-tone-${left.lastRunTone}`}>{left.lastRunText}</span>
          {left.closingText !== null && left.closingTone !== null && (
            <div className={`fa-lastrun fa-closing fa-tone-${left.closingTone}`} data-testid="fa-seat-closing">
              {left.closingText}
            </div>
          )}
        </dd>
        <dt>{left.memoryLabel}</dt>
        <dd data-testid="fa-seat-memory">
          {left.memoryText}{" "}
          <Link className="fa-link" to={left.memoryHref}>
            {left.memoryLinkLabel}
          </Link>
        </dd>
      </dl>

      <h3 className="fa-seat-subtitle">{left.numbersTitle}</h3>
      <div className="fa-numbers">
        {left.numbers.map((n) => (
          <NumberPill key={n.text} n={n} />
        ))}
      </div>

      <h3 className="fa-seat-subtitle">{left.recentTitle}</h3>
      {left.recentEmptyText !== null ? (
        <p className="fa-dim">{left.recentEmptyText}</p>
      ) : (
        <div className="fa-scroll">
          <ActivityTable rows={left.recent} showFactory={false} />
        </div>
      )}
      <Link className="fa-link" to={left.allHref}>
        {left.allLabel}
      </Link>
    </section>
  );
}
