// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

// A factory agent's page (Screen 2), the Waiting for you list (Screen 4) and the switch gate: each renders the
// Gateway's fold verbatim (rule 7), and the only controls are the ones the mandate names.

const client = vi.hoisted(() => ({
  getFactoryAgent: vi.fn(),
  getFactoryWaiting: vi.fn(),
  markFactoryItemHandled: vi.fn(),
  setFactoryPaused: vi.fn(),
  getFactoryAgentsSwitch: vi.fn(),
  saveFactoryReport: vi.fn(),
  downloadFactoryCsv: vi.fn(),
}));

vi.mock("@devthrottle/client-core/factory/factoryAgentsClient", () => client);

import { FactoryAgentPageView } from "./FactoryAgentPageView";
import { FactoryWaitingView } from "./FactoryWaitingView";
import { FactoryAreaGate } from "./FactoryAreaGate";
import { resetFactorySwitchCache } from "./useFactorySwitch";
import { AGENT_PAGE, WAITING } from "./fixtures";

describe("A factory agent's page (Screen 2)", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    client.getFactoryAgent.mockResolvedValue(AGENT_PAGE);
  });

  function renderPage() {
    return render(
      <MemoryRouter initialEntries={["/factory-agents/website-business/front-desk"]}>
        <Routes>
          <Route path="/factory-agents/:factory/:agent" element={<FactoryAgentPageView />} />
        </Routes>
      </MemoryRouter>,
    );
  }

  it("says the definition is not stored yet, in exactly the Gateway's sentence", async () => {
    renderPage();

    expect((await screen.findByTestId("fa-definition")).textContent).toBe("Definition not stored yet - #2177 mission 1");
    expect(client.getFactoryAgent).toHaveBeenCalledWith("website-business", "front-desk", expect.anything());
  });

  it("shows what wakes it, a paused trigger as paused, the last 7 days and the recent rows", async () => {
    renderPage();

    await screen.findByTestId("factory-agent-page");
    expect(screen.getByText("Trigger: New business mail, every 5 min (paused)")).toBeTruthy();
    expect(screen.getAllByText("PAUSED").every((el) => el.className.includes("fa-tone-paused"))).toBe(true);
    expect(screen.getByText("14 runs")).toBeTruthy();
    expect(screen.getByText("Asked what it costs after the free build - money question")).toBeTruthy();
  });

  it("offers Resume as the Gateway words it, and posts it for this factory agent", async () => {
    client.setFactoryPaused.mockResolvedValue(undefined);
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: "Resume factory agent" }));
    await waitFor(() => expect(client.setFactoryPaused).toHaveBeenCalledWith("resume", "website-business", "front-desk"));
  });

  it("links Ask the Fleet Manager to the Gateway's prefilled address, which sends nothing by itself", async () => {
    renderPage();

    const ask = await screen.findByTestId("fa-ask");
    expect(ask.textContent).toBe("Ask the Fleet Manager to change it");
    expect(ask.getAttribute("href")).toBe(AGENT_PAGE.askHref);
  });

  it("offers no edit control at all", async () => {
    renderPage();
    await screen.findByTestId("factory-agent-page");

    const labels = screen.getAllByRole("button").map((b) => b.textContent);
    expect(labels).toEqual(["Resume factory agent"]);
  });

  it("with no trigger, offers no Pause and says why", async () => {
    client.getFactoryAgent.mockResolvedValue({ ...AGENT_PAGE, pause: null, pauseUnavailableText: "Nothing to pause (fixture)." });
    renderPage();

    expect(await screen.findByText("Nothing to pause (fixture).")).toBeTruthy();
    expect(screen.queryAllByRole("button")).toEqual([]);
  });
});

describe("Waiting for you (Screen 4)", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    client.getFactoryWaiting.mockResolvedValue(WAITING);
  });

  function renderWaiting() {
    return render(
      <MemoryRouter initialEntries={["/factory-agents/waiting?factory=website-business"]}>
        <Routes>
          <Route path="/factory-agents/waiting" element={<FactoryWaitingView />} />
        </Routes>
      </MemoryRouter>,
    );
  }

  it("renders each item in the Gateway's words, with I have handled it only where the Gateway offers it", async () => {
    renderWaiting();

    const escalation = await screen.findByTestId("fa-waiting-11111111-1111-1111-1111-111111111111");
    expect(screen.getByText("2 items: 1 escalated, 1 asked.")).toBeTruthy();
    expect(within(escalation).getByText("ESCALATED").className).toContain("fa-tone-amber");
    expect(within(escalation).getByText("Front Desk v3, 22 Sep 05:30")).toBeTruthy();
    expect(within(escalation).getByRole("button", { name: "I have handled it" })).toBeTruthy();
    const asked = screen.getByTestId("fa-waiting-22222222-2222-2222-2222-222222222222");
    expect(within(asked).queryByRole("button")).toBeNull();
    expect(client.getFactoryWaiting).toHaveBeenCalledWith("website-business", expect.anything());
  });

  it("I have handled it posts the correction and reloads the list from the Gateway", async () => {
    client.markFactoryItemHandled.mockResolvedValue(undefined);
    renderWaiting();

    fireEvent.click(await screen.findByRole("button", { name: "I have handled it" }));
    await waitFor(() => expect(client.markFactoryItemHandled).toHaveBeenCalledWith("11111111-1111-1111-1111-111111111111"));
    await waitFor(() => expect(client.getFactoryWaiting).toHaveBeenCalledTimes(2));
  });

  it("says nothing waits when the Gateway says so", async () => {
    client.getFactoryWaiting.mockResolvedValue({ ...WAITING, items: [], summary: "Nothing is waiting for you.", emptyText: "Nothing waits (fixture)." });
    renderWaiting();

    expect(await screen.findByText("Nothing waits (fixture).")).toBeTruthy();
  });
});

describe("The Factory Agents switch gate", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    resetFactorySwitchCache();
  });

  function renderGate() {
    return render(
      <MemoryRouter>
        <FactoryAreaGate>
          <div>the factory area</div>
        </FactoryAreaGate>
      </MemoryRouter>,
    );
  }

  it("off: shows nothing of the area - the ordinary page not found", async () => {
    client.getFactoryAgentsSwitch.mockResolvedValue({ enabled: false });
    renderGate();

    expect(await screen.findByText("Page not found")).toBeTruthy();
    expect(screen.queryByText("the factory area")).toBeNull();
  });

  it("on: shows the area", async () => {
    client.getFactoryAgentsSwitch.mockResolvedValue({ enabled: true });
    renderGate();

    expect(await screen.findByText("the factory area")).toBeTruthy();
  });
});
