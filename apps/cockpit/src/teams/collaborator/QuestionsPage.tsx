import { useCallback, useState } from "react";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import { answerQuestion, getMyQuestions, type TeamQuestion, type TeamQuestions } from "@devthrottle/client-core/teams/teamQuestionsClient";
import { Button, EmptyState, ErrorBanner, LoadingState, PageHeader } from "../../components";
import { shortDate, TEAM_REPORTS_POLL_MS } from "./teamReportFormat";
import { refreshTeamPageCounts } from "../useTeamPageCounts";
import "../../team/team.css";
import "./collaborator.css";
import { backgroundRecovered, describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-team-questions";

// QUESTIONS (screen S8): the slot devthrottle_internal#2306 made, filled by devthrottle_internal#2307 - the questions
// waiting on this person, in the team on screen. Each is in a dev report its author sent them; they answer by picking
// one option, and may add words of their own.
//
// THE CHOICE GOES TO THE SESSION THAT ASKED; THE WORDS GO TO A PERSON. Both are the Gateway's doing: this page sends the
// option and the comment to one route, and the Gateway delivers the option to the session and the comment to the
// person who asked, never to an agent. Every sentence here - who asked, "recommended", where the words go, what became
// of an answer - is the Gateway's, rendered verbatim (rule 7). The navigation and the route guard are #2306's.
export function QuestionsPage() {
  const { current } = useCurrentTeam();
  if (current === null) {
    throw new Error("The Questions page was drawn with no team on screen. TeamPageRoute must guard it.");
  }
  return <QuestionsView key={current.id} teamId={current.id} />;
}

function QuestionsView({ teamId }: { teamId: string }) {
  const [questions, setQuestions] = useState<TeamQuestions | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(
    async (signal: AbortSignal) => {
      try {
        setQuestions(await getMyQuestions(teamId, signal));
        backgroundRecovered(SURFACE, "load your questions");
        setError(null);
      } catch (err) {
        if (err instanceof Error && err.name === "AbortError") return;
        setError(describeAndReport(SURFACE, "load your questions", err, { background: true }));
      }
    },
    [teamId],
  );
  useVisiblePolling(refresh, TEAM_REPORTS_POLL_MS);

  const reload = useCallback(() => {
    void refresh(new AbortController().signal);
  }, [refresh]);

  return (
    <section className="pane team-page team-questions" data-testid="team-page-questions">
      <PageHeader title="Questions" subtitle={questions?.subtitle ?? "Questions your team is waiting on you to answer."} />
      {error && <ErrorBanner message={error} />}
      {questions === null && !error && <LoadingState message="Loading questions..." />}
      {questions !== null && questions.waiting.length === 0 && <EmptyState message={questions.emptyText} />}
      {questions !== null && questions.waiting.length > 0 && (
        <ul className="team-question-list" data-testid="team-questions-waiting">
          {questions.waiting.map((q) => (
            <li key={cardKey(q)}>
              <QuestionCard teamId={teamId} question={q} onAnswered={reload} />
            </li>
          ))}
        </ul>
      )}
      {questions !== null && questions.answered.length > 0 && (
        <div className="team-questions-answered" data-testid="team-questions-answered">
          <h2 className="team-reports-heading">{questions.answeredHeading}</h2>
          <ul className="team-question-list">
            {questions.answered.map((q) => (
              <li key={cardKey(q)}>
                <QuestionCard teamId={teamId} question={q} onAnswered={reload} />
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}

// A card is one question IN ONE VERSION (review F7): when a newer version of the report is sent, its card is a new one,
// so a choice or words left on the older version never carry over to options that may have changed.
function cardKey(q: TeamQuestion): string {
  return `${q.reportId}/${q.version}/${q.questionId}`;
}

function QuestionCard({ teamId, question, onAnswered }: { teamId: string; question: TeamQuestion; onAnswered: () => void }) {
  const recommended = question.options.find((o) => o.recommended)?.value ?? "";
  const [chosen, setChosen] = useState(recommended);
  const [comment, setComment] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [sent, setSent] = useState<TeamQuestion | null>(null);
  // WHAT THE GATEWAY SAID LAST WINS (review F7, rule 7). A polled question that carries an answer is the newest word on
  // it - a held answer becomes delivered there - so it outranks the answer this card was given when it was sent. That
  // answer is shown only until a poll brings the question back answered.
  const q = question.answer !== null ? question : (sent ?? question);
  const name = `q-${q.reportId}-${q.questionId}`;

  const send = useCallback(async () => {
    setSending(true);
    setSendError(null);
    try {
      setSent(await answerQuestion(teamId, q, chosen, comment));
      setComment("");
      // The count beside Questions in the rail is read again at once, so it never disagrees with this page.
      refreshTeamPageCounts();
      onAnswered();
    } catch (err) {
      setSendError(describeAndReport(SURFACE, "send your answer", err));
      onAnswered();
    } finally {
      setSending(false);
    }
  }, [teamId, q, chosen, comment, onAnswered]);

  return (
    <article className={q.answer === null ? "team-card team-question team-question-waiting" : "team-card team-question"} data-testid="team-question" data-question-id={q.questionId}>
      <div className="team-question-meta">
        <span className="team-question-tag">{q.reportTitle}</span> asked <time dateTime={q.askedAtUtc}>{shortDate(q.askedAtUtc)}</time> by {q.askedBy}
      </div>
      <p className="team-question-title">{q.question}</p>

      {q.answer === null && (
        <>
          <div className="team-question-options" role="radiogroup" aria-label={q.question}>
            {q.options.map((o) => (
              <label key={o.value} className={chosen === o.value ? "team-option team-option-on" : "team-option"}>
                <input
                  type="radio"
                  name={name}
                  value={o.value}
                  checked={chosen === o.value}
                  disabled={!q.canAnswer || sending}
                  onChange={() => setChosen(o.value)}
                  data-testid="team-question-option"
                />
                <span className="team-option-title">{o.label}</span>
                {o.recommended && <span className="team-question-recommended">{q.recommendedLabel}</span>}
              </label>
            ))}
          </div>
          {q.canComment && (
            <textarea
              className="team-input team-textarea"
              aria-label={q.commentPlaceholder}
              placeholder={q.commentPlaceholder}
              rows={3}
              value={comment}
              disabled={sending}
              onChange={(e) => setComment(e.target.value)}
              data-testid="team-question-comment"
            />
          )}
          {sendError && (
            <div className="dev-report-error" role="alert" data-testid="team-question-error">
              {sendError}
            </div>
          )}
          <div className="team-row">
            <Button variant="primary" disabled={!q.canAnswer || sending || chosen === ""} onClick={() => void send()} data-testid="team-question-send">
              {sending ? "Sending..." : q.sendLabel}
            </Button>
            <span className="team-hint" data-testid="team-question-note">{q.commentNote}</span>
          </div>
        </>
      )}

      {q.answer !== null && (
        <div className="team-question-answer" data-testid="team-question-answer">
          <p className="team-question-chosen">{q.answer.chosenLabel}</p>
          <p className="team-hint" data-testid="team-question-status">{q.answer.statusLabel}</p>
          {q.answer.yourComment !== null && (
            <div className="team-question-your-words" data-testid="team-question-your-comment">
              <span className="team-question-your-words-label">{q.answer.yourCommentLabel}</span>
              <p>{q.answer.yourComment}</p>
            </div>
          )}
        </div>
      )}
    </article>
  );
}
