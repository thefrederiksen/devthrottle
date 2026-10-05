import { EmptyState, PageHeader } from "../../components";
import "./collaborator.css";

// QUESTIONS (screen S8, devthrottle_internal#2306): where a Collaborator lands. This is the SLOT - the shell, its
// route and its navigation item are #2306's; what fills the page (the questions waiting on this person, and their
// answers) is devthrottle_internal#2307, written here and nowhere else. The navigation and the route guard never need
// to change for it.
export function QuestionsPage() {
  return (
    <section className="pane team-page" data-testid="team-page-questions">
      <PageHeader title="Questions" subtitle="Questions your team is waiting on you to answer." />
      <EmptyState message="No questions waiting on you." />
    </section>
  );
}
