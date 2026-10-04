import { useEffect, useState } from "react";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import {
  getMentorPage,
  weekAfter,
  weekBefore,
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
 *  shown as written, never moved through this browser's time zone. */
function dayAndMonth(date: string): string {
  const [, month, day] = date.split("-").map(Number);
  return `${day} ${MONTHS[month - 1]}`;
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

function MentorTeamPage({ teamId }: { teamId: string }) {
  // undefined asks the Gateway for its own choice of week: the most recent one that has closed in the team's time zone.
  const [week, setWeek] = useState<string | undefined>(undefined);
  const [latestWeek, setLatestWeek] = useState<string | null>(null);
  const [answer, setAnswer] = useState<MentorAnswer | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setAnswer(null);
    setLoadError(null);
    getMentorPage(teamId, week, controller.signal).then(
      (result) => {
        if (controller.signal.aborted) return;
        setAnswer(result);
        if (week === undefined && result.kind === "page") setLatestWeek(result.page.week);
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        setLoadError(gatewayErrorMessage(err, "read the Mentor page"));
      },
    );
    return () => controller.abort();
  }, [teamId, week, attempt]);

  if (loadError !== null) {
    return (
      <section className="mentor-page">
        <PageHeader title="Mentor" />
        <ErrorBanner message={loadError} onRetry={() => setAttempt((n) => n + 1)} />
      </section>
    );
  }
  if (answer === null) return <LoadingState message="Loading the Mentor's page..." />;
  // The Gateway refused (a Collaborator - the Mentor writes nothing about someone who runs no sessions) or knows no
  // such team for this person. Either way there is no Mentor page for them, so the address is an ordinary missing page.
  if (answer.kind !== "page") return <NotFound />;

  const page = answer.page;
  const own = page.scope === "own";
  const isLatest = latestWeek !== null && page.week === latestWeek;

  return (
    <section className="mentor-page" data-testid="mentor-page">
      <PageHeader
        title={own ? "Your week, from the Mentor" : `Mentor - week of ${dayAndMonth(page.weekStart)}`}
        subtitle={own ? readersSentence(page.readers) : "Each person reads their own block, word for word."}
      />

      <nav className="mentor-weeks" aria-label="Week">
        <Button onClick={() => setWeek(weekBefore(page.weekStart))}>Previous week</Button>
        <span className="mentor-week-label" data-testid="mentor-week">
          {`Week of ${dayAndMonth(page.weekStart)} to ${dayAndMonth(page.weekEnd)}`}
        </span>
        <Button onClick={() => setWeek(weekAfter(page.weekStart))} disabled={isLatest || latestWeek === null}>
          Next week
        </Button>
      </nav>

      <MentorWeek page={page} />

      <p className="mentor-footer" data-testid="mentor-footer">
        The Mentor is an AI. It wrote these blocks from the week&apos;s sessions. The quoted prompts are the only words
        here that a person typed, and they are copied exactly.
      </p>
    </section>
  );
}

/** "priya@example.com, your Manager, reads this same page." - worded from the Gateway's list of who reads it. */
export function readersSentence(readers: MentorReader[]): string {
  if (readers.length === 0) return "Nobody else reads this page.";
  if (readers.length === 1) return `${readers[0].email}, your ${readers[0].role}, reads this same page.`;
  const named = readers.map((r) => `${r.email} (your ${r.role})`);
  return `${named.slice(0, -1).join(", ")} and ${named[named.length - 1]} read this same page.`;
}

function MentorWeek({ page }: { page: MentorPage }) {
  if (!page.written) {
    return <EmptyState message="The Mentor has not written this week yet." />;
  }
  if (page.blocks.length === 0) {
    return (
      <EmptyState
        message={
          page.scope === "own"
            ? "The Mentor wrote nothing about you for this week. It writes only about someone who ran sessions that week."
            : "The Mentor wrote nothing for this week. It writes only about someone who ran sessions that week."
        }
      />
    );
  }
  return (
    <div className="mentor-blocks">
      {page.blocks.map((block) => (
        <MentorBlockCard key={block.personSubject} block={block} />
      ))}
    </div>
  );
}

/** One person's week. The ONE drawing of a block - the Owner's, the Manager's and the person's own page all use it. */
export function MentorBlockCard({ block }: { block: MentorBlock }) {
  return (
    <article className="mentor-block" data-testid="mentor-block" aria-label={block.personEmail}>
      <header className="mentor-who">
        <span className="mentor-person">{block.personEmail}</span>
        <span className="mentor-role">{block.role}</span>
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
