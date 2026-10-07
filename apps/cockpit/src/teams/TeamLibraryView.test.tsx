// @vitest-environment jsdom
import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, cleanup, screen, waitFor, fireEvent } from "@testing-library/react";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import type { CurrentTeamState } from "@devthrottle/client-core/teams/CurrentTeam";
import type { TeamLibrary } from "@devthrottle/client-core/teams/teamLibraryClient";
import { GatewayError } from "@devthrottle/client-core/api/client";

// The team's skills and workflows page (devthrottle_internal#2304, S5), as each role sees it. The page renders what
// the Gateway decided - canChange, the refusal sentence, each row's canChange, who changed it - and never reads the
// role to decide anything (rule 7). The server refuses regardless; these pin what the page shows.

const client = vi.hoisted(() => ({
  getTeamLibrary: vi.fn(),
  getTeamItemText: vi.fn(),
  addTeamSkill: vi.fn(),
  addTeamWorkflowFrom: vi.fn(),
  changeTeamItem: vi.fn(),
  getStartingWorkflows: vi.fn(),
  removeTeamItem: vi.fn(),
}));
vi.mock("@devthrottle/client-core/teams/teamLibraryClient", () => ({
  ...client,
  newAddProgress: () => ({ createdId: null, createdVersion: null }),
}));

const team = vi.hoisted(() => ({ state: null as unknown as CurrentTeamState }));
vi.mock("@devthrottle/client-core/teams/CurrentTeam", () => ({ useCurrentTeam: () => team.state }));

import { TeamLibraryView, TeamOrOwn } from "./TeamLibraryView";

const ACME: TeamSummary = { id: "team-acme", name: "Acme", role: "Developer", memberCount: 4, people: "4 people", app: { full: true, pages: [], landing: null, elsewhere: null } };

const DEVELOPER_REFUSAL = "In this team you are a Developer, and a Developer may not change the team's shared skills and workflows.";

function library(role: string, canChange: boolean): TeamLibrary {
  return {
    team: { id: ACME.id, name: ACME.name, role },
    canChange,
    changeRefusal: canChange ? null : DEVELOPER_REFUSAL,
    builtInNote: "DevThrottle's own built-in skills and workflows are available to every session as well.",
    items: [
      { id: "release-checklist", name: "release-checklist", summary: "The steps every release follows.", kind: "Skill", enabled: true, version: 3, changedAtUtc: "2026-10-02T12:00:00Z", changedBy: "priya@example.com", canChange },
      { id: "team-review", name: "team-review", summary: "One builds, another reviews.", kind: "Workflow", enabled: true, version: 1, changedAtUtc: "2026-09-28T12:00:00Z", changedBy: "soren@example.com", canChange },
    ],
  };
}

function state(overrides: Partial<CurrentTeamState>): CurrentTeamState {
  return {
    status: "ready",
    teams: [ACME],
    current: ACME,
    resolving: false,
    choosing: false,
    error: null,
    choose: () => null,
    openOwnAccountForThisLoad: () => {},
    refresh: async () => {},
    ...overrides,
  };
}

beforeEach(() => {
  cleanup();
  for (const fn of Object.values(client)) fn.mockReset();
});

describe("TeamLibraryView", () => {
  it.each([["Owner"], ["Manager"]])("TeamLibraryView_%s_SeesTheListAndCanAddChangeAndRemove", async (role) => {
    client.getTeamLibrary.mockResolvedValue(library(role, true));
    render(<TeamLibraryView team={{ ...ACME, role }} />);

    expect(await screen.findByText("release-checklist")).toBeTruthy();
    expect(screen.getByText("team-review")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Add skill" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Add workflow" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Change release-checklist" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Remove team-review" })).toBeTruthy();
    expect(screen.queryByText(DEVELOPER_REFUSAL)).toBeNull();
    expect(screen.getByText(/priya@example.com,/)).toBeTruthy();
    expect(client.getTeamLibrary).toHaveBeenCalledWith("team-acme", expect.anything());
  });

  it("TeamLibraryView_Developer_SeesTheListReadOnly_WithTheGatewaysSentence", async () => {
    client.getTeamLibrary.mockResolvedValue(library("Developer", false));
    render(<TeamLibraryView team={ACME} />);

    expect(await screen.findByText("release-checklist")).toBeTruthy();
    expect(screen.getByRole("button", { name: "View release-checklist" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Add skill" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Add workflow" })).toBeNull();
    expect(screen.queryByRole("button", { name: /^Change / })).toBeNull();
    expect(screen.queryByRole("button", { name: /^Remove / })).toBeNull();
    expect(screen.getByRole("note").textContent).toBe(DEVELOPER_REFUSAL);
  });

  it("TeamLibraryView_Collaborator_IsShownTheGatewaysRefusal_AndNoList", async () => {
    const sentence = "In this team you are a Collaborator, and a Collaborator may not use the team's shared skills and workflows.";
    client.getTeamLibrary.mockRejectedValue(new GatewayError(403, sentence, { reason: sentence, code: "team_action_refused" }));
    render(<TeamLibraryView team={{ ...ACME, role: "Collaborator" }} />);

    // An answer, not a failure: the Gateway's sentence as a note, no red banner and no "Try again".
    expect((await screen.findByRole("note")).textContent).toBe(sentence);
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.queryByRole("button", { name: "Try again" })).toBeNull();
    expect(screen.queryByText(/Every session you start on this team/)).toBeNull();
    expect(screen.queryByText("release-checklist")).toBeNull();
    expect(screen.queryByRole("button", { name: "Add skill" })).toBeNull();
  });

  it("TeamLibraryView_AGatewayFailure_IsAnErrorWithRetry_NotANote", async () => {
    client.getTeamLibrary.mockRejectedValue(new GatewayError(503, "The Gateway is restarting.", { reason: "The Gateway is restarting." }));
    render(<TeamLibraryView team={ACME} />);
    expect((await screen.findByRole("alert")).textContent).toContain("The Gateway is restarting.");
    expect(screen.getByRole("button", { name: "Try again" })).toBeTruthy();
  });

  it("TeamLibraryView_TheVerdictNotTheRole_DecidesWhatIsOffered", async () => {
    // The role label says Developer, the Gateway's verdict says yes: the page follows the verdict.
    client.getTeamLibrary.mockResolvedValue(library("Developer", true));
    render(<TeamLibraryView team={ACME} />);
    expect(await screen.findByRole("button", { name: "Add skill" })).toBeTruthy();
  });

  it("TeamLibraryView_ARowTheGatewaySaysCannotChange_OffersOnlyView", async () => {
    const lib = library("Manager", true);
    lib.items[1] = { ...lib.items[1], canChange: false };
    client.getTeamLibrary.mockResolvedValue(lib);
    render(<TeamLibraryView team={ACME} />);
    expect(await screen.findByRole("button", { name: "Change release-checklist" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Change team-review" })).toBeNull();
  });

  it("TeamLibraryView_NoItems_SaysSo", async () => {
    client.getTeamLibrary.mockResolvedValue({ ...library("Owner", true), items: [] });
    render(<TeamLibraryView team={ACME} />);
    expect(await screen.findByText("The Acme team has no skills or workflows of its own yet.")).toBeTruthy();
  });

  it("TeamLibraryView_AddSkill_SendsItToTheTeam_AndReloads", async () => {
    client.getTeamLibrary.mockResolvedValue(library("Manager", true));
    client.addTeamSkill.mockResolvedValue(undefined);
    render(<TeamLibraryView team={ACME} />);
    fireEvent.click(await screen.findByRole("button", { name: "Add skill" }));

    fireEvent.change(screen.getByPlaceholderText("Release checklist"), { target: { value: "Deploy notes" } });
    fireEvent.change(screen.getByPlaceholderText("The steps every release follows."), { target: { value: "How we deploy." } });
    fireEvent.change(screen.getByPlaceholderText(/# Release checklist/), { target: { value: "# Deploy notes" } });
    fireEvent.click(screen.getByRole("button", { name: "Add to the team" }));

    await waitFor(() => expect(client.addTeamSkill).toHaveBeenCalledWith("team-acme", {
      id: "deploy-notes", name: "Deploy notes", summary: "How we deploy.", bodyMarkdown: "# Deploy notes",
    }, { createdId: null, createdVersion: null }));
    await waitFor(() => expect(client.getTeamLibrary).toHaveBeenCalledTimes(2));
  });

  it("TeamLibraryView_AddSkill_ASecondAttemptAfterThePublishFailed_FinishesTheSameSkill", async () => {
    client.getTeamLibrary.mockResolvedValue(library("Manager", true));
    // The first attempt creates the skill and then its publish fails; the client records that in the progress.
    client.addTeamSkill.mockImplementationOnce(async (_team: string, skill: { id: string }, progress: { createdId: string | null }) => {
      progress.createdId = skill.id;
      throw new GatewayError(503, "The Gateway is restarting.", { reason: "The Gateway is restarting." });
    });
    client.addTeamSkill.mockResolvedValueOnce(undefined);
    render(<TeamLibraryView team={ACME} />);
    fireEvent.click(await screen.findByRole("button", { name: "Add skill" }));
    fireEvent.change(screen.getByPlaceholderText("Release checklist"), { target: { value: "Deploy notes" } });
    fireEvent.change(screen.getByPlaceholderText("The steps every release follows."), { target: { value: "s" } });
    fireEvent.change(screen.getByPlaceholderText(/# Release checklist/), { target: { value: "b" } });
    fireEvent.click(screen.getByRole("button", { name: "Add to the team" }));
    expect(await screen.findByText(/The Gateway is restarting/)).toBeTruthy();

    // Renaming it now cannot move it to a second id: the skill already exists under the first one.
    fireEvent.change(screen.getByPlaceholderText("Release checklist"), { target: { value: "Deploy guide" } });
    expect(screen.getByText("deploy-notes")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Add to the team" }));

    await waitFor(() => expect(client.addTeamSkill).toHaveBeenCalledTimes(2));
    expect(client.addTeamSkill.mock.calls[1][1]).toMatchObject({ id: "deploy-notes", name: "Deploy guide" });
    expect(client.addTeamSkill.mock.calls[1][2]).toMatchObject({ createdId: "deploy-notes" });
  });

  it("TeamLibraryView_AddRefusedByTheServer_ShowsItsSentence_AndStaysOpen", async () => {
    client.getTeamLibrary.mockResolvedValue(library("Manager", true));
    client.addTeamSkill.mockRejectedValue(new GatewayError(403, DEVELOPER_REFUSAL, { reason: DEVELOPER_REFUSAL }));
    render(<TeamLibraryView team={ACME} />);
    fireEvent.click(await screen.findByRole("button", { name: "Add skill" }));
    fireEvent.change(screen.getByPlaceholderText("Release checklist"), { target: { value: "X" } });
    fireEvent.change(screen.getByPlaceholderText("The steps every release follows."), { target: { value: "s" } });
    fireEvent.change(screen.getByPlaceholderText(/# Release checklist/), { target: { value: "b" } });
    fireEvent.click(screen.getByRole("button", { name: "Add to the team" }));

    expect(await screen.findByText(new RegExp(DEVELOPER_REFUSAL.slice(0, 40)))).toBeTruthy();
    expect(screen.getByRole("dialog", { name: "Add skill" })).toBeTruthy();
  });

  it("TeamLibraryView_Change_SendsTheNewWords", async () => {
    client.getTeamLibrary.mockResolvedValue(library("Owner", true));
    client.getTeamItemText.mockResolvedValue("# old words");
    client.changeTeamItem.mockResolvedValue(undefined);
    render(<TeamLibraryView team={ACME} />);
    fireEvent.click(await screen.findByRole("button", { name: "Change release-checklist" }));

    const textarea = await screen.findByDisplayValue("# old words");
    fireEvent.change(textarea, { target: { value: "# new words" } });
    fireEvent.click(screen.getByRole("button", { name: "Save for the team" }));

    await waitFor(() => expect(client.changeTeamItem).toHaveBeenCalledWith(
      "team-acme", expect.objectContaining({ id: "release-checklist", kind: "Skill", version: 3 }),
      { summary: "The steps every release follows.", text: "# new words" }));
  });

  it("TeamLibraryView_Change_EmptyWords_AreNeverOffered", async () => {
    // The Gateway would accept empty words as a draft and then refuse to publish them.
    client.getTeamLibrary.mockResolvedValue(library("Owner", true));
    client.getTeamItemText.mockResolvedValue("# old words");
    render(<TeamLibraryView team={ACME} />);
    fireEvent.click(await screen.findByRole("button", { name: "Change release-checklist" }));
    fireEvent.change(await screen.findByDisplayValue("# old words"), { target: { value: "   " } });
    expect((screen.getByRole("button", { name: "Save for the team" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("TeamLibraryView_Remove_AsksFirst_ThenRemoves", async () => {
    client.getTeamLibrary.mockResolvedValue(library("Owner", true));
    client.removeTeamItem.mockResolvedValue(undefined);
    render(<TeamLibraryView team={ACME} />);
    fireEvent.click(await screen.findByRole("button", { name: "Remove team-review" }));
    expect(client.removeTeamItem).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(client.removeTeamItem).toHaveBeenCalledWith("team-acme", expect.objectContaining({ id: "team-review", kind: "Workflow" })));
  });

  it("TeamLibraryView_AddWorkflow_StartsFromABuiltIn", async () => {
    client.getTeamLibrary.mockResolvedValue(library("Manager", true));
    client.getStartingWorkflows.mockResolvedValue([{ id: "standalone-with-review", name: "Standalone with review", summary: "s" }]);
    client.addTeamWorkflowFrom.mockResolvedValue(undefined);
    render(<TeamLibraryView team={ACME} />);
    fireEvent.click(await screen.findByRole("button", { name: "Add workflow" }));
    fireEvent.change(await screen.findByPlaceholderText("Our review"), { target: { value: "Our review" } });
    expect(screen.getByText("our-review")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Add to the team" }));

    // The name typed is the name sent - the row is called what the person called it, not the built-in's name.
    await waitFor(() => expect(client.addTeamWorkflowFrom).toHaveBeenCalledWith(
      "team-acme", "standalone-with-review", { id: "our-review", name: "Our review" }, { createdId: null, createdVersion: null }));
  });
});

describe("TeamOrOwn", () => {
  it("TeamOrOwn_OwnAccount_ShowsTheOwnPage_AndReadsNoTeamLibrary", () => {
    team.state = state({ current: null });
    render(<TeamOrOwn own={<div>own skills page</div>} />);
    expect(screen.getByText("own skills page")).toBeTruthy();
    expect(client.getTeamLibrary).not.toHaveBeenCalled();
  });

  it("TeamOrOwn_NoTeamsOffered_ShowsTheOwnPage", () => {
    team.state = state({ status: "not-offered", teams: [], current: null });
    render(<TeamOrOwn own={<div>own skills page</div>} />);
    expect(screen.getByText("own skills page")).toBeTruthy();
  });

  it("TeamOrOwn_ATeamOnScreen_ShowsTheTeamsPage", async () => {
    team.state = state({});
    client.getTeamLibrary.mockResolvedValue(library("Developer", false));
    render(<TeamOrOwn own={<div>own skills page</div>} />);
    expect(await screen.findByRole("heading", { name: "Skills and workflows" })).toBeTruthy();
    expect(screen.queryByText("own skills page")).toBeNull();
  });

  it("TeamOrOwn_ARememberedTeamNotYetConfirmed_ShowsLoading_NeverTheOwnPage", () => {
    team.state = state({ status: "loading", teams: [], current: null, resolving: true });
    render(<TeamOrOwn own={<div>own skills page</div>} />);
    expect(screen.queryByText("own skills page")).toBeNull();
    expect(screen.getByRole("status").textContent).toContain("Loading your team");
  });

  it("TeamOrOwn_TheTeamReadFailedWhileResolving_ShowsTheError_NeverTheOwnPage", () => {
    team.state = state({ status: "error", teams: [], current: null, resolving: true, error: "The Gateway could not be reached." });
    render(<TeamOrOwn own={<div>own skills page</div>} />);
    expect(screen.queryByText("own skills page")).toBeNull();
    expect(screen.getByRole("alert").textContent).toContain("The Gateway could not be reached.");
  });
});
