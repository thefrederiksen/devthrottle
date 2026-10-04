// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup, screen, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { CurrentTeamProvider, currentTeamStorageKey } from "@devthrottle/client-core/teams/CurrentTeam";
import type { MyTeamsAnswer, TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import type { MentorAnswer, MentorBlock, MentorPage } from "@devthrottle/client-core/teams/mentorClient";

// The Mentor's weekly page (devthrottle_internal#2305, screens S6 and S7) for each role, against the contract's data:
// a test team of two - Priya the Manager, who ran no sessions and so has no block, and Rob the Developer, who has one.
// The page renders what the Gateway sent and decides nothing; these tests hold it to that.

const mentor = vi.hoisted(() => ({
  answers: new Map<string, unknown>(),
  calls: [] as Array<{ teamId: string; week: string | undefined }>,
}));
vi.mock("@devthrottle/client-core/teams/mentorClient", async (importActual) => {
  const actual = await importActual<typeof import("@devthrottle/client-core/teams/mentorClient")>();
  return {
    ...actual,
    getMentorPage: vi.fn(async (teamId: string, week?: string) => {
      mentor.calls.push({ teamId, week });
      const answer = mentor.answers.get(week ?? "default");
      if (answer === undefined) throw new Error(`no answer staged for week ${week ?? "default"}`);
      if (answer instanceof Error) throw answer;
      return answer;
    }),
  };
});

import { GatewayError } from "@devthrottle/client-core/api/client";
import { MentorView } from "./MentorView";

const TEAM_ID = "team-test";
const team = (role: string): TeamSummary => ({ id: TEAM_ID, name: "Teams test", role, memberCount: 2, people: "2 people" });

const ROB_QUOTE = "fix the signup thing so it doesnt break on mobile";

const ROB_BLOCK: MentorBlock = {
  personSubject: "sub-rob",
  personEmail: "rob@example.com",
  role: "Developer",
  tone: "hard",
  toneLabel: "a hard week",
  workedOn: "The new signup page and two bug fixes in the installer.",
  howItWent: null,
  wentBadlyAndWhy:
    "On Tuesday the same task was restarted four times. The first instruction didn't say which file to change, so the agent guessed differently each time.",
  quotes: [{ promptId: "p_3f9a", at: "2026-09-29T09:14:03Z", text: ROB_QUOTE }],
  oneThingToTry: "Name the file and the result you expect in the first line, before asking for the change.",
  writtenAtUtc: "2026-10-05T00:20:11Z",
};

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
      blocks: [ROB_BLOCK],
      readers: [{ email: "priya@example.com", role: "Manager" }],
      ...overrides,
    },
  };
}

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

function blocks(): HTMLElement[] {
  return screen.queryAllByTestId("mentor-block");
}

describe("MentorView", () => {
  beforeEach(() => {
    cleanup();
    window.localStorage.clear();
    mentor.answers.clear();
    mentor.calls.length = 0;
  });

  it("MentorView_Manager_ShowsEveryBlockTheGatewaySent_AndNoneForThePersonWithNoSessions", async () => {
    mentor.answers.set("default", week({ scope: "everyone" }));
    renderAs({ kind: "teams", teams: [team("Manager")] });

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Mentor - week of 28 September");
    expect(screen.getByText("Each person reads their own block, word for word.")).toBeTruthy();
    expect(blocks()[0].getAttribute("aria-label")).toBe("rob@example.com");
    // Priya ran no sessions: the page says nothing about her at all - no block, no line, no placeholder.
    expect(document.body.textContent).not.toContain("priya@example.com");
    expect(mentor.calls).toEqual([{ teamId: TEAM_ID, week: undefined }]);
  });

  it("MentorView_Owner_ShowsTheSameBlocksAsTheManager", async () => {
    mentor.answers.set("default", week({ scope: "everyone" }));
    renderAs({ kind: "teams", teams: [team("Owner")] });

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(blocks()[0].textContent).toContain(ROB_BLOCK.workedOn);
  });

  it("MentorView_Developer_ShowsOnlyTheirOwnBlock_WordForWordWhatTheManagerReads", async () => {
    mentor.answers.set("default", week({ scope: "everyone" }));
    const manager = renderAs({ kind: "teams", teams: [team("Manager")] });
    await waitFor(() => expect(blocks()).toHaveLength(1));
    const managersCopy = blocks()[0].outerHTML;
    manager.unmount();
    window.localStorage.clear();

    mentor.answers.set("default", week({ scope: "own" }));
    renderAs({ kind: "teams", teams: [team("Developer")] });

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Your week, from the Mentor");
    // The page tells Rob who else reads it, worded from the Gateway's list of readers.
    expect(screen.getByText("priya@example.com, your Manager, reads this same page.")).toBeTruthy();
    // The same stored row, drawn by the same component: not one character differs from the Manager's copy.
    expect(blocks()[0].outerHTML).toBe(managersCopy);
  });

  it("MentorView_Developer_RendersOnlyWhatTheGatewayReturned", async () => {
    // Only the Developer's own block comes back; nothing on the page may add anyone else.
    mentor.answers.set("default", week({ scope: "own", blocks: [ROB_BLOCK] }));
    renderAs({ kind: "teams", teams: [team("Developer")] });

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(blocks().map((b) => b.getAttribute("aria-label"))).toEqual(["rob@example.com"]);
  });

  it("MentorView_Collaborator_HasNoPage", async () => {
    mentor.answers.set("default", { kind: "refused", reason: "A Collaborator has no Mentor page." });
    renderAs({ kind: "teams", teams: [team("Collaborator")] });

    await waitFor(() => expect(screen.getByText("Page not found")).toBeTruthy());
    expect(screen.queryByTestId("mentor-page")).toBeNull();
    expect(blocks()).toHaveLength(0);
  });

  it("MentorView_NotOnATeam_IsTheOrdinaryMissingPage_AndAsksNothing", async () => {
    renderAs({ kind: "teams", teams: [] }, null);

    await waitFor(() => expect(screen.getByText("Page not found")).toBeTruthy());
    expect(mentor.calls).toEqual([]);
  });

  it("MentorView_TeamsNotOffered_IsTheOrdinaryMissingPage", async () => {
    renderAs({ kind: "not-offered", reason: "Teams are off." }, null);

    await waitFor(() => expect(screen.getByText("Page not found")).toBeTruthy());
    expect(mentor.calls).toEqual([]);
  });

  it("MentorView_QuoteText_IsExactlyTheTextInTheResponse", async () => {
    const odd = "  fix  the signup thing\nso it doesnt break on mobile!! <b>not bold</b>  ";
    mentor.answers.set(
      "default",
      week({ blocks: [{ ...ROB_BLOCK, quotes: [{ promptId: "p1", at: "2026-09-29T09:14:03Z", text: odd }] }] }),
    );
    renderAs({ kind: "teams", teams: [team("Manager")] });

    await waitFor(() => expect(screen.getAllByTestId("mentor-quote")).toHaveLength(1));
    const quote = screen.getByTestId("mentor-quote");
    expect(quote.tagName).toBe("BLOCKQUOTE");
    expect(quote.textContent).toBe(odd);
    expect(quote.querySelector("b")).toBeNull();
  });

  it("MentorView_WrittenEmptyWeek_RendersNoInventedBlock", async () => {
    mentor.answers.set("default", week({ written: true, blocks: [] }));
    renderAs({ kind: "teams", teams: [team("Manager")] });

    await waitFor(() =>
      expect(
        screen.getByText("The Mentor wrote nothing for this week. It writes only about someone who ran sessions that week."),
      ).toBeTruthy(),
    );
    expect(blocks()).toHaveLength(0);
  });

  it("MentorView_UnwrittenWeek_SaysSoAndRendersNoBlock", async () => {
    mentor.answers.set("default", week({ written: false, blocks: [] }));
    renderAs({ kind: "teams", teams: [team("Manager")] });

    await waitFor(() => expect(screen.getByText("The Mentor has not written this week yet.")).toBeTruthy());
    expect(blocks()).toHaveLength(0);
  });

  it("MentorView_HasNoLeaderboardOrRanking_AndKeepsTheGatewaysOrder", async () => {
    const zed: MentorBlock = { ...ROB_BLOCK, personSubject: "sub-zed", personEmail: "zed@example.com", tone: "good", toneLabel: "a good week" };
    const amy: MentorBlock = { ...ROB_BLOCK, personSubject: "sub-amy", personEmail: "amy@example.com", tone: "hard", toneLabel: "a hard week" };
    mentor.answers.set("default", week({ blocks: [zed, amy] }));
    renderAs({ kind: "teams", teams: [team("Owner")] });

    await waitFor(() => expect(blocks()).toHaveLength(2));
    // Not sorted by name, not sorted by how the week went: the Gateway's order, as sent.
    expect(blocks().map((b) => b.getAttribute("aria-label"))).toEqual(["zed@example.com", "amy@example.com"]);
    // No table, no numbered list, and no word or number that ranks or scores people.
    const page = screen.getByTestId("mentor-page");
    expect(page.querySelector("table, ol, [role='table'], [role='grid'], meter, progress")).toBeNull();
    expect(page.textContent ?? "").not.toMatch(/leaderboard|rank|score|lines of code|top performer|#\d/i);
  });

  it("MentorView_HasNoLinkThatBrowsesWhatAPersonTyped", async () => {
    mentor.answers.set("default", week({}));
    renderAs({ kind: "teams", teams: [team("Manager")] });

    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(screen.getByTestId("mentor-page").querySelectorAll("a")).toHaveLength(0);
  });

  it("MentorView_Footer_SaysTheMentorIsAnAI", async () => {
    mentor.answers.set("default", week({}));
    renderAs({ kind: "teams", teams: [team("Manager")] });

    await waitFor(() => expect(screen.getByTestId("mentor-footer")).toBeTruthy());
    expect(screen.getByTestId("mentor-footer").textContent).toContain("The Mentor is an AI.");
  });

  it("MentorView_WeekChooser_AsksForThePreviousWeek_AndCannotGoPastTheLatest", async () => {
    mentor.answers.set("default", week({}));
    mentor.answers.set(
      "2026-W39",
      week({ week: "2026-W39", weekStart: "2026-09-21", weekEnd: "2026-09-27", written: true, blocks: [] }),
    );
    mentor.answers.set("2026-W40", week({}));
    renderAs({ kind: "teams", teams: [team("Manager")] });

    await waitFor(() => expect(blocks()).toHaveLength(1));
    const next = screen.getByRole("button", { name: "Next week" }) as HTMLButtonElement;
    expect(next.disabled).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: "Previous week" }));
    await waitFor(() => expect(screen.getByTestId("mentor-week").textContent).toBe("Week of 21 September to 27 September"));
    expect(mentor.calls.at(-1)).toEqual({ teamId: TEAM_ID, week: "2026-W39" });
    expect(blocks()).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: "Next week" }));
    await waitFor(() => expect(blocks()).toHaveLength(1));
    expect(mentor.calls.at(-1)).toEqual({ teamId: TEAM_ID, week: "2026-W40" });
  });

  it("MentorView_FailedRead_SaysWhy", async () => {
    mentor.answers.set("default", new GatewayError(500, "The Mentor's page could not be read because of a fault.", { reason: "The Mentor's page could not be read because of a fault." }));
    renderAs({ kind: "teams", teams: [team("Manager")] });

    await waitFor(() => expect(screen.getByText(/could not be read because of a fault/)).toBeTruthy());
    expect(blocks()).toHaveLength(0);
  });
});
