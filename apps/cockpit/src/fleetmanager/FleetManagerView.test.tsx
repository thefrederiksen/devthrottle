// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { FleetManagerPlacement } from "@devthrottle/client-core/settings/fleetManagerClient";
import type { FleetManagerPage } from "@devthrottle/client-core/fleetmanager/pageClient";
import { emptyPage, FM_SESSION, morningPage } from "./fixtures";

// The Fleet Manager page (step 6): the header and the not-running state are the setting's answer, the panel is the
// page's answer, and both are rendered exactly as the Gateway sent them.

const api = vi.hoisted(() => ({
  placement: vi.fn<() => Promise<FleetManagerPlacement>>(),
  start: vi.fn<() => Promise<FleetManagerPlacement>>(),
  page: vi.fn<() => Promise<FleetManagerPage>>(),
  sendPrompt: vi.fn(async () => undefined),
}));

vi.mock("@devthrottle/client-core/settings/fleetManagerClient", () => ({
  getFleetManagerPlacement: api.placement,
  startFleetManager: api.start,
}));
vi.mock("@devthrottle/client-core/fleetmanager/pageClient", () => ({
  getFleetManagerPage: api.page,
  answerFleetOutcome: vi.fn(async () => undefined),
}));
vi.mock("@devthrottle/client-core/api/client", () => ({ sendPrompt: api.sendPrompt }));
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  reportClientError: vi.fn(),
  describeAndReport: (_s: string, _a: string, err: unknown) => (err instanceof Error ? err.message : String(err)),
}));
vi.mock("@devthrottle/client-core/history/useSessionChat", () => ({
  useSessionChat: () => ({
    bubbles: [],
    emptyText: "Waiting for the conversation to start (fake).",
    staleNotice: null,
    loadFailed: false,
    loadError: null,
    filter: { showToolCalls: false, showToolResults: false, showThinking: false },
    setFilter: () => undefined,
  }),
}));
// The composer is the sessions' own and has its own tests; here it only has to be mounted for a running Fleet Manager.
vi.mock("../sessions/SessionComposer", () => ({
  SessionComposer: (p: { sessionId?: string; placeholder?: string; enterSends?: boolean }) => (
    <div data-testid="composer" data-session={p.sessionId} data-enter-sends={String(p.enterSends)}>
      {p.placeholder}
    </div>
  ),
}));

import { FleetManagerView } from "./FleetManagerView";

function action(label: string, offered: boolean, note: string | null = null) {
  return { offered, label, note, busyLabel: `${label} busy (fake)` };
}

function placement(state: "running" | "not-running" | "unreachable", thinking = false): FleetManagerPlacement {
  const running = state === "running";
  return {
    agent: "ClaudeCode",
    agentLabel: "Claude Code",
    machine: "WORKSTATION-A",
    isDefault: false,
    agentNote: "",
    agents: [],
    machineNote: "",
    machines: [],
    save: action("Save", true),
    generatedAtUtc: "2026-09-16T14:40:00Z",
    status: {
      state,
      sentence: running ? "Running now (fake)." : "The Fleet Manager is not running. Start it where the setting says (fake).",
      line: running ? (thinking ? "thinking, watching 2 sessions (fake)" : "idle, watching 2 sessions (fake)") : "not running (fake)",
      thinking,
      tone: running ? "ok" : state === "unreachable" ? "bad" : "idle",
      sessionId: FM_SESSION,
      watching: running ? 2 : 0,
      open: action("Open it", running),
      start: action("Start it", state === "not-running", state === "not-running" ? "Up to 90 seconds (fake)." : null),
      restart: action("Restart it", running),
    },
  };
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={["/fleet-manager"]}>
      <FleetManagerView />
    </MemoryRouter>,
  );
}

describe("FleetManagerView", () => {
  beforeEach(() => {
    cleanup();
    api.placement.mockReset();
    api.start.mockReset();
    api.page.mockReset();
    api.sendPrompt.mockClear();
  });

  it("shows where it runs and the Gateway's state line under the title", async () => {
    api.placement.mockResolvedValue(placement("running", true));
    api.page.mockResolvedValue(morningPage());
    renderPage();

    const sub = await screen.findByTestId("fmp-sub");
    await waitFor(() => expect(sub.textContent).toBe("Claude Code on WORKSTATION-A (change) - thinking, watching 2 sessions (fake)"));
    expect(within(sub).getByRole("link", { name: "(change)" }).getAttribute("href")).toBe("/settings?tab=fleetmanager");
    const composer = await screen.findByTestId("composer");
    expect(composer.getAttribute("data-session")).toBe(FM_SESSION);
    expect(composer.getAttribute("data-enter-sends")).toBe("true");
  });

  it("when it is not running, says so in the Gateway's words and offers to start it where the setting says", async () => {
    api.placement.mockResolvedValueOnce(placement("not-running")).mockResolvedValue(placement("running"));
    api.start.mockResolvedValue(placement("running"));
    api.page.mockResolvedValue(morningPage());
    renderPage();

    const bar = await screen.findByTestId("fmp-not-running");
    expect(bar.textContent).toContain("The Fleet Manager is not running. Start it where the setting says (fake).");
    expect(bar.textContent).toContain("Up to 90 seconds (fake).");
    expect(within(bar).getByRole("link", { name: "Move it in Settings" }).getAttribute("href")).toBe("/settings?tab=fleetmanager");
    expect(screen.queryByTestId("composer")).toBeNull();
    expect((screen.getByRole("button", { name: "What did I miss?" }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(within(bar).getByRole("button", { name: "Start it" }));
    expect(within(bar).getByRole("button", { name: "Start it busy (fake)" })).toBeTruthy();
    await waitFor(() => expect(api.start).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.queryByTestId("fmp-not-running")).toBeNull());
  });

  it("when its computer cannot be reached, offers no start, only the move in Settings", async () => {
    api.placement.mockResolvedValue(placement("unreachable"));
    api.page.mockResolvedValue(morningPage());
    renderPage();

    const bar = await screen.findByTestId("fmp-not-running");
    expect(within(bar).queryByRole("button")).toBeNull();
    expect(within(bar).getByRole("link", { name: "Move it in Settings" })).toBeTruthy();
    expect(api.start).not.toHaveBeenCalled();
  });

  it("the quick prompt sends its words to the Fleet Manager verbatim", async () => {
    api.placement.mockResolvedValue(placement("running"));
    api.page.mockResolvedValue(morningPage());
    renderPage();

    const quick = await screen.findByRole("button", { name: "What did I miss?" });
    await waitFor(() => expect((quick as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(quick);

    await waitFor(() => expect(api.sendPrompt).toHaveBeenCalledWith(FM_SESSION, "What did I miss?", true));
  });

  it("renders the panel's sections, items and the not-the-Fleet-Manager's count verbatim", async () => {
    api.placement.mockResolvedValue(placement("running"));
    api.page.mockResolvedValue(morningPage());
    renderPage();

    const waiting = await screen.findByTestId("fmp-sec-waiting");
    expect(waiting.textContent).toContain("Waiting on you (fake)");
    expect(waiting.textContent).toContain("Asks which check to keep (fake Wingman label)");
    expect(waiting.textContent).toContain("Ready - risk low - checks passed - Fix the flaky list test");
    expect(waiting.textContent).toContain("26m");
    expect(screen.getByTestId("fmp-sec-under-way").textContent).toContain("widgets-internal - 1h 29m");
    const landed = screen.getByTestId("fmp-sec-landed");
    expect(landed.textContent).toContain("Answered today (fake)");
    expect(landed.textContent).toContain("You said \"Merge: Session tree on the web\" at 01:52");
    expect(landed.textContent).toContain("which the Gateway does not record yet. (fake)");
    expect(screen.getByTestId("fmp-notmine").textContent).toBe(
      "26 sessions are not the Fleet Manager's. (fake) They still ask you directly. (fake)",
    );
    // The three cards are in the conversation.
    expect(screen.getByText("Ready for you (fake)")).toBeTruthy();
    expect(screen.getByText("Finding (fake)")).toBeTruthy();
    expect(screen.getByText("Decision - only you can make this (fake)")).toBeTruthy();
  });

  it("offers the walkthrough from Waiting on you in the Gateway's words, and not when nothing waits", async () => {
    api.placement.mockResolvedValue(placement("running"));
    api.page.mockResolvedValueOnce(morningPage());
    renderPage();

    const link = await screen.findByTestId("fmp-walkthrough");
    expect(link.textContent).toBe("Take me through them (fake)");
    expect(link.getAttribute("href")).toBe("/fleet-manager/walkthrough");

    cleanup();
    api.page.mockResolvedValue(emptyPage());
    renderPage();
    await screen.findByText("Nothing is waiting on you. (fake)");
    expect(screen.queryByTestId("fmp-walkthrough")).toBeNull();
  });

  it("an empty account shows the Gateway's empty sentences", async () => {
    api.placement.mockResolvedValue(placement("not-running"));
    api.page.mockResolvedValue(emptyPage());
    renderPage();

    expect(await screen.findByText("Nothing is waiting on you. (fake)")).toBeTruthy();
    expect(screen.getByText("No Fleet Manager is marked. (fake)")).toBeTruthy();
    expect(screen.getByText("No Ready card was answered today. (fake)")).toBeTruthy();
  });

  it("a failed panel read shows the Gateway's words, and the header still renders", async () => {
    api.placement.mockResolvedValue(placement("running"));
    api.page.mockRejectedValue(new Error("the Gateway could not read the records (fake)"));
    renderPage();

    const alerts = await screen.findAllByRole("alert");
    expect(alerts.some((a) => a.textContent === "the Gateway could not read the records (fake)")).toBe(true);
    await waitFor(() => expect(screen.getByTestId("fmp-sub").textContent).toContain("idle, watching 2 sessions (fake)"));
  });
});
