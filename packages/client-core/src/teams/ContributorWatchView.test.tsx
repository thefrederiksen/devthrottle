import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import contributorFixture from "../factory/watch/factoryWatchView.contributor.fixture.json";
import type { FactoryWatchView } from "../factory/watch/factoryWatchTypes";

// "WATCH YOUR FACTORY" FOR A CONTRIBUTOR (piece 6): the explainer card before the first watch, then the player. The card
// is shown exactly when the Gateway's view carries one, and every word on it is the view's: the words below are
// deliberately odd, so any word the component wrote for itself would fail.

const own = (contributorFixture as unknown as Record<string, FactoryWatchView>).own;
const ODD = {
  paragraphs: ["First~odd paragraph, as sent.", "Second~odd: with punctuation; kept.", "Third~odd."],
  buttonText: "Go~watch~odd",
};

const client = vi.hoisted(() => ({
  view: null as unknown,
  seen: [] as { teamId: string; factory: string }[],
  seenFails: false,
}));
vi.mock("./contributorClient", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  getContributorWatch: vi.fn(async () => client.view),
  markWatchExplainerSeen: vi.fn(async (teamId: string, factory: string) => {
    client.seen.push({ teamId, factory });
    if (client.seenFails) throw new Error("The Gateway could not remember it~odd");
  }),
}));

import { ContributorWatchView } from "./ContributorWatchView";

beforeEach(() => {
  client.seen = [];
  client.seenFails = false;
  vi.stubGlobal("requestAnimationFrame", () => 0);
  vi.stubGlobal("cancelAnimationFrame", () => undefined);
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function mount(titleInFrame?: boolean) {
  return render(
    <ContributorWatchView teamId="acme" factory="website" errorClassName="odd-error" titleInFrame={titleInFrame} />,
  );
}

describe("ContributorWatchView", () => {
  it("ContributorWatchView_TheViewCarriesAnExplainer_ShowsTheCardWithExactlyItsWords_AndNoPlayer", async () => {
    client.view = { ...own, explainer: ODD };

    const { container } = mount();

    const paragraphs = await screen.findAllByTestId("watch-explainer-paragraph");
    expect(paragraphs.map((p) => p.textContent)).toEqual(ODD.paragraphs);
    expect(screen.getByTestId("watch-explainer-go").textContent).toBe(ODD.buttonText);
    expect(screen.getByRole("heading").textContent).toBe(own.title);
    // The card says nothing of its own: its whole text is the view's title, paragraphs and button.
    expect(screen.getByTestId("watch-explainer").textContent).toBe(own.title + ODD.paragraphs.join("") + ODD.buttonText);
    expect(container.querySelector(".fwp")).toBeNull();
  });

  it("ContributorWatchView_TheButton_ClosesTheCard_StartsThePlayer_AndTellsTheGateway", async () => {
    client.view = { ...own, explainer: ODD };
    const { container } = mount();

    fireEvent.click(await screen.findByTestId("watch-explainer-go"));

    expect(screen.queryByTestId("watch-explainer")).toBeNull();
    expect(container.querySelector(".fwp")).not.toBeNull();
    await waitFor(() => expect(client.seen).toEqual([{ teamId: "acme", factory: "website" }]));
  });

  it("ContributorWatchView_TheViewCarriesNoExplainer_ShowsThePlayerAtOnce", async () => {
    client.view = { ...own, explainer: null };

    const { container } = mount();

    await waitFor(() => expect(container.querySelector(".fwp")).not.toBeNull());
    expect(screen.queryByTestId("watch-explainer")).toBeNull();
    expect(client.seen).toEqual([]);
  });

  it("ContributorWatchView_RememberingFails_ThePersonStillWatches_AndTheReasonIsShown", async () => {
    client.view = { ...own, explainer: ODD };
    client.seenFails = true;
    const { container } = mount();

    const go = await screen.findByTestId("watch-explainer-go");
    await act(async () => {
      fireEvent.click(go);
    });

    expect(container.querySelector(".fwp")).not.toBeNull();
    await waitFor(() => expect(container.querySelector(".odd-error")?.textContent).toContain("could not remember it~odd"));
  });

  it("ContributorWatchView_Refused_ShowsTheGatewaysSentence", async () => {
    client.view = { refused: "Not~sent~to~you~odd" };

    mount();

    expect((await screen.findByTestId("contributor-watch-refused")).textContent).toBe("Not~sent~to~you~odd");
  });

  it("ContributorWatchView_TitleNotInFrame_TheCardAndThePlayerEachKeepTheirHeading", async () => {
    // The Cockpit: its breadcrumb is a way back, so the card and the player keep the factory's name as their heading.
    client.view = { ...own, explainer: ODD };
    const { container } = mount();

    expect((await screen.findByRole("heading")).textContent).toBe(own.title);
    fireEvent.click(screen.getByTestId("watch-explainer-go"));

    expect(container.querySelector(".fwp-title")?.textContent).toBe(own.title);
  });

  it("ContributorWatchView_TitleInFrame_TheNameIsNotRepeated_OnTheCardOrThePlayer_ButStillLabelsThem", async () => {
    // The phone: its app bar shows the factory's name, so neither the card nor the player shows it again.
    client.view = { ...own, explainer: ODD };
    const { container } = mount(true);

    await screen.findByTestId("watch-explainer");
    expect(screen.queryByRole("heading")).toBeNull();
    expect(screen.getByRole("dialog").getAttribute("aria-label")).toBe(own.title);
    expect(screen.getByTestId("watch-explainer").textContent).toBe(ODD.paragraphs.join("") + ODD.buttonText);

    fireEvent.click(screen.getByTestId("watch-explainer-go"));

    expect(container.querySelector(".fwp")).not.toBeNull();
    expect(container.querySelector(".fwp-title")).toBeNull();
    expect(container.querySelector(".fwp-svg")?.getAttribute("aria-label")).toBe(own.title);
  });
});
