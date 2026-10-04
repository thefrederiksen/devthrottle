import { useEffect, useState } from "react";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import {
  getMentorPage,
  shiftWeek,
  type MentorAnswer,
  type MentorBlock,
  type MentorPage,
  type MentorReader,
} from "@devthrottle/client-core/teams/mentorClient";
import { Button, EmptyState, ErrorBanner, LoadingState, PageHeader } from "../components";
import { NotFound } from "../panes/NotFound";
import "./mentor.css";

// THE MENTOR'S WEEKLY PAGE for the current team (devthrottle_internal#2305, screens S6 and S7).
//
// CRITICAL RULE 7 - THE GATEWAY DECIDES WHO READS WHAT. An Owner or Manager gets every block of the week, a Developer
// gets only their own, a Collaborator gets a refusal - and all of that is in the Gateway's answer before it arrives.
// This page lays out whatever blocks it was sent, in the order it was sent them, through ONE block component: the
// block a Developer reads about themselves and the block their Manager reads about them are the same stored row, and
// drawing both with the same component is what keeps the words from differing on screen.
//
// WHAT IS NOT HERE, ON PURPOSE (owner rulings, 3 October 2026): no leaderboard, no ranking, no score, no count of lines
// of code, no sorting of people by how they did, and no link or control that browses anything else a person typed -
// only the prompts the Mentor quoted, shown as quotes, exactly as the Gateway sent them.
//
// A PERSON NOT ON A TEAM SEES NO CHANGE. With no team on this Gateway (or Teams not turned on) the address answers the
// Cockpit's ordinary "Page not found", exactly as it did before this page existed.

const MONTHS = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December",
];

/** "2026-09-28" -> "28 September". The Gateway's dates are calendar dates in the team's own time zone; they are
 *  shown as written, never moved through this browser's time zone. The client has checked they are real dates. */
function dayAndMonth(date: string): string {
  const [, month, day] = date.split("-").map(Number);
  return `${day} ${MONTHS[month - 1]}`;
}

/** The words for someone the contract lists with no email on record (Tech Lead ruling, review F1): their role and
 *  that plain fact - never a made-up name. */
function personLabel(email: string | null, role: string): string {
  return email ?? `${role} - no email on record`;
}

export function MentorView() {
  const { status, teams, current, resolving, error } = useCurrentTeam();

  if (status === "loading" || resolving) {
    if (status === "error" && error !== null) return <ErrorBanner message={error} />;
    return <LoadingState message="Loading the Mentor's page..." />;
  }
  if (status !== "ready" || teams.length === 0) return <NotFound />;
  if (current === null) {
    return (
      <section className="mentor-page">
        <PageHeader title="Mentor" />
        <EmptyState message="The Mentor writes about a team. Choose one of your teams at the top of the menu to read its Mentor page." />
      </section>
    );
  }
  // Keyed by the team, so switching team starts again at that team's most recent week.
  return <MentorTeamPage key={current.id} teamId={current.id} />;
}

/** The week being asked for. No week means the Gateway's own choice: the most recent one that has closed. */
interface WeekRequest {
  week?: string;
  /** Its Monday, when the page already knows it - so the chooser can name the week before the Gateway answers. */
  weekStart?: string;
}

type Read = { state: "loading" } | { state: "failed"; message: string } | { state: "answered"; answer: MentorAnswer };

function MentorTeamPage({ teamId }: { teamId: string }) {
  const [request, setRequest] = useState<WeekRequest>({});
  const [read, setRead] = useState<Read>({ state: "loading" });
  // The last page the Gateway sent, of any week: its scope and readers keep the heading in place while another week
  // loads or fails, so the week chooser never disappears from under the reader (review F5).
  const [lastPage, setLastPage] = useState<MentorPage | null>(null);
  // The Monday of the Gateway's own latest week: "Next week" stops there.
  const [latestStart, setLatestStart] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setRead({ state: "loading" });
    getMentorPage(teamId, request.week, controller.signal).then(
      (answer) => {
        if (controller.signal.aborted) return;
        setRead({ state: "answered", answer });
        if (answer.kind !== "page") return;
        setLastPage(answer.page);
        if (request.week === undefined) setLatestStart(answer.page.weekStart);
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        setRead({ state: "failed", message: gatewayErrorMessage(err, "read the Mentor page") });
      },
    );
    return () => controller.abort();
  }, [teamId, request, attempt]);

  // The Gateway refused (a Collaborator - the Mentor writes nothing about someone who runs no sessions) or knows no
  // such team for this person. Either way there is no Mentor page for them, so the address is an ordinary missing page.
  if (read.state === "answered" && read.answer.kind !== "page") return <NotFound />;

  const retry = () => setAttempt((n) => n + 1);
  const shownPage = read.state === "answered" && read.answer.kind === "page" ? read.answer.page : null;
  const weekStart = request.weekStart ?? shownPage?.weekStart ?? null;

  if (lastPage === null || weekStart === null) {
    // Nothing has been answered yet: there is no week to step from and no heading to keep.
    if (read.state === "failed") {
      return (
        <section className="mentor-page">
          <PageHeader title="Mentor" />
          <ErrorBanner message={read.message} onRetry={retry} />
        </section>
      );
    }
    return <LoadingState message="Loading the Mentor's page..." />;
  }

  const own = lastPage.scope === "own";
  const step = (weeks: number) => setRequest(shiftWeek(weekStart, weeks));

  return (
    <section className="mentor-page" data-testid="mentor-page">
      <PageHeader
        title={own ? "Your week, from the Mentor" : `Mentor - week of ${dayAndMonth(weekStart)}`}
        subtitle={own ? readersSentence(lastPage.readers) : "Each person reads their own block, word for word."}
      />

      <nav className="mentor-weeks" aria-label="Week">
        <Button onClick={() => step(-1)}>Previous week</Button>
        <span className="mentor-week-label" data-testid="mentor-week">
          {shownPage !== null
            ? `Week of ${dayAndMonth(shownPage.weekStart)} to ${dayAndMonth(shownPage.weekEnd)}`
            : `Week of ${dayAndMonth(weekStart)}`}
        </span>
        <Button onClick={() => step(1)} disabled={latestStart === null || weekStart >= latestStart}>
          Next week
        </Button>
      </nav>

      {read.state === "loading" && <LoadingState message="Loading the Mentor's page..." />}
      {read.state === "failed" && <ErrorBanner message={read.message} onRetry={retry} />}
      {shownPage !== null && <MentorWeek page={shownPage} />}

      <p className="mentor-footer" data-testid="mentor-footer">
        The Mentor is an AI. It wrote these blocks from the week&apos;s sessions. The quoted prompts are the only words
        here that a person typed, and they are copied exactly.
      </p>
    </section>
  );
}

/** How a reader relates to the person reading their own page (Tech Lead ruling, review F13): the Owner is "the team's
 *  Owner"; anyone else is "your" role. Only the WORDING turns on the label - who reads the page is the Gateway's list. */
function relation(reader: MentorReader): string {
  return reader.role === "Owner" ? "the team's Owner" : `your ${reader.role}`;
}

/** "priya@example.com, your Manager, reads this same page." - worded from the Gateway's list of who reads it, which
 *  the client has already refused when empty (review F3). */
export function readersSentence(readers: MentorReader[]): string {
  const named = (r: MentorReader) => personLabel(r.email, r.role);
  if (readers.length === 1) return `${named(readers[0])}, ${relation(readers[0])}, reads this same page.`;
  const each = readers.map((r) => `${named(r)} (${relation(r)})`);
  return `${each.slice(0, -1).join(", ")} and ${each[each.length - 1]} read this same page.`;
}

function MentorWeek({ page }: { page: MentorPage }) {
  if (!page.written) {
    return <EmptyState message="The Mentor has not written this week yet." />;
  }
  if (page.blocks.length === 0) {
    return (
      <EmptyState
        message={
          // The Gateway does not say WHY no block was written (no sessions, no prompts, or an answer it did not accept),
          // so the page does not either.
          page.scope === "own" ? "No block was written for you this week." : "No block was written for anyone this week."
        }
      />
    );
  }
  return (
    <div className="mentor-blocks">
      {page.blocks.map((block, index) => (
        // The Gateway's order is fixed for a week and a block has no identifier of its own on the page, so its place
        // in that order keys it.
        <MentorBlockCard key={index} block={block} />
      ))}
    </div>
  );
}

/** One person's week. The ONE drawing of a block - the Owner's, the Manager's and the person's own page all use it. */
export function MentorBlockCard({ block }: { block: MentorBlock }) {
  const who = personLabel(block.personEmail, block.role);
  return (
    <article className="mentor-block" data-testid="mentor-block" aria-label={who}>
      <header className="mentor-who">
        <span className="mentor-person">{who}</span>
        {/* With no email on record the role is already in the heading. */}
        {block.personEmail !== null && <span className="mentor-role">{block.role}</span>}
        <span className={`mentor-tone mentor-tone-${block.tone}`}>{block.toneLabel}</span>
      </header>

      <h3 className="mentor-hd">Worked on</h3>
      <p className="mentor-text">{block.workedOn}</p>

      {block.howItWent !== null && (
        <>
          <h3 className="mentor-hd">How it went</h3>
          <p className="mentor-text">{block.howItWent}</p>
        </>
      )}

      {block.wentBadlyAndWhy !== null && (
        <>
          <h3 className="mentor-hd">Where it went badly, and why</h3>
          <p className="mentor-text">{block.wentBadlyAndWhy}</p>
          {block.quotes.map((quote) => (
            <blockquote key={quote.promptId} className="mentor-quote" data-testid="mentor-quote">
              {quote.text}
            </blockquote>
          ))}
        </>
      )}

      <h3 className="mentor-hd">One thing to try next week</h3>
      <p className="mentor-text">{block.oneThingToTry}</p>
    </article>
  );
}
