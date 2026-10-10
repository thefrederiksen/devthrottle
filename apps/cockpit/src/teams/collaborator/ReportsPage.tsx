import { useCallback, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import {
  getMyTeamReports,
  getReportsSentToMe,
  type OwnReports,
  type ReceivedReports,
} from "@devthrottle/client-core/teams/teamReportsClient";
import { EmptyState, ErrorBanner, LoadingState, PageHeader } from "../../components";
import { OwnTeamReportView, ReceivedReportView } from "./TeamReportViews";
import { shortDate, TEAM_REPORTS_POLL_MS } from "./teamReportFormat";
import "./collaborator.css";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-team-reports";

// REPORTS (screen S10, devthrottle_internal#2309): the dev reports somebody on the team sent to this person, newest
// first - from whom and when, new or read. Open one to read it and comment on it; a comment goes to the person who
// wrote the report and never to an agent.
//
// A person who runs sessions in the team also sees THEIR OWN reports in the team here, below, and sends one to members
// from it. Whether that part is shown is the Gateway's answer (`showYourReports`, rule 7) - never this page's reading of
// the role.
//
// THE ADDRESS STAYS /reports. A report opens in place, named in the query (`?report=<id>` for one sent to you,
// `?yours=<id>` for one of your own), because only the exact page address is one of the team's pages: anything under it
// is "not available" in a Collaborator's Cockpit, and a phone goes to the mobile app there (devthrottle_internal#2306).

export function ReportsPage() {
  const { current } = useCurrentTeam();
  const [params, setParams] = useSearchParams();
  const backToList = useCallback(() => setParams({}), [setParams]);
  // TeamPageRoute mounts this page only inside a team whose verdict lists it.
  if (current === null) throw new Error("ReportsPage: rendered outside a team; TeamPageRoute should not have mounted it");
  const teamId = current.id;

  const received = params.get("report");
  const yours = params.get("yours");

  if (received !== null) return <ReceivedReportView key={`r-${received}`} teamId={teamId} reportId={received} onBack={backToList} />;
  if (yours !== null) return <OwnTeamReportView key={`y-${yours}`} teamId={teamId} reportId={yours} onBack={backToList} />;
  return <ReportLists key={teamId} teamId={teamId} open={(kind, id) => setParams({ [kind]: id })} />;
}

function ReportLists({ teamId, open }: { teamId: string; open: (kind: "report" | "yours", id: string) => void }) {
  const [received, setReceived] = useState<ReceivedReports | null>(null);
  const [own, setOwn] = useState<OwnReports | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(
    async (signal: AbortSignal) => {
      try {
        const next = await getReportsSentToMe(teamId, signal);
        setReceived(next);
        setOwn(next.showYourReports ? await getMyTeamReports(teamId, signal) : null);
        setError(null);
      } catch (err) {
        if (err instanceof Error && err.name === "AbortError") return;
        setError(describeAndReport(SURFACE, "load the reports", err));
      }
    },
    [teamId],
  );
  useVisiblePolling(refresh, TEAM_REPORTS_POLL_MS);

  return (
    <section className="pane team-page team-reports" data-testid="team-page-reports">
      <PageHeader title="Reports" subtitle="Sent to you by your team." />
      {error && <ErrorBanner message={error} />}
      {received === null && !error && <LoadingState message="Loading reports..." />}
      {received !== null && received.reports.length === 0 && <EmptyState message={received.emptyText} />}
      {received !== null && received.reports.length > 0 && (
        <ul className="team-report-list" data-testid="team-reports-received">
          {received.reports.map((r) => (
            <li key={r.id}>
              <button type="button" className="team-report-row" data-testid="team-report-row" data-report-id={r.id} onClick={() => open("report", r.id)}>
                <span className="team-report-row-main">
                  <span className="team-report-row-title">{r.title}</span>
                  <span className="team-report-row-meta">
                    From {r.from} <span aria-hidden="true">&middot;</span> <time dateTime={r.sentAtUtc}>{shortDate(r.sentAtUtc)}</time>
                  </span>
                  {r.questionsLabel !== null && (
                    <span className="team-report-row-questions" data-testid="team-report-questions-label">
                      {r.questionsLabel}
                    </span>
                  )}
                </span>
                <span className={r.read ? "team-report-pill team-report-pill-read" : "team-report-pill team-report-pill-new"} data-testid="team-report-read">
                  {r.readLabel}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}

      {own !== null && (
        <div className="team-reports-own" data-testid="team-reports-own">
          <h2 className="team-reports-heading">Your reports in this team</h2>
          {own.reports.length === 0 && <EmptyState message={own.emptyText} />}
          {own.reports.length > 0 && (
            <ul className="team-report-list">
              {own.reports.map((r) => (
                <li key={r.id}>
                  <button type="button" className="team-report-row" data-testid="team-report-own-row" data-report-id={r.id} onClick={() => open("yours", r.id)}>
                    <span className="team-report-row-main">
                      <span className="team-report-row-title">{r.title}</span>
                      <span className="team-report-row-meta">
                        {r.sentToLabel} <span aria-hidden="true">&middot;</span> <time dateTime={r.updatedAtUtc}>{shortDate(r.updatedAtUtc)}</time>
                      </span>
                    </span>
                    {r.commentsLabel !== null && <span className="team-report-pill team-report-pill-new">{r.commentsLabel}</span>}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </section>
  );
}
