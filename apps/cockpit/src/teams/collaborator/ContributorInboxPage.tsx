import { useCallback, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import {
  getContributorInbox,
  readCombined,
  type ContributorInbox,
  type ContributorInboxItem,
} from "@devthrottle/client-core/teams/contributorClient";
import { sendRequest } from "@devthrottle/client-core/teams/requestsClient";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import { Button, EmptyState, ErrorBanner, LoadingState, PageHeader } from "../../components";
import { QuestionCard } from "./QuestionsPage";
import { ReceivedReportView } from "./TeamReportViews";
import { CONTRIBUTOR_INBOX_PATH, inboxItem, pageLabel, useTeamsWithPage } from "./ContributorFactoriesPage";
import { dateAndTime, shortDate, TEAM_REPORTS_POLL_MS } from "./teamReportFormat";
import { backgroundRecovered, describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import "../../team/team.css";
import "./collaborator.css";

const SURFACE = "cockpit-contributor-inbox";

// A CONTRIBUTOR'S INBOX (Team implementation piece 4.1; mockup.html, the contributor's Inbox): only what was sent to this
// person, and their own conversation with the team's Owner - on every team they are a contributor on, together, each
// item with its team's name, in the Gateway's order. The first version's Questions, Requests and Reports fold in here: a
// report opens with its questions to answer above it - the ones the Gateway sent with the item, in its order - and "Write
// to the team's Owner" sends a request. Every word, label, tone and order is the Gateway's (rule 7).
//
// ?team=<id> alone opens the Inbox with the write box on that team's Owner: a factory page's "Write to the team's Owner"
// carries its team, so the words never go to another team's Owner.
//
// THE ADDRESS STAYS /contributor/inbox; an item opens in place (?team=<id>&item=<id>).
export function ContributorInboxPage() {
  const mine = useTeamsWithPage("inbox");
  const label = pageLabel(mine, "inbox");
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const [items, setItems] = useState<ContributorInboxItem[] | null>(null);
  const [answers, setAnswers] = useState<ContributorInbox[]>([]);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(
    async (signal: AbortSignal) => {
      try {
        const next = await readCombined(mine, getContributorInbox, (a) => a.items, signal);
        setAnswers(next.answers);
        setItems(next.items);
        setError(null);
        backgroundRecovered(SURFACE, "load your inbox");
      } catch (err) {
        if (err instanceof Error && err.name === "AbortError") return;
        setError(describeAndReport(SURFACE, "load your inbox", err, { background: true }));
      }
    },
    [mine],
  );
  useVisiblePolling(refresh, TEAM_REPORTS_POLL_MS);
  const reload = useCallback(() => void refresh(new AbortController().signal), [refresh]);

  const openTeam = params.get("team");
  const openId = params.get("item");
  const open = items?.find((i) => i.teamId === openTeam && i.id === openId) ?? null;
  const first = answers[0];

  return (
    <section className="pane team-page contributor-inbox" data-testid="contributor-inbox">
      {label !== null && <PageHeader title={label} subtitle={first?.intro} />}
      {error && <ErrorBanner message={error} />}
      {items === null && !error && <LoadingState message="Loading your inbox..." />}
      {items !== null && (
        <div className={open === null ? "contributor-two" : open.kind === "report" ? "contributor-two contributor-two-full" : "contributor-two contributor-two-open"}>
          <div className="contributor-list">
            {items.length === 0 && first !== undefined && <EmptyState message={first.emptyText} />}
            {items.length > 0 && (
              <ul className="team-report-list" data-testid="contributor-inbox-items">
                {items.map((i) => (
                  <li key={`${i.teamId}/${i.kind}/${i.id}`}>
                    <Link
                      className={open === i ? "team-report-row contributor-item-on" : "team-report-row"}
                      to={inboxItem(i.teamId, i.id)}
                      data-testid="contributor-inbox-item"
                      data-kind={i.kind}
                    >
                      <span className="team-report-row-main">
                        <span className="team-report-row-title">{i.title}</span>
                        <span className="team-report-row-meta">
                          <span className="contributor-team-label" data-testid="contributor-team-label">{i.teamLabel}</span>{" "}
                          {i.fromLabel} <span aria-hidden="true">&middot;</span> <time dateTime={i.atUtc}>{shortDate(i.atUtc)}</time>
                        </span>
                      </span>
                      <span className={`contributor-pill contributor-pill-${i.tone}`} data-testid="contributor-item-state">
                        {i.stateLabel}
                      </span>
                    </Link>
                  </li>
                ))}
              </ul>
            )}
            {first !== undefined && (
              <WriteBox key={openTeam ?? ""} teams={mine} startOn={openTeam} answers={answers} onSent={reload} />
            )}
          </div>
          {open !== null && (
            <div className="contributor-detail" data-testid="contributor-inbox-detail">
              {open.kind === "report" ? (
                <ReportItem
                  key={`${open.teamId}/${open.id}`}
                  item={open}
                  backLabel={label}
                  onBack={() => navigate(CONTRIBUTOR_INBOX_PATH)}
                  onAnswered={reload}
                />
              ) : (
                <MessageItem item={open} backLabel={label} />
              )}
            </div>
          )}
        </div>
      )}
    </section>
  );
}

/** A report sent to the person: its questions to answer, as the Gateway sent them with the item, then the report itself.
 *  An answer reads the Inbox again, which brings the questions as they now stand. */
function ReportItem({ item, backLabel, onBack, onAnswered }: {
  item: ContributorInboxItem;
  backLabel: string | null;
  onBack: () => void;
  onAnswered: () => void;
}) {
  return (
    <>
      {item.questions.length > 0 && (
        <ul className="team-question-list" data-testid="contributor-report-questions">
          {item.questions.map((q) => (
            <li key={`${q.reportId}/${q.version}/${q.questionId}`}>
              <QuestionCard teamId={item.teamId} question={q} onAnswered={onAnswered} />
            </li>
          ))}
        </ul>
      )}
      <ReceivedReportView teamId={item.teamId} reportId={item.id} onBack={onBack} backLabel={backLabel ?? undefined} />
    </>
  );
}

/** Something the person wrote to the team's Owner, and what became of it. */
function MessageItem({ item, backLabel }: { item: ContributorInboxItem; backLabel: string | null }) {
  return (
    <article className="team-card" data-testid="contributor-message">
      <div className="contributor-crumbs">{backLabel !== null && <Link to={CONTRIBUTOR_INBOX_PATH}>{backLabel}</Link>}</div>
      <div className="team-hint">
        <span className="contributor-team-label">{item.teamLabel}</span> {item.fromLabel}
      </div>
      <h2 className="contributor-card-title">{item.title}</h2>
      <p className="contributor-message-text">{item.text}</p>
      {item.notice !== null && (
        <p className="team-ok" role="status" data-testid="contributor-message-notice">
          {item.notice}
        </p>
      )}
      <ol className="contributor-trail">
        {item.trail.map((s) => (
          <li key={`${s.atUtc}/${s.label}`}>
            <span className="team-hint">{dateAndTime(s.atUtc)}</span> {s.sentence}
            {s.reason !== null && <div className="team-hint">{s.reason}</div>}
          </li>
        ))}
      </ol>
    </article>
  );
}

/** The box for writing to the team's Owner. With more than one team, the person picks which team's Owner it goes to; it
 *  starts on the team the address names (a factory page's write link), and otherwise on the first of their teams. */
function WriteBox({ teams, startOn, answers, onSent }: {
  teams: TeamSummary[];
  startOn: string | null;
  answers: ContributorInbox[];
  onSent: () => void;
}) {
  const named = teams.find((t) => t.id === startOn);
  const [teamId, setTeamId] = useState(named !== undefined ? named.id : teams[0]?.id ?? "");
  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [sentNote, setSentNote] = useState<string | null>(null);
  const words = answers.find((a) => a.teamId === teamId)?.write ?? answers[0].write;

  const send = useCallback(async () => {
    setSending(true);
    setSendError(null);
    setSentNote(null);
    try {
      const sent = await sendRequest(teamId, text);
      setText("");
      setSentNote(sent.stateLabel);
      onSent();
    } catch (err) {
      setSendError(describeAndReport(SURFACE, "send your message", err));
    } finally {
      setSending(false);
    }
  }, [teamId, text, onSent]);

  return (
    <div className="team-card contributor-write" data-testid="contributor-write-box">
      <h2 className="contributor-card-title">{words.label}</h2>
      {teams.length > 1 && (
        <select
          className="team-input"
          aria-label="Which team"
          value={teamId}
          onChange={(e) => setTeamId(e.target.value)}
          data-testid="contributor-write-team"
        >
          {teams.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
            </option>
          ))}
        </select>
      )}
      <textarea
        className="team-input team-textarea"
        aria-label={words.label}
        placeholder={words.placeholder}
        rows={3}
        value={text}
        disabled={sending}
        onChange={(e) => setText(e.target.value)}
        data-testid="contributor-write-text"
      />
      {sendError && <ErrorBanner message={sendError} />}
      <div className="team-row">
        <Button variant="primary" disabled={sending || text.trim() === "" || teamId === ""} onClick={() => void send()} data-testid="contributor-write-send">
          {sending ? "Sending..." : words.sendLabel}
        </Button>
        <span className="team-hint">{sentNote ?? words.note}</span>
      </div>
    </div>
  );
}
