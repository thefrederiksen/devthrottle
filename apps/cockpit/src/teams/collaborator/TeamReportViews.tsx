import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { DevReportViewer } from "@devthrottle/client-core/devreports/DevReportViewer";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import {
  commentOnReport,
  getMyTeamReport,
  getReportSentToMe,
  markReportRead,
  ownTeamReportApi,
  receivedReportApi,
  sendMyTeamReport,
  type OwnReportDetail,
  type ReceivedReportDetail,
} from "@devthrottle/client-core/teams/teamReportsClient";
import { Button, ErrorBanner, LoadingState } from "../../components";
import { dateAndTime, TEAM_REPORTS_POLL_MS } from "./teamReportFormat";
import "./collaborator.css";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-team-report";

// ONE REPORT OPEN ON THE TEAM'S REPORTS PAGE (devthrottle_internal#2309). The report itself is the SAME viewer the
// owner reads their reports in - the same frame host and the same trust rules (CONTRACT.md section 4) - fed from the
// team's routes. Beside it, instead of the agent's conversation, is what this reader does with it:
//
//   - a report SENT TO YOU: the version you were sent, your comments on it, and the box to write one. A comment goes to
//     the person who wrote the report and is never shown to an agent; the Gateway's own sentence says so above the box,
//     and when the author can no longer read comments it says that instead and there is no box.
//   - one of YOUR OWN reports: who it went to, which version they hold and whether they read it, sending it to more
//     members (or the newest version to someone holding an older one), and the comments people wrote on it.
//
// The viewer opens only once the Gateway has answered, because whether the report's own notes are offered, and whether
// the answer controls in its markup may be used, are that answer (`notesOpen` and `answersOpen`, both false for every
// team reader today) - never this page's decision (review F2, round-3 review R1).

interface ViewProps {
  teamId: string;
  reportId: string;
  onBack: () => void;
}

function Missing({ onBack }: { onBack: () => void }) {
  return (
    <section className="pane team-page" data-testid="team-report-missing">
      <p>This report is not here. It may not have been sent to you, or the address may be wrong.</p>
      <Button onClick={onBack}>All reports</Button>
    </section>
  );
}

/** Until the Gateway has answered for this report: a back button and the loading line, or the failure in its words. */
function ReportLoading({ error, onBack }: { error: string | null; onBack: () => void }) {
  return (
    <section className="pane team-page" data-testid="team-report-loading">
      <BackButton onBack={onBack} />
      {error ? <ErrorBanner message={error} /> : <LoadingState message="Loading the report..." />}
    </section>
  );
}

function BackButton({ onBack }: { onBack: () => void }) {
  return (
    <button type="button" className="dev-report-action dev-report-back" data-testid="team-report-back" onClick={onBack}>
      All reports
    </button>
  );
}

export function ReceivedReportView({ teamId, reportId, onBack }: ViewProps) {
  const api = useMemo(() => receivedReportApi(teamId), [teamId]);
  const [detail, setDetail] = useState<ReceivedReportDetail | null>(null);
  const [missing, setMissing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  // The version this page has marked read - not merely that it marked one - so a version sent while the report is open
  // is marked when it is shown (delta review D5). It is set only when the Gateway ACCEPTED the read (round-3 review
  // R3): a read that failed leaves the version unmarked, and the next poll's answer tries it again.
  const markedVersion = useRef<number | null>(null);
  // The version a read is on its way for, so a second answer arriving meanwhile does not post it twice.
  const markingVersion = useRef<number | null>(null);

  const refresh = useCallback(
    async (signal: AbortSignal) => {
      try {
        const next = await getReportSentToMe(teamId, reportId, signal);
        if (next === null) {
          setMissing(true);
          return;
        }
        setDetail(next);
        setError(null);
      } catch (err) {
        if (err instanceof Error && err.name === "AbortError") return;
        setError(describeAndReport(SURFACE, "load the report", err));
      }
    },
    [teamId, reportId],
  );
  useVisiblePolling(refresh, TEAM_REPORTS_POLL_MS);

  // Opening a version is reading it - once per version, and only once the Gateway has said it was sent to this person.
  // The request names the version shown; the Gateway marks it only when it is the one held, and otherwise says why.
  // A refused or failed read is shown in the Gateway's words and tried again on the next poll - which also brings the
  // version now held, when a newer one was sent - never in a tight loop.
  useEffect(() => {
    if (detail === null || detail.report.read) return;
    const version = detail.report.version;
    if (markedVersion.current === version || markingVersion.current === version) return;
    markingVersion.current = version;
    markReportRead(teamId, reportId, version)
      .then(() => {
        markedVersion.current = version;
      })
      .catch((err: unknown) => setError(describeAndReport(SURFACE, "mark the report read", err)))
      .finally(() => {
        if (markingVersion.current === version) markingVersion.current = null;
      });
  }, [detail, teamId, reportId]);

  const send = useCallback(async () => {
    setSending(true);
    setSendError(null);
    try {
      const comment = await commentOnReport(teamId, reportId, draft);
      setDetail((d) => (d === null ? d : { ...d, comments: [...d.comments, comment] }));
      setDraft("");
    } catch (err) {
      setSendError(describeAndReport(SURFACE, "send the comment", err));
      // The Gateway may have closed the comments since the page last read it (the author left the team, or can no
      // longer open their reports): read its answer again so the page says so and offers no box.
      void getReportSentToMe(teamId, reportId).then((next) => {
        if (next !== null) setDetail(next);
      });
    } finally {
      setSending(false);
    }
  }, [teamId, reportId, draft]);

  if (missing) return <Missing onBack={onBack} />;
  if (detail === null) return <ReportLoading error={error} onBack={onBack} />;

  return (
    <div className="team-report-open" data-testid="team-report-open">
      <DevReportViewer
        reportId={reportId}
        api={api}
        notes={detail.notesOpen === true}
        answers={detail.answersOpen === true}
        onNotFound={() => setMissing(true)}
        leading={<BackButton onBack={onBack} />}
        renderConversation={() => (
          <aside className="reports-conversation team-report-side" aria-label="Comments" data-testid="team-report-comments">
            <h2 className="reports-conversation-title">Comments</h2>
            <div className="team-report-side-body">
              <p className="team-report-from">
                From {detail.report.from}
              </p>
              {detail.report.questionsLabel !== null && (
                <p className="team-report-note" data-testid="team-report-questions-waiting">
                  <Link to="/questions">{detail.report.questionsLabel}</Link>
                </p>
              )}
              <p className="team-report-note" data-testid="team-report-comments-note">
                {detail.commentsNote}
              </p>
              {error && <ErrorBanner message={error} />}
              <ul className="team-report-comments">
                {detail.comments.map((c) => (
                  <li key={c.id} className="team-report-comment" data-testid="team-report-comment">
                    <time dateTime={c.atUtc}>{dateAndTime(c.atUtc)}</time>
                    {c.aboutLabel !== null && <span className="team-report-comment-about">{c.aboutLabel}</span>}
                    <p>{c.text}</p>
                  </li>
                ))}
              </ul>
              {detail.canComment && (
                <>
                  <label className="team-report-label" htmlFor="team-report-comment-box">
                    Your comment
                  </label>
                  <textarea
                    id="team-report-comment-box"
                    className="team-report-textarea"
                    data-testid="team-report-comment-box"
                    rows={4}
                    value={draft}
                    onChange={(e) => setDraft(e.target.value)}
                  />
                </>
              )}
              {sendError && (
                <div className="dev-report-error" role="alert" data-testid="team-report-comment-error">
                  {sendError}
                </div>
              )}
              {detail.canComment && (
                <Button variant="primary" data-testid="team-report-comment-send" disabled={sending || draft.trim().length === 0} onClick={() => void send()}>
                  {sending ? "Sending..." : "Send comment"}
                </Button>
              )}
            </div>
          </aside>
        )}
      />
    </div>
  );
}

export function OwnTeamReportView({ teamId, reportId, onBack }: ViewProps) {
  const api = useMemo(() => ownTeamReportApi(teamId), [teamId]);
  const [detail, setDetail] = useState<OwnReportDetail | null>(null);
  const [missing, setMissing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [chosen, setChosen] = useState<string[]>([]);
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);

  const refresh = useCallback(
    async (signal: AbortSignal) => {
      try {
        const next = await getMyTeamReport(teamId, reportId, signal);
        if (next === null) {
          setMissing(true);
          return;
        }
        setDetail(next);
        setError(null);
      } catch (err) {
        if (err instanceof Error && err.name === "AbortError") return;
        setError(describeAndReport(SURFACE, "load the report", err));
      }
    },
    [teamId, reportId],
  );
  useVisiblePolling(refresh, TEAM_REPORTS_POLL_MS);

  const toggle = (memberId: string) =>
    setChosen((now) => (now.includes(memberId) ? now.filter((m) => m !== memberId) : [...now, memberId]));

  // The send names the version this page is showing, so what is sent is what the author read (delta review D2). When
  // the Gateway refuses it - the session published a newer one meanwhile - its sentence is shown and the page reads
  // again, which brings the newer version to read before sending.
  const shownVersion = detail?.report.version ?? null;
  const send = useCallback(async () => {
    if (shownVersion === null) return;
    setSending(true);
    setSendError(null);
    try {
      setDetail(await sendMyTeamReport(teamId, reportId, chosen, shownVersion));
      setChosen([]);
    } catch (err) {
      setSendError(describeAndReport(SURFACE, "send the report", err));
      void getMyTeamReport(teamId, reportId).then((next) => {
        if (next !== null) setDetail(next);
      });
    } finally {
      setSending(false);
    }
  }, [teamId, reportId, chosen, shownVersion]);

  if (missing) return <Missing onBack={onBack} />;
  if (detail === null) return <ReportLoading error={error} onBack={onBack} />;

  return (
    <div className="team-report-open" data-testid="team-report-own-open">
      <DevReportViewer
        reportId={reportId}
        api={api}
        notes={detail.notesOpen === true}
        answers={detail.answersOpen === true}
        onNotFound={() => setMissing(true)}
        leading={<BackButton onBack={onBack} />}
        renderConversation={() => (
          <aside className="reports-conversation team-report-side" aria-label="Sharing and comments" data-testid="team-report-sharing">
            <div className="team-report-side-body">
              {error && <ErrorBanner message={error} />}
              <h2 className="team-report-side-heading">Comments from people</h2>
              {detail.comments.length === 0 && (
                <p className="team-report-note" data-testid="team-report-no-comments">
                  {detail.commentsEmptyText}
                </p>
              )}
              <ul className="team-report-comments">
                {detail.comments.map((c) => (
                  <li key={c.id} className="team-report-comment" data-testid="team-report-comment-from-person">
                    <span className="team-report-comment-who">{c.from}</span> <time dateTime={c.atUtc}>{dateAndTime(c.atUtc)}</time>
                    {c.aboutLabel !== null && (
                      <span className="team-report-comment-about" data-testid="team-report-comment-about">
                        {c.aboutLabel}
                      </span>
                    )}
                    <p>{c.text}</p>
                  </li>
                ))}
              </ul>

              <h2 className="team-report-side-heading">Sent to</h2>
              {detail.recipients.length === 0 && <p className="team-report-note">{detail.recipientsEmptyText}</p>}
              <ul className="team-report-recipients">
                {detail.recipients.map((r) => (
                  <li key={r.memberId} data-testid="team-report-recipient">
                    <span>{r.name}</span> <span className="team-report-recipient-read">{r.readLabel}</span>
                    {r.versionLabel !== null && (
                      <span className="team-report-recipient-version" data-testid="team-report-recipient-version">
                        {" "}
                        {r.versionLabel}
                      </span>
                    )}
                  </li>
                ))}
              </ul>

              {detail.choices.length > 0 && (
                <fieldset className="team-report-send" data-testid="team-report-send">
                  <legend className="team-report-side-heading">Send to</legend>
                  <p className="team-report-note">{detail.sendNote}</p>
                  {detail.choices.map((c) => (
                    <label key={c.memberId} className="team-report-choice">
                      <input
                        type="checkbox"
                        data-testid="team-report-choice"
                        value={c.memberId}
                        checked={chosen.includes(c.memberId)}
                        onChange={() => toggle(c.memberId)}
                      />{" "}
                      {c.name} <span className="team-report-choice-role">{c.role}</span>
                      {c.heldLabel !== null && <span className="team-report-choice-held"> {c.heldLabel}</span>}
                    </label>
                  ))}
                  {sendError && (
                    <div className="dev-report-error" role="alert" data-testid="team-report-send-error">
                      {sendError}
                    </div>
                  )}
                  <Button variant="primary" data-testid="team-report-send-button" disabled={sending || chosen.length === 0} onClick={() => void send()}>
                    {sending ? "Sending..." : "Send report"}
                  </Button>
                </fieldset>
              )}
            </div>
          </aside>
        )}
      />
    </div>
  );
}
