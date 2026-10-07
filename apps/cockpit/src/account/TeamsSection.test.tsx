// @vitest-environment jsdom
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { render, cleanup, screen, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useParams } from "react-router-dom";
import { CurrentTeamProvider, useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import type { MyTeamsAnswer, TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import { TeamSwitcher } from "../teams/TeamSwitcher";
import { TeamsSection } from "./TeamsSection";

// The Teams section of the Account page (Teams v1): hidden on a Gateway with no teams, lists the person's teams with
// their roles, and creates a team - which refreshes the shared list, puts the team on screen and opens its Team page.

const FULL = { full: true as const, pages: [], landing: null, elsewhere: null };
const PAULS: TeamSummary = { id: "team-paul", name: "Paul's project", role: "Developer", memberCount: 2, people: "2 people", app: FULL };
const CREATED: TeamSummary = { id: "team-new", name: "Soren Test Team", role: "Owner", memberCount: 1, people: "1 person", app: FULL };
const CREATED_WIRE = { ...CREATED };

function teamsAnswer(teams: TeamSummary[]): MyTeamsAnswer {
  return { kind: "teams", teams, start: { where: "own-account" } };
}

function CurrentProbe() {
  const { current } = useCurrentTeam();
  return <div data-testid="probe">{current === null ? "own account" : current.name}</div>;
}

function TeamPageProbe() {
  const { teamId } = useParams();
  return <div data-testid="team-page">{teamId}</div>;
}

function renderSection(load: () => Promise<MyTeamsAnswer>) {
  return render(
    <MemoryRouter initialEntries={["/account"]}>
      <CurrentTeamProvider load={load}>
        <TeamSwitcher />
        <CurrentProbe />
        <Routes>
          <Route path="/account" element={<TeamsSection />} />
          <Route path="/team/:teamId/members" element={<TeamPageProbe />} />
        </Routes>
      </CurrentTeamProvider>
    </MemoryRouter>,
  );
}

function jsonResponse(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function typeName(value: string) {
  fireEvent.change(screen.getByRole("textbox"), { target: { value } });
}

describe("TeamsSection", () => {
  beforeEach(() => {
    cleanup();
    window.localStorage.clear();
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("TeamsSection_TeamsNotOffered_RendersNothing", async () => {
    const load = vi.fn().mockResolvedValue({ kind: "not-offered", reason: "This Gateway has not turned Teams on." });
    renderSection(load);

    await waitFor(() => expect(load).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByTestId("probe").textContent).toBe("own account"));
    expect(screen.queryByTestId("account-teams")).toBeNull();
    expect(screen.queryByText("Create a team")).toBeNull();
  });

  it("TeamsSection_StillLoading_RendersNothing", () => {
    renderSection(() => new Promise<MyTeamsAnswer>(() => {}));
    expect(screen.queryByTestId("account-teams")).toBeNull();
  });

  it("TeamsSection_NoTeams_OffersCreateAndTheSwitcherStaysHidden", async () => {
    renderSection(() => Promise.resolve(teamsAnswer([])));

    await waitFor(() => expect(screen.getByTestId("account-teams")).toBeTruthy());
    expect(screen.getByText("You are not in a team.")).toBeTruthy();
    expect(screen.getByText("A team has one Owner, who pays for it. You become the Owner of the team you create.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Create" })).toBeTruthy();
    // The rail is unchanged for a person with no team.
    expect(screen.queryByTestId("team-switcher")).toBeNull();
  });

  it("TeamsSection_SomeTeams_ListsEachWithTheRole", async () => {
    renderSection(() => Promise.resolve(teamsAnswer([PAULS, CREATED])));

    await waitFor(() => expect(screen.getAllByTestId("account-team-row")).toHaveLength(2));
    const rows = screen.getAllByTestId("account-team-row").map((r) => r.textContent);
    expect(rows).toEqual(["Paul's projectDeveloper - 2 people", "Soren Test TeamOwner - 1 person"]);
  });

  it("TeamsSection_CreateSucceeds_RefreshesTheListChoosesTheTeamAndOpensItsTeamPage", async () => {
    const load = vi
      .fn<() => Promise<MyTeamsAnswer>>()
      .mockResolvedValueOnce(teamsAnswer([]))
      .mockResolvedValue(teamsAnswer([CREATED]));
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ team: CREATED_WIRE }, 201));
    vi.stubGlobal("fetch", fetchMock);
    renderSection(load);

    await waitFor(() => expect(screen.getByTestId("account-teams")).toBeTruthy());
    typeName("Soren Test Team");
    fireEvent.click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(screen.getByTestId("team-page").textContent).toBe("team-new"));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe("/teams");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toEqual({ name: "Soren Test Team" });
    // The list was read again, the switcher shows the new team, and it is the team on screen.
    expect(load).toHaveBeenCalledTimes(2);
    expect(screen.getByTestId("team-switcher")).toBeTruthy();
    expect(screen.getByTestId("probe").textContent).toBe("Soren Test Team");
  });

  it("TeamsSection_GatewayRefuses_ShowsTheGatewaysOwnSentence", async () => {
    const load = vi.fn().mockResolvedValue(teamsAnswer([]));
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({ error: "A team needs a name." }, 400)));
    renderSection(load);

    await waitFor(() => expect(screen.getByTestId("account-teams")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(screen.getByRole("alert").textContent).toBe("A team needs a name."));
    // Nothing moved: still the Account page, still the own account, and the button is usable again.
    expect(screen.queryByTestId("team-page")).toBeNull();
    expect(screen.getByTestId("probe").textContent).toBe("own account");
    expect((screen.getByRole("button", { name: "Create" }) as HTMLButtonElement).disabled).toBe(false);
    expect(load).toHaveBeenCalledTimes(1);
  });

  it("TeamsSection_CreateClickedTwice_SendsOneRequest", async () => {
    let answer: (r: Response) => void = () => {};
    const fetchMock = vi.fn().mockReturnValue(new Promise<Response>((resolve) => (answer = resolve)));
    vi.stubGlobal("fetch", fetchMock);
    renderSection(
      vi
        .fn<() => Promise<MyTeamsAnswer>>()
        .mockResolvedValueOnce(teamsAnswer([]))
        .mockResolvedValue(teamsAnswer([CREATED])),
    );

    await waitFor(() => expect(screen.getByTestId("account-teams")).toBeTruthy());
    typeName("Soren Test Team");
    const form = screen.getByRole("button", { name: "Create" }).closest("form")!;
    // Two submits in the same tick - before React has drawn the disabled button.
    fireEvent.submit(form);
    fireEvent.submit(form);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect((screen.getByRole("button", { name: "Creating..." }) as HTMLButtonElement).disabled).toBe(true);
    answer(jsonResponse({ team: CREATED_WIRE }, 201));
    await waitFor(() => expect(screen.getByTestId("team-page").textContent).toBe("team-new"));
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
