// @vitest-environment jsdom
import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, cleanup, screen, waitFor, act } from "@testing-library/react";
import { CurrentTeamProvider, currentTeamStorageKey, retryDelayMs, useCurrentTeam, type CurrentTeamState } from "./CurrentTeam";
import type { MyTeamsAnswer, TeamSummary } from "./teamsClient";

// The ONE current team a shell has (devthrottle_internal#2312). The switcher writes it; the Fleet Map, the Team page
// and the Skills page read it. These pin what every reader may rely on.

const accountState = vi.hoisted(() => ({ id: "account-a" as string | null }));
vi.mock("../auth/accountStore", () => ({
  activeAccount: () => (accountState.id === null ? null : { id: accountState.id }),
}));

const TEAM_A: TeamSummary = { id: "team-a", name: "Alpha", role: "Manager", memberCount: 3, people: "3 people", app: { full: true, pages: [], landing: null, elsewhere: null } };
const TEAM_B: TeamSummary = { id: "team-b", name: "Beta", role: "Developer", memberCount: 2, people: "2 people", app: { full: true, pages: [], landing: null, elsewhere: null } };

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

  // Review finding F1: one failed read must not leave the tab without its teams until a reload.
  it("CurrentTeamProvider_ReadFails_IsAskedAgainWithBackoff_AndRecovers", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      window.localStorage.setItem("devthrottle.currentTeam.account-a", "team-b");
      const load = vi
        .fn<() => Promise<MyTeamsAnswer>>()
        .mockRejectedValueOnce(new Error("502"))
        .mockRejectedValueOnce(new Error("502"))
        .mockResolvedValue({ kind: "teams", teams: [TEAM_A, TEAM_B] });
      mount(load);

      await waitFor(() => expect(seen!.status).toBe("error"));
      expect(load).toHaveBeenCalledTimes(1);

      await act(() => vi.advanceTimersByTimeAsync(14_000));
      expect(load).toHaveBeenCalledTimes(1);
      await act(() => vi.advanceTimersByTimeAsync(1_000));
      expect(load).toHaveBeenCalledTimes(2);

      await act(() => vi.advanceTimersByTimeAsync(30_000));
      expect(load).toHaveBeenCalledTimes(3);
      await waitFor(() => expect(seen!.status).toBe("ready"));
      // The remembered team is back on screen once the Gateway answers.
      expect(seen!.current?.id).toBe("team-b");

      // And nothing asks again once it has succeeded.
      await act(() => vi.advanceTimersByTimeAsync(120_000));
      expect(load).toHaveBeenCalledTimes(3);
    } finally {
      vi.useRealTimers();
    }
  });

  it("RetryDelayMs_BacksOff_15Then30ThenEvery60", () => {
    expect([1, 2, 3, 4, 10].map(retryDelayMs)).toEqual([15_000, 30_000, 60_000, 60_000, 60_000]);
  });

  // Review finding F3: a null current is not "the own account" while a remembered team is unconfirmed.
  it("Resolving_ARememberedTeam_IsTrueWhileUnconfirmed_AndFalseOnceRead", async () => {
    window.localStorage.setItem("devthrottle.currentTeam.account-a", "team-a");
    let answer!: (a: MyTeamsAnswer) => void;
    mount(() => new Promise<MyTeamsAnswer>((resolve) => (answer = resolve)));

    expect(seen!.current).toBeNull();
    expect(seen!.resolving).toBe(true);

    act(() => answer({ kind: "teams", teams: [TEAM_A] }));
    await waitFor(() => expect(seen!.current?.id).toBe("team-a"));
    expect(seen!.resolving).toBe(false);
  });

  it("Resolving_ARememberedTeamAndTheReadFails_StaysTrue", async () => {
    window.localStorage.setItem("devthrottle.currentTeam.account-a", "team-a");
    mount(() => Promise.reject(new Error("boom")));
    await waitFor(() => expect(seen!.status).toBe("error"));
    expect(seen!.resolving).toBe(true);
  });

  it("Resolving_NoRememberedTeam_IsNeverTrue", async () => {
    mount(() => Promise.reject(new Error("boom")));
    expect(seen!.resolving).toBe(false);
    await waitFor(() => expect(seen!.status).toBe("error"));
    expect(seen!.resolving).toBe(false);
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
