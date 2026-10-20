// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Outlet, useRoutes } from "react-router-dom";
import { GatewayError } from "@devthrottle/client-core/api/client";
import type { MailDeskAnswer } from "@devthrottle/client-core/maildesk/mailDeskClient";
import { oddChannels } from "@devthrottle/client-core/maildesk/mailDeskFixture.testkit";

// The Cockpit's Mail Desk address (Mail Desk mission, phase 7) driven through the REAL route table the app mounts
// (COCKPIT_ROUTES), with the real page and the Cockpit's real frame, "Page not found" and error banner. Only the
// Gateway's answer is staged. The page's own behaviour is tested in client-core; here it is the Cockpit's half: the
// address exists, the switch off looks exactly like an unknown address, and the page sits in the Cockpit's pane.

vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => true,
  getDeviceKey: () => "a-device-key",
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));
// The shell frame (rail, header, live connection) cannot run in jsdom and is not the subject.
vi.mock("../AppShell", () => ({ AppShell: () => <Outlet /> }));

const read = vi.hoisted(() => ({ answer: null as (() => Promise<MailDeskAnswer>) | null }));
vi.mock("@devthrottle/client-core/maildesk/mailDeskClient", () => ({
  getMailDeskChannels: vi.fn(() => {
    if (read.answer === null) throw new Error("no Mail Desk answer staged");
    return read.answer();
  }),
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", async (importActual) => ({
  ...(await importActual<typeof import("@devthrottle/client-core/errors/reportClientError")>()),
  reportClientError: vi.fn(),
}));

import { COCKPIT_ROUTES } from "../routes";

function Routes() {
  return <>{useRoutes(COCKPIT_ROUTES)}</>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  read.answer = null;
});

afterEach(() => {
  cleanup();
});

describe("the Cockpit's Mail Desk address", () => {
  it("MailDeskView_SwitchOn_ShowsTheGatewaysTitleInThePaneWithOneCardPerChannel", async () => {
    const envelope = oddChannels();
    read.answer = () => Promise.resolve({ kind: "channels", envelope });

    const { container } = renderAt("/mail-desk");

    expect(await screen.findAllByTestId("mail-desk-channel")).toHaveLength(envelope.channels.length);
    const title = container.querySelector(".pane > h1.pane-title");
    expect(title?.textContent).toBe(envelope.title);
  });

  it("MailDeskView_SwitchOff_LooksExactlyLikeAnUnknownAddress", async () => {
    const unknown = renderAt("/no-such-page");
    const expected = unknown.container.innerHTML;
    expect(expected).toContain("Page not found");
    cleanup();

    read.answer = () => Promise.resolve({ kind: "off" });
    const view = renderAt("/mail-desk");

    await screen.findByText("Page not found");
    expect(view.container.innerHTML).toBe(expected);
  });

  it("MailDeskView_FailedRead_ShowsTheCockpitsErrorBanner", async () => {
    read.answer = () => Promise.reject(new GatewayError(500, "fault", { reason: "The Mail Desk store is not answering." }));

    const { container } = renderAt("/mail-desk");

    const banner = await screen.findByRole("alert");
    expect(banner.className).toContain("ui-error-banner");
    expect(banner.textContent).toContain("The Mail Desk store is not answering.");
    expect(container.querySelector(".pane")).not.toBeNull();
    expect(screen.queryByText("Page not found")).toBeNull();
  });
});
