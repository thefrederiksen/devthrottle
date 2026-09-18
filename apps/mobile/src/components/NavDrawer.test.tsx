// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The phone's menu. The Assistant was removed from the product (the Fleet Manager mission, step 9) - the Fleet
// Manager that replaced it is on the Cockpit only - so the menu must not offer it. What the phone keeps is
// pinned alongside, so a removal that took a neighbour with it fails here too.

vi.mock("@devthrottle/client-core/auth/useAccounts", () => ({
  useAccounts: () => ({ accounts: [], active: null, many: false }),
}));

import { NavDrawer } from "./NavDrawer";

function openMenu() {
  render(
    <MemoryRouter>
      <NavDrawer />
    </MemoryRouter>,
  );
  fireEvent.click(screen.getByRole("button", { name: "Open menu" }));
  return Array.from(screen.getByRole("navigation", { name: "Navigation" }).querySelectorAll("a"));
}

afterEach(() => {
  cleanup();
});

describe("NavDrawer", () => {
  it("NavDrawer_Opened_OffersNoAssistant", () => {
    const links = openMenu();

    expect(links.length).toBeGreaterThan(0);
    expect(links.map((a) => a.textContent).filter((t) => /assistant/i.test(t ?? ""))).toEqual([]);
    expect(links.map((a) => a.getAttribute("href")).filter((h) => h === "/assistant")).toEqual([]);
  });

  it("NavDrawer_Opened_KeepsTheSessionsVoiceAndSettingsEntries", () => {
    const links = openMenu();

    expect(links.map((a) => [a.textContent, a.getAttribute("href")])).toEqual([
      ["Sessions", "/"],
      ["New session", "/new"],
      ["Voice Recorder", "/recorder"],
      ["Your Throttle", "/throttle"],
      ["Repos", "/repos"],
      ["Account", "/account"],
      ["Settings", "/settings"],
      ["Diagnostics", "/diagnostics"],
      ["About", "/about"],
      ["Sign out", "/account"],
    ]);
  });
});
