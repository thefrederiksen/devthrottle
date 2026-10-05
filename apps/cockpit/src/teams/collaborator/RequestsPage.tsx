import { EmptyState, PageHeader } from "../../components";
import "./collaborator.css";

// REQUESTS (screen S9, devthrottle_internal#2306): the SLOT for devthrottle_internal#2308, which adds the request form
// and what happened to each request. The navigation and the route guard never need to change for it.
export function RequestsPage() {
  return (
    <section className="pane team-page" data-testid="team-page-requests">
      <PageHeader title="Requests" subtitle="Ask for something. A Manager reads it and decides." />
      <EmptyState message="No requests from you yet." />
    </section>
  );
}
