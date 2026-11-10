import { useState, type ReactNode } from "react";
import { Link, useSearchParams } from "react-router-dom";
import type { ContributorFactoryCard } from "@devthrottle/client-core/teams/contributorClient";
import { ContributorFactoryCards, useContributorFactories } from "@devthrottle/client-core/teams/ContributorFactoryCards";
import { ContributorWatchView } from "@devthrottle/client-core/teams/ContributorWatchView";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import { useMyTeamsWithPage } from "@devthrottle/client-core/teams/useMyTeamsWithPage";
import { AppBarAlerts } from "../components/AppBarAlerts";

// A CONTRIBUTOR'S FACTORIES ON THE PHONE ("Watch your factory", piece 6): the factories sent to the person by name, on
// every team they are a contributor on, each with its "Watch your factory" button - and that factory's watch view, the
// explainer card first if they have not seen it. The SAME cards and the SAME watch view the Cockpit shows, from
// client-core; this file is only the phone's frame: the screen, the Back link and the app bar.
//
// The phone has no customer page for one factory, so its cards are not links - only the button is (no dead links).
// Every word is the Gateway's (rule 7): the page's title is the Gateway's label for the page, from the team's page list.

/** The phone's address for this page, the same one the Cockpit and the Gateway's page list use. */
export const CONTRIBUTOR_FACTORIES_PATH = "/contributor/factories";

const SURFACE = "mobile-contributor-factories";
const WATCH_VIEW = "watch";

/** Where one factory's "Watch your factory" opens on the phone. */
export function phoneWatchHref(c: Pick<ContributorFactoryCard, "teamId" | "factory">): string {
  return `${CONTRIBUTOR_FACTORIES_PATH}?team=${encodeURIComponent(c.teamId)}&factory=${encodeURIComponent(c.factory)}&view=${WATCH_VIEW}`;
}

/** What the Gateway's page list calls the Factories page, or null before a team naming it has been read. */
export function factoriesLabel(teams: TeamSummary[] | null): string | null {
  return teams?.[0]?.app.pages.find((p) => p.id === "factories")?.label ?? null;
}

function Screen({ title, back, children }: { title: string | null; back: string; children: ReactNode }) {
  return (
    <div className="screen contributor-screen">
      <header className="app-bar">
        <Link className="back-link" to={back}>
          Back
        </Link>
        {title !== null && <h1>{title}</h1>}
        <div className="app-bar-spacer" />
        <AppBarAlerts />
      </header>
      {children}
    </div>
  );
}

export function ContributorFactories() {
  const [params] = useSearchParams();
  const team = params.get("team");
  const factory = params.get("factory");
  const { teams, error } = useMyTeamsWithPage("factories", SURFACE);
  if (team !== null && factory !== null && params.get("view") === WATCH_VIEW)
    return <PhoneWatch key={`${team}/${factory}`} teamId={team} factory={factory} />;
  return (
    <Screen title={factoriesLabel(teams)} back="/">
      {error !== null && (
        <div className="banner banner-error" role="alert">
          {/* error-reported-by: useMyTeamsWithPage */}
          {error}
        </div>
      )}
      {teams === null && error === null && <p data-testid="contributor-factories-loading">Loading...</p>}
      {teams !== null && <FactoriesList teams={teams} />}
    </Screen>
  );
}

function FactoriesList({ teams }: { teams: TeamSummary[] }) {
  const { cards, answers, error } = useContributorFactories(teams, SURFACE);
  return (
    <div data-testid="contributor-factories">
      {answers[0] !== undefined && <p className="contributor-intro">{answers[0].intro}</p>}
      {error !== null && (
        <div className="banner banner-error" role="alert">
          {/* error-reported-by: useContributorFactories */}
          {error}
        </div>
      )}
      {cards === null && error === null && teams.length > 0 && <p>Loading...</p>}
      {cards !== null && cards.length === 0 && answers.length > 0 && <p className="contributor-empty">{answers[0].emptyText}</p>}
      {cards !== null && cards.length > 0 && <ContributorFactoryCards cards={cards} watchHref={phoneWatchHref} />}
    </div>
  );
}

function PhoneWatch({ teamId, factory }: { teamId: string; factory: string }) {
  const [title, setTitle] = useState<string | null>(null);
  return (
    <Screen title={title} back={CONTRIBUTOR_FACTORIES_PATH}>
      <ContributorWatchView teamId={teamId} factory={factory} errorClassName="banner banner-error" onTitle={setTitle} />
    </Screen>
  );
}
