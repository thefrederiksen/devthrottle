// @vitest-environment jsdom

// THE TEAM'S GOVERNANCE TAB (Teams v1). The fixtures are shaped exactly as GET /teams/{teamId}/governance answers an
// Owner and a Developer (proven in the Gateway's TeamGovernanceTests and HostedTeamGovernanceEndpointsTests). These tests
// prove the tab renders the verdicts it is given and nothing it is not: the Owner's controls send only what changed and
// draw the Gateway's answer; a Developer reads the same rules with every control locked and the Gateway's sentence; a
// refusal is shown in the Gateway's words; and no word on it says the rules are not real.

import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { GovernanceChange, TeamGovernance } from "@devthrottle/client-core/teams/governanceClient";
import { GatewayError } from "@devthrottle/client-core/api/client";
import { TeamGovernanceView } from "./TeamGovernanceView";

const TEAM = "3f1d2c9e-0000-4000-8000-000000000003";

const client = {
  getTeamGovernance: vi.fn<(teamId: string) => Promise<TeamGovernance>>(),
  changeTeamGovernance: vi.fn<(teamId: string, change: GovernanceChange) => Promise<TeamGovernance>>(),
};
vi.mock("@devthrottle/client-core/teams/governanceClient", () => ({
  getTeamGovernance: (teamId: string) => client.getTeamGovernance(teamId),
  changeTeamGovernance: (teamId: string, change: GovernanceChange) => client.changeTeamGovernance(teamId, change),
}));

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function governance(canChange: boolean, overrides: Partial<TeamGovernance> = {}): TeamGovernance {
  return {
    teamName: "Acme",
    summary: "Acme - the rules every member's sessions work under. Only the Owner and Managers change them.",
    canChange,
    note: canChange ? null : "Only the team's Owner and Managers can change these rules.",
    review: [
      { id: "agentReviewsPullRequests", label: "An agent reviews every pull request before a person merges it", detail: null, on: true },
      { id: "noSelfMerge", label: "Nobody merges their own agent's work", detail: "A second member approves.", on: true },
      { id: "workStartsAsAssignedIssue", label: "Every piece of work starts as an assigned issue", detail: null, on: false },
    ],
    library: {
      note: "Chosen from the team's own skills and workflows, for every member's Directors.",
      items: [{ kind: "Skill", id: "review-before-merge", name: "Review before merge", level: "Required", gone: null }],
      choices: canChange ? [{ kind: "Workflow", id: "release-checklist", name: "Release checklist" }] : [],
      emptyLibraryNote: null,
    },
    agents: [
      { id: "claudeCode", label: "Claude Code", detail: null, on: true },
      { id: "codex", label: "Codex", detail: null, on: true },
      { id: "otherAgents", label: "Any other agent", detail: null, on: false },
    ],
    readAccess: [
      { label: "A member's sessions and transcripts", who: "Only that member" },
      { label: "Prompts quoted on a Mentor page", who: "Owner and Managers" },
      { label: "Each member's Mentor page", who: "The member, Owner and Managers" },
      { label: "Fleet Map", who: "Names and status only" },
    ],
    limits: [
      { id: "agentHoursPerWeek", label: "Agent hours per member per week", detail: "Warn the member and their Manager at 80%", value: 45, display: "45 h", min: 1, max: 168 },
      { id: "sessionsAtOnce", label: "Sessions running at once per member", detail: null, value: 8, display: "8", min: 1, max: 100 },
      { id: "keepMentorPagesMonths", label: "Keep Mentor pages", detail: null, value: null, display: "No limit", min: 1, max: 120 },
    ],
    changes: [
      { id: "c2", sentence: "peter@acme.example made \"Review before merge\" required", when: "Mon 6 Oct 2026, 09:14 UTC" },
      { id: "c1", sentence: "soren@acme.example switched off \"Any other agent\"", when: "Fri 3 Oct 2026, 16:38 UTC" },
    ],
    ...overrides,
  };
}

function mount() {
  return render(<MemoryRouter><TeamGovernanceView teamId={TEAM} /></MemoryRouter>);
}

function section(name: string) {
  return screen.getByRole("region", { name });
}

describe("the Governance tab", () => {
  it("shows the six sections with the Gateway's words, the record newest first, and no word saying the rules are not real", async () => {
    client.getTeamGovernance.mockResolvedValue(governance(true));
    const { container } = mount();

    await screen.findByText("Acme - the rules every member's sessions work under. Only the Owner and Managers change them.");
    for (const name of ["Review", "Required skills and workflows", "Agents members may run", "Who may read what", "Limits", "Changes to the rules"])
      expect(section(name)).toBeTruthy();
    expect(within(section("Who may read what")).getByText("Names and status only")).toBeTruthy();
    expect(within(section("Changes to the rules")).getAllByRole("listitem").map((li) => li.textContent)).toEqual([
      "peter@acme.example made \"Review before merge\" requiredMon 6 Oct 2026, 09:14 UTC",
      "soren@acme.example switched off \"Any other agent\"Fri 3 Oct 2026, 16:38 UTC",
    ]);
    expect(container.textContent ?? "").not.toMatch(/coming soon|preview|demo|beta/i);
  });

  it("lets the Owner flip one switch, sending only that rule, and draws the Gateway's answer", async () => {
    client.getTeamGovernance.mockResolvedValue(governance(true));
    const after = governance(true, {
      agents: governance(true).agents.map((a) => (a.id === "codex" ? { ...a, on: false } : a)),
      changes: [{ id: "c3", sentence: "soren@acme.example switched off \"Codex\"", when: "Wed 8 Oct 2026, 10:00 UTC" }, ...governance(true).changes],
    });
    client.changeTeamGovernance.mockResolvedValue(after);
    mount();

    const codex = await screen.findByRole("switch", { name: "Codex" });
    expect(codex.getAttribute("aria-checked")).toBe("true");
    fireEvent.click(codex);

    await waitFor(() => expect(screen.getByRole("switch", { name: "Codex" }).getAttribute("aria-checked")).toBe("false"));
    expect(client.changeTeamGovernance).toHaveBeenCalledWith(TEAM, { agents: { codex: false } });
    expect(screen.getByText("soren@acme.example switched off \"Codex\"")).toBeTruthy();
  });

  it("lets the Owner add a skill or workflow at a level, and set or remove a limit", async () => {
    client.getTeamGovernance.mockResolvedValue(governance(true));
    client.changeTeamGovernance.mockResolvedValue(governance(true));
    mount();

    fireEvent.change(await screen.findByLabelText("Skill or workflow to add"), { target: { value: "Workflow:release-checklist" } });
    fireEvent.change(screen.getByLabelText("Level to add it at"), { target: { value: "Suggested" } });
    fireEvent.click(within(section("Required skills and workflows")).getByRole("button", { name: "Add" }));
    await waitFor(() => expect(client.changeTeamGovernance).toHaveBeenCalledWith(TEAM,
      { items: [{ kind: "Workflow", id: "release-checklist", level: "Suggested" }] }));

    const sessions = screen.getByLabelText("Sessions running at once per member");
    fireEvent.change(sessions, { target: { value: "" } });
    fireEvent.click(within(sessions.parentElement as HTMLElement).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(client.changeTeamGovernance).toHaveBeenLastCalledWith(TEAM, { limits: { sessionsAtOnce: null } }));
  });

  it("shows a Developer the same rules read-only, with the Gateway's sentence and no control that could change them", async () => {
    client.getTeamGovernance.mockResolvedValue(governance(false));
    mount();

    await screen.findByText("Only the team's Owner and Managers can change these rules.");
    const switches = screen.getAllByRole("switch");
    expect(switches).toHaveLength(6);
    for (const s of switches) expect((s as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByLabelText("Skill or workflow to add")).toBeNull();
    expect(screen.queryByRole("combobox")).toBeNull();
    expect(screen.queryByRole("spinbutton")).toBeNull();
    expect(screen.queryByRole("button", { name: "Save" })).toBeNull();
    expect(within(section("Limits")).getByText("45 h")).toBeTruthy();
    expect(within(section("Required skills and workflows")).getByText("Required")).toBeTruthy();

    fireEvent.click(switches[0]);
    expect(client.changeTeamGovernance).not.toHaveBeenCalled();
  });

  it("shows a refused change in the Gateway's words and keeps the rules as they were", async () => {
    client.getTeamGovernance.mockResolvedValue(governance(true));
    client.changeTeamGovernance.mockRejectedValue(new GatewayError(409, "Someone else changed the team's rules at the same moment, so this change was not saved. Reload the page and try again.", { reason: "Someone else changed the team's rules at the same moment, so this change was not saved. Reload the page and try again." }));
    mount();

    fireEvent.click(await screen.findByRole("switch", { name: "Every piece of work starts as an assigned issue" }));

    expect((await screen.findByRole("alert")).textContent).toContain("Someone else changed the team's rules at the same moment");
    expect(screen.getByRole("switch", { name: "Every piece of work starts as an assigned issue" }).getAttribute("aria-checked")).toBe("false");
  });

  it("shows a role the Gateway refuses the tab to the Gateway's sentence instead of the rules", async () => {
    client.getTeamGovernance.mockRejectedValue(new GatewayError(403, "A Collaborator may not see the team's governance rules.", { reason: "A Collaborator may not see the team's governance rules." }));
    mount();

    expect((await screen.findByRole("note")).textContent).toContain("may not see the team's governance rules");
    expect(screen.queryByRole("switch")).toBeNull();
  });
});
