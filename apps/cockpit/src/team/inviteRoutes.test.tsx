// @vitest-environment jsdom

// TEAM INVITATIONS IN THE COCKPIT (devthrottle_internal#2301): the invite form (S2) and the accept page (S3), driven
// through the REAL route table the app mounts (COCKPIT_ROUTES), so the sign-in gate is the app's own.
//
// Issue test 4 - "an invitation to an email with no account leads through sign-up and back to the accept page" - has
// three legs here: a signed-out browser at /invite/{token} is sent to sign-in carrying exactly that address; the
// remembered next survives the sign-in round trip's safety check; and the router, told to go there as DeviceCallback
// does, lands on the accept page with the same token. The Gateway half (a new account accepting) is proven in the
// Gateway suites.

import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent } from "@testing-library/react";
import { Outlet, RouterProvider, createMemoryRouter } from "react-router-dom";
import type { InviteOptions, TeamInvitation } from "@devthrottle/client-core/teams/invitationsClient";
import { rememberEnrollNext, takeEnrollNext } from "@devthrottle/client-core/auth/enrollRequest";

const TOKEN = "q3J8vZ_x-0aB1cD2eF3gH4iJ5kL6mN7oP8qR9sT0uVw";
const TEAM = "3f1d2c9e-0000-4000-8000-000000000001";

let deviceKey: string | null = null;
vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => deviceKey !== null,
  getDeviceKey: () => deviceKey,
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));

const client = {
  getInviteOptions: vi.fn<(teamId: string) => Promise<InviteOptions>>(),
  listInvitations: vi.fn<(teamId: string) => Promise<TeamInvitation[]>>(),
  sendInvitation: vi.fn(),
  resendInvitation: vi.fn(),
  cancelInvitation: vi.fn(),
  openInvitation: vi.fn<(token: string) => Promise<TeamInvitation>>(),
  acceptInvitation: vi.fn<(token: string) => Promise<TeamInvitation>>(),
  declineInvitation: vi.fn<(token: string) => Promise<TeamInvitation>>(),
};
vi.mock("@devthrottle/client-core/teams/invitationsClient", () => ({
  getInviteOptions: (teamId: string) => client.getInviteOptions(teamId),
  listInvitations: (teamId: string) => client.listInvitations(teamId),
  sendInvitation: (...a: unknown[]) => client.sendInvitation(...a),
  resendInvitation: (...a: unknown[]) => client.resendInvitation(...a),
  cancelInvitation: (...a: unknown[]) => client.cancelInvitation(...a),
  openInvitation: (token: string) => client.openInvitation(token),
  acceptInvitation: (token: string) => client.acceptInvitation(token),
  declineInvitation: (token: string) => client.declineInvitation(token),
}));

// The shell frame cannot run in jsdom and is not the subject; the invite form routes into it as its outlet.
vi.mock("../AppShell", () => ({ AppShell: () => <Outlet /> }));

import { COCKPIT_ROUTES } from "../routes";

function invitation(overrides: Partial<TeamInvitation> = {}): TeamInvitation {
  return {
    id: "inv-1",
    teamId: TEAM,
    teamName: "DevThrottle",
    email: "anna@devthrottle.com",
    role: "Developer",
    state: "sent",
    invitedBy: "priya@devthrottle.com",
    acceptedBy: null,
    paidBy: "soren@centerconsulting.com",
    sentAtUtc: "2026-10-01T10:00:00Z",
    expiresAtUtc: "2026-10-08T10:00:00Z",
    signedInAs: "anna@devthrottle.com",
    canRespond: true,
    refusal: null,
    ...overrides,
  };
}

function options(overrides: Partial<InviteOptions> = {}): InviteOptions {
  return {
    teamId: TEAM,
    teamName: "DevThrottle",
    yourRole: "Manager",
    roles: [
      { role: "Manager", allowed: false, hint: "Only the Owner can invite a Manager." },
      { role: "Developer", allowed: true, hint: "Runs sessions on their own computer. A paid seat on soren@centerconsulting.com's bill." },
      { role: "Collaborator", allowed: true, hint: "Answers questions, sends requests, reads reports. Free." },
    ],
    blocked: null,
    expiryNote: "The invitation expires in 7 days.",
    ...overrides,
  };
}

function mountAt(entry: string) {
  const router = createMemoryRouter(COCKPIT_ROUTES, { initialEntries: [entry] });
  render(<RouterProvider router={router} />);
  return router;
}

afterEach(() => {
  cleanup();
  Object.values(client).forEach((f) => f.mockReset());
  deviceKey = null;
  sessionStorage.clear();
});

describe("the accept page (S3)", () => {
  it("sends a signed-out browser - a person with no account yet - to sign-in carrying the accept page's address", () => {
    deviceKey = null;

    const router = mountAt(`/invite/${TOKEN}`);

    expect(router.state.location.pathname + router.state.location.search).toBe(
      `/signin?next=${encodeURIComponent(`/invite/${TOKEN}`)}`,
    );
    expect(client.openInvitation).not.toHaveBeenCalled();
  });

  it("keeps the accept page as the place to return to through the sign-up round trip", () => {
    // SignIn remembers next before leaving for devthrottle.com; DeviceCallback takes it on the way back.
    rememberEnrollNext(`/invite/${TOKEN}`);

    expect(takeEnrollNext()).toBe(`/invite/${TOKEN}`);
  });

  it("lands back on the same invitation once signed up, as the callback navigates there", async () => {
    deviceKey = "a-device-key";
    client.openInvitation.mockResolvedValue(invitation());

    const router = mountAt("/signin");
    await router.navigate(`/invite/${TOKEN}`);

    expect(await screen.findByText("priya@devthrottle.com invited you to the DevThrottle team")).toBeTruthy();
    expect(client.openInvitation).toHaveBeenCalledWith(TOKEN);
    expect(screen.getByText(/as a Developer\. soren@centerconsulting\.com pays for your seat\./)).toBeTruthy();
    expect(screen.getByText("You're signed in as anna@devthrottle.com.")).toBeTruthy();
  });

  it("joins the team on Join the team and says so", async () => {
    deviceKey = "a-device-key";
    client.openInvitation.mockResolvedValue(invitation());
    client.acceptInvitation.mockResolvedValue(invitation({ state: "accepted", canRespond: false }));

    mountAt(`/invite/${TOKEN}`);
    fireEvent.click(await screen.findByRole("button", { name: "Join the team" }));

    expect(await screen.findByText("You joined the DevThrottle team")).toBeTruthy();
    expect(client.acceptInvitation).toHaveBeenCalledWith(TOKEN);
  });

  it("shows the Gateway's reason, and no buttons, for an invitation that can no longer be used", async () => {
    deviceKey = "a-device-key";
    const reason = "This invitation has expired. It was sent on 1 Oct 2026 and was good for 7 days. Ask priya@devthrottle.com to send a new one.";
    client.openInvitation.mockResolvedValue(invitation({ state: "expired", canRespond: false, refusal: reason }));

    mountAt(`/invite/${TOKEN}`);

    expect(await screen.findByText(reason)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Join the team" })).toBeNull();
  });

  it("asks before declining, then declines", async () => {
    deviceKey = "a-device-key";
    client.openInvitation.mockResolvedValue(invitation());
    client.declineInvitation.mockResolvedValue(invitation({ state: "declined", canRespond: false }));

    mountAt(`/invite/${TOKEN}`);
    fireEvent.click(await screen.findByRole("button", { name: "Decline" }));
    expect(client.declineInvitation).not.toHaveBeenCalled();
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(Array.from(dialog.querySelectorAll("button")).find((b) => b.textContent === "Decline")!);

    expect(await screen.findByText("You declined the invitation to DevThrottle")).toBeTruthy();
    expect(client.declineInvitation).toHaveBeenCalledWith(TOKEN);
  });
});

describe("the invite form (S2)", () => {
  it("offers the roles the Gateway allows, greys out the rest with its reason, and sends the typed address", async () => {
    deviceKey = "a-device-key";
    client.getInviteOptions.mockResolvedValue(options());
    client.listInvitations.mockResolvedValue([]);
    client.sendInvitation.mockResolvedValue({
      invitation: invitation({ email: "rob@any-domain.io" }),
      email: { sent: true, message: "The invitation email is on its way." },
    });

    mountAt(`/team/${TEAM}/invite`);

    const manager = (await screen.findByDisplayValue("Manager")) as HTMLInputElement;
    expect(manager.disabled).toBe(true);
    expect(screen.getByText("Only the Owner can invite a Manager.")).toBeTruthy();
    expect(((screen.getByDisplayValue("Developer")) as HTMLInputElement).checked).toBe(true);

    fireEvent.change(screen.getByLabelText("Email"), { target: { value: "rob@any-domain.io" } });
    fireEvent.click(screen.getByRole("button", { name: "Send invitation" }));

    await waitFor(() => expect(client.sendInvitation).toHaveBeenCalledWith(TEAM, "rob@any-domain.io", "Developer"));
    expect(await screen.findByText(/Invitation sent to rob@any-domain\.io as a Developer\./)).toBeTruthy();
  });

  it("says why nothing can be sent when the Gateway blocks the form, and keeps Send disabled", async () => {
    deviceKey = "a-device-key";
    client.getInviteOptions.mockResolvedValue(options({ blocked: "The team's bill has not started - the Owner finishes billing first." }));
    client.listInvitations.mockResolvedValue([]);

    mountAt(`/team/${TEAM}/invite`);

    expect(await screen.findByText("The team's bill has not started - the Owner finishes billing first.")).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Email"), { target: { value: "rob@x.io" } });
    expect((screen.getByRole("button", { name: "Send invitation" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("lists waiting invitations with Resend, and asks before Cancel", async () => {
    deviceKey = "a-device-key";
    client.getInviteOptions.mockResolvedValue(options());
    client.listInvitations.mockResolvedValue([invitation()]);
    client.resendInvitation.mockResolvedValue({
      invitation: invitation({ expiresAtUtc: "2026-10-12T10:00:00Z" }),
      email: { sent: true, message: "The invitation email is on its way." },
    });

    mountAt(`/team/${TEAM}/invite`);

    fireEvent.click(await screen.findByRole("button", { name: "Resend" }));
    await waitFor(() => expect(client.resendInvitation).toHaveBeenCalledWith(TEAM, "inv-1"));

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(await screen.findByRole("alertdialog")).toBeTruthy();
    expect(client.cancelInvitation).not.toHaveBeenCalled();
  });
});
