// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup, screen, fireEvent } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { CurrentTeamProvider, currentTeamStorageKey } from "@devthrottle/client-core/teams/CurrentTeam";
import type { MyTeamsAnswer, TeamSummary } from "@devthrottle/client-core/teams/teamsClient";

// Reports in Work, for everyone (owner, 8 Oct 2026): /reports is the person's own sessions' reports on their own account,
// and the team's Reports page with a team on screen - only where the Gateway's verdict for the team lists it.

const listed = vi.hoisted(() => ({ calls: [] as Array<string | undefined> }));
vi.mock("@devthrottle/client-core/devreports/DevReportList", () => ({
  DevReportList: ({ sessionId, onOpen }: { sessionId: string | undefined; onOpen: (r: { id: string }) => void }) => {
    listed.calls.push(sessionId);
    return (
      <button type="button" data-testid="dev-report-row" onClick={() => onOpen({ id: "r-7" })}>
        a report
      </button>
    );
  },
}));
vi.mock("@devthrottle/client-core/devreports/DevReportViewer", () => ({
  DevReportViewer: ({ reportId, leading }: { reportId: string; leading: React.ReactNode }) => (
    <div data-testid="viewer">
      {reportId}
      {leading}
    </div>
  ),
}));
vi.mock("../teams/collaborator/ReportsPage", () => ({ ReportsPage: () => <div data-testid="team-reports">team reports</div> }));

import { ReportsRoute } from "./ReportsRoute";

const TEAM: TeamSummary = {
  id: "team-r",
  name: "DevThrottle",
  role: "Owner",
  memberCount: 5,
  people: "5 people",
  app: { full: true, pages: [{ id: "reports", label: "Reports", path: "/reports", countPath: null }], landing: null, elsewhere: null },
};

function Where() {
  const location = useLocation();
  return <div data-testid="where">{location.pathname + location.search}</div>;
}

function renderWith(teams: MyTeamsAnswer, chosen: string | null) {
  if (chosen !== null) window.localStorage.setItem(currentTeamStorageKey(), chosen);
  return render(
    <MemoryRouter initialEntries={["/reports"]}>
      <CurrentTeamProvider load={() => Promise.resolve(teams)}>
        <Routes>
          <Route path="/reports" element={<><ReportsRoute /><Where /></>} />
        </Routes>
      </CurrentTeamProvider>
    </MemoryRouter>,
  );
}

describe("ReportsRoute", () => {
  beforeEach(() => {
    cleanup();
    window.localStorage.clear();
    listed.calls = [];
  });

  it("ReportsRoute_OwnAccount_ListsEveryReportOfTheAccount_AndOpensOneInPlace", async () => {
    renderWith({ kind: "teams", teams: [TEAM], start: { where: "own-account" } }, null);

    fireEvent.click(await screen.findByTestId("dev-report-row"));
    expect(listed.calls[0]).toBeUndefined();
    expect((await screen.findByTestId("viewer")).textContent).toContain("r-7");
    expect(screen.getByTestId("where").textContent).toBe("/reports?report=r-7");

    fireEvent.click(screen.getByTestId("reports-back"));
    expect(await screen.findByTestId("personal-reports")).toBeTruthy();
  });

  it("ReportsRoute_TeamsNotOffered_IsStillThePersonsOwnReports", async () => {
    renderWith({ kind: "not-offered", reason: "dark" }, null);

    expect(await screen.findByTestId("personal-reports")).toBeTruthy();
  });

  it("ReportsRoute_ATeamOnScreen_IsTheTeamsReportsPage", async () => {
    renderWith({ kind: "teams", teams: [TEAM], start: { where: "own-account" } }, TEAM.id);

    expect(await screen.findByTestId("team-reports")).toBeTruthy();
    expect(screen.queryByTestId("personal-reports")).toBeNull();
  });

  it("ReportsRoute_ATeamWhoseVerdictDoesNotListReports_IsTheOrdinaryMissingPage", async () => {
    const without = { ...TEAM, app: { ...TEAM.app, pages: [] } } as TeamSummary;
    renderWith({ kind: "teams", teams: [without], start: { where: "own-account" } }, TEAM.id);

    expect(await screen.findByText("Page not found")).toBeTruthy();
  });
});
