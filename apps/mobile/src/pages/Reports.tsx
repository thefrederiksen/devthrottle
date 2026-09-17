import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { listSessions } from "@devthrottle/client-core/api/client";
import { DevReportList } from "@devthrottle/client-core/devreports/DevReportList";
import { SessionAppBar } from "../components/SessionAppBar";
import { useSessionManage } from "../components/useSessionManage";
import { ViewTabs } from "../components/ViewTabs";

// The phone's Reports screen (dev reports mission, phase 3): one session's dev reports. The list is the
// shared client-core view the Cockpit's Reports tab also mounts; this page is only the phone's frame - the
// session app bar, the view tabs, and opening a report as its own full-screen route.
export function Reports() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const navigate = useNavigate();
  const manage = useSessionManage(sessionId);
  const [name, setName] = useState<string | null>(null);

  // One-shot fetch of the session's display name for the header, as the other session screens do.
  useEffect(() => {
    const controller = new AbortController();
    listSessions(controller.signal)
      .then((all) => {
        const match = all.find((s) => s.sessionId === sessionId) ?? null;
        if (match?.name && match.name.trim()) setName(match.name.trim());
      })
      .catch(() => {
        /* header label is best-effort */
      });
    return () => controller.abort();
  }, [sessionId]);

  return (
    <div className="terminal-screen">
      <SessionAppBar title={name ?? "Session"} manage={manage} showSnooze showSwitchToVoice />
      <ViewTabs sessionId={sessionId} active="reports" />
      <div className="reports-stage">
        {sessionId && (
          <DevReportList
            sessionId={sessionId}
            onOpen={(report) =>
              navigate(`/session/${encodeURIComponent(sessionId)}/reports/${encodeURIComponent(report.id)}`)
            }
          />
        )}
      </div>
    </div>
  );
}
