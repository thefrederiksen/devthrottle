import { useCallback, useEffect, useState } from "react";
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

  // The way back to the session this report came from (phase 3b). Its words are the Gateway's; this page only
  // knows where the session lives on the phone, which is its default view.
  const backToSession = useCallback(
    (reportSessionId: string) => navigate(`/session/${encodeURIComponent(reportSessionId)}`),
    [navigate],
  );

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
          onBackToSession={backToSession}
          renderConversation={(conversation) => (
            <>
              <ArmedNote picking={conversation.noteMode.picking} onArmed={() => setSheetOpen(false)} />
              <button
                type="button"
                className="report-conversation-strip"
                data-testid="report-conversation-open"
                onClick={() => setSheetOpen(true)}
              >
                {/* ARMED, THE STRIP SAYS WHAT TO DO (issue #3077). The note controls are in the sheet now, and
                    the sheet has just got out of the way so the reader can reach the report - so this line,
                    the only thing left on screen beside the report, carries the instruction. */}
                {conversation.noteMode.picking ? (
                  "Tap the paragraph, table cell or diagram part your note is about"
                ) : (
                  <>
                    Conversation - {conversation.queued.length} queued, {conversation.sent.length} sent,{" "}
                    {conversation.replies.length} {conversation.replies.length === 1 ? "reply" : "replies"}
                  </>
                )}
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

/**
 * THE SHEET GETS OUT OF THE WAY WHEN A NOTE IS ARMED (issue #3077).
 *
 * The note controls live in the app's panel, which on a phone is a sheet over the report - so pressing "Add a
 * note" in it leaves the reader looking at the panel they must now click THROUGH. The desktop has no such
 * problem (its panel is beside the report), which is exactly why this belongs to the phone's frame and not to
 * the shared panel. A component rather than an effect in the page, because the picking flag arrives in the
 * viewer's render callback.
 */
function ArmedNote({ picking, onArmed }: { picking: boolean; onArmed: () => void }) {
  useEffect(() => {
    if (picking) onArmed();
  }, [picking, onArmed]);
  return null;
}
