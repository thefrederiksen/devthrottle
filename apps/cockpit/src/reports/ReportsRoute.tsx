import { useCallback } from "react";
import { useSearchParams } from "react-router-dom";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import { DevReportConversation } from "@devthrottle/client-core/devreports/DevReportConversation";
import { DevReportList } from "@devthrottle/client-core/devreports/DevReportList";
import { DevReportViewer } from "@devthrottle/client-core/devreports/DevReportViewer";
import { LoadingState, PageHeader } from "../components";
import { TeamPageRoute } from "../teams/collaborator/TeamPageRoute";
import { ReportsPage } from "../teams/collaborator/ReportsPage";

// REPORTS, IN WORK, FOR EVERYONE (owner, 8 Oct 2026: "The mentor and the reports could also be there for the
// individual account. Just in the same place."). One address, /reports, and which page it is follows what is on screen:
//
//   - a team: the team's Reports page, exactly as before - open only when the Gateway's page verdict for the team lists
//     it (TeamPageRoute, rule 7);
//   - the person's own account: every dev report their own sessions sent them, the same list the phone reads, newest
//     first as the Gateway sends it. A report opens in place (`?report=<id>`), with its conversation beside it, as in a
//     session's Reports tab.
export function ReportsRoute() {
  const { current, resolving, status } = useCurrentTeam();
  if (resolving || status === "loading") return <LoadingState message="Loading your team..." />;
  if (current !== null) {
    return (
      <TeamPageRoute pageId="reports">
        <ReportsPage />
      </TeamPageRoute>
    );
  }
  return <PersonalReportsPage />;
}

/** The person's own account: their sessions' reports. */
export function PersonalReportsPage() {
  const [params, setParams] = useSearchParams();
  const open = params.get("report");
  const backToList = useCallback(() => setParams({}), [setParams]);

  if (open === null) {
    return (
      <section className="pane personal-reports" data-testid="personal-reports">
        <PageHeader title="Reports" subtitle="What your sessions sent you when they finished something." />
        <div className="reports-tab" data-testid="reports-tab">
          <DevReportList sessionId={undefined} onOpen={(report) => setParams({ report: report.id })} />
        </div>
      </section>
    );
  }

  return (
    <div className="reports-tab reports-tab-open" data-testid="personal-report-open">
      <div className="reports-tab-body">
        <DevReportViewer
          key={open}
          reportId={open}
          onNotFound={backToList}
          leading={
            <button type="button" className="dev-report-action" data-testid="reports-back" onClick={backToList}>
              All reports
            </button>
          }
          trailing={
            <a
              className="dev-report-action"
              data-testid="reports-fullscreen"
              href={`/report/${encodeURIComponent(open)}`}
              target="_blank"
              rel="noopener noreferrer"
            >
              Full screen
            </a>
          }
          renderConversation={(conversation) => (
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
