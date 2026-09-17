import { useCallback } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { DevReportLanding } from "@devthrottle/client-core/devreports/DevReportLanding";

// The Cockpit's report landing (dev reports mission, phase 3b): `/report/{report id}`, which is where the
// printed address `<gateway>/r/<report id>` sends anything that is not a phone. It is the ONE route that
// needs nothing but a report id - the Gateway cannot look the report up, because that route has to answer
// with nobody signed in.
//
// The shared landing does the reading and the saying; this page only knows where a report lives in the
// Cockpit, which is the Reports tab of its session with that report open. REPLACE, not push: this address is
// a doorway, and the browser's Back should go to wherever the reader came from rather than to a screen whose
// only job is to forward them again.
export function ReportLanding() {
  const { reportId } = useParams<{ reportId: string }>();
  const navigate = useNavigate();

  const onFound = useCallback(
    (sessionId: string, foundReportId: string) => {
      navigate(
        `/session/${encodeURIComponent(sessionId)}?tab=reports&report=${encodeURIComponent(foundReportId)}`,
        { replace: true },
      );
    },
    [navigate],
  );

  return <DevReportLanding reportId={reportId} onFound={onFound} />;
}
