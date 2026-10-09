import { useCallback, useState } from "react";
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

export function DevReportList({ sessionId, onOpen }: DevReportListProps) {
  // The newest page, re-read on every refresh, and its marker for the page after it.
  const [newest, setNewest] = useState<DevReportSummary[] | null>(null);
  const [newestNext, setNewestNext] = useState<string | null>(null);
  // The older pages the person asked for, kept as read, and the marker for the page after the last of them.
  const [older, setOlder] = useState<DevReportSummary[]>([]);
  const [olderNext, setOlderNext] = useState<string | null | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [olderError, setOlderError] = useState<string | null>(null);

  const refresh = useCallback(
    async (signal: AbortSignal) => {
      try {
        const page = await listDevReports(sessionId, null, signal);
        setNewest(page.reports);
        setNewestNext(page.next);
        setError(null);
      } catch (err) {
        if (err instanceof Error && err.name === "AbortError") return;
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
    setLoadingOlder(true);
    setOlderError(null);
    try {
      const page = await listDevReports(sessionId, nextMarker);
      setOlder((kept) => [...kept, ...page.reports]);
      setOlderNext(page.next);
    } catch (err) {
      setOlderError(gatewayErrorMessage(err, "load older reports"));
    } finally {
      setLoadingOlder(false);
    }
  };

  // A report updated since an older page was read moves into the newest page; it is shown once, where it is newest.
  const reports = newest === null
    ? null
    : [...newest, ...older.filter((o) => !newest.some((n) => n.id === o.id))];

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
