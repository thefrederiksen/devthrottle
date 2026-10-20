// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The phone's menu. The Assistant was removed from the product (the Fleet Manager mission, step 9) - the Fleet
// Manager that replaced it is on the Cockpit only - so the menu must not offer it. What the phone keeps is
// pinned alongside, so a removal that took a neighbour with it fails here too.

vi.mock("@devthrottle/client-core/auth/useAccounts", () => ({
  useAccounts: () => ({ accounts: [], active: null, many: false }),
}));

// The Mail Desk entry (Mail Desk mission, phase 7) follows the Gateway's answer to the Mail Desk page's own read: the
// switch off (404) hides it, the list offers it, and any other failure also hides it (contract F1). Unstaged, the
// switch is off.
const mailDeskRead = vi.hoisted(() => ({ answer: null as unknown, calls: 0 }));
vi.mock("@devthrottle/client-core/maildesk/mailDeskClient", () => ({
  getMailDeskChannels: vi.fn(async () => {
    mailDeskRead.calls += 1;
    if (mailDeskRead.answer === null) return { kind: "off" };
    if (mailDeskRead.answer instanceof Error) throw mailDeskRead.answer;
    return mailDeskRead.answer;
  }),
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", async (importActual) => ({
  ...(await importActual<typeof import("@devthrottle/client-core/errors/reportClientError")>()),
  reportClientError: vi.fn(),
}));

import { GatewayError } from "@devthrottle/client-core/api/client";
import { reportClientError } from "@devthrottle/client-core/errors/reportClientError";
import { resetMailDeskEntryCache } from "@devthrottle/client-core/maildesk/useMailDeskEntry";
import { NavDrawer } from "./NavDrawer";

const MAIL_DESK_LIST = { kind: "channels", envelope: { count: 0, channels: [], title: "t", summary: "s", emptyLabel: "e" } };

function openMenu() {
  render(
    <MemoryRouter>
      <NavDrawer />
    </MemoryRouter>,
  );
  fireEvent.click(screen.getByRole("button", { name: "Open menu" }));
  return Array.from(screen.getByRole("navigation", { name: "Navigation" }).querySelectorAll("a"));
}

/** Open the menu once the Mail Desk read has settled, so "no entry" is never just "not answered yet". */
async function openMenuSettled() {
  openMenu();
  await waitFor(() => expect(mailDeskRead.calls).toBe(1));
  await act(async () => {
    await new Promise((r) => setTimeout(r, 0));
  });
  return Array.from(screen.getByRole("navigation", { name: "Navigation" }).querySelectorAll("a"));
}

afterEach(() => {
  cleanup();
  mailDeskRead.answer = null;
  mailDeskRead.calls = 0;
  resetMailDeskEntryCache();
  vi.mocked(reportClientError).mockClear();
});

describe("NavDrawer", () => {
  it("NavDrawer_Opened_OffersNoAssistant", () => {
    const links = openMenu();

    expect(links.length).toBeGreaterThan(0);
    expect(links.map((a) => a.textContent).filter((t) => /assistant/i.test(t ?? ""))).toEqual([]);
    expect(links.map((a) => a.getAttribute("href")).filter((h) => h === "/assistant")).toEqual([]);
  });

  it("NavDrawer_Opened_KeepsTheSessionsVoiceAndSettingsEntries", async () => {
    const links = await openMenuSettled();

    expect(links.map((a) => [a.textContent, a.getAttribute("href")])).toEqual([
      ["Sessions", "/"],
      ["New session", "/new"],
      ["Voice Recorder", "/recorder"],
      ["Your Throttle", "/throttle"],
      ["Repos", "/repos"],
      ["Rules", "/rules"],
      ["Account", "/account"],
      ["Settings", "/settings"],
      ["Diagnostics", "/diagnostics"],
      ["About", "/about"],
      ["Sign out", "/account"],
    ]);
  });

  it("NavDrawer_MailDeskOn_OffersTheMailDeskAfterRepos", async () => {
    mailDeskRead.answer = MAIL_DESK_LIST;

    const links = await openMenuSettled();

    const entries = links.map((a) => [a.textContent, a.getAttribute("href")]);
    expect(entries.slice(4, 7)).toEqual([
      ["Repos", "/repos"],
      ["Mail Desk", "/mail-desk"],
      ["Rules", "/rules"],
    ]);
  });

  it("NavDrawer_MailDeskSwitchOff_DrawsNoMailDeskEntry", async () => {
    mailDeskRead.answer = { kind: "off" };

    const links = await openMenuSettled();

    expect(links.filter((a) => a.getAttribute("href") === "/mail-desk" || a.textContent === "Mail Desk")).toEqual([]);
    expect(reportClientError).not.toHaveBeenCalled();
  });

  // Contract section 1, corrected after the phase review (F1): drawn ONLY on a successful read. No failure draws it.
  it.each([
    ["Offline", new TypeError("Failed to fetch")],
    ["Timeout", Object.assign(new Error("The operation timed out."), { name: "TimeoutError" })],
    ["ServerFault", new GatewayError(503, "the Gateway is restarting")],
    ["BrokenAnswer", new GatewayError(502, "The Gateway sent a Mail Desk list this app cannot read")],
  ])("NavDrawer_MailDeskReadFails_%s_DrawsNoMailDeskEntryAndReportsIt", async (_kind, failure) => {
    mailDeskRead.answer = failure;

    const links = await openMenuSettled();

    expect(links.filter((a) => a.getAttribute("href") === "/mail-desk" || a.textContent === "Mail Desk")).toEqual([]);
    expect(reportClientError).toHaveBeenCalledTimes(1);
  });
});
