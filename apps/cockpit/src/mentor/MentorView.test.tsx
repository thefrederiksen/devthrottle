// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup, screen, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { CurrentTeamProvider, currentTeamStorageKey } from "@devthrottle/client-core/teams/CurrentTeam";
import type { MyTeamsAnswer, TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import type { MentorAnswer, MentorBlock, MentorPage } from "@devthrottle/client-core/teams/mentorClient";

// The Mentor's weekly page (devthrottle_internal#2305, screens S6 and S7), rendered from the Gateway's answer.
//
// WHAT THESE TESTS CAN AND CANNOT PROVE (review F6). The page never reads the person's role label - who sees which
// blocks is decided on the Gateway - so these tests are named by what the Gateway SENT (scope "everyone", scope "own",
// a refusal), not by a role. That an Owner, a Manager and a Developer are sent the right blocks, and the same words,
// is the Gateway's proof. What is proven here is that the page draws exactly what it was sent, adds nothing, drops
// nothing, and keeps working when a week fails.
//
// The block and readers below are the contract's own example (contract.md, 4 October 2026).

type Staged = MentorAnswer | Error | Promise<MentorAnswer>;
const mentor = vi.hoisted(() => ({
  // Each week's answers, used in order; the last one repeats.
  answers: new Map<string, unknown[]>(),
  calls: [] as Array<{ teamId: string; week: string | undefined }>,
}));
vi.mock("@devthrottle/client-core/teams/mentorClient", async (importActual) => {
  const actual = await importActual<typeof import("@devthrottle/client-core/teams/mentorClient")>();
  return {
    ...actual,
    getMentorPage: vi.fn(async (teamId: string, week?: string) => {
      mentor.calls.push({ teamId, week });
      const queue = mentor.answers.get(week ?? "default");
      if (queue === undefined || queue.length === 0) throw new Error(`no answer staged for week ${week ?? "default"}`);
      const answer = queue.length > 1 ? queue.shift() : queue[0];
      if (answer instanceof Error) throw answer;
      return answer;
    }),
    // The person's own page: staged under "personal" (or "personal:<week>"), recorded with no team.
    getPersonalMentorPage: vi.fn(async (week?: string) => {
      mentor.calls.push({ teamId: "(own account)", week });
      const queue = mentor.answers.get(week === undefined ? "personal" : `personal:${week}`);
      if (queue === undefined || queue.length === 0) throw new Error(`no personal answer staged for week ${week ?? "default"}`);
      const answer = queue.length > 1 ? queue.shift() : queue[0];
      if (answer instanceof Error) throw answer;
      return answer;
    }),
  };
});

import { GatewayError } from "@devthrottle/client-core/api/client";
import { MentorView, readersSentence } from "./MentorView";

function stage(week: string, ...answers: Staged[]) {
  mentor.answers.set(week, answers);
}

const TEAM_ID = "team-test";
const team = (role: string): TeamSummary => ({ id: TEAM_ID, name: "Teams test", role, memberCount: 3, people: "3 people", app: { full: true, pages: [], landing: null, elsewhere: null } });

const ROB_QUOTE = "fix the signup thing so it doesnt break on mobile";

const ROB_BLOCK: MentorBlock = {
  personEmail: "rob@example.com",
  role: "Developer",
  tone: "hard",
  toneLabel: "a hard week",
  workedOn: "The new signup page and two bug fixes in the installer.",
  howItWent: null,
  wentBadlyAndWhy:
    "On Tuesday they restarted the same task four times. Their first instruction didn't say which file to change, so the agent guessed differently each time.",
  quotes: [{ promptId: "p_3f9a...", at: "2026-09-29T09:14:03Z", text: ROB_QUOTE }],
  oneThingToTry: "Name the file and the result you expect in the first line, before asking for the change.",
  writtenAtUtc: "2026-10-05T00:20:11Z",
  isYou: false,
};

const READERS = [
  { email: "olivia@example.com", role: "Owner" },
  { email: "priya@example.com", role: "Manager" },
];

function week(overrides: Partial<MentorPage>): MentorAnswer {
  return {
    kind: "page",
    page: {
      teamId: TEAM_ID,
      week: "2026-W40",
      weekStart: "2026-09-28",
      weekEnd: "2026-10-04",
      timeZone: "Europe/Copenhagen",
      scope: "everyone",
      written: true,
      writingNote: null,
      blocks: [ROB_BLOCK],
      readers: READERS,
      emptyNote: null,
      ...overrides,
    },
  };
}

const W39 = { week: "2026-W39", weekStart: "2026-09-21", weekEnd: "2026-09-27" };
const W38 = { week: "2026-W38", weekStart: "2026-09-14", weekEnd: "2026-09-20" };

function renderAs(teams: MyTeamsAnswer, chosen: string | null = TEAM_ID) {
  if (chosen !== null) window.localStorage.setItem(currentTeamStorageKey(), chosen);
  const load = () => Promise.resolve(teams);
  return render(
    <MemoryRouter initialEntries={["/mentor"]}>
      <CurrentTeamProvider load={load}>
        <MentorView />
      </CurrentTeamProvider>
    </MemoryRouter>,
  );
}

const onTeam = (): MyTeamsAnswer => ({ kind: "teams", teams: [team("Manager")], start: { where: "own-account" } });

function blocks(): HTMLElement[] {
  return screen.queryAllByTestId("mentor-block");
}

function failure(sentence: string): GatewayError {
  return new GatewayError(500, sentence, { reason: sentence });
}

describe("MentorView", () => {
  beforeEach(() => {
    cleanup();
    window.localStorage.clear();
    mentor.answers.clear();
    mentor.calls.length = 0;
  });

  it("MentorView_ScopeEveryone_ShowsEveryBlockSent_AndNothingAboutAnyoneWithNoBlock", async () => {
    stage("default", week({ scope: "everyone" }));
    renderAs(onTeam());

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Mentor - week of 28 September");
    expect(screen.getByText("Each person reads their own block, word for word.")).toBeTruthy();
    expect(blocks()[0].getAttribute("aria-label")).toBe("rob@example.com");
    // Priya and Olivia ran no sessions: the page says nothing about them at all - no block, no line, no placeholder.
    expect(document.body.textContent).not.toContain("priya@example.com");
    expect(document.body.textContent).not.toContain("olivia@example.com");
    expect(mentor.calls).toEqual([{ teamId: TEAM_ID, week: undefined }]);
  });

  it("MentorView_ScopeOwn_ShowsTheOwnHeading_AndWhoElseReadsIt", async () => {
    stage("default", week({ scope: "own" }));
    renderAs(onTeam());

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Your week, from the Mentor");
    expect(
      screen.getByText("olivia@example.com (the team's Owner) and priya@example.com (your Manager) read this same page."),
    ).toBeTruthy();
    expect(blocks().map((b) => b.getAttribute("aria-label"))).toEqual(["rob@example.com"]);
  });

  it("MentorBlockCard_TheSameBlock_IsDrawnIdenticallyOnBothPages", async () => {
    // A guard on the component only: it fails if the block's drawing ever reads anything outside the block (the
    // scope, the readers). That both pages are SENT the same words is the Gateway's test, not this one.
    stage("default", week({ scope: "everyone" }));
    const everyone = renderAs(onTeam());
    await waitFor(() => expect(blocks()).toHaveLength(1));
    const everyonesCopy = blocks()[0].outerHTML;
    everyone.unmount();
    window.localStorage.clear();

    stage("default", week({ scope: "own" }));
    renderAs(onTeam());

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(blocks()[0].outerHTML).toBe(everyonesCopy);
  });

  it("MentorView_NullPersonEmail_IsHeadedByRoleAndNoEmailOnRecord", async () => {
    stage("default", week({ blocks: [{ ...ROB_BLOCK, personEmail: null }] }));
    renderAs(onTeam());

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(blocks()[0].getAttribute("aria-label")).toBe("Developer - no email on record");
    expect(blocks()[0].querySelector(".mentor-person")?.textContent).toBe("Developer - no email on record");
    expect(blocks()[0].querySelector(".mentor-role")).toBeNull();
  });

  it("MentorView_Refused_HasNoPage", async () => {
    stage("default", { kind: "refused" });
    renderAs(onTeam());

    await waitFor(() => expect(screen.getByText("Page not found")).toBeTruthy());
    expect(screen.queryByTestId("mentor-page")).toBeNull();
  });

  it("MentorView_NotOffered_HasNoPage", async () => {
    stage("default", { kind: "not-offered" });
    renderAs(onTeam());

    await waitFor(() => expect(screen.getByText("Page not found")).toBeTruthy());
    expect(screen.queryByTestId("mentor-page")).toBeNull();
  });

  it("MentorView_OwnAccount_GatewayOffersNoPersonalPage_IsTheOrdinaryMissingPage", async () => {
    stage("personal", { kind: "not-offered" });
    renderAs({ kind: "teams", teams: [], start: { where: "own-account" } }, null);

    await waitFor(() => expect(screen.getByText("Page not found")).toBeTruthy());
    expect(mentor.calls).toEqual([{ teamId: "(own account)", week: undefined }]);
  });

  it("MentorView_TeamsNotOffered_IsTheOrdinaryMissingPage", async () => {
    stage("personal", { kind: "not-offered" });
    renderAs({ kind: "not-offered", reason: "Teams are off." }, null);

    await waitFor(() => expect(screen.getByText("Page not found")).toBeTruthy());
    expect(mentor.calls.every((c) => c.teamId === "(own account)")).toBe(true);
  });

  it("MentorView_OwnAccount_ShowsThePersonsOwnWeek_WithNoNameOverIt_AndOnlyYouReadsIt", async () => {
    stage("personal", week({ teamId: null, scope: "personal", readers: [], blocks: [{ ...ROB_BLOCK, isYou: true, role: "Personal account" }] }));
    renderAs({ kind: "teams", teams: [], start: { where: "own-account" } }, null);

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Mentor");
    expect(
      screen.getByText("Your week, 28 September - 4 October. Written by the Mentor from your sessions; only you read this page."),
    ).toBeTruthy();
    expect(screen.getByText("What you worked on")).toBeTruthy();
    expect(document.body.textContent).not.toContain("rob@example.com");
    expect(screen.getByTestId("mentor-quote").textContent).toBe(ROB_QUOTE);
  });

  it("MentorView_OwnAccount_EmptyWeek_ShowsTheGatewaysSentence", async () => {
    const note = "Your first Mentor page arrives after the Mentor's first weekly run. It is written from your sessions, once a week.";
    stage("personal", week({ teamId: null, scope: "personal", readers: [], blocks: [], written: false, emptyNote: note }));
    renderAs({ kind: "teams", teams: [], start: { where: "own-account" } }, null);

    await waitFor(() => expect(screen.getByText(note)).toBeTruthy());
    expect(blocks()).toHaveLength(0);
  });

  it("MentorView_QuoteText_IsExactlyTheTextInTheResponse", async () => {
    const odd = "  fix  the signup thing\nso it doesnt break on mobile!! <b>not bold</b>  ";
    stage("default", week({ blocks: [{ ...ROB_BLOCK, quotes: [{ promptId: "p1", at: "2026-09-29T09:14:03Z", text: odd }] }] }));
    renderAs(onTeam());

    await waitFor(() => expect(screen.getAllByTestId("mentor-quote")).toHaveLength(1));
    const quote = screen.getByTestId("mentor-quote");
    expect(quote.tagName).toBe("BLOCKQUOTE");
    expect(quote.textContent).toBe(odd);
    expect(quote.querySelector("b")).toBeNull();
  });

  it("MentorView_WrittenEmptyWeek_RendersNoInventedBlock", async () => {
    stage("default", week({ written: true, blocks: [] }));
    renderAs(onTeam());

    await waitFor(() => expect(screen.getByText("No block was written for anyone this week.")).toBeTruthy());
    expect(blocks()).toHaveLength(0);
  });

  it("MentorView_WrittenWeekWithNoBlockForYou_SaysSo_AndNeverWhy", async () => {
    // The Gateway does not say why there is no block (no sessions, no prompts, or an answer it refused), so the page
    // does not claim one (review G7).
    stage("default", week({ scope: "own", written: true, blocks: [] }));
    renderAs(onTeam());

    await waitFor(() => expect(screen.getByText("No block was written for you this week.")).toBeTruthy());
    expect(blocks()).toHaveLength(0);
    expect(screen.getByTestId("mentor-page").textContent ?? "").not.toMatch(/ran sessions|no sessions/i);
  });

  it("MentorView_AWeekStillBeingWritten_ShowsTheBlocksSoFar_UnderTheGatewaysLine", async () => {
    // Review H1: written false WITH blocks - the Mentor saved some blocks and is waiting on its model for someone else.
    const line = "The Mentor is still writing this week. More blocks may follow.";
    stage("default", week({ written: false, writingNote: line }));
    renderAs(onTeam());

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(screen.getByTestId("mentor-writing-note").textContent).toBe(line);
    expect(screen.queryByText("The Mentor has not written this week yet.")).toBeNull();
  });

  it("MentorView_AWrittenWeek_ShowsNoStillWritingLine", async () => {
    stage("default", week({}));
    renderAs(onTeam());

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(screen.queryByTestId("mentor-writing-note")).toBeNull();
  });

  it("MentorView_UnwrittenWeek_SaysSoAndRendersNoBlock", async () => {
    stage("default", week({ written: false, blocks: [] }));
    renderAs(onTeam());

    await waitFor(() => expect(screen.getByText("The Mentor has not written this week yet.")).toBeTruthy());
    expect(blocks()).toHaveLength(0);
  });

  it("MentorView_HasNoLeaderboardOrRanking_AndKeepsTheGatewaysOrder", async () => {
    const zed: MentorBlock = { ...ROB_BLOCK, personEmail: "zed@example.com", tone: "good", toneLabel: "a good week" };
    const amy: MentorBlock = { ...ROB_BLOCK, personEmail: "amy@example.com", tone: "hard", toneLabel: "a hard week" };
    stage("default", week({ blocks: [zed, amy] }));
    renderAs(onTeam());

    await waitFor(() => expect(blocks()).toHaveLength(2));
    // Not sorted by name, not sorted by how the week went: the Gateway's order, as sent.
    expect(blocks().map((b) => b.getAttribute("aria-label"))).toEqual(["zed@example.com", "amy@example.com"]);
    // No table, no numbered list, and no word or number that ranks or scores people.
    const page = screen.getByTestId("mentor-page");
    expect(page.querySelector("table, ol, [role='table'], [role='grid'], meter, progress")).toBeNull();
    expect(page.textContent ?? "").not.toMatch(/leaderboard|rank|score|lines of code|top performer|#\d/i);
  });

  it("MentorView_HasNoLinkThatBrowsesWhatAPersonTyped", async () => {
    stage("default", week({}));
    renderAs(onTeam());

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(screen.getByTestId("mentor-page").querySelectorAll("a")).toHaveLength(0);
  });

  it("MentorView_Footer_SaysTheMentorIsAnAI", async () => {
    stage("default", week({}));
    renderAs(onTeam());

    await waitFor(() => expect(screen.getByTestId("mentor-footer")).toBeTruthy());
    expect(screen.getByTestId("mentor-footer").textContent).toContain("The Mentor is an AI.");
  });

  it("MentorView_WeekChooser_AsksForThePreviousWeek_AndCannotGoPastTheLatest", async () => {
    stage("default", week({}));
    stage("2026-W39", week({ ...W39, blocks: [] }));
    stage("2026-W40", week({}));
    renderAs(onTeam());

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect((screen.getByRole("button", { name: "Next week" }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: "Previous week" }));
    await waitFor(() => expect(screen.getByTestId("mentor-week").textContent).toBe("Week of 21 September to 27 September"));
    expect(mentor.calls.at(-1)).toEqual({ teamId: TEAM_ID, week: "2026-W39" });
    expect(blocks()).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: "Next week" }));
    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(mentor.calls.at(-1)).toEqual({ teamId: TEAM_ID, week: "2026-W40" });
    expect((screen.getByRole("button", { name: "Next week" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("MentorView_WeekChooser_StaysWhileAWeekLoads_AndCanBeClickedTwiceInARow", async () => {
    stage("default", week({}));
    stage("2026-W39", new Promise<MentorAnswer>(() => {}));
    stage("2026-W38", week({ ...W38, blocks: [] }));
    renderAs(onTeam());

    await waitFor(() => expect(blocks()).toHaveLength(1));
    fireEvent.click(screen.getByRole("button", { name: "Previous week" }));
    // Week 39 never answers: the chooser is still there, naming the week being asked for.
    await waitFor(() => expect(screen.getByTestId("mentor-week").textContent).toBe("Week of 21 September"));
    expect(screen.getByRole("status").textContent).toContain("Loading");

    fireEvent.click(screen.getByRole("button", { name: "Previous week" }));
    await waitFor(() => expect(screen.getByTestId("mentor-week").textContent).toBe("Week of 14 September to 20 September"));
    expect(mentor.calls.map((c) => c.week)).toEqual([undefined, "2026-W39", "2026-W38"]);
  });

  it("MentorView_FailedChosenWeek_KeepsTheChooser_SaysWhy_AndCanStepAway", async () => {
    stage("default", week({}));
    stage("2026-W39", failure("The Mentor's page for that week could not be read because of a fault."));
    stage("2026-W40", week({}));
    renderAs(onTeam());

    await waitFor(() => expect(blocks()).toHaveLength(1));
    fireEvent.click(screen.getByRole("button", { name: "Previous week" }));

    await waitFor(() => expect(screen.getByText(/could not be read because of a fault/)).toBeTruthy());
    expect(screen.getByTestId("mentor-week").textContent).toBe("Week of 21 September");
    expect(blocks()).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: "Next week" }));
    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(screen.queryByText(/could not be read because of a fault/)).toBeNull();
  });

  it("MentorView_FailedRead_SaysWhy_AndTryAgainAsksAgain", async () => {
    stage("default", failure("The Mentor's page could not be read because of a fault."), week({}));
    renderAs(onTeam());

    await waitFor(() => expect(screen.getByText(/could not be read because of a fault/)).toBeTruthy());
    expect(blocks()).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(mentor.calls).toHaveLength(2);
  });
});

describe("readersSentence", () => {
  it("ReadersSentence_OneManager_NamesThemAsYourManager", () => {
    expect(readersSentence([{ email: "priya@example.com", role: "Manager" }])).toBe(
      "priya@example.com, your Manager, reads this same page.",
    );
  });

  it("ReadersSentence_OnlyTheOwner_NamesThemAsTheTeamsOwner", () => {
    expect(readersSentence([{ email: "olivia@example.com", role: "Owner" }])).toBe(
      "olivia@example.com, the team's Owner, reads this same page.",
    );
  });

  it("ReadersSentence_ThreeReaders_ListsEveryoneInTheGatewaysOrder", () => {
    expect(
      readersSentence([
        { email: "olivia@example.com", role: "Owner" },
        { email: "priya@example.com", role: "Manager" },
        { email: "sam@example.com", role: "Manager" },
      ]),
    ).toBe(
      "olivia@example.com (the team's Owner), priya@example.com (your Manager) and sam@example.com (your Manager) read this same page.",
    );
  });

  it("ReadersSentence_NoEmailOnRecord_NamesTheRoleNeverAMadeUpName", () => {
    expect(readersSentence([{ email: null, role: "Manager" }])).toBe(
      "Manager - no email on record, your Manager, reads this same page.",
    );
  });
});
