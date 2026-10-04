// @vitest-environment jsdom
import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, cleanup, screen, waitFor, act } from "@testing-library/react";
import { CurrentTeamProvider, currentTeamStorageKey, useCurrentTeam, type CurrentTeamState } from "./CurrentTeam";
import type { MyTeamsAnswer, TeamSummary } from "./teamsClient";

// The ONE current team a shell has (devthrottle_internal#2312). The switcher writes it; the Fleet Map, the Team page
// and the Skills page read it. These pin what every reader may rely on.

const accountState = vi.hoisted(() => ({ id: "account-a" as string | null }));
vi.mock("../auth/accountStore", () => ({
  activeAccount: () => (accountState.id === null ? null : { id: accountState.id }),
}));

const TEAM_A: TeamSummary = { id: "team-a", name: "Alpha", role: "Manager", memberCount: 3, people: "3 people" };
const TEAM_B: TeamSummary = { id: "team-b", name: "Beta", role: "Developer", memberCount: 2, people: "2 people" };

let seen: CurrentTeamState | null = null;
function Probe() {
  seen = useCurrentTeam();
  return null;
}

function mount(load: () => Promise<MyTeamsAnswer>) {
  render(
    <CurrentTeamProvider load={load}>
      <Probe />
    </CurrentTeamProvider>,
  );
}

const teams = (list: TeamSummary[]) => () => Promise.resolve<MyTeamsAnswer>({ kind: "teams", teams: list });

describe("CurrentTeam", () => {
  beforeEach(() => {
    cleanup();
    seen = null;
    accountState.id = "account-a";
    window.localStorage.clear();
  });

  it("CurrentTeamProvider_BeforeTheGatewayAnswers_IsLoadingWithNoTeam", () => {
    mount(() => new Promise<MyTeamsAnswer>(() => {}));
    expect(seen!.status).toBe("loading");
    expect(seen!.current).toBeNull();
  });

  it("CurrentTeamProvider_Teams_StartsOnTheOwnAccount", async () => {
    mount(teams([TEAM_A, TEAM_B]));
    await waitFor(() => expect(seen!.status).toBe("ready"));
    expect(seen!.teams.map((t) => t.id)).toEqual(["team-a", "team-b"]);
    expect(seen!.current).toBeNull();
  });

  it("CurrentTeamProvider_NotOffered_HasNoTeams", async () => {
    mount(() => Promise.resolve<MyTeamsAnswer>({ kind: "not-offered", reason: "dark" }));
    await waitFor(() => expect(seen!.status).toBe("not-offered"));
    expect(seen!.teams).toEqual([]);
    expect(seen!.current).toBeNull();
  });

  it("CurrentTeamProvider_ReadFails_ReportsTheErrorAndNoTeam", async () => {
    mount(() => Promise.reject(new Error("boom")));
    await waitFor(() => expect(seen!.status).toBe("error"));
    expect(seen!.error).toBeTruthy();
    expect(seen!.current).toBeNull();
  });

  it("Choose_ATeam_PutsItOnScreenAndRemembersItForThisAccount", async () => {
    mount(teams([TEAM_A, TEAM_B]));
    await waitFor(() => expect(seen!.status).toBe("ready"));

    act(() => seen!.choose("team-b"));
    expect(seen!.current?.id).toBe("team-b");
    expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBe("team-b");

    act(() => seen!.choose(null));
    expect(seen!.current).toBeNull();
    expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBeNull();
  });

  it("Choose_ATeamThatIsNotTheirs_IsRefused", async () => {
    mount(teams([TEAM_A]));
    await waitFor(() => expect(seen!.status).toBe("ready"));
    expect(() => seen!.choose("team-someone-elses")).toThrow(/not one of yours/);
    expect(seen!.current).toBeNull();
  });

  it("CurrentTeamProvider_RememberedTeam_IsRestoredOnTheNextLoad", async () => {
    window.localStorage.setItem("devthrottle.currentTeam.account-a", "team-a");
    mount(teams([TEAM_A, TEAM_B]));
    await waitFor(() => expect(seen!.current?.id).toBe("team-a"));
  });

  it("CurrentTeamProvider_RememberedTeamNoLongerListed_GoesBackToTheOwnAccountAndForgetsIt", async () => {
    window.localStorage.setItem("devthrottle.currentTeam.account-a", "team-left");
    mount(teams([TEAM_A]));
    await waitFor(() => expect(seen!.status).toBe("ready"));
    expect(seen!.current).toBeNull();
    await waitFor(() => expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBeNull());
  });

  it("CurrentTeamStorageKey_IsPerAccount_SoAnotherAccountDoesNotInheritTheChoice", async () => {
    window.localStorage.setItem("devthrottle.currentTeam.account-a", "team-a");
    accountState.id = "account-b";
    expect(currentTeamStorageKey()).toBe("devthrottle.currentTeam.account-b");

    mount(teams([TEAM_A]));
    await waitFor(() => expect(seen!.status).toBe("ready"));
    expect(seen!.current).toBeNull();
  });

  it("UseCurrentTeam_OutsideTheProvider_Throws", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    expect(() => render(<Probe />)).toThrow(/outside a CurrentTeamProvider/);
    spy.mockRestore();
    expect(screen.queryByText("anything")).toBeNull();
  });
});
