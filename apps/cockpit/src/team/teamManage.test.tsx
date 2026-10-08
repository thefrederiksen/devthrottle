// @vitest-environment jsdom

// RENAME, DELETE AND LEAVE A TEAM (Teams v1, the owner's 8 October ruling), on the Members tab: "The team" card. The
// Owner renames the team and - once alone - deletes it, typing its name to confirm; every other member leaves it. Each
// fixture is shaped as GET /teams/{teamId}/page answers that role (proven in the Gateway suite's TeamManageTests); these
// tests prove the card renders the verdicts it is given, confirms each change with the Gateway's sentence, and puts the
// own account on screen once the team is gone.

import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import type { TeamPage } from "@devthrottle/client-core/teams/teamPageClient";
import { GatewayError } from "@devthrottle/client-core/api/client";

const TEAM = "3f1d2c9e-0000-4000-8000-000000000001";

vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => true,
  getDeviceKey: () => "device-key",
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));

const client = {
  getTeamPage: vi.fn<(teamId: string) => Promise<TeamPage>>(),
  renameTeam: vi.fn<(teamId: string, name: string) => Promise<void>>(),
  leaveTeam: vi.fn<(teamId: string) => Promise<void>>(),
  deleteTeam: vi.fn<(teamId: string, typed: string) => Promise<void>>(),
};
vi.mock("@devthrottle/client-core/teams/teamPageClient", () => ({
  getTeamPage: (teamId: string) => client.getTeamPage(teamId),
  changeMemberRole: vi.fn(),
  removeMember: vi.fn(),
  renameTeam: (teamId: string, name: string) => client.renameTeam(teamId, name),
  leaveTeam: (teamId: string) => client.leaveTeam(teamId),
  deleteTeam: (teamId: string, typed: string) => client.deleteTeam(teamId, typed),
}));
vi.mock("@devthrottle/client-core/teams/invitationsClient", () => ({ resendInvitation: vi.fn(), cancelInvitation: vi.fn() }));

const currentTeam = { choose: vi.fn(), refresh: vi.fn<() => Promise<void>>() };
vi.mock("@devthrottle/client-core/teams/CurrentTeam", () => ({
  useCurrentTeam: () => currentTeam,
}));

import { TeamPageView } from "./TeamPageView";
import { AFTER_LEAVING_ADDRESS } from "./TeamManageSection";

const DELETE_WARNING = "Deleting DevThrottle ends the team plan, cancels the team's waiting invitations and signs your Directors out of the team.";
const LEAVE_WARNING = "You will leave DevThrottle at once, and your Directors on the team are signed out of it. Your paid seat comes off the team's bill.";
const OTHERS_REMAIN = "2 other people are still in the team. Remove everyone else first, so nobody is cut off by surprise; then you can delete the team.";

function page(yourRole: string, manage: TeamPage["manage"]): TeamPage {
  return {
    teamId: TEAM,
    teamName: "DevThrottle",
    yourRole,
    summary: "3 paid seats",
    canInvite: yourRole !== "Developer",
    billNotice: null,
    members: [],
    invitations: [],
    bill: null,
    manage,
  };
}

const OWNER_WITH_OTHERS = page("Owner", {
  canRename: true, canDelete: false, deleteBlocked: OTHERS_REMAIN, deleteWarning: DELETE_WARNING, canLeave: false, leaveWarning: null,
});
const OWNER_ALONE = page("Owner", {
  canRename: true, canDelete: true, deleteBlocked: null, deleteWarning: DELETE_WARNING, canLeave: false, leaveWarning: null,
});
const DEVELOPER = page("Developer", {
  canRename: false, canDelete: false, deleteBlocked: null, deleteWarning: null, canLeave: true, leaveWarning: LEAVE_WARNING,
});

function Where() {
  const location = useLocation();
  return <div data-testid="where">{location.pathname + location.search}</div>;
}

function renderMembers() {
  return render(
    <MemoryRouter initialEntries={["/settings?tab=members"]}>
      <Routes>
        <Route path="*" element={<><TeamPageView teamId={TEAM} section="members" /><Where /></>} />
      </Routes>
    </MemoryRouter>,
  );
}

const card = () => screen.findByRole("region", { name: "The team" });
const dialog = () => screen.getByRole("alertdialog");

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("The team card - the Owner", () => {
  it("shows the name with Rename, and Delete disabled with the Gateway's sentence while others remain; no Leave", async () => {
    client.getTeamPage.mockResolvedValue(OWNER_WITH_OTHERS);
    renderMembers();

    const section = await card();
    expect(within(section).getByTestId("team-manage-name").textContent).toBe("DevThrottle");
    expect(within(section).getByRole("button", { name: "Rename" })).toBeTruthy();
    const del = within(section).getByRole("button", { name: "Delete this team" }) as HTMLButtonElement;
    expect(del.disabled).toBe(true);
    expect(within(section).getByTestId("team-delete-blocked").textContent).toBe(OTHERS_REMAIN);
    expect(within(section).queryByRole("button", { name: "Leave this team" })).toBeNull();
  });

  it("renames the team, reads the team list again so the menu shows the new name, and reloads the page", async () => {
    client.getTeamPage.mockResolvedValueOnce(OWNER_WITH_OTHERS).mockResolvedValue({ ...OWNER_WITH_OTHERS, teamName: "DevThrottle Labs" });
    client.renameTeam.mockResolvedValue();
    currentTeam.refresh.mockResolvedValue();
    renderMembers();

    fireEvent.click(within(await card()).getByRole("button", { name: "Rename" }));
    fireEvent.change(screen.getByLabelText("Team name"), { target: { value: "  DevThrottle Labs " } });
    fireEvent.click(screen.getByRole("button", { name: "Save name" }));

    await waitFor(() => expect(screen.getByTestId("team-manage-name").textContent).toBe("DevThrottle Labs"));
    expect(client.renameTeam).toHaveBeenCalledWith(TEAM, "  DevThrottle Labs ");
    expect(currentTeam.refresh).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("status").textContent).toBe("The team is now called DevThrottle Labs.");
  });

  it("shows the Gateway's refusal of a name and keeps the form open", async () => {
    client.getTeamPage.mockResolvedValue(OWNER_WITH_OTHERS);
    client.renameTeam.mockRejectedValue(new GatewayError(400, "Give the team a name.", { reason: "Give the team a name." }));
    renderMembers();

    fireEvent.click(within(await card()).getByRole("button", { name: "Rename" }));
    fireEvent.change(screen.getByLabelText("Team name"), { target: { value: "  " } });
    fireEvent.click(screen.getByRole("button", { name: "Save name" }));

    expect((await screen.findByRole("alert")).textContent).toContain("Give the team a name.");
    expect(screen.getByLabelText("Team name")).toBeTruthy();
    expect(currentTeam.refresh).not.toHaveBeenCalled();
  });

  it("deletes the team only once its name is typed exactly, then puts the own account on screen", async () => {
    client.getTeamPage.mockResolvedValue(OWNER_ALONE);
    client.deleteTeam.mockResolvedValue();
    currentTeam.refresh.mockResolvedValue();
    renderMembers();

    const del = within(await card()).getByRole("button", { name: "Delete this team" }) as HTMLButtonElement;
    expect(del.disabled).toBe(false);
    expect(screen.queryByTestId("team-delete-blocked")).toBeNull();
    fireEvent.click(del);

    expect(within(dialog()).getByText(DELETE_WARNING)).toBeTruthy();
    const confirm = within(dialog()).getByRole("button", { name: "Delete this team" }) as HTMLButtonElement;
    const typed = within(dialog()).getByLabelText(/To confirm, type the team's name/);
    expect(confirm.disabled).toBe(true);
    fireEvent.change(typed, { target: { value: "devthrottle" } });
    expect(confirm.disabled).toBe(true);
    fireEvent.change(typed, { target: { value: "DevThrottle" } });
    expect(confirm.disabled).toBe(false);
    fireEvent.click(confirm);

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe(AFTER_LEAVING_ADDRESS));
    expect(client.deleteTeam).toHaveBeenCalledWith(TEAM, "DevThrottle");
    expect(currentTeam.choose).toHaveBeenCalledWith(null);
    expect(currentTeam.refresh).toHaveBeenCalledTimes(1);
  });

  it("keeps the delete confirmation open with the Gateway's refusal, and stays on the team", async () => {
    client.getTeamPage.mockResolvedValue(OWNER_ALONE);
    client.deleteTeam.mockRejectedValue(new GatewayError(409, OTHERS_REMAIN, { reason: OTHERS_REMAIN }));
    renderMembers();

    fireEvent.click(within(await card()).getByRole("button", { name: "Delete this team" }));
    fireEvent.change(within(dialog()).getByLabelText(/To confirm/), { target: { value: "DevThrottle" } });
    fireEvent.click(within(dialog()).getByRole("button", { name: "Delete this team" }));

    await waitFor(() => expect(within(dialog()).getByText(OTHERS_REMAIN)).toBeTruthy());
    expect(currentTeam.choose).not.toHaveBeenCalled();
    expect(screen.getByTestId("where").textContent).toBe("/settings?tab=members");
  });
});

describe("The team card - a member", () => {
  it("offers Leave this team, and neither Rename nor Delete", async () => {
    client.getTeamPage.mockResolvedValue(DEVELOPER);
    renderMembers();

    const section = await card();
    expect(within(section).getByTestId("team-manage-name").textContent).toBe("DevThrottle");
    expect(within(section).getByRole("button", { name: "Leave this team" })).toBeTruthy();
    expect(within(section).queryByRole("button", { name: "Rename" })).toBeNull();
    expect(within(section).queryByRole("button", { name: "Delete this team" })).toBeNull();
  });

  it("confirms with the Gateway's sentence, leaves, and puts the own account on screen", async () => {
    client.getTeamPage.mockResolvedValue(DEVELOPER);
    client.leaveTeam.mockResolvedValue();
    currentTeam.refresh.mockResolvedValue();
    renderMembers();

    fireEvent.click(within(await card()).getByRole("button", { name: "Leave this team" }));
    expect(within(dialog()).getByText(LEAVE_WARNING)).toBeTruthy();
    expect(client.leaveTeam).not.toHaveBeenCalled();
    fireEvent.click(within(dialog()).getByRole("button", { name: "Leave this team" }));

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe(AFTER_LEAVING_ADDRESS));
    expect(client.leaveTeam).toHaveBeenCalledWith(TEAM);
    expect(currentTeam.choose).toHaveBeenCalledWith(null);
  });

  it("Stay in the team closes the confirmation and changes nothing", async () => {
    client.getTeamPage.mockResolvedValue(DEVELOPER);
    renderMembers();

    fireEvent.click(within(await card()).getByRole("button", { name: "Leave this team" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Stay in the team" }));

    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(client.leaveTeam).not.toHaveBeenCalled();
  });

  it("says plainly when the team was left but the list could not be read again, and does not offer to leave twice", async () => {
    client.getTeamPage.mockResolvedValue(DEVELOPER);
    client.leaveTeam.mockResolvedValue();
    currentTeam.refresh.mockRejectedValue(new Error("network down"));
    renderMembers();

    fireEvent.click(within(await card()).getByRole("button", { name: "Leave this team" }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Leave this team" }));

    expect((await screen.findByRole("alert")).textContent).toContain("You have left DevThrottle. But your teams could not be read again");
    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(client.leaveTeam).toHaveBeenCalledTimes(1);
  });
});
