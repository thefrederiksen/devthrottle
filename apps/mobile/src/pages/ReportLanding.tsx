import { useCallback } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { DevReportLanding } from "@devthrottle/client-core/devreports/DevReportLanding";

// The phone's report landing (dev reports mission, phase 3b): `/mobile/report/{report id}`, which is where
// the printed address `<gateway>/r/<report id>` sends a phone. It is the ONE route that needs nothing but a
// report id - the Gateway cannot look the report up, because that route has to answer with nobody signed in.
//
// The shared landing does the reading and the saying; this page only knows where a report lives on the
// phone, which is the full-screen report screen inside its session. REPLACE, not push: this address is a
// doorway, and Back from the report should leave the app rather than return to a screen that only forwards.
export function ReportLanding() {
  const { reportId } = useParams<{ reportId: string }>();
  const navigate = useNavigate();

  const onFound = useCallback(
    (sessionId: string, foundReportId: string) => {
      navigate(
        `/session/${encodeURIComponent(sessionId)}/reports/${encodeURIComponent(foundReportId)}`,
        { replace: true },
      );
    },
    [navigate],
  );

  return (
    <div className="terminal-screen">
      <header className="app-bar">
        <h1 className="term-title">Report</h1>
      </header>
      <DevReportLanding reportId={reportId} onFound={onFound} />
    </div>
  );
}
