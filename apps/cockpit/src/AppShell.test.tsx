// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The left rail's ORDER is a product decision, not an accident of the array literal, so it is pinned
// here: the Fleet Manager first (the Fleet Manager mission, step 6 - it replaced the Assistant), then Sessions,
// then Fleet Map. Without this test the order is one careless re-sort away from changing silently - nothing else
// in the app reads it.

vi.mock("@devthrottle/client-core/net/useKeepWarm", () => ({
  useKeepWarm: () => {},
}));

vi.mock("@devthrottle/client-core/dictation/dictionaryClient", () => ({
  getSuggestionCount: vi.fn(async () => 0),
}));

vi.mock("./network/CockpitStatusPill", () => ({
  CockpitStatusPill: () => null,
}));

const page = vi.hoisted(() => ({ waitingCount: 0 }));
vi.mock("@devthrottle/client-core/fleetmanager/pageClient", () => ({
  getFleetManagerPage: vi.fn(async () => ({ waitingCount: page.waitingCount })),
}));

// The Gateway says whether the Factory Agents area is on; the rail only follows it (rule 7).
const factory = vi.hoisted(() => ({ enabled: false }));
vi.mock("@devthrottle/client-core/factory/factoryAgentsClient", () => ({
  getFactoryAgentsSwitch: vi.fn(async () => ({ enabled: factory.enabled })),
}));

import { screen, waitFor } from "@testing-library/react";
import { AppShell } from "./AppShell";
import { resetFactorySwitchCache } from "./factory/useFactorySwitch";

function railLabels(): string[] {
  const list = document.querySelector(".nav-list:not(.nav-list-foot)");
  if (list === null) throw new Error("the shell rendered no main nav list");
  return Array.from(list.querySelectorAll(".nav-link-label")).map((el) => el.textContent ?? "");
}

describe("Cockpit left rail", () => {
  beforeEach(() => {
    // This project runs vitest without globals, so testing-library's automatic cleanup is not
    // registered - without this, each render leaks into the next test's document.
    cleanup();
    factory.enabled = false;
    resetFactorySwitchCache();
  });

  it("opens with the Fleet Manager, then Sessions, then Fleet Map", () => {
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    expect(railLabels().slice(0, 3)).toEqual(["Fleet Manager", "Sessions", "Fleet Map"]);
  });

  it("badges the Fleet Manager with the Gateway's count of what is waiting", async () => {
    page.waitingCount = 7;
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    const entry = screen.getByRole("link", { name: /Fleet Manager/ });
    expect(entry.getAttribute("href")).toBe("/fleet-manager");
    await waitFor(() => expect(entry.querySelector(".nav-badge")?.textContent).toBe("7"));
    expect(entry.querySelector(".nav-badge")?.getAttribute("title")).toBe("7 waiting on you");
  });

  it("shows no badge when nothing is waiting", async () => {
    page.waitingCount = 0;
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    const entry = screen.getByRole("link", { name: /Fleet Manager/ });
    await new Promise((r) => setTimeout(r, 20));
    expect(entry.querySelector(".nav-badge")).toBeNull();
  });

  // The Assistant was removed from the product (the Fleet Manager mission, step 9). No rail entry - main list
  // or foot - may name it or link to its old address.
  it("has no Assistant entry anywhere in the rail", () => {
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    const links = Array.from(document.querySelectorAll(".nav-list a"));
    expect(links.length).toBeGreaterThan(10);
    expect(links.map((a) => a.textContent ?? "").filter((t) => /assistant/i.test(t))).toEqual([]);
    expect(links.map((a) => a.getAttribute("href")).filter((h) => h === "/assistant")).toEqual([]);
  });

  it("leaves the rest of the rail where it was", () => {
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    expect(railLabels()).toEqual([
      "Fleet Manager",
      "Sessions",
      "Fleet Map",
      "History",
      "Directors",
      "Schedule",
      "Workflows",
      // Skills sits immediately after Workflows on purpose: two lists on one shelf (the central
      // skill library, devthrottle_internal issue 995). Nothing else moved.
      "Skills",
      "Dictionary",
      "Voice Recorder",
      "Transcription",
      "Network",
    ]);
  });

  // Website Business Factory: Factory Agents sits after Fleet Map and before History - only while the Gateway's
  // factoryAgents.enabled switch is on. Off, the rail is exactly what it was.
  it("shows Factory Agents after Fleet Map and before History when the Gateway says the area is on", async () => {
    factory.enabled = true;
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await waitFor(() => expect(railLabels()).toContain("Factory Agents"));
    expect(railLabels().slice(0, 5)).toEqual(["Fleet Manager", "Sessions", "Fleet Map", "Factory Agents", "History"]);
    expect(screen.getByRole("link", { name: /Factory Agents/ }).getAttribute("href")).toBe("/factory-agents");
  });

  it("has no Factory Agents item when the Gateway says the area is off", async () => {
    factory.enabled = false;
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await new Promise((r) => setTimeout(r, 20));
    expect(railLabels()).not.toContain("Factory Agents");
  });
});
