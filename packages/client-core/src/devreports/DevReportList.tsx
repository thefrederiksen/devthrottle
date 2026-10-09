import { useCallback, useEffect, useRef, useState } from "react";
import { gatewayErrorMessage } from "../api/client";
import { useVisiblePolling } from "../polling/useVisiblePolling";
import { listDevReports, type DevReportSummary } from "./devReportsClient";
import "./devReports.css";

// One session's dev reports, shared by the Cockpit's Reports tab and the phone's Reports screen - or, with no session
// named, every report the account's sessions sent, for the Cockpit's Reports page on a person's own account. Every
// field is the Gateway's - title, status, version, updated time, open items - rendered as sent; this list
// decides nothing about what a status means. The shell supplies the frame and what opening a report does.
//
// ONE PAGE AT A TIME. The list reads the newest page and re-reads only that page while it is on screen; older reports
// come a page at a time when the person asks for them ("Show older reports"), and are not re-read. Reading the whole
// history on every refresh is what made the account's Reports page time out.

/** How often the list is re-read while it is on screen. There is no push for dev reports. */
export const DEV_REPORT_POLL_MS = 5000;

export interface DevReportListProps {
  /** The session whose reports to list; undefined for every report of the account, each naming its session. */
  sessionId: string | undefined;
  onOpen: (report: DevReportSummary) => void;
}

function formatTime(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

/** The Gateway's list order: newest update first, then session, then key, all ordinal. */
function byListOrder(a: DevReportSummary, b: DevReportSummary): number {
  const at = Date.parse(b.updatedAtUtc) - Date.parse(a.updatedAtUtc);
  if (at !== 0) return at;
  if (a.sessionId !== b.sessionId) return a.sessionId < b.sessionId ? -1 : 1;
  if (a.key !== b.key) return a.key < b.key ? -1 : 1;
  return 0;
}

/** Fold freshly read reports into those on screen: one row per report, the later version of a report winning, in the
 *  list's order. Used once older pages are on screen, so a report pushed off the newest page by a newer one stays where
 *  the person can see it instead of vanishing (older pages are never re-read). */
function fold(shown: DevReportSummary[], read: DevReportSummary[]): DevReportSummary[] {
  const byId = new Map(shown.map((r) => [r.id, r]));
  for (const r of read) {
    const kept = byId.get(r.id);
    if (kept === undefined || Date.parse(r.updatedAtUtc) >= Date.parse(kept.updatedAtUtc)) byId.set(r.id, r);
  }
  return [...byId.values()].sort(byListOrder);
}

export function DevReportList({ sessionId, onOpen }: DevReportListProps) {
  // The reports on screen: the newest page alone until the person asks for older ones, then everything read so far.
  const [reports, setReports] = useState<DevReportSummary[] | null>(null);
  // The newest page's marker for the page after it, and - once older pages are read - the last older page's.
  const [newestNext, setNewestNext] = useState<string | null>(null);
  const [olderNext, setOlderNext] = useState<string | null | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [olderError, setOlderError] = useState<string | null>(null);
  // Which list this is: bumped when the session changes, so an answer for the previous session is dropped, never shown.
  const generation = useRef(0);
  const olderOnScreen = useRef(false);

  // Another session's list starts from nothing: no reports, no markers, no older pages of the session before.
  useEffect(() => {
    generation.current += 1;
    olderOnScreen.current = false;
    setReports(null);
    setNewestNext(null);
    setOlderNext(undefined);
    setError(null);
    setOlderError(null);
    setLoadingOlder(false);
  }, [sessionId]);

  const refresh = useCallback(
    async (signal: AbortSignal) => {
      const asked = generation.current;
      try {
        const page = await listDevReports(sessionId, null, signal);
        if (asked !== generation.current) return;
        setReports((shown) => (olderOnScreen.current && shown !== null ? fold(shown, page.reports) : page.reports));
        setNewestNext(page.next);
        setError(null);
      } catch (err) {
        if (err instanceof Error && err.name === "AbortError") return;
        if (asked !== generation.current) return;
        setError(gatewayErrorMessage(err));
      }
    },
    [sessionId],
  );
  useVisiblePolling(refresh, DEV_REPORT_POLL_MS);

  // Where the next older page starts: after the last older page read, or after the newest page.
  const nextMarker = olderNext === undefined ? newestNext : olderNext;

  const showOlder = async () => {
    if (nextMarker === null) return;
    const asked = generation.current;
    // From the moment older reports are ASKED for, a refresh folds rather than replaces: a refresh that lands while this
    // page is in flight must not drop the report it pushes off the newest page (review round 2).
    olderOnScreen.current = true;
    setLoadingOlder(true);
    setOlderError(null);
    try {
      const page = await listDevReports(sessionId, nextMarker);
      if (asked !== generation.current) return;
      setReports((shown) => fold(shown ?? [], page.reports));
      setOlderNext(page.next);
    } catch (err) {
      if (asked !== generation.current) return;
      setOlderError(gatewayErrorMessage(err, "load older reports"));
    } finally {
      if (asked === generation.current) setLoadingOlder(false);
    }
  };

  return (
    <div className="dev-report-list" data-testid="dev-report-list">
      {error && (
        <div className="dev-report-error" role="alert">
          {error}
        </div>
      )}
      {reports === null && !error && <div className="dev-report-empty">Loading reports...</div>}
      {reports !== null && reports.length === 0 && (
        <div className="dev-report-empty" data-testid="dev-report-list-empty">
          {sessionId === undefined
            ? "No session has sent you a report yet. A session sends one when it finishes something you should read."
            : "This session has not published any reports."}
        </div>
      )}
      {reports !== null &&
        reports.map((report) => (
          <button
            key={report.id}
            type="button"
            className="dev-report-row"
            data-testid="dev-report-row"
            data-report-id={report.id}
            onClick={() => onOpen(report)}
          >
            <span className="dev-report-row-title" data-testid="dev-report-row-title">
              {report.title}
            </span>
            {/* The whole account's list names the session each report came from, in the Gateway's words. */}
            {sessionId === undefined && report.sessionLabel !== undefined && (
              <span className="dev-report-row-session" data-testid="dev-report-row-session">
                {report.sessionLabel}
              </span>
            )}
            <span className="dev-report-row-meta">
              <span className="dev-report-status" data-testid="dev-report-row-status">
                {report.status}
              </span>
              <span data-testid="dev-report-row-version">Version {report.version}</span>
              <time dateTime={report.updatedAtUtc} data-testid="dev-report-row-updated">
                {formatTime(report.updatedAtUtc)}
              </time>
              <span data-testid="dev-report-row-open-items">{report.openItems} open</span>
            </span>
          </button>
        ))}
      {reports !== null && nextMarker !== null && (
        <button
          type="button"
          className="dev-report-more"
          data-testid="dev-report-list-more"
          disabled={loadingOlder}
          onClick={() => void showOlder()}
        >
          {loadingOlder ? "Loading older reports..." : "Show older reports"}
        </button>
      )}
      {olderError !== null && (
        <div className="dev-report-error" role="alert" data-testid="dev-report-list-more-error">
          {olderError}
        </div>
      )}
    </div>
  );
}
