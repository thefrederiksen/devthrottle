import { useCallback, useEffect, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { GatewayError, gatewayErrorMessage } from "../api/client";
import { useVisiblePolling } from "../polling/useVisiblePolling";
import { DevReportController, type DevReportApi, type DevReportSnapshot } from "./controller";
import type { DevReportConversationModel } from "./DevReportConversation";
import { DEV_REPORT_POLL_MS } from "./DevReportList";
import { getDevReport, getDevReportHtml, sendDevReportItems } from "./devReportsClient";
import { reportFileName, saveHtmlFile } from "./exportReport";
import { DEV_REPORT_NOTES_SCRIPT } from "./notesScript";
import { DevReportStateStore } from "./stateStore";
import { APP_DEV_REPORT_THEME } from "./theme";
import "./devReports.css";

// One dev report, shared by the Cockpit and the phone: the report frame (through the framework-free host and
// controller) and a slot for the conversation. The shell decides where the conversation goes - beside the
// report on the desktop, in a bottom sheet on the phone - and supplies every bit of page chrome.

export const gatewayDevReportApi: DevReportApi = {
  getDetail: getDevReport,
  getHtml: getDevReportHtml,
  send: sendDevReportItems,
};

export interface DevReportViewerProps {
  reportId: string;
  /** Where the conversation goes. Given the model; render DevReportConversation in the shell's own frame. */
  renderConversation: (conversation: DevReportConversationModel, snapshot: DevReportSnapshot) => ReactNode;
  /** The Gateway answered 404 for this report: it does not appear. The shell usually goes back to the list. */
  onNotFound?: () => void;
  /**
   * ONE BAR OVER THE REPORT (issue #3077). The shell's own actions go in the report's own bar rather than in
   * a second strip above it: `leading` is the way out of this report (the Cockpit's "All reports"), `trailing`
   * sits with Export at the right (the Cockpit's "Full screen"). A shell that passes neither - the phone,
   * which has an app bar of its own - gets the bar exactly as it was.
   */
  leading?: ReactNode;
  trailing?: ReactNode;
  /**
   * Take the reader back to the session this report came from, in this app. The viewer draws the link and
   * the Gateway supplies its words (`backLabel`); the shell only knows how to navigate. Without this - or
   * without the Gateway's words - there is no back link at all, because the alternative is inventing one.
   */
  onBackToSession?: (sessionId: string) => void;
}

const idleSubscribe = () => () => {};

/** A string the Gateway actually sent, or null. Whitespace is not words. */
function nonEmpty(value: string | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed ? trimmed : null;
}

const idleSnapshot: DevReportSnapshot = {
  detail: null,
  notFound: false,
  loadError: null,
  loadedVersion: null,
  pageState: { queued: [], sent: [], replies: [], draft: null, answerDrafts: [], scroll: { x: 0, y: 0 } },
  connected: false,
  sending: false,
  sendError: null,
  noteMode: { picking: false, selectionQuote: null },
};

export function DevReportViewer({ reportId, renderConversation, onNotFound, onBackToSession, leading, trailing }: DevReportViewerProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const [controller, setController] = useState<DevReportController | null>(null);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) throw new Error("DevReportViewer: the frame container was not rendered");
    const next = new DevReportController({
      reportId,
      api: gatewayDevReportApi,
      store: new DevReportStateStore(window.localStorage),
      container,
      window,
      script: DEV_REPORT_NOTES_SCRIPT,
      theme: APP_DEV_REPORT_THEME,
      onRefused: (why) => console.warn(`[DevReportViewer] report ${reportId}: refused a frame message - ${why}`),
    });
    setController(next);
    return () => {
      next.dispose();
      setController(null);
    };
  }, [reportId]);

  const snapshot = useSyncExternalStore(controller?.subscribe ?? idleSubscribe, controller?.getSnapshot ?? (() => idleSnapshot));

  // EXPORT: the report's bytes, saved as one HTML file (see exportReport.ts for what that file is and is
  // not). The frame already holds a copy of those bytes, but it is a sandboxed frame with an opaque origin,
  // so this page cannot read them back out of it - the export asks the Gateway for the same version the
  // frame is showing. A failed export SAYS SO, in the Gateway's own words where there are any; a button that
  // reports nothing reads as broken.
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);
  const exportVersion = snapshot.loadedVersion ?? snapshot.detail?.report.version ?? null;
  const exportReport = useCallback(async () => {
    const detail = snapshot.detail;
    if (!detail || exportVersion === null) return;
    setExporting(true);
    setExportError(null);
    try {
      const page = await getDevReportHtml(reportId, exportVersion);
      if (!page) {
        setExportError("This report does not appear any more, so there was nothing to save.");
        return;
      }
      saveHtmlFile(window.document, reportFileName(detail.report.title, page.version), page.html);
    } catch (err) {
      setExportError(
        err instanceof GatewayError
          ? gatewayErrorMessage(err, "save the report")
          : `The report could not be saved${err instanceof Error && err.message ? ` (${err.message})` : ""}.`,
      );
    } finally {
      setExporting(false);
    }
  }, [reportId, snapshot.detail, exportVersion]);

  const refresh = useCallback((signal: AbortSignal) => (controller ? controller.refresh(signal) : undefined), [controller]);
  useVisiblePolling(refresh, DEV_REPORT_POLL_MS);

  useEffect(() => {
    if (snapshot.notFound) onNotFound?.();
  }, [snapshot.notFound, onNotFound]);

  // The Gateway's two finished strings, rendered verbatim or not at all (repository rule 7). A report record
  // from an older Gateway carries neither, and then there is no back link and no session line - never an
  // identifier, and never a sentence this file composed.
  const sessionLabel = nonEmpty(snapshot.detail?.report.sessionLabel);
  const backLabel = nonEmpty(snapshot.detail?.report.backLabel);

  const conversation: DevReportConversationModel = {
    queued: snapshot.pageState.queued,
    sent: snapshot.detail?.items ?? [],
    replies: snapshot.detail?.replies ?? [],
    sending: snapshot.sending,
    sendError: snapshot.sendError,
    send: () => void controller?.sendQueued(),
    noteMode: snapshot.noteMode,
    setNoteMode: (mode) => controller?.setNoteMode(mode),
    connected: snapshot.connected,
  };

  return (
    <div className="dev-report-viewer" data-testid="dev-report-viewer" data-connected={snapshot.connected ? "true" : "false"}>
      <div className="dev-report-viewer-main">
        {snapshot.detail && (
          <div className="dev-report-viewer-bar">
            {leading}
            {backLabel && onBackToSession && (
              <button
                type="button"
                className="dev-report-action dev-report-back"
                data-testid="dev-report-back"
                onClick={() => onBackToSession(snapshot.detail!.report.sessionId)}
              >
                {backLabel}
              </button>
            )}
            <span className="dev-report-viewer-title">{snapshot.detail.report.title}</span>
            {sessionLabel && (
              <span className="dev-report-viewer-session" data-testid="dev-report-session-label">
                {sessionLabel}
              </span>
            )}
            <span className="dev-report-status">{snapshot.detail.report.status}</span>
            <span className="dev-report-viewer-version" data-testid="dev-report-version">
              Version {snapshot.loadedVersion ?? snapshot.detail.report.version}
            </span>
            {/* The bar's actions, together at its right end: the shell's own (the Cockpit's Full screen) and
                Export, which saves this report as one HTML file and belongs to BOTH surfaces because it is
                the report's action rather than any one shell's chrome. */}
            <div className="dev-report-bar-end">
              {trailing}
              <button
                type="button"
                className="dev-report-action dev-report-export"
                data-testid="dev-report-export"
                onClick={() => void exportReport()}
                disabled={exporting}
              >
                {exporting ? "Saving..." : "Export HTML"}
              </button>
            </div>
          </div>
        )}
        {exportError && (
          <div className="dev-report-error" role="alert" data-testid="dev-report-export-error">
            {exportError}
          </div>
        )}
        {snapshot.loadError && (
          <div className="dev-report-error" role="alert">
            {snapshot.loadError}
          </div>
        )}
        {!snapshot.detail && !snapshot.loadError && !snapshot.notFound && <div className="dev-report-empty">Loading report...</div>}
        <div className="dev-report-frame-box" ref={containerRef} />
      </div>
      {snapshot.detail && renderConversation(conversation, snapshot)}
    </div>
  );
}
