import { useCallback, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { DevReportConversation } from "@devthrottle/client-core/devreports/DevReportConversation";
import { DevReportViewer } from "@devthrottle/client-core/devreports/DevReportViewer";

// ONE DEV REPORT, FULL SCREEN, AT ITS OWN ADDRESS (issue #3074): `/report/{report id}`.
//
// This address used to be a doorway - it read the report, learned its session, and replaced itself with that
// session's Reports tab. Inside the tab the report had a rail, a session list, a tab strip and a queue dock
// around it, and about 700 pixels of a 1920 screen left for itself. That is the wrong screen for the two
// readers this address exists for:
//
//   - the reader the printed `<gateway>/r/{report id}` link was sent to, who does NOT have the session. The
//     session's Reports tab is a place they have no reason to be.
//   - the owner reading a long report, who wants the report and nothing else.
//
// So the address is the report now. The page is the shared viewer with the conversation beside it, and no
// app chrome at all: the route is mounted OUTSIDE the AppShell (routes.tsx) rather than inside it, which is
// what actually removes the rail - a full-width page under the shell is still a page with a rail.
//
// Nothing was lost by the change. The way back into the session is the viewer's own back link, in the
// Gateway's words, for a reader who has that session; a reader who does not never sees a screen built for
// somebody else's fleet. And it is still INSIDE the gate, so signed out it still goes through
// /signin?next=/report/{id}.
export function ReportPage() {
  const { reportId } = useParams<{ reportId: string }>();
  const navigate = useNavigate();
  const [notFound, setNotFound] = useState(false);

  // Back into the app, at this report inside its session - the one screen that has the rest of the session
  // around it. The Gateway supplies the words on the link; this page only knows where that screen lives.
  const backToSession = useCallback(
    (sessionId: string) => {
      const report = reportId === undefined ? "" : `?tab=reports&report=${encodeURIComponent(reportId)}`;
      navigate(`/session/${encodeURIComponent(sessionId)}${report}`);
    },
    [navigate, reportId],
  );

  if (reportId === undefined || notFound) {
    return (
      <div className="report-page report-page-missing">
        <p className="dev-report-empty" data-testid="report-page-missing">
          This report does not appear. It may have been removed, or the address may be wrong.
        </p>
      </div>
    );
  }

  return (
    <div className="report-page" data-testid="report-page">
      <DevReportViewer
        key={reportId}
        reportId={reportId}
        onNotFound={() => setNotFound(true)}
        onBackToSession={backToSession}
        renderConversation={(conversation) => (
          // The same conversation as the Reports tab, in the same place: this page is the tab with the
          // chrome taken away, not a second design.
          <aside className="reports-conversation" aria-label="Conversation">
            <h2 className="reports-conversation-title">Conversation</h2>
            <DevReportConversation conversation={conversation} />
          </aside>
        )}
      />
    </div>
  );
}
