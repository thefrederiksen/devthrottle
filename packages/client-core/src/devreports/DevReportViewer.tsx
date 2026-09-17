import { useCallback, useEffect, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { useVisiblePolling } from "../polling/useVisiblePolling";
import { DevReportController, type DevReportApi, type DevReportSnapshot } from "./controller";
import type { DevReportConversationModel } from "./DevReportConversation";
import { DEV_REPORT_POLL_MS } from "./DevReportList";
import { getDevReport, getDevReportHtml, sendDevReportItems } from "./devReportsClient";
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
}

const idleSubscribe = () => () => {};
const idleSnapshot: DevReportSnapshot = {
  detail: null,
  notFound: false,
  loadError: null,
  loadedVersion: null,
  pageState: { queued: [], sent: [], replies: [], draft: null, answerDrafts: [], scroll: { x: 0, y: 0 } },
  connected: false,
  sending: false,
  sendError: null,
};

export function DevReportViewer({ reportId, renderConversation, onNotFound }: DevReportViewerProps) {
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

  const refresh = useCallback((signal: AbortSignal) => (controller ? controller.refresh(signal) : undefined), [controller]);
  useVisiblePolling(refresh, DEV_REPORT_POLL_MS);

  useEffect(() => {
    if (snapshot.notFound) onNotFound?.();
  }, [snapshot.notFound, onNotFound]);

  const conversation: DevReportConversationModel = {
    queued: snapshot.pageState.queued,
    sent: snapshot.detail?.items ?? [],
    replies: snapshot.detail?.replies ?? [],
    sending: snapshot.sending,
    sendError: snapshot.sendError,
    send: () => void controller?.sendQueued(),
  };

  return (
    <div className="dev-report-viewer" data-testid="dev-report-viewer" data-connected={snapshot.connected ? "true" : "false"}>
      <div className="dev-report-viewer-main">
        {snapshot.detail && (
          <div className="dev-report-viewer-bar">
            <span className="dev-report-viewer-title">{snapshot.detail.report.title}</span>
            <span className="dev-report-status">{snapshot.detail.report.status}</span>
            <span className="dev-report-viewer-version" data-testid="dev-report-version">
              Version {snapshot.loadedVersion ?? snapshot.detail.report.version}
            </span>
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
