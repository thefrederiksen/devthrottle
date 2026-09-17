import { useCallback, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { DevReportConversation } from "@devthrottle/client-core/devreports/DevReportConversation";
import { DevReportViewer } from "@devthrottle/client-core/devreports/DevReportViewer";

// One dev report, full screen on the phone (dev reports mission, phase 3). The report frame and the
// conversation are the shared client-core view; this page is the phone's frame around them: a Back button
// to the session's reports, the report filling the screen, and the conversation - queued, sent, replies and
// Send - in a bottom sheet opened from the strip under the report.
export function ReportView() {
  const { sessionId, reportId } = useParams<{ sessionId: string; reportId: string }>();
  const navigate = useNavigate();
  const [sheetOpen, setSheetOpen] = useState(false);

  const backToList = useCallback(() => {
    navigate(`/session/${encodeURIComponent(sessionId ?? "")}/reports`, { replace: true });
  }, [navigate, sessionId]);

  return (
    <div className="terminal-screen report-screen">
      <header className="app-bar">
        <button type="button" className="file-view-back" data-testid="report-back" onClick={backToList}>
          Back
        </button>
        <h1 className="term-title">Report</h1>
      </header>
      {reportId && (
        <DevReportViewer
          key={reportId}
          reportId={reportId}
          onNotFound={backToList}
          renderConversation={(conversation) => (
            <>
              <button
                type="button"
                className="report-conversation-strip"
                data-testid="report-conversation-open"
                onClick={() => setSheetOpen(true)}
              >
                Conversation - {conversation.queued.length} queued, {conversation.sent.length} sent,{" "}
                {conversation.replies.length} {conversation.replies.length === 1 ? "reply" : "replies"}
              </button>
              {sheetOpen && (
                <div className="report-sheet-overlay" onClick={() => setSheetOpen(false)}>
                  <div
                    className="report-sheet"
                    role="dialog"
                    aria-modal="true"
                    aria-label="Conversation"
                    data-testid="report-conversation-sheet"
                    onClick={(e) => e.stopPropagation()}
                  >
                    <div className="report-sheet-head">
                      <span className="report-sheet-title">Conversation</span>
                      <button
                        type="button"
                        className="file-view-back"
                        data-testid="report-conversation-close"
                        onClick={() => setSheetOpen(false)}
                      >
                        Close
                      </button>
                    </div>
                    <div className="report-sheet-body">
                      <DevReportConversation conversation={conversation} />
                    </div>
                  </div>
                </div>
              )}
            </>
          )}
        />
      )}
    </div>
  );
}
