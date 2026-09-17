import { useCallback, useState } from "react";
import { gatewayErrorMessage } from "../api/client";
import { useVisiblePolling } from "../polling/useVisiblePolling";
import { listDevReports, type DevReportSummary } from "./devReportsClient";
import "./devReports.css";

// One session's dev reports, shared by the Cockpit's Reports tab and the phone's Reports screen. Every
// field is the Gateway's - title, status, version, updated time, open items - rendered as sent; this list
// decides nothing about what a status means. The shell supplies the frame and what opening a report does.

/** How often the list is re-read while it is on screen. There is no push for dev reports. */
export const DEV_REPORT_POLL_MS = 5000;

export interface DevReportListProps {
  sessionId: string;
  onOpen: (report: DevReportSummary) => void;
}

function formatTime(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

export function DevReportList({ sessionId, onOpen }: DevReportListProps) {
  const [reports, setReports] = useState<DevReportSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(
    async (signal: AbortSignal) => {
      try {
        const next = await listDevReports(sessionId, signal);
        setReports(next);
        setError(null);
      } catch (err) {
        if (err instanceof Error && err.name === "AbortError") return;
        setError(gatewayErrorMessage(err));
      }
    },
    [sessionId],
  );
  useVisiblePolling(refresh, DEV_REPORT_POLL_MS);

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
          This session has not published any reports.
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
    </div>
  );
}
