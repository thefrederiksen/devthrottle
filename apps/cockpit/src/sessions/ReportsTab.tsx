import { DevReportConversation } from "@devthrottle/client-core/devreports/DevReportConversation";
import { DevReportList } from "@devthrottle/client-core/devreports/DevReportList";
import { DevReportViewer } from "@devthrottle/client-core/devreports/DevReportViewer";

// The Cockpit Reports tab (dev reports mission, phase 3): the selected session's dev reports, and the open
// report with its conversation beside it. Everything that is not this frame - the list, the report frame and
// its trust rules, the conversation, the Gateway calls - lives once in client-core and is shared with the
// phone. This file only decides the desktop layout.
//
// WHICH REPORT IS OPEN IS NOT THIS COMPONENT'S TO REMEMBER (phase 3b). It was component state, and state a
// link cannot reach is a report a link cannot open. SessionDetail reads it from the address and hands it
// down, so `/session/{sid}?tab=reports&report={rid}` lands straight in the report.

export interface ReportsTabProps {
  sessionId: string | undefined;
  /** The report the address says is open, or null for the list. */
  openReportId: string | null;
  onOpenReport: (reportId: string) => void;
  onCloseReport: () => void;
  /** Leave the report and go to the session it came from, in this app. */
  onBackToSession: (sessionId: string) => void;
}

export function ReportsTab({ sessionId, openReportId, onOpenReport, onCloseReport, onBackToSession }: ReportsTabProps) {
  if (!sessionId) return null;

  if (openReportId === null) {
    return (
      <div className="reports-tab" data-testid="reports-tab">
        <DevReportList sessionId={sessionId} onOpen={(report) => onOpenReport(report.id)} />
      </div>
    );
  }

  return (
    <div className="reports-tab reports-tab-open" data-testid="reports-tab">
      <div className="reports-tab-bar">
        <button type="button" className="reports-back" data-testid="reports-back" onClick={onCloseReport}>
          All reports
        </button>
        {/* THE SAME REPORT WITH NOTHING AROUND IT (issue #3074). A real link to a real address, not a button
            that opens a panel: the reader can copy it, and it is the same address the printed link resolves
            to - so what he opens here is exactly what the person he sends it to will see. A new tab, because
            leaving the session behind is not what he asked for. */}
        <a
          className="reports-fullscreen"
          data-testid="reports-fullscreen"
          href={`/report/${encodeURIComponent(openReportId)}`}
          target="_blank"
          rel="noopener noreferrer"
        >
          Full screen
        </a>
      </div>
      <div className="reports-tab-body">
        <DevReportViewer
          key={openReportId}
          reportId={openReportId}
          onNotFound={onCloseReport}
          onBackToSession={onBackToSession}
          renderConversation={(conversation) => (
            /* THE one conversation on this screen now that the hosted page draws none: this heading names it,
               and the Send below is the only Send anywhere on the screen. */
            <aside className="reports-conversation" aria-label="Conversation">
              <h2 className="reports-conversation-title">Conversation</h2>
              <DevReportConversation conversation={conversation} />
            </aside>
          )}
        />
      </div>
    </div>
  );
}
