import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { ContributorFactoryCards } from "./ContributorFactoryCards";
import type { ContributorFactoryCard } from "./contributorClient";

// The contributor's factory cards (piece 6), shared by the Cockpit and the phone: every word is the Gateway's, the
// "Watch your factory" button goes where the shell says, and a card is a link only on a surface that passes one.

const card: ContributorFactoryCard = {
  teamId: "acme", teamLabel: "Acme~odd", factory: "website", name: "Website~odd", nowText: "Now~odd",
  needsYouLabel: null, watchLabel: "Watch~odd~label", order: "1",
};

afterEach(() => cleanup());

describe("ContributorFactoryCards", () => {
  it("ContributorFactoryCards_EveryCard_HasTheGatewaysWatchButton_GoingWhereTheShellSays", () => {
    render(
      <MemoryRouter>
        <ContributorFactoryCards cards={[card]} watchHref={(c) => `/watch-here/${c.teamId}/${c.factory}`} />
      </MemoryRouter>,
    );

    const button = screen.getByTestId("contributor-watch-button");
    expect(button.textContent).toBe("Watch~odd~label");
    expect(button.getAttribute("href")).toBe("/watch-here/acme/website");
  });

  it("ContributorFactoryCards_NoOpenHref_TheCardIsNotALink_OnlyTheButtonIs", () => {
    render(
      <MemoryRouter>
        <ContributorFactoryCards cards={[card]} watchHref={() => "/w"} />
      </MemoryRouter>,
    );

    const links = within(screen.getByTestId("contributor-factory-card")).getAllByRole("link");
    expect(links.map((l) => l.textContent)).toEqual(["Watch~odd~label"]);
  });

  it("ContributorFactoryCards_WithOpenHref_TheCardOpensThere", () => {
    render(
      <MemoryRouter>
        <ContributorFactoryCards cards={[card]} openHref={() => "/open/acme"} watchHref={() => "/w"} />
      </MemoryRouter>,
    );

    expect(screen.getByTestId("contributor-factory-open").getAttribute("href")).toBe("/open/acme");
  });
});
