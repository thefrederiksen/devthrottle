// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

// A factory's page (issue #3383): it opens on the Map tab and draws the map the Gateway folded, verbatim (rule 7).
// Picking a box shows its spec beside the map; nothing on the page can change the factory.

const client = vi.hoisted(() => ({
  getFactoryMap: vi.fn(),
}));

vi.mock("@devthrottle/client-core/factory/factoryAgentsClient", () => client);

import { FactoryPageView } from "./FactoryPageView";
import { MAP, MAP_EMPTY } from "./fixtures";

function renderPage(path = "/factory-agents/website-business") {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/factory-agents/:factory" element={<FactoryPageView />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("A factory's page (issue #3383)", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    client.getFactoryMap.mockResolvedValue(MAP);
  });

  it("opens on the Map tab and draws every box and arrow the Gateway sent", async () => {
    renderPage();

    await screen.findByTestId("fa-map");
    expect(client.getFactoryMap).toHaveBeenCalledWith("website-business", expect.anything());
    expect(screen.getByRole("tab", { name: "Map" }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByTestId("fa-node-scout")).toBeTruthy();
    expect(screen.getByTestId("fa-node-bookkeeper")).toBeTruthy();
    expect(screen.getByTestId("fa-node-owner")).toBeTruthy();
    const failed = screen.getByTestId("fa-edge-scout-owner");
    expect(failed.querySelector("path")!.getAttribute("d")).toBe("M 150 150 C 250 150 350 150 415 150");
    expect(failed.querySelector("polygon")!.getAttribute("points")).toBe("425,150 415,153.5 415,146.5");
    expect(failed.getAttribute("class")).toContain("fa-tone-red");
    expect(screen.getByTestId("fa-edge-scout-bookkeeper").getAttribute("class")).toContain("fa-map-line-dashed");
    expect(screen.getByTestId("fa-map-source").textContent).toBe(MAP.sourceText);
  });

  it("colours an agent by the tone the Gateway chose and writes its status word in the box", async () => {
    renderPage();

    const scout = await screen.findByTestId("fa-node-scout");
    expect(scout.getAttribute("class")).toContain("fa-tone-red");
    expect(within(scout).getByText("FAULT")).toBeTruthy();
    expect(screen.getByTestId("fa-node-bookkeeper").getAttribute("class")).toContain("fa-map-dashed");
  });

  it("shows a picked box's spec beside the map, with a link to the agent's page", async () => {
    renderPage();

    await screen.findByTestId("fa-map");
    expect(screen.getByTestId("fa-map-spec").textContent).toContain("Pick a box");
    fireEvent.click(screen.getByTestId("fa-node-scout"));

    const spec = screen.getByTestId("fa-map-spec");
    expect(within(spec).getByText("market: small businesses on Facebook only")).toBeTruthy();
    expect(within(spec).getByText("FAULT. 07:00 - 1 failed.")).toBeTruthy();
    expect(within(spec).getByRole("link", { name: "Open Scout" }).getAttribute("href")).toBe("/factory-agents/website-business/scout");
    expect(screen.getByTestId("fa-node-scout").getAttribute("aria-pressed")).toBe("true");
  });

  it("picks a box from the keyboard too", async () => {
    renderPage();

    fireEvent.keyDown(await screen.findByTestId("fa-node-bookkeeper"), { key: "Enter" });

    expect(within(screen.getByTestId("fa-map-spec")).getByText("Planned: not built yet.")).toBeTruthy();
    expect(within(screen.getByTestId("fa-map-spec")).queryByRole("link")).toBeNull();
  });

  it("says in the Gateway's words when the factory has published no map", async () => {
    client.getFactoryMap.mockResolvedValue(MAP_EMPTY);
    renderPage();

    expect(await screen.findByText("Website Business has not published a map yet.")).toBeTruthy();
    expect(screen.queryByTestId("fa-map")).toBeNull();
  });

  it("has an Agents tab with the factory's rows, and the only change control asks the Fleet Manager", async () => {
    renderPage("/factory-agents/website-business?tab=agents");

    await screen.findByTestId("factory-page");
    expect(screen.getByRole("tab", { name: "Agents (2)" }).getAttribute("aria-selected")).toBe("true");
    expect(screen.queryByTestId("fa-map")).toBeNull();
    expect(screen.getAllByRole("row").length).toBeGreaterThan(1);
    expect(screen.getByTestId("fa-change").getAttribute("href")).toBe(MAP.changeHref);
  });
});
