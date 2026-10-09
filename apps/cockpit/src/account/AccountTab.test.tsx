// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, cleanup, screen, within, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// THE ACCOUNT TAB OF SETTINGS (owner, 8 Oct 2026; devthrottle#3681, Mockup C). It holds only you: email, how you sign
// in, devices, your teams - and ONE sign-out, which names the account. The browser's accounts list left it for the menu
// behind your name. The Gateway's own sign-in (POST /account/logout) is shown only where the Gateway says it holds one
// - a self-hosted Gateway - and worded as disconnecting the Gateway, so it cannot be taken for signing out.

vi.mock("@devthrottle/client-core/auth/accountActions", () => ({
  signOutAccount: vi.fn(async () => ({ ok: true })),
}));

import { CurrentTeamProvider } from "@devthrottle/client-core/teams/CurrentTeam";
import { AccountTab } from "./AccountTab";

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

function gateway(status: Record<string, unknown>) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (path: string) => {
      if (path === "/account/status") return json(status);
      if (path === "/account/devices") return json({ signedIn: true, devices: [] });
      throw new Error(`unexpected request ${path}`);
    }),
  );
}

function mount() {
  return render(
    <MemoryRouter>
      <CurrentTeamProvider load={() => Promise.resolve({ kind: "not-offered", reason: "dark" })}>
        <AccountTab />
      </CurrentTeamProvider>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  cleanup();
  window.localStorage.clear();
  window.localStorage.setItem(
    "cc.accounts",
    JSON.stringify([
      { id: "a1", label: "soren@centerconsulting.com", email: "soren@centerconsulting.com", deviceKey: "k1", installId: "i1" },
      { id: "a2", label: "soren@duksrevo.com", email: "soren@duksrevo.com", deviceKey: "k2", installId: "i2" },
    ]),
  );
  window.localStorage.setItem("cc.activeAccount", "a1");
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the Account tab", () => {
  it("on the hosted Gateway: you, your devices and one sign-out that names the account - and no Gateway Log out", async () => {
    gateway({ signedIn: true, email: "soren@centerconsulting.com", gatewaySignIn: false });
    mount();

    const you = await screen.findByRole("region", { name: "Your account" });
    expect(within(you).getByText("soren@centerconsulting.com")).toBeTruthy();
    const signOuts = screen.getAllByRole("button", { name: /sign out|log out/i });
    expect(signOuts.map((b) => b.textContent)).toEqual(["Sign out of soren@centerconsulting.com"]);
    expect(await screen.findByText("Your devices")).toBeTruthy();
    expect(screen.queryByTestId("account-gateway-sign-in")).toBeNull();
    // The browser's other accounts are not listed here any more - they are in the menu behind your name.
    expect(screen.queryByText("soren@duksrevo.com")).toBeNull();
    // How you sign in is shown only when the Gateway knows it; hosted does not record the provider.
    expect(screen.queryByText("Signed in with")).toBeNull();
  });

  it("on a self-hosted Gateway: the Gateway's own sign-in, worded as disconnecting the Gateway, apart from the sign-out", async () => {
    gateway({ signedIn: true, email: "soren@centerconsulting.com", provider: "github", gatewaySignIn: true });
    mount();

    const section = await screen.findByTestId("account-gateway-sign-in");
    expect(within(section).getByText("Connected to DevThrottle as soren@centerconsulting.com")).toBeTruthy();
    expect(within(section).getByRole("button", { name: "Disconnect this Gateway" })).toBeTruthy();
    expect(within(section).getByText(/It does not sign this browser out\./)).toBeTruthy();
    expect(screen.getByText("Signed in with")).toBeTruthy();
    expect(screen.getByText("github")).toBeTruthy();
    // Still exactly one sign-out, and nothing anywhere called "Log out".
    expect(screen.getAllByRole("button", { name: /^Sign out/ })).toHaveLength(1);
    expect(screen.queryByRole("button", { name: /log out/i })).toBeNull();
  });

  it("on a self-hosted Gateway that is not connected, offers to connect it", async () => {
    gateway({ signedIn: false, gatewaySignIn: true });
    mount();

    const section = await screen.findByTestId("account-gateway-sign-in");
    expect(within(section).getByRole("button", { name: "Connect this Gateway to DevThrottle" })).toBeTruthy();
    await waitFor(() => expect(screen.queryByText("Your devices")).toBeNull());
  });
});
