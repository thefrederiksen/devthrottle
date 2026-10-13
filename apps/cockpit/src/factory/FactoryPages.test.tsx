// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

// The Waiting for you list (Screen 4) and the switch gate: each renders the Gateway's fold verbatim (rule 7), and the
// only controls are the ones the mandate names. A factory agent's own page (Screen 2) became the seat page
// (FactorySeatView.test.tsx).

const client = vi.hoisted(() => ({
  getFactoryWaiting: vi.fn(),
  markFactoryItemHandled: vi.fn(),
  setFactoryPaused: vi.fn(),
  getFactoryAgentsSwitch: vi.fn(),
  saveFactoryReport: vi.fn(),
  downloadFactoryCsv: vi.fn(),
}));

vi.mock("@devthrottle/client-core/factory/factoryAgentsClient", () => client);

// The real describeAndReport formats the sentence; only the report it makes is captured.
const reported = vi.hoisted(() => vi.fn());
vi.mock("@devthrottle/client-core/errors/reportClientError", async (importOriginal) => {
  const real = await importOriginal<typeof import("@devthrottle/client-core/errors/reportClientError")>();
  return {
    ...real,
    describeAndReport: (surface: string, action: string, err: unknown, context?: { sessionId?: string }) => {
      reported({ surface, action, err });
      return real.describeAndReport(surface, action, err, context);
    },
  };
});

import { FactoryWaitingView } from "./FactoryWaitingView";
import { FactoryAreaGate } from "./FactoryAreaGate";
import { resetFactorySwitchCache } from "./useFactorySwitch";
import { WAITING } from "./fixtures";

describe("Waiting for you (Screen 4)", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    client.getFactoryWaiting.mockResolvedValue(WAITING);
  });

  function renderWaiting() {
    return render(
      <MemoryRouter initialEntries={["/factories/waiting?factory=website-business"]}>
        <Routes>
          <Route path="/factories/waiting" element={<FactoryWaitingView />} />
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

  // Factories is always in the menu (owner, 8 Oct 2026), so off is no longer "Page not found": the page says so and how
  // to start, in the Gateway's own sentence.
  it("off: says Factories is off and how to start, in the Gateway's own words, and shows nothing of the area", async () => {
    client.getFactoryAgentsSwitch.mockResolvedValue({ enabled: false, howToStart: "Factories is off on this Gateway. To start, do the thing." });
    renderGate();

    const off = await screen.findByTestId("factories-off");
    expect(off.textContent).toContain("Factories is off on this Gateway. To start, do the thing.");
    expect(screen.queryByText("Page not found")).toBeNull();
    expect(screen.queryByText("the factory area")).toBeNull();
  });

  // The Gateway's contract is a sentence with every "off"; without one the page says so as an error, in the page -
  // never a blank screen (the Cockpit has no error boundary).
  it("off without the Gateway's sentence: says so as an error, in the page", async () => {
    client.getFactoryAgentsSwitch.mockResolvedValue({ enabled: false, howToStart: null });
    renderGate();

    expect((await screen.findByRole("alert")).textContent).toContain("did not say how to start it");
    expect(screen.queryByText("the factory area")).toBeNull();
    // A fault in the Gateway the user is looking at is reported like any other error, once.
    expect(reported).toHaveBeenCalledTimes(1);
    expect(reported.mock.calls[0][0]).toMatchObject({ surface: "cockpit-factory-switch", action: "read how to start Factories" });
  });

  it("the switch cannot be read: says so, and reports it once", async () => {
    client.getFactoryAgentsSwitch.mockRejectedValue(new Error("boom"));
    renderGate();

    expect((await screen.findByRole("alert")).textContent).toContain("could not ask the Gateway whether Factories is on");
    expect(reported).toHaveBeenCalledTimes(1);
    expect(reported.mock.calls[0][0]).toMatchObject({ surface: "cockpit-factory-switch", action: "ask the Gateway whether Factories is on" });
  });

  it("on: shows the area", async () => {
    client.getFactoryAgentsSwitch.mockResolvedValue({ enabled: true, howToStart: null });
    renderGate();

    expect(await screen.findByText("the factory area")).toBeTruthy();
  });
});
