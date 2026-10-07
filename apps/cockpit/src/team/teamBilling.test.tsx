// @vitest-environment jsdom

// THE BILLING SECTION OF THE TEAM PAGE (Teams v1, the team bill without Stripe), driven through the REAL route table the
// app mounts. The fixtures are shaped exactly as GET /teams/{teamId}/page answers the Owner and a Manager (proven in the
// Gateway's TeamBillTests); these tests prove the section renders the verdicts it is given - the Owner's buttons, the
// checkout, the auto-renew switch, the cancel warning, the history - and nothing it is not: a Manager gets no control, a
// Developer gets no section, and no word on it says "free".

import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent, within } from "@testing-library/react";
import { Outlet, RouterProvider, createMemoryRouter } from "react-router-dom";
import type { TeamBill, TeamPage } from "@devthrottle/client-core/teams/teamPageClient";

const TEAM = "3f1d2c9e-0000-4000-8000-000000000002";

vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => true,
  getDeviceKey: () => "device-key",
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));

const client = {
  getTeamPage: vi.fn<(teamId: string) => Promise<TeamPage>>(),
  startTeamPlan: vi.fn<(teamId: string) => Promise<void>>(),
  renewTeamPlan: vi.fn<(teamId: string) => Promise<void>>(),
  setTeamPlanAutoRenew: vi.fn<(teamId: string, on: boolean) => Promise<void>>(),
  cancelTeamPlan: vi.fn<(teamId: string) => Promise<void>>(),
};
vi.mock("@devthrottle/client-core/teams/teamPageClient", () => ({
  getTeamPage: (teamId: string) => client.getTeamPage(teamId),
  changeMemberRole: vi.fn(),
  removeMember: vi.fn(),
  startTeamPlan: (teamId: string) => client.startTeamPlan(teamId),
  renewTeamPlan: (teamId: string) => client.renewTeamPlan(teamId),
  setTeamPlanAutoRenew: (teamId: string, on: boolean) => client.setTeamPlanAutoRenew(teamId, on),
  cancelTeamPlan: (teamId: string) => client.cancelTeamPlan(teamId),
}));
vi.mock("@devthrottle/client-core/teams/invitationsClient", () => ({ resendInvitation: vi.fn(), cancelInvitation: vi.fn() }));
vi.mock("../AppShell", () => ({ AppShell: () => <Outlet /> }));

import { COCKPIT_ROUTES } from "../routes";

const CHECKOUT = {
  seatsLine: "3 paid seats",
  priceLine: "US$49.00 a seat a month",
  totalLine: "Total: US$147.00 a month",
  chargeLine: "No charge - you will not be charged",
  periodLine: "7 Oct 2026 to 7 Nov 2026, then every month until you cancel",
};

const LINE = {
  id: "line-1",
  period: "7 Oct 2026 to 7 Nov 2026",
  seats: 3,
  amount: "US$49.00 x 3 paid seats = US$147.00",
  charged: "Charged: US$0.00",
  reason: "Plan started",
};

function bill(overrides: Partial<TeamBill>): TeamBill {
  return {
    state: "active",
    statusLabel: "Active",
    statusLine: "Renews on 7 Nov 2026. Auto-renew is on.",
    seats: 3,
    seatsLine: "3 paid seats",
    priceLine: "US$49.00 a seat a month",
    amountLine: "US$49.00 x 3 paid seats = US$147.00 a month",
    chargeLine: "No charge",
    periodEndUtc: "2026-11-07T09:30:00Z",
    periodEnd: "7 Nov 2026",
    autoRenew: true,
    canChange: true,
    canStart: false,
    canRenew: false,
    canSetAutoRenew: true,
    canCancel: true,
    checkout: null,
    note: null,
    cancelWarning: "The team plan stays active until 7 Nov 2026, then ends. After that nobody can be invited or join, and paid features stop for the team. You can renew at any time.",
    history: [LINE],
    ...overrides,
  };
}

const NOT_STARTED = bill({
  state: "not-started", statusLabel: "Not started",
  statusLine: "The team plan has not started. Members can be invited once it has.",
  periodEndUtc: null, periodEnd: null, autoRenew: false, canStart: true, canSetAutoRenew: false, canCancel: false,
  checkout: { title: "Start the team plan", confirmLabel: "Start the plan", ...CHECKOUT },
  cancelWarning: null, history: [],
});

const ENDED = bill({
  state: "ended", statusLabel: "Ended", statusLine: "Ended on 7 Nov 2026. Renew to start a new month.",
  autoRenew: false, canRenew: true, canSetAutoRenew: false, canCancel: false,
  checkout: { title: "Renew the team plan", confirmLabel: "Renew now", ...CHECKOUT },
  cancelWarning: null,
});

const MANAGER = bill({
  canChange: false, canSetAutoRenew: false, canCancel: false, cancelWarning: null,
  note: "Only the team's Owner can change the billing.",
});

function page(role: string, teamBill: TeamBill | null): TeamPage {
  return {
    teamId: TEAM,
    teamName: "DevThrottle",
    yourRole: role,
    summary: "3 paid seats, 1 Collaborator",
    canInvite: role !== "Developer",
    members: [],
    invitations: [],
    bill: teamBill,
  };
}

function renderPage() {
  const router = createMemoryRouter(COCKPIT_ROUTES, { initialEntries: [`/team/${TEAM}/members`] });
  return render(<RouterProvider router={router} />);
}

async function billing(): Promise<HTMLElement> {
  return screen.findByRole("region", { name: "Billing" });
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("the Billing section, for the Owner", () => {
  it("not started: Start the team plan opens a checkout with the total and no charge, and starts the plan on the server", async () => {
    client.getTeamPage.mockResolvedValueOnce(page("Owner", NOT_STARTED)).mockResolvedValueOnce(page("Owner", bill({})));
    client.startTeamPlan.mockResolvedValue();
    renderPage();

    const section = await billing();
    expect(within(section).getByText("Not started")).toBeTruthy();
    expect(within(section).getByText("No charge")).toBeTruthy();
    expect(within(section).queryByRole("switch")).toBeNull();
    fireEvent.click(within(section).getByRole("button", { name: "Start the team plan" }));

    const dialog = screen.getByRole("alertdialog");
    expect(within(dialog).getByText("Total: US$147.00 a month")).toBeTruthy();
    expect(within(dialog).getByText("No charge - you will not be charged")).toBeTruthy();
    expect(client.startTeamPlan).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole("button", { name: "Start the plan" }));

    await waitFor(() => expect(client.startTeamPlan).toHaveBeenCalledWith(TEAM));
    await screen.findByText("The team plan has started.");
    expect(client.getTeamPage).toHaveBeenCalledTimes(2);
    expect(within(await billing()).getByText("Active")).toBeTruthy();
  });

  it("active: the auto-renew switch, Cancel plan with the Gateway's warning, and the history that charged nothing", async () => {
    client.getTeamPage.mockResolvedValue(page("Owner", bill({})));
    client.setTeamPlanAutoRenew.mockResolvedValue();
    client.cancelTeamPlan.mockResolvedValue();
    renderPage();

    const section = await billing();
    expect(within(section).getByText("Renews on 7 Nov 2026. Auto-renew is on.")).toBeTruthy();
    expect(within(section).getByText("US$49.00 x 3 paid seats = US$147.00 a month")).toBeTruthy();
    expect(within(section).getByText("Charged: US$0.00")).toBeTruthy();
    expect(within(section).getByText("US$49.00 x 3 paid seats = US$147.00")).toBeTruthy();

    const toggle = within(section).getByRole("switch");
    expect(toggle.getAttribute("aria-checked")).toBe("true");
    fireEvent.click(toggle);
    await waitFor(() => expect(client.setTeamPlanAutoRenew).toHaveBeenCalledWith(TEAM, false));

    fireEvent.click(within(await billing()).getByRole("button", { name: "Cancel plan" }));
    const dialog = screen.getByRole("alertdialog");
    expect(within(dialog).getByText(/stays active until 7 Nov 2026, then ends/)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel plan" }));
    await waitFor(() => expect(client.cancelTeamPlan).toHaveBeenCalledWith(TEAM));
  });

  it("ended: Renew now opens the checkout and renews on the server", async () => {
    client.getTeamPage.mockResolvedValue(page("Owner", ENDED));
    client.renewTeamPlan.mockResolvedValue();
    renderPage();

    const section = await billing();
    expect(within(section).getByText("Ended")).toBeTruthy();
    expect(within(section).queryByRole("button", { name: "Cancel plan" })).toBeNull();
    fireEvent.click(within(section).getByRole("button", { name: "Renew now" }));
    fireEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Renew now" }));

    await waitFor(() => expect(client.renewTeamPlan).toHaveBeenCalledWith(TEAM));
  });

  it("a refused change shows the Gateway's sentence in the checkout and changes nothing", async () => {
    client.getTeamPage.mockResolvedValue(page("Owner", NOT_STARTED));
    const { GatewayError } = await import("@devthrottle/client-core/api/client");
    const sentence = "The team plan is already running.";
    client.startTeamPlan.mockRejectedValue(new GatewayError(409, sentence, { reason: sentence }));
    renderPage();

    fireEvent.click(within(await billing()).getByRole("button", { name: "Start the team plan" }));
    fireEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Start the plan" }));

    expect(await within(screen.getByRole("alertdialog")).findByText(new RegExp(sentence))).toBeTruthy();
    expect(client.getTeamPage).toHaveBeenCalledTimes(1);
  });
});

describe("the Billing section, for everyone else", () => {
  it("Manager: the same bill, read-only - no switch, no buttons, and the Gateway's note", async () => {
    client.getTeamPage.mockResolvedValue(page("Manager", MANAGER));
    renderPage();

    const section = await billing();
    expect(within(section).getByText("Active")).toBeTruthy();
    expect(within(section).getByText("Charged: US$0.00")).toBeTruthy();
    expect(within(section).queryByRole("switch")).toBeNull();
    expect(within(section).queryAllByRole("button")).toHaveLength(0);
    expect(within(section).getByText("Only the team's Owner can change the billing.")).toBeTruthy();
    expect(within(section).getByText("On")).toBeTruthy();
  });

  it("Developer: the Gateway sends no bill, and there is no Billing section", async () => {
    client.getTeamPage.mockResolvedValue(page("Developer", null));
    renderPage();

    await screen.findByText("Team DevThrottle");
    expect(screen.queryByRole("region", { name: "Billing" })).toBeNull();
  });

  it("no state of the section says free", async () => {
    for (const shown of [NOT_STARTED, bill({}), ENDED, MANAGER]) {
      client.getTeamPage.mockResolvedValue(page("Owner", shown));
      renderPage();
      const section = await billing();
      expect(section.textContent ?? "").not.toMatch(/free/i);
      cleanup();
    }
  });
});
