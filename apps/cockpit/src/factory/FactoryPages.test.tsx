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

  it("marks each item with the anchor the 08:00 factory email links to", async () => {
    renderWaiting();

    const escalation = await screen.findByTestId("fa-waiting-11111111-1111-1111-1111-111111111111");
    expect(escalation.id).toBe("item-11111111-1111-1111-1111-111111111111");
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

  it("on with the new fields (reason null): shows the area", async () => {
    client.getFactoryAgentsSwitch.mockResolvedValue({ enabled: true, reason: null, howToStart: null, title: null, subscribeUrl: null });
    renderGate();

    expect(await screen.findByText("the factory area")).toBeTruthy();
    expect(screen.queryByTestId("factories-upgrade")).toBeNull();
  });

  // The hosted Gateway says the account's plan does not include Factories: the page is the upgrade page, in the
  // Gateway's words, with the Gateway's pricing address.
  const PLAN_REQUIRED = {
    enabled: false,
    reason: "plan_required",
    howToStart: "A factory runs your agents on a schedule. Gateway sentence for the test.",
    title: "Gateway heading for the test",
    subscribeUrl: "https://example.test/pricing",
  };

  it("plan_required: shows the upgrade page with the Gateway's heading, sentence and pricing link", async () => {
    client.getFactoryAgentsSwitch.mockResolvedValue(PLAN_REQUIRED);
    renderGate();

    const page = await screen.findByTestId("factories-upgrade");
    expect(within(page).getByRole("heading", { level: 1 }).textContent).toBe("Gateway heading for the test");
    expect(screen.getByTestId("factories-upgrade-body").textContent).toBe(PLAN_REQUIRED.howToStart);
    const seePro = screen.getByRole("link", { name: "See Pro" });
    expect(seePro.getAttribute("href")).toBe("https://example.test/pricing");
    expect(seePro.getAttribute("target")).toBe("_blank");
    expect(seePro.getAttribute("rel")).toBe("noopener noreferrer");
    expect(screen.getByRole("button", { name: "Try again" })).toBeTruthy();
    expect(screen.getByTestId("factories-upgrade-hint").textContent).toBe(
      "Subscribed already? Press Try again. It can take a minute after subscribing.",
    );
    expect(screen.queryByTestId("factories-off")).toBeNull();
    expect(screen.queryByText("the factory area")).toBeNull();
  });

  it("plan_required: Try again forgets the answer and asks the Gateway again, showing the area once it is on", async () => {
    client.getFactoryAgentsSwitch
      .mockResolvedValueOnce(PLAN_REQUIRED)
      .mockResolvedValueOnce({ enabled: true, reason: null, howToStart: null, title: null, subscribeUrl: null });
    renderGate();

    fireEvent.click(await screen.findByRole("button", { name: "Try again" }));

    expect(await screen.findByText("the factory area")).toBeTruthy();
    expect(client.getFactoryAgentsSwitch).toHaveBeenCalledTimes(2);
    expect(screen.queryByTestId("factories-upgrade")).toBeNull();
  });

  it("plan_required: Try again while still without the plan shows the upgrade page again", async () => {
    client.getFactoryAgentsSwitch.mockResolvedValue(PLAN_REQUIRED);
    renderGate();

    fireEvent.click(await screen.findByRole("button", { name: "Try again" }));

    await waitFor(() => expect(client.getFactoryAgentsSwitch).toHaveBeenCalledTimes(2));
    expect(await screen.findByTestId("factories-upgrade")).toBeTruthy();
    expect(reported).not.toHaveBeenCalled();
  });

  it("plan_required without its heading or address: says so as an error, in the page, and reports it once", async () => {
    client.getFactoryAgentsSwitch.mockResolvedValue({ ...PLAN_REQUIRED, subscribeUrl: null });
    renderGate();

    expect((await screen.findByRole("alert")).textContent).toContain("did not send the page's heading or pricing address");
    expect(screen.queryByTestId("factories-upgrade")).toBeNull();
    expect(reported).toHaveBeenCalledTimes(1);
    expect(reported.mock.calls[0][0]).toMatchObject({ surface: "cockpit-factory-switch", action: "read the Factories upgrade page" });
  });

  // Every other off reason keeps today's page with the Gateway's sentence.
  it.each(["not_switched_on", "plan_unknown"])("%s: shows the how-to-start page, not the upgrade page", async (reason) => {
    client.getFactoryAgentsSwitch.mockResolvedValue({
      enabled: false,
      reason,
      howToStart: `Gateway sentence for ${reason}.`,
      title: null,
      subscribeUrl: null,
    });
    renderGate();

    const off = await screen.findByTestId("factories-off");
    expect(off.textContent).toContain(`Gateway sentence for ${reason}.`);
    expect(screen.queryByTestId("factories-upgrade")).toBeNull();
    expect(screen.queryByRole("link", { name: "See Pro" })).toBeNull();
  });

  // plan_unknown is the hosted Gateway's failed plan read ("try again shortly"): it is not remembered, so the next visit
  // to Factories asks the Gateway again - while a settled off answer is still asked only once per page load.
  it("plan_unknown is not remembered: the next visit asks the Gateway again and shows the area once it is on", async () => {
    client.getFactoryAgentsSwitch
      .mockResolvedValueOnce({ enabled: false, reason: "plan_unknown", howToStart: "Could not check the plan; try again shortly.", title: null, subscribeUrl: null })
      .mockResolvedValueOnce({ enabled: true, reason: null, howToStart: null, title: null, subscribeUrl: null });
    renderGate();
    expect((await screen.findByTestId("factories-off")).textContent).toContain("Could not check the plan; try again shortly.");
    cleanup();

    renderGate();

    expect(await screen.findByText("the factory area")).toBeTruthy();
    expect(client.getFactoryAgentsSwitch).toHaveBeenCalledTimes(2);
  });

  it("a settled off answer is remembered: the next visit does not ask the Gateway again", async () => {
    client.getFactoryAgentsSwitch.mockResolvedValue({ enabled: false, reason: "not_switched_on", howToStart: "Off here.", title: null, subscribeUrl: null });
    renderGate();
    await screen.findByTestId("factories-off");
    cleanup();

    renderGate();

    expect(await screen.findByTestId("factories-off")).toBeTruthy();
    expect(client.getFactoryAgentsSwitch).toHaveBeenCalledTimes(1);
  });

  // A Gateway too old to send a reason: exactly today's behaviour (the first "off" test above sends only enabled and
  // howToStart, and shows the how-to-start page with no upgrade page).
  it("no reason from an older Gateway: no upgrade page", async () => {
    client.getFactoryAgentsSwitch.mockResolvedValue({ enabled: false, howToStart: "Older Gateway sentence." });
    renderGate();

    expect((await screen.findByTestId("factories-off")).textContent).toContain("Older Gateway sentence.");
    expect(screen.queryByTestId("factories-upgrade")).toBeNull();
  });
});
