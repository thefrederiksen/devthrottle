import { EmptyState, PageHeader } from "../../components";
import "./collaborator.css";

// REPORTS (screen S10, devthrottle_internal#2306): the SLOT for devthrottle_internal#2309, which lists the reports sent
// to this person. The navigation and the route guard never need to change for it.
export function ReportsPage() {
  return (
    <section className="pane team-page" data-testid="team-page-reports">
      <PageHeader title="Reports" subtitle="Sent to you by your team." />
      <EmptyState message="No reports sent to you yet." />
    </section>
  );
}
