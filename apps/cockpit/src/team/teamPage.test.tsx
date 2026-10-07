// @vitest-environment jsdom

// THE TEAM PAGE IN THE COCKPIT (devthrottle_internal#2303), screen S1, driven through the REAL route table the app
// mounts (COCKPIT_ROUTES). One test per role view the issue names - Owner, Manager, Developer, and the Collaborator who
// has no Team page - each fed the Gateway's answer for that role, plus the change-role and remove flows.
//
// What each role may click is the Gateway's verdict on the model (rule 7). The fixtures below are shaped exactly as
// GET /teams/{teamId}/page answers each role (proven in the Gateway suites' TeamPageTests and
// HostedTeamPageEndpointsTests); these tests prove the page renders the verdicts it is given and nothing it is not.

import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent, within } from "@testing-library/react";
import { Outlet, RouterProvider, createMemoryRouter } from "react-router-dom";
import type { TeamPage, TeamPageMember } from "@devthrottle/client-core/teams/teamPageClient";
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
  changeMemberRole: vi.fn<(teamId: string, memberId: string, role: string) => Promise<void>>(),
  removeMember: vi.fn<(teamId: string, memberId: string) => Promise<void>>(),
};
vi.mock("@devthrottle/client-core/teams/teamPageClient", () => ({
  getTeamPage: (teamId: string) => client.getTeamPage(teamId),
  changeMemberRole: (teamId: string, memberId: string, role: string) => client.changeMemberRole(teamId, memberId, role),
  removeMember: (teamId: string, memberId: string) => client.removeMember(teamId, memberId),
}));
const invitations = { resendInvitation: vi.fn(), cancelInvitation: vi.fn() };
vi.mock("@devthrottle/client-core/teams/invitationsClient", () => ({
  resendInvitation: (...a: unknown[]) => invitations.resendInvitation(...a),
  cancelInvitation: (...a: unknown[]) => invitations.cancelInvitation(...a),
}));

// The shell frame cannot run in jsdom and is not the subject; the page routes into it as its outlet.
vi.mock("../AppShell", () => ({ AppShell: () => <Outlet /> }));

import { COCKPIT_ROUTES } from "../routes";

const ROLES = ["Manager", "Developer", "Collaborator"];

function member(name: string, role: string, overrides: Partial<TeamPageMember> = {}): TeamPageMember {
  return {
    memberId: `id-${name}`,
    name: `${name}@devthrottle.com`,
    email: `${name}@devthrottle.com`,
    role,
    seat: role === "Collaborator" ? "No charge" : "Paid",
    isYou: false,
    joinedAtUtc: "2026-09-01T10:00:00Z",
    canChangeRole: false,
    roleChoices: [],
    canRemove: false,
    removeWarning: null,
    ...overrides,
  };
}

const removable = (name: string) => ({ canRemove: true, removeWarning: `${name}@devthrottle.com will leave the team at once.` });
const dropdown = { canChangeRole: true, roleChoices: ROLES };

/** The Owner's answer: every row but their own is a dropdown with Remove. */
function ownerPage(): TeamPage {
  return {
    teamId: TEAM,
    teamName: "DevThrottle",
    yourRole: "Owner",
    summary: "3 paid seats, 2 Collaborators (no charge), 1 invitation waiting",
    canInvite: true,
    members: [
      member("soren", "Owner", { isYou: true }),
      member("priya", "Manager", { ...dropdown, ...removable("priya") }),
      member("rob", "Developer", { ...dropdown, ...removable("rob") }),
      member("mike", "Collaborator", { ...dropdown, ...removable("mike") }),
      member("james", "Collaborator", { ...dropdown, ...removable("james") }),
    ],
    invitations: [{
      id: "inv-1", email: "anna@devthrottle.com", role: "Developer", state: "sent", seat: "Paid when accepted",
      invitedBy: "priya@devthrottle.com", sentAtUtc: "2026-10-01T10:00:00Z", expiresAtUtc: "2026-10-08T10:00:00Z",
      canResend: true, canCancel: true,
    }],
    // The Billing section has its own tests (teamBilling.test.tsx); these are about the member list.
    bill: null,
  };
}

/** The Manager's answer: no dropdowns; Remove only on Developers and Collaborators. */
function managerPage(): TeamPage {
  return {
    ...ownerPage(),
    yourRole: "Manager",
    members: [
      member("soren", "Owner"),
      member("priya", "Manager", { isYou: true }),
      member("pat", "Manager"),
      member("rob", "Developer", removable("rob")),
      member("mike", "Collaborator", removable("mike")),
    ],
  };
}

/** The Developer's answer: the list, nothing to click, no invitations. */
function developerPage(): TeamPage {
  return {
    ...ownerPage(),
    yourRole: "Developer",
    summary: "3 paid seats, 2 Collaborators (no charge)",
    canInvite: false,
    members: [
      member("soren", "Owner"),
      member("priya", "Manager"),
      member("rob", "Developer", { isYou: true }),
      member("mike", "Collaborator"),
    ],
    invitations: [],
  };
}

function renderAt(path: string) {
  const router = createMemoryRouter(COCKPIT_ROUTES, { initialEntries: [path] });
  return render(<RouterProvider router={router} />);
}

const PATH = `/team/${TEAM}/members`;

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function rowOf(name: string): HTMLElement {
  return screen.getByText(`${name}@devthrottle.com`).closest("tr") as HTMLElement;
}

describe("the Team page, per role", () => {
  it("Owner: every role but their own is a dropdown, Remove on everyone but themselves, Invite someone shown", async () => {
    client.getTeamPage.mockResolvedValue(ownerPage());
    renderAt(PATH);

    await screen.findByText("Team DevThrottle");
    expect(client.getTeamPage).toHaveBeenCalledWith(TEAM);
    expect(screen.getByText("3 paid seats, 2 Collaborators (no charge), 1 invitation waiting")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Invite someone" }).getAttribute("href")).toBe(`/team/${TEAM}/invite`);

    const own = rowOf("soren");
    expect(within(own).queryByRole("combobox")).toBeNull();
    expect(within(own).queryByRole("button", { name: "Remove" })).toBeNull();
    expect(within(own).getByText("Owner")).toBeTruthy();
    for (const name of ["priya", "rob", "mike", "james"]) {
      const row = rowOf(name);
      const select = within(row).getByRole("combobox") as HTMLSelectElement;
      expect(Array.from(select.options).map((o) => o.value)).toEqual(ROLES);
      expect(within(row).getByRole("button", { name: "Remove" })).toBeTruthy();
    }
    expect(screen.getAllByRole("combobox")).toHaveLength(4);
    expect(screen.getAllByRole("button", { name: "Remove" })).toHaveLength(4);

    // The waiting invitation: seat, expiry, resend and cancel.
    const invited = screen.getByText("anna@devthrottle.com").closest("tr") as HTMLElement;
    expect(within(invited).getByText("Paid when accepted")).toBeTruthy();
    expect(within(invited).getByText(/^Expires /)).toBeTruthy();
    expect(within(invited).getByRole("button", { name: "Resend" })).toBeTruthy();
    expect(within(invited).getByRole("button", { name: "Cancel" })).toBeTruthy();
  });

  it("an expired invitation shows Expired, and offers Resend and Cancel when the Gateway says so", async () => {
    const page = ownerPage();
    page.invitations = [{
      ...page.invitations[0], id: "inv-old", email: "wrong.address@devthrottle.com", state: "expired",
      expiresAtUtc: "2026-09-20T10:00:00Z", canResend: true, canCancel: true,
    }];
    client.getTeamPage.mockResolvedValue(page);
    invitations.cancelInvitation.mockResolvedValue({});
    renderAt(PATH);

    const row = (await screen.findByText("wrong.address@devthrottle.com")).closest("tr") as HTMLElement;
    expect(within(row).getByText(/^Expired /)).toBeTruthy();
    expect(within(row).getByRole("button", { name: "Resend" })).toBeTruthy();
    fireEvent.click(within(row).getByRole("button", { name: "Cancel" }));
    fireEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Cancel invitation" }));

    await waitFor(() => expect(invitations.cancelInvitation).toHaveBeenCalledWith(TEAM, "inv-old"));
  });

  it("Resend shows the new accept link once, with Copy invitation link; the list itself never shows one", async () => {
    const page = ownerPage();
    client.getTeamPage.mockResolvedValue(page);
    const url = "https://gateway.example/invite/a-new-token";
    invitations.resendInvitation.mockResolvedValue({
      invitation: { ...page.invitations[0], expiresAtUtc: "2026-10-14T10:00:00Z" },
      email: { sent: false, message: "The invitation is saved, but its email was not sent: not configured. Resend it from the team's invitations." },
      link: { url, note: "Send this link to them yourself." },
    });
    renderAt(PATH);

    const row = (await screen.findByText(page.invitations[0].email)).closest("tr") as HTMLElement;
    expect(screen.queryByLabelText("The invitation link")).toBeNull();
    fireEvent.click(within(row).getByRole("button", { name: "Resend" }));

    expect(((await screen.findByLabelText("The invitation link")) as HTMLInputElement).value).toBe(url);
    expect(screen.getByRole("button", { name: "Copy invitation link" })).toBeTruthy();
    expect(screen.getByText("Send this link to them yourself.")).toBeTruthy();
  });

  it("Manager: role is plain text, Remove only on Developers and Collaborators, can invite", async () => {
    client.getTeamPage.mockResolvedValue(managerPage());
    renderAt(PATH);

    await screen.findByText("Team DevThrottle");
    expect(screen.queryAllByRole("combobox")).toHaveLength(0);
    expect(within(rowOf("pat")).getByText("Manager")).toBeTruthy();
    expect(within(rowOf("soren")).queryByRole("button", { name: "Remove" })).toBeNull();
    expect(within(rowOf("priya")).queryByRole("button", { name: "Remove" })).toBeNull();
    expect(within(rowOf("pat")).queryByRole("button", { name: "Remove" })).toBeNull();
    expect(within(rowOf("rob")).getByRole("button", { name: "Remove" })).toBeTruthy();
    expect(within(rowOf("mike")).getByRole("button", { name: "Remove" })).toBeTruthy();
    expect(screen.getByRole("link", { name: "Invite someone" })).toBeTruthy();
  });

  it("Developer: the list, and nothing to click", async () => {
    client.getTeamPage.mockResolvedValue(developerPage());
    renderAt(PATH);

    await screen.findByText("Team DevThrottle");
    expect(screen.getByText("rob@devthrottle.com")).toBeTruthy();
    expect(screen.getByText("3 paid seats, 2 Collaborators (no charge)")).toBeTruthy();
    expect(screen.queryAllByRole("combobox")).toHaveLength(0);
    expect(screen.queryAllByRole("button")).toHaveLength(0);
    expect(screen.queryByRole("link", { name: "Invite someone" })).toBeNull();
  });

  it("Collaborator: no Team page - the Gateway's sentence, and no member list", async () => {
    const sentence = "In this team you are a Collaborator, and a Collaborator may not open the Team page.";
    client.getTeamPage.mockRejectedValue(new GatewayError(403, sentence, { reason: sentence }));
    renderAt(PATH);

    expect((await screen.findByRole("note")).textContent).toBe(sentence);
    expect(screen.queryByRole("table")).toBeNull();
    expect(screen.queryByRole("button", { name: /try again|retry/i })).toBeNull();
  });
});

describe("changing the team on the page", () => {
  it("changing a role sends it to the server and shows what the server now holds", async () => {
    const after = ownerPage();
    after.members[2] = member("rob", "Collaborator", { ...dropdown, ...removable("rob"), seat: "Free" });
    after.summary = "2 paid seats, 3 Collaborators (no charge), 1 invitation waiting";
    client.getTeamPage.mockResolvedValueOnce(ownerPage()).mockResolvedValueOnce(after);
    client.changeMemberRole.mockResolvedValue();
    renderAt(PATH);

    const select = within(await waitFor(() => rowOf("rob"))).getByRole("combobox");
    fireEvent.change(select, { target: { value: "Collaborator" } });

    await screen.findByText("rob@devthrottle.com is now a Collaborator.");
    expect(client.changeMemberRole).toHaveBeenCalledWith(TEAM, "id-rob", "Collaborator");
    // The page re-reads the server: the role, the seat and the count are the server's, not the page's guess.
    expect(client.getTeamPage).toHaveBeenCalledTimes(2);
    expect(screen.getByText("2 paid seats, 3 Collaborators (no charge), 1 invitation waiting")).toBeTruthy();
    expect(within(rowOf("rob")).getByText("Free")).toBeTruthy();
  });

  it("a refused role change shows the Gateway's sentence and changes nothing", async () => {
    client.getTeamPage.mockResolvedValue(ownerPage());
    const sentence = "The Owner's role cannot be changed.";
    client.changeMemberRole.mockRejectedValue(new GatewayError(409, sentence, { reason: sentence }));
    renderAt(PATH);

    fireEvent.change(within(await waitFor(() => rowOf("rob"))).getByRole("combobox"), { target: { value: "Manager" } });

    expect((await screen.findByRole("status")).textContent).toBe(sentence);
    expect(client.getTeamPage).toHaveBeenCalledTimes(1);
  });

  it("remove asks first with the Gateway's sentence, then removes on the server", async () => {
    client.getTeamPage.mockResolvedValue(ownerPage());
    client.removeMember.mockResolvedValue();
    renderAt(PATH);

    fireEvent.click(within(await waitFor(() => rowOf("mike"))).getByRole("button", { name: "Remove" }));
    expect(await screen.findByText("mike@devthrottle.com will leave the team at once.")).toBeTruthy();
    expect(client.removeMember).not.toHaveBeenCalled();

    const dialog = screen.getByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(client.removeMember).toHaveBeenCalledWith(TEAM, "id-mike"));
    await screen.findByText("mike@devthrottle.com has been removed from the team.");
  });
});
