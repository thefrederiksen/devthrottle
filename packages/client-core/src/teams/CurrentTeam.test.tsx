// @vitest-environment jsdom
import { describe, it, expect, beforeEach, vi } from "vitest";
import { useEffect, useRef } from "react";
import { render, cleanup, screen, waitFor, act } from "@testing-library/react";
import {
  CurrentTeamProvider,
  currentTeamStorageKey,
  rememberTeamOnThisBrowser,
  retryDelayMs,
  useCurrentTeam,
  type CurrentTeamState,
} from "./CurrentTeam";
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

const teams = (list: TeamSummary[]) => () => Promise.resolve<MyTeamsAnswer>({ kind: "teams", teams: list, start: { where: "own-account" } });

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

  // Teams v1: a team created on the Account page is read in at once and can be put on screen in the same tick.
  it("Refresh_AfterATeamIsCreated_ListsItAndChooseCanPickItAtOnce", async () => {
    const load = vi
      .fn<() => Promise<MyTeamsAnswer>>()
      .mockResolvedValueOnce({ kind: "teams", teams: [], start: { where: "own-account" } })
      .mockResolvedValue({ kind: "teams", teams: [TEAM_A], start: { where: "own-account" } });
    mount(load);
    await waitFor(() => expect(seen!.status).toBe("ready"));
    expect(seen!.teams).toEqual([]);

    await act(async () => {
      await seen!.refresh();
      // Before React draws the new list: choose must already know the team.
      expect(seen!.choose("team-a")).toEqual(TEAM_A);
    });

    expect(load).toHaveBeenCalledTimes(2);
    expect(seen!.teams.map((t) => t.id)).toEqual(["team-a"]);
    expect(seen!.current?.id).toBe("team-a");
    expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBe("team-a");
  });

  it("Refresh_ReadFails_ThrowsAndLeavesTheListAsItWas", async () => {
    const load = vi
      .fn<() => Promise<MyTeamsAnswer>>()
      .mockResolvedValueOnce({ kind: "teams", teams: [TEAM_B], start: { where: "own-account" } })
      .mockRejectedValue(new Error("boom"));
    mount(load);
    await waitFor(() => expect(seen!.status).toBe("ready"));

    await act(async () => {
      await expect(seen!.refresh()).rejects.toThrow("boom");
    });
    expect(seen!.status).toBe("ready");
    expect(seen!.teams.map((t) => t.id)).toEqual(["team-b"]);
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
        .mockResolvedValue({ kind: "teams", teams: [TEAM_A, TEAM_B], start: { where: "own-account" } });
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

    act(() => answer({ kind: "teams", teams: [TEAM_A], start: { where: "own-account" } }));
    await waitFor(() => expect(seen!.current?.id).toBe("team-a"));
    expect(seen!.resolving).toBe(false);
  });

  it("Resolving_ARememberedTeamAndTheReadFails_StaysTrue", async () => {
    window.localStorage.setItem("devthrottle.currentTeam.account-a", "team-a");
    mount(() => Promise.reject(new Error("boom")));
    await waitFor(() => expect(seen!.status).toBe("error"));
    expect(seen!.resolving).toBe(true);
  });

  // Review finding F1: a browser with nothing remembered waits for the Gateway's start verdict, so a Collaborator's first
  // arrival never flashes the whole app - but a failed read does not hold it: it starts on the own account, as before.
  // Delta review D1: a browser with nothing remembered never waits - not for the first answer, not on a failed read.
  it("Resolving_NothingRemembered_IsNeverTrue_AndAFailedReadStaysOnTheOwnAccount", async () => {
    mount(() => Promise.reject(new Error("boom")));
    expect(seen!.resolving).toBe(false);
    await waitFor(() => expect(seen!.status).toBe("error"));
    expect(seen!.resolving).toBe(false);
    expect(seen!.current).toBeNull();
    expect(seen!.choosing).toBe(false);
  });

  it("Choose_ATeam_PutsItOnScreenAndRemembersItForThisAccount", async () => {
    mount(teams([TEAM_A, TEAM_B]));
    await waitFor(() => expect(seen!.status).toBe("ready"));

    act(() => seen!.choose("team-b"));
    expect(seen!.current?.id).toBe("team-b");
    expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBe("team-b");

    // The own account, picked on purpose, is remembered as such - apart from "never chose", which would ask the
    // Gateway where to start again.
    act(() => seen!.choose(null));
    expect(seen!.current).toBeNull();
    expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBe("own-account");
  });

  // devthrottle#3681: an old /team/{teamId}/members link opened in a fresh browser picks its team in the SAME commit the
  // first answer arrives in - a child's effect runs before the provider's. The Gateway's start must not then overwrite
  // that pick with "own account".
  it("Choose_InTheCommitTheFirstAnswerArrives_IsKept_NotReplacedByTheGatewaysStart", async () => {
    function PickOnFirstAnswer() {
      const state = useCurrentTeam();
      seen = state;
      const picked = useRef(false);
      useEffect(() => {
        if (state.status !== "ready" || picked.current) return;
        picked.current = true;
        state.choose("team-b");
      }, [state.status]);
      return null;
    }
    render(
      <CurrentTeamProvider load={teams([TEAM_A, TEAM_B])}>
        <PickOnFirstAnswer />
      </CurrentTeamProvider>,
    );

    await waitFor(() => expect(seen!.current?.id).toBe("team-b"));
    await act(async () => new Promise((r) => setTimeout(r, 10)));
    expect(seen!.current?.id).toBe("team-b");
    expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBe("team-b");
  });

  it("Choose_ATeam_AnswersTheTeamNowOnScreen", async () => {
    mount(teams([TEAM_A, TEAM_B]));
    await waitFor(() => expect(seen!.status).toBe("ready"));
    let answered: TeamSummary | null = null;
    act(() => {
      answered = seen!.choose("team-b");
    });
    expect(answered!.id).toBe("team-b");
  });

  it("Start_NothingRemembered_TheGatewaysTeam_IsOnScreenAndRemembered", async () => {
    mount(() => Promise.resolve<MyTeamsAnswer>({ kind: "teams", teams: [TEAM_A, TEAM_B], start: { where: "team", teamId: "team-b" } }));
    await waitFor(() => expect(seen!.current?.id).toBe("team-b"));
    expect(seen!.resolving).toBe(false);
    await waitFor(() => expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBe("gateway:team-b"));
  });

  // Delta review D1: a dark Gateway, and a person with no team, never wait - and nothing is stored that would make a
  // later load wait either.
  it("NothingRemembered_TeamsDark_NeverResolving_NeverChoosing_OwnAccount", async () => {
    let answer: (a: MyTeamsAnswer) => void = () => {};
    mount(() => new Promise<MyTeamsAnswer>((resolve) => (answer = resolve)));
    expect(seen!.resolving).toBe(false);
    expect(seen!.current).toBeNull();
    act(() => answer({ kind: "not-offered", reason: "dark" }));
    await waitFor(() => expect(seen!.status).toBe("not-offered"));
    expect(seen!.resolving).toBe(false);
    expect(seen!.choosing).toBe(false);
    expect(seen!.current).toBeNull();
  });

  it("NothingRemembered_NoTeams_NeverResolving_AndStoresTheGatewaysAnswerNotAPick", async () => {
    mount(teams([]));
    expect(seen!.resolving).toBe(false);
    await waitFor(() => expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBe("gateway:own-account"));
    expect(seen!.current).toBeNull();
    expect(seen!.resolving).toBe(false);
  });

  // Delta review D2: the Gateway's stored answer is replaced by its next answer; only a pick outranks it.
  it("AStoredGatewayAnswer_IsReplacedByTheNextAnswer", async () => {
    window.localStorage.setItem("devthrottle.currentTeam.account-a", "gateway:own-account");
    mount(() => Promise.resolve<MyTeamsAnswer>({ kind: "teams", teams: [TEAM_A], start: { where: "team", teamId: "team-a" } }));
    // The own account the Gateway answered last time draws at once.
    expect(seen!.resolving).toBe(false);
    await waitFor(() => expect(seen!.current?.id).toBe("team-a"));
    expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBe("gateway:team-a");
  });

  it("AStoredGatewayTeam_WaitsForTheListLikeAPickedTeam", async () => {
    window.localStorage.setItem("devthrottle.currentTeam.account-a", "gateway:team-a");
    mount(() => Promise.resolve<MyTeamsAnswer>({ kind: "teams", teams: [TEAM_A], start: { where: "team", teamId: "team-a" } }));
    expect(seen!.resolving).toBe(true);
    await waitFor(() => expect(seen!.current?.id).toBe("team-a"));
    expect(seen!.resolving).toBe(false);
  });

  it("Start_NothingRemembered_TheChooser_WaitsForThePersonAndRemembersNothing", async () => {
    mount(() => Promise.resolve<MyTeamsAnswer>({ kind: "teams", teams: [TEAM_A, TEAM_B], start: { where: "choose" } }));
    await waitFor(() => expect(seen!.status).toBe("ready"));
    expect(seen!.choosing).toBe(true);
    expect(seen!.current).toBeNull();
    expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBeNull();

    act(() => seen!.choose("team-a"));
    expect(seen!.choosing).toBe(false);
    expect(seen!.current?.id).toBe("team-a");
  });

  it("Start_ARememberedPick_OutranksTheGatewaysStart", async () => {
    window.localStorage.setItem("devthrottle.currentTeam.account-a", "own-account");
    mount(() => Promise.resolve<MyTeamsAnswer>({ kind: "teams", teams: [TEAM_A], start: { where: "team", teamId: "team-a" } }));
    await waitFor(() => expect(seen!.status).toBe("ready"));
    expect(seen!.current).toBeNull();
    expect(seen!.resolving).toBe(false);
  });

  it("RememberTeamOnThisBrowser_TheNextShellOpensOnThatTeam", async () => {
    rememberTeamOnThisBrowser("team-b");
    mount(() => Promise.resolve<MyTeamsAnswer>({ kind: "teams", teams: [TEAM_A, TEAM_B], start: { where: "own-account" } }));
    await waitFor(() => expect(seen!.current?.id).toBe("team-b"));
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

  it("CurrentTeamProvider_RememberedTeamNoLongerListed_ForgetsItAndStartsWhereTheGatewaySays", async () => {
    window.localStorage.setItem("devthrottle.currentTeam.account-a", "team-left");
    mount(teams([TEAM_A]));
    await waitFor(() => expect(seen!.status).toBe("ready"));
    expect(seen!.current).toBeNull();
    await waitFor(() => expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBe("gateway:own-account"));
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

  // Round 3 review, R3: the way out of a team that could not be opened is for this load only.
  it("OpenOwnAccountForThisLoad_PutsTheOwnAccountOnScreen_AndRemembersNothing", async () => {
    window.localStorage.setItem("devthrottle.currentTeam.account-a", "gateway:team-a");
    mount(() => Promise.reject(new Error("Gateway restarting")));
    await waitFor(() => expect(seen!.status).toBe("error"));
    expect(seen!.resolving).toBe(true);

    act(() => seen!.openOwnAccountForThisLoad());

    expect(seen!.resolving).toBe(false);
    expect(seen!.current).toBeNull();
    expect(window.localStorage.getItem("devthrottle.currentTeam.account-a")).toBe("gateway:team-a");
  });
});
