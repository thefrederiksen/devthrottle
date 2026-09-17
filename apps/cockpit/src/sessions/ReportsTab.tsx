import { useEffect, useState } from "react";
import { DevReportConversation } from "@devthrottle/client-core/devreports/DevReportConversation";
import { DevReportList } from "@devthrottle/client-core/devreports/DevReportList";
import { DevReportViewer } from "@devthrottle/client-core/devreports/DevReportViewer";

// The Cockpit Reports tab (dev reports mission, phase 3): the selected session's dev reports, and the open
// report with its conversation beside it. Everything that is not this frame - the list, the report frame and
// its trust rules, the conversation, the Gateway calls - lives once in client-core and is shared with the
// phone. This file only decides the desktop layout and what opening and closing a report does.

export function ReportsTab({ sessionId }: { sessionId: string | undefined }) {
  const [openReportId, setOpenReportId] = useState<string | null>(null);

  // A different session never shows the previous session's open report.
  useEffect(() => setOpenReportId(null), [sessionId]);

  if (!sessionId) return null;

  if (openReportId === null) {
    return (
      <div className="reports-tab" data-testid="reports-tab">
        <DevReportList sessionId={sessionId} onOpen={(report) => setOpenReportId(report.id)} />
      </div>
    );
  }

  return (
    <div className="reports-tab reports-tab-open" data-testid="reports-tab">
      <div className="reports-tab-bar">
        <button type="button" className="reports-back" data-testid="reports-back" onClick={() => setOpenReportId(null)}>
          All reports
        </button>
      </div>
      <div className="reports-tab-body">
        <DevReportViewer
          key={openReportId}
          reportId={openReportId}
          onNotFound={() => setOpenReportId(null)}
          renderConversation={(conversation) => (
            <aside className="reports-conversation">
              <DevReportConversation conversation={conversation} />
            </aside>
          )}
        />
      </div>
    </div>
  );
}
