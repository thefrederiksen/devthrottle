import { useCallback, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import {
  getContributorFactories,
  getCustomerPage,
  readCombined,
  teamsWithPage,
  type ContributorFactories,
  type ContributorFactoryCard,
  type CustomerPage,
  type CustomerPageRefused,
} from "@devthrottle/client-core/teams/contributorClient";
import { EmptyState, ErrorBanner, LoadingState, PageHeader } from "../../components";
import { TEAM_REPORTS_POLL_MS } from "./teamReportFormat";
import { backgroundRecovered, describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import "../../team/team.css";
import "./collaborator.css";

const SURFACE = "cockpit-contributor-factories";

/** The page's own address, as the Gateway's page list names it. */
export const CONTRIBUTOR_FACTORIES_PATH = "/contributor/factories";
/** The Inbox's address, where an "Answer" or a sent item opens. */
export const CONTRIBUTOR_INBOX_PATH = "/contributor/inbox";

// A CONTRIBUTOR'S FACTORIES (Team implementation piece 4.1; mockup.html, the contributor's Factories): every factory sent
// to this person by name, on every team they are a contributor on, together - each card with its team's name, no team
// switcher. Open one for its customer page: what needs them, where the project is, today, what they have so far, and what
// was sent to them. Every word, the order, and which factories appear at all are the Gateway's (rule 7).
//
// THE ADDRESS STAYS /contributor/factories. A factory opens in place, named in the query (?team=<id>&factory=<id>),
// because only the exact page address is one of the contributor's pages - as on the first version's Reports page.
export function ContributorFactoriesPage() {
  const [params] = useSearchParams();
  const team = params.get("team");
  const factory = params.get("factory");
  if (team !== null && factory !== null) return <CustomerPageView key={`${team}/${factory}`} teamId={team} factory={factory} />;
  return <FactoriesList />;
}

function FactoriesList() {
  const mine = useTeamsWithPage("factories");
  const [cards, setCards] = useState<ContributorFactoryCard[] | null>(null);
  const [answers, setAnswers] = useState<ContributorFactories[]>([]);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(
    async (signal: AbortSignal) => {
      try {
        const next = await readCombined(mine, getContributorFactories, (a) => a.factories, signal);
        setAnswers(next.answers);
        setCards(next.items);
        setError(null);
        backgroundRecovered(SURFACE, "load your factories");
      } catch (err) {
        if (err instanceof Error && err.name === "AbortError") return;
        setError(describeAndReport(SURFACE, "load your factories", err, { background: true }));
      }
    },
    [mine],
  );
  useVisiblePolling(refresh, TEAM_REPORTS_POLL_MS);

  return (
    <section className="pane team-page contributor-factories" data-testid="contributor-factories">
      <PageHeader title="Factories" subtitle="The factories building something for you." />
      {error && <ErrorBanner message={error} />}
      {cards === null && !error && <LoadingState message="Loading your factories..." />}
      {cards !== null && cards.length === 0 && answers.length > 0 && <EmptyState message={answers[0].emptyText} />}
      {cards !== null && cards.length > 0 && (
        <ul className="contributor-grid" data-testid="contributor-factory-cards">
          {cards.map((c) => (
            <li key={`${c.teamId}/${c.factory}`}>
              <Link
                className="team-card contributor-factory-card"
                to={`${CONTRIBUTOR_FACTORIES_PATH}?team=${encodeURIComponent(c.teamId)}&factory=${encodeURIComponent(c.factory)}`}
                data-testid="contributor-factory-card"
              >
                <span className="contributor-card-head">
                  <span className="contributor-card-name">{c.name}</span>
                  {c.needsYouLabel !== null && (
                    <span className="contributor-pill contributor-pill-waiting" data-testid="contributor-needs-you">
                      {c.needsYouLabel}
                    </span>
                  )}
                </span>
                <span className="contributor-team-label" data-testid="contributor-team-label">{c.teamLabel}</span>
                {c.nowText !== null && <span className="contributor-card-now">{c.nowText}</span>}
              </Link>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function CustomerPageView({ teamId, factory }: { teamId: string; factory: string }) {
  const [page, setPage] = useState<CustomerPage | CustomerPageRefused | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(
    async (signal: AbortSignal) => {
      try {
        setPage(await getCustomerPage(teamId, factory, signal));
        setError(null);
        backgroundRecovered(SURFACE, "load this factory");
      } catch (err) {
        if (err instanceof Error && err.name === "AbortError") return;
        setError(describeAndReport(SURFACE, "load this factory", err, { background: true }));
      }
    },
    [teamId, factory],
  );
  useVisiblePolling(refresh, TEAM_REPORTS_POLL_MS);

  const back = (
    <div className="contributor-crumbs">
      <Link to={CONTRIBUTOR_FACTORIES_PATH}>Factories</Link>
      {page !== null && "name" in page && <span> / {page.name}</span>}
    </div>
  );

  if (page !== null && "refused" in page) {
    return (
      <section className="pane team-page contributor-customer" data-testid="contributor-customer-refused">
        {back}
        <EmptyState message={page.refused} />
      </section>
    );
  }

  return (
    <section className="pane team-page contributor-customer" data-testid="contributor-customer">
      {back}
      {error && <ErrorBanner message={error} />}
      {page === null && !error && <LoadingState message="Loading this factory..." />}
      {page !== null && (
        <>
          <div className="contributor-customer-head">
            <div>
              <h1 className="pane-title contributor-customer-title">{page.name}</h1>
              <span className="contributor-team-label">{page.teamLabel}</span>
            </div>
            <Link className="ui-btn ui-btn-secondary" to={CONTRIBUTOR_INBOX_PATH} data-testid="contributor-write">
              {page.writeLabel}
            </Link>
          </div>

          <article className="team-card" data-testid="contributor-needs-you-card">
            <h2 className="contributor-card-title">{page.needsYou.title}</h2>
            {page.needsYou.items.length === 0 && <p className="team-hint">{page.needsYou.emptyText}</p>}
            {page.needsYou.items.map((n) => (
              <div className="contributor-row" key={n.reportId}>
                <span>{n.title}</span>
                <Link className="ui-btn ui-btn-primary" to={inboxItem(teamId, n.reportId)} data-testid="contributor-answer">
                  {n.actionLabel}
                </Link>
              </div>
            ))}
          </article>

          <article className="team-card" data-testid="contributor-stage-card">
            <h2 className="contributor-card-title">{page.stage.title}</h2>
            <ol className="contributor-stages">
              {page.stage.steps.map((s) => (
                <li key={s.label} className={`contributor-stage contributor-stage-${s.state}`}>
                  {s.label}
                </li>
              ))}
            </ol>
            {page.stage.note !== null && <p className="team-hint">{page.stage.note}</p>}
          </article>

          <article className="team-card" data-testid="contributor-today-card">
            <h2 className="contributor-card-title">{page.today.title}</h2>
            {page.today.items.length === 0 && <p className="team-hint">{page.today.emptyText}</p>}
            {page.today.items.map((t) => (
              <div className="contributor-row" key={`${t.atUtc}/${t.text}`}>
                <span>{t.text}</span>
                <time className="team-hint" dateTime={t.atUtc}>{t.timeLabel}</time>
              </div>
            ))}
          </article>

          <article className="team-card" data-testid="contributor-so-far-card">
            <h2 className="contributor-card-title">{page.soFar.title}</h2>
            {page.soFar.items.length === 0 && <p className="team-hint">{page.soFar.emptyText}</p>}
            {page.soFar.items.map((l) => (
              <div key={l.href}>
                <a href={l.href} target="_blank" rel="noreferrer">{l.label}</a>
              </div>
            ))}
          </article>

          <article className="team-card" data-testid="contributor-sent-card">
            <h2 className="contributor-card-title">{page.sentToYou.title}</h2>
            {page.sentToYou.items.length === 0 && <p className="team-hint">{page.sentToYou.emptyText}</p>}
            {page.sentToYou.items.map((r) => (
              <div className="contributor-row" key={r.reportId}>
                <Link to={inboxItem(teamId, r.reportId)}>{r.title}</Link>
                <span className={`contributor-pill contributor-pill-${r.tone}`}>{r.stateLabel}</span>
              </div>
            ))}
          </article>

          <article className="team-card" data-testid="contributor-can-do-card">
            <h2 className="contributor-card-title">{page.canDo.title}</h2>
            <p>{page.canDo.text}</p>
            <p className="team-hint">{page.canDo.note}</p>
          </article>
        </>
      )}
    </section>
  );
}

/** The teams whose page list names `pageId`, as one value that changes only when that set of teams changes - the list of
 *  teams is read again from time to time, and a new array of the same teams must not restart a page's reads. */
export function useTeamsWithPage(pageId: string): TeamSummary[] {
  const { teams } = useCurrentTeam();
  const found = teamsWithPage(teams, pageId);
  const key = found.map((t) => `${t.id}|${t.name}`).join(",");
  const held = useRef<{ key: string; teams: TeamSummary[] } | null>(null);
  if (held.current === null || held.current.key !== key) held.current = { key, teams: found };
  return held.current.teams;
}

/** Where one Inbox item opens. */
export function inboxItem(teamId: string, itemId: string): string {
  return `${CONTRIBUTOR_INBOX_PATH}?team=${encodeURIComponent(teamId)}&item=${encodeURIComponent(itemId)}`;
}
