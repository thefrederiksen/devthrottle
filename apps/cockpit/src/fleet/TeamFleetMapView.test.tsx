// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import type { TeamFleetMap } from "@devthrottle/client-core/teams/teamFleetMapClient";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";

// The Fleet Map for one team (devthrottle_internal#2312): D4, a Developer's own Directors; D5, a Manager's view of
// every Director on the team by person. The Gateway decides what is in the answer; these prove the page lays it out
// as sent, offers only the layouts sent, and that NOTHING on it opens.

const read = vi.hoisted(() => ({ fn: vi.fn() }));
vi.mock("@devthrottle/client-core/teams/teamFleetMapClient", () => ({
  getTeamFleetMap: (...args: unknown[]) => read.fn(...args),
}));

import { GatewayError } from "@devthrottle/client-core/api/client";
import { TeamFleetMapView } from "./TeamFleetMapView";

const TEAM: TeamSummary = { id: "team-dt", name: "DevThrottle", role: "Manager", memberCount: 4, people: "4 people" };

// D5, as the Gateway sends it to Priya, a Manager.
const D5: TeamFleetMap = {
  teamId: "team-dt",
  teamName: "DevThrottle",
  role: "Manager",
  scope: "everyone",
  summary: "Everyone's Directors on this team. Names and status only; sessions do not open.",
  layouts: ["by-person", "by-director"],
  emptyText: "No Director is on this team yet.",
  people: [
    {
      person: "priya@example.com",
      isYou: true,
      directors: [{ name: "Priya - desktop", machine: "PRIYA-PC", sessions: [{ name: "October release", status: "done" }] }],
    },
    {
      person: "rob@example.com",
      isYou: false,
      directors: [
        {
          name: "Rob - laptop",
          machine: "ROB-XPS",
          sessions: [
            { name: "Signup - Developer", status: "working" },
            { name: "Installer bug - Developer", status: "waiting" },
          ],
        },
      ],
    },
    {
      person: "soren@example.com",
      isYou: false,
      directors: [
        {
          name: "Soren - DevThrottle",
          machine: "SOREN_NORTH",
          sessions: [{ name: "Teams - Developer - invitations", status: "working" }],
        },
      ],
    },
  ],
};

// D4, as the Gateway sends it to Rob, a Developer: his own Directors only, one layout.
const D4: TeamFleetMap = {
  ...D5,
  role: "Developer",
  scope: "own",
  summary: "Your Directors on this team.",
  layouts: ["by-director"],
  emptyText: "None of your Directors is on this team yet.",
  people: [{ ...D5.people[1], isYou: true }],
};

beforeEach(() => {
  read.fn.mockReset();
});

afterEach(() => {
  cleanup();
});

describe("TeamFleetMapView", () => {
  it("TeamFleetMapView_D5Manager_OpensByPersonWithEveryPersonAndTheirSessions", async () => {
    read.fn.mockResolvedValue(D5);
    render(<TeamFleetMapView team={TEAM} />);

    const byPerson = await screen.findByTestId("team-fleet-map-by-person");
    expect(screen.getByRole("heading", { name: "Fleet Map - DevThrottle" })).toBeTruthy();
    expect(screen.getByText(D5.summary)).toBeTruthy();

    const lanes = within(byPerson).getAllByRole("region");
    expect(lanes.map((l) => l.getAttribute("aria-label"))).toEqual(["priya@example.com", "rob@example.com", "soren@example.com"]);
    expect(within(lanes[0]).getByText("(you)")).toBeTruthy();

    const rob = lanes[1];
    expect(within(rob).getByText("Rob - laptop")).toBeTruthy();
    expect(within(rob).getByText("ROB-XPS")).toBeTruthy();
    expect(within(rob).getByText("Signup - Developer")).toBeTruthy();
    expect(within(rob).getByText("waiting")).toBeTruthy();
    expect(read.fn).toHaveBeenCalledWith("team-dt", expect.anything());
  });

  it("TeamFleetMapView_D5Manager_CanSwitchToByDirector", async () => {
    read.fn.mockResolvedValue(D5);
    render(<TeamFleetMapView team={TEAM} />);

    await screen.findByTestId("team-fleet-map-by-person");
    const pivots = within(screen.getByRole("group", { name: "Lay the team's fleet out" })).getAllByRole("button");
    expect(pivots.map((b) => b.textContent)).toEqual(["By person", "By director"]);

    fireEvent.click(screen.getByRole("button", { name: "By director" }));

    const byDirector = await screen.findByTestId("team-fleet-map-by-director");
    expect(within(byDirector).getAllByRole("region").map((l) => l.getAttribute("aria-label"))).toEqual([
      "Priya - desktop",
      "Rob - laptop",
      "Soren - DevThrottle",
    ]);
    // On a whole-team map each Director says whose it is.
    expect(within(byDirector).getByText("rob@example.com")).toBeTruthy();
  });

  it("TeamFleetMapView_D4Developer_ShowsOnlyWhatWasSent_ByDirector_WithNoLayoutChoice", async () => {
    read.fn.mockResolvedValue(D4);
    render(<TeamFleetMapView team={{ ...TEAM, role: "Developer" }} />);

    const byDirector = await screen.findByTestId("team-fleet-map-by-director");
    expect(screen.getByText("Your Directors on this team.")).toBeTruthy();
    expect(within(byDirector).getAllByRole("region").map((l) => l.getAttribute("aria-label"))).toEqual(["Rob - laptop"]);
    expect(screen.queryByRole("group", { name: "Lay the team's fleet out" })).toBeNull();
    expect(screen.queryByText("Soren - DevThrottle")).toBeNull();
    // His own map does not label his Directors with his own name.
    expect(within(byDirector).queryByText("rob@example.com")).toBeNull();
  });

  it("TeamFleetMapView_NothingOnATeammatesSession_Opens", async () => {
    read.fn.mockResolvedValue(D5);
    const { container } = render(<TeamFleetMapView team={TEAM} />);

    const byPerson = await screen.findByTestId("team-fleet-map-by-person");
    expect(within(byPerson).queryAllByRole("link")).toEqual([]);
    expect(within(byPerson).queryAllByRole("button")).toEqual([]);
    expect(container.querySelectorAll("a, [href], [onclick], [tabindex]").length).toBe(0);

    fireEvent.click(screen.getByRole("button", { name: "By director" }));
    const byDirector = await screen.findByTestId("team-fleet-map-by-director");
    expect(within(byDirector).queryAllByRole("link")).toEqual([]);
    expect(within(byDirector).queryAllByRole("button")).toEqual([]);
  });

  it("TeamFleetMapView_Collaborator_ShowsTheGatewaysRefusalAndNoMap", async () => {
    read.fn.mockRejectedValue(
      new GatewayError(403, "x", { reason: "In this team you are a Collaborator, and a Collaborator may not see the team's Fleet Map." }),
    );
    render(<TeamFleetMapView team={{ ...TEAM, role: "Collaborator" }} />);

    const refused = await screen.findByTestId("team-fleet-map-refused");
    expect(refused.textContent).toContain("a Collaborator may not see the team's Fleet Map");
    expect(screen.queryByTestId("team-fleet-map-by-person")).toBeNull();
    expect(screen.queryByTestId("team-fleet-map-by-director")).toBeNull();
  });

  it("TeamFleetMapView_NoDirectors_ShowsTheGatewaysEmptySentence", async () => {
    read.fn.mockResolvedValue({ ...D4, people: [] });
    render(<TeamFleetMapView team={TEAM} />);

    expect(await screen.findByText("None of your Directors is on this team yet.")).toBeTruthy();
  });

  it("TeamFleetMapView_ReadFails_SaysSo", async () => {
    read.fn.mockRejectedValue(new GatewayError(500, "x", { reason: "The Gateway could not read the team just now." }));
    render(<TeamFleetMapView team={TEAM} />);

    expect(await screen.findByText(/could not read the team just now/)).toBeTruthy();
  });

  it("TeamFleetMapView_ALaterReadFails_KeepsTheLastMapAndSaysItCouldNotRefresh", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      read.fn.mockResolvedValueOnce(D5).mockRejectedValue(new GatewayError(503, "x", { reason: "Gateway is restarting." }));
      render(<TeamFleetMapView team={TEAM} />);
      await screen.findByTestId("team-fleet-map-by-person");

      await vi.advanceTimersByTimeAsync(10_000);

      await waitFor(() => expect(screen.getByText(/Gateway is restarting/)).toBeTruthy());
      expect(screen.getByTestId("team-fleet-map-by-person")).toBeTruthy();
    } finally {
      vi.useRealTimers();
    }
  });

  it("TeamFleetMapView_AnotherTeam_IsReadForThatTeam", async () => {
    read.fn.mockResolvedValue(D5);
    const { rerender } = render(<TeamFleetMapView team={TEAM} />);
    await screen.findByTestId("team-fleet-map-by-person");

    read.fn.mockResolvedValue({ ...D4, teamId: "team-paul", teamName: "Paul's project" });
    rerender(<TeamFleetMapView team={{ ...TEAM, id: "team-paul", name: "Paul's project" }} />);

    await screen.findByRole("heading", { name: "Fleet Map - Paul's project" });
    expect(read.fn).toHaveBeenLastCalledWith("team-paul", expect.anything());
  });
});
