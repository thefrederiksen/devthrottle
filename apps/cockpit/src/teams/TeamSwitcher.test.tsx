// @vitest-environment jsdom
import { describe, it, expect, beforeEach } from "vitest";
import { render, cleanup, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { CurrentTeamProvider, useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import type { MyTeamsAnswer, TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import { TeamSwitcher } from "./TeamSwitcher";

// The team switcher at the top of the rail (devthrottle_internal#2312, S11). Its whole contract is in these cases:
// no team - nothing at all; one team; several teams, each with the person's role; and a Gateway that offers no teams.

const DEVTHROTTLE: TeamSummary = { id: "team-dt", name: "DevThrottle", role: "Owner", memberCount: 5, people: "5 people" };
const PAULS: TeamSummary = { id: "team-paul", name: "Paul's project", role: "Developer", memberCount: 2, people: "2 people" };

function loader(answer: MyTeamsAnswer | Error): () => Promise<MyTeamsAnswer> {
  return () => (answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer));
}

// What a page reading the shared current team sees - the switcher writes it, this reads it.
function CurrentTeamProbe() {
  const { current } = useCurrentTeam();
  return <div data-testid="probe">{current === null ? "own account" : current.name}</div>;
}

function renderSwitcher(load: () => Promise<MyTeamsAnswer>) {
  return render(
    <CurrentTeamProvider load={load}>
      <TeamSwitcher />
      <CurrentTeamProbe />
    </CurrentTeamProvider>,
  );
}

function optionTexts(): string[] {
  const select = within(screen.getByTestId("team-switcher")).getByRole("combobox");
  return Array.from((select as HTMLSelectElement).options).map((o) => o.textContent ?? "");
}

describe("TeamSwitcher", () => {
  beforeEach(() => {
    cleanup();
    window.localStorage.clear();
  });

  it("TeamSwitcher_NoTeams_RendersNothing", async () => {
    const { container } = renderSwitcher(loader({ kind: "teams", teams: [] }));

    await waitFor(() => expect(screen.getByTestId("probe").textContent).toBe("own account"));
    expect(screen.queryByTestId("team-switcher")).toBeNull();
    expect(screen.queryByTestId("team-switcher-error")).toBeNull();
    expect(container.textContent).toBe("own account");
  });

  it("TeamSwitcher_TeamsNotOffered_RendersNothing", async () => {
    renderSwitcher(loader({ kind: "not-offered", reason: "This Gateway has not turned Teams on." }));

    await waitFor(() => expect(screen.getByTestId("probe").textContent).toBe("own account"));
    expect(screen.queryByTestId("team-switcher")).toBeNull();
    expect(screen.queryByTestId("team-switcher-error")).toBeNull();
  });

  it("TeamSwitcher_OneTeam_ListsOwnAccountThenTheTeamWithTheRole", async () => {
    renderSwitcher(loader({ kind: "teams", teams: [DEVTHROTTLE] }));

    await waitFor(() => expect(screen.getByTestId("team-switcher")).toBeTruthy());
    expect(optionTexts()).toEqual(["Your own account", "DevThrottle - Owner"]);
    // Nothing changes until the person picks the team.
    expect(screen.getByTestId("probe").textContent).toBe("own account");
  });

  it("TeamSwitcher_SeveralTeams_ListsEachWithTheRoleAndPicksOne", async () => {
    renderSwitcher(loader({ kind: "teams", teams: [DEVTHROTTLE, PAULS] }));

    await waitFor(() => expect(screen.getByTestId("team-switcher")).toBeTruthy());
    expect(optionTexts()).toEqual(["Your own account", "DevThrottle - Owner", "Paul's project - Developer"]);

    expect(screen.queryByTestId("team-switcher-role")).toBeNull();

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "team-paul" } });
    expect(screen.getByTestId("probe").textContent).toBe("Paul's project");
    // The role stays readable under the control, however long the team's name.
    expect(screen.getByTestId("team-switcher-role").textContent).toBe("Developer - 2 people");

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "own-account" } });
    expect(screen.getByTestId("probe").textContent).toBe("own account");
  });

  it("TeamSwitcher_TeamsCouldNotBeRead_SaysSo", async () => {
    renderSwitcher(loader(new Error("network down")));

    await waitFor(() => expect(screen.getByTestId("team-switcher-error")).toBeTruthy());
    expect(screen.queryByTestId("team-switcher")).toBeNull();
    expect(screen.getByTestId("probe").textContent).toBe("own account");
  });
});
