// A CONTRIBUTOR'S FACTORY CARDS (Team implementation piece 4.1; "Watch your factory" piece 6): the cards on the
// contributor's Factories page, shared by the Cockpit and the phone. Each card has a "Watch your factory" button that
// opens the watch view of that factory - the explainer card first, if the person has not seen it.
//
// Every word is the Gateway's (rule 7): the factory's name, the team's label, the line about what it is doing now, the
// "needs you" label and the button's words. The order is the Gateway's too (`useContributorFactories` sorts by its key).
//
// NO DEAD LINKS. Where a card and its button go is passed in by the shell: the Cockpit has a customer page for each
// factory and passes `openHref`; the phone has none, so its cards are not links - only the button is.

import { useCallback, useState } from "react";
import { Link } from "react-router-dom";
import { useVisiblePolling } from "../polling/useVisiblePolling";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import type { TeamSummary } from "./teamsClient";
import { getContributorFactories, readCombined, type ContributorFactories, type ContributorFactoryCard } from "./contributorClient";
import "./contributorFactories.css";

/** How often the phone's Factories page is read again while it is on screen. The Cockpit passes its own team pages'. */
export const CONTRIBUTOR_FACTORIES_POLL_MS = 30_000;

const LOAD = "load your factories";

/** What `useContributorFactories` answers. */
export interface ContributorFactoriesRead {
  /** Every team's cards together, in the Gateway's order; null until the first read answers. */
  cards: ContributorFactoryCard[] | null;
  /** Each team's own answer, for its intro and its empty sentence. */
  answers: ContributorFactories[];
  /** Why the latest read failed, already reported. */
  error: string | null;
}

/** The contributor's factories on every team that takes part, combined in the Gateway's order. */
export function useContributorFactories(
  teams: TeamSummary[],
  surface: string,
  pollMs: number = CONTRIBUTOR_FACTORIES_POLL_MS,
): ContributorFactoriesRead {
  const [cards, setCards] = useState<ContributorFactoryCard[] | null>(null);
  const [answers, setAnswers] = useState<ContributorFactories[]>([]);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(
    async (signal: AbortSignal) => {
      try {
        const next = await readCombined(teams, getContributorFactories, (a) => a.factories, signal);
        setAnswers(next.answers);
        setCards(next.items);
        setError(null);
        backgroundRecovered(surface, LOAD);
      } catch (err) {
        if (err instanceof Error && err.name === "AbortError") return;
        setError(describeAndReport(surface, LOAD, err, { background: true }));
      }
    },
    [teams, surface],
  );
  useVisiblePolling(refresh, pollMs);
  return { cards, answers, error };
}

export interface ContributorFactoryCardsProps {
  cards: ContributorFactoryCard[];
  /** Where a card opens, on a surface that has a page for one factory; leave it out and the card is not a link. */
  openHref?: (card: ContributorFactoryCard) => string;
  /** Where the card's "Watch your factory" button goes on this surface. */
  watchHref: (card: ContributorFactoryCard) => string;
}

export function ContributorFactoryCards({ cards, openHref, watchHref }: ContributorFactoryCardsProps) {
  return (
    <ul className="cfc-grid" data-testid="contributor-factory-cards">
      {cards.map((c) => {
        const body = (
          <>
            <span className="cfc-head">
              <span className="cfc-name">{c.name}</span>
              {c.needsYouLabel !== null && (
                <span className="cfc-pill" data-testid="contributor-needs-you">
                  {c.needsYouLabel}
                </span>
              )}
            </span>
            <span className="cfc-team" data-testid="contributor-team-label">
              {c.teamLabel}
            </span>
            {c.nowText !== null && <span className="cfc-now">{c.nowText}</span>}
          </>
        );
        return (
          <li key={`${c.teamId}/${c.factory}`} className="cfc-card" data-testid="contributor-factory-card">
            {openHref === undefined ? (
              <div className="cfc-body">{body}</div>
            ) : (
              <Link className="cfc-body cfc-open" to={openHref(c)} data-testid="contributor-factory-open">
                {body}
              </Link>
            )}
            <Link className="cfc-watch" to={watchHref(c)} data-testid="contributor-watch-button">
              {c.watchLabel}
            </Link>
          </li>
        );
      })}
    </ul>
  );
}
