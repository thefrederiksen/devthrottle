// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { FleetManagerPlacement } from "@devthrottle/client-core/settings/fleetManagerClient";
import type { FleetManagerPage } from "@devthrottle/client-core/fleetmanager/pageClient";
import { emptyPage, FM_SESSION, morningPage } from "./fixtures";
import { readFileSync } from "node:fs";
import { join } from "node:path";

// The Fleet Manager page (step 6): the header, the not-running state and the "Start fresh" button are the setting's
// answer, the outcome cards are the page's answer, and both are rendered exactly as the Gateway sent them. The right
// panel is hidden for now, so the conversation takes the full width.

const api = vi.hoisted(() => ({
  placement: vi.fn<() => Promise<FleetManagerPlacement>>(),
  start: vi.fn<() => Promise<FleetManagerPlacement>>(),
  restart: vi.fn<() => Promise<FleetManagerPlacement>>(),
  page: vi.fn<() => Promise<FleetManagerPage>>(),
  sendPrompt: vi.fn(async () => ({ delivering: false })),
}));

vi.mock("@devthrottle/client-core/settings/fleetManagerClient", () => ({
  getFleetManagerPlacement: api.placement,
  startFleetManager: api.start,
  restartFleetManager: api.restart,
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
    filter: { showToolCalls: false, showToolResults: false, showThinking: false, myPromptsOnly: false },
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

function startFresh(offered: boolean) {
  return {
    ...action("Start fresh (fake)", offered),
    confirmTitle: offered ? "Start a fresh one? (fake)" : null,
    confirmMessage: offered ? "A brand new one starts and the old one closes (fake)." : null,
  };
}

function placement(state: "running" | "not-running" | "unreachable", thinking = false): FleetManagerPlacement {
  const running = state === "running";
  // What the Gateway decides for the page in each state (fake words, so a test proves they are rendered as sent).
  const page = {
    where: "Claude Code on WORKSTATION-A",
    changeLabel: "(change)",
    composerUsable: running,
    composerPlaceholder: "Tell the Fleet Manager (fake)...",
    composerOffText: running ? null : "not running (fake off text)",
    composerHint: "Enter sends (fake hint).",
    quickPromptsUsable: running,
    quickPromptBusyLabel: "Sending (fake)...",
    thinkingShown: running && thinking,
    notRunningBarShown: !running,
    settingsLabel: "Move it in Settings",
    startFresh: startFresh(running),
  };
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
      page,
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
    api.restart.mockReset();
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

  it("renders the Gateway's page controls and decides nothing from the state itself", async () => {
    // A running state whose controls say the composer and quick prompt are not usable and the bar shows: the page
    // follows the controls, never its own reading of "running".
    const p = placement("running", true);
    p.status.page = {
      ...p.status.page,
      where: null,
      composerUsable: false,
      composerOffText: "held (fake)",
      quickPromptsUsable: false,
      thinkingShown: false,
      notRunningBarShown: true,
      settingsLabel: "Change it (fake)",
    };
    api.placement.mockResolvedValue(p);
    api.page.mockResolvedValue(morningPage());
    renderPage();

    const bar = await screen.findByTestId("fmp-not-running");
    expect(within(bar).getByRole("link", { name: "Change it (fake)" })).toBeTruthy();
    expect(screen.queryByTestId("composer")).toBeNull();
    expect(screen.getByText("held (fake)")).toBeTruthy();
    expect(screen.getByText("Enter sends (fake hint).")).toBeTruthy();
    expect((screen.getByRole("button", { name: "What did I miss?" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByText("thinking, watching 2 sessions (fake)", { selector: ".fmp-thinking" })).toBeNull();
    expect(screen.getByTestId("fmp-sub").textContent).toBe("(change) - thinking, watching 2 sessions (fake)");
  });

  it("uses the Gateway's placeholder for the composer when it is usable", async () => {
    api.placement.mockResolvedValue(placement("running"));
    api.page.mockResolvedValue(morningPage());
    renderPage();

    expect((await screen.findByTestId("composer")).textContent).toBe("Tell the Fleet Manager (fake)...");
    expect(screen.queryByTestId("fmp-not-running")).toBeNull();
  });

  it("while a restart or a move is under way, shows the Gateway's sentence", async () => {
    const p = placement("running");
    p.status.replacement = "The Fleet Manager running now is waiting for you (fake).";
    p.status.replacementTone = "bad";
    api.placement.mockResolvedValue(p);
    api.page.mockResolvedValue(morningPage());
    renderPage();

    const bar = await screen.findByTestId("fmp-replacement");
    expect(bar.textContent).toBe("The Fleet Manager running now is waiting for you (fake).");
    expect(bar.className).toContain("fmp-bar-bad");
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

    // Through sendTypedPrompt (voice delivery phase 5), which passes no spoken id and no spans.
    await waitFor(() =>
      expect(api.sendPrompt).toHaveBeenCalledWith(FM_SESSION, "What did I miss?", true, undefined, undefined, undefined),
    );
  });

  it("does not render the right panel: the conversation is the only thing in the body", async () => {
    api.placement.mockResolvedValue(placement("running"));
    api.page.mockResolvedValue(morningPage());
    const { container } = renderPage();

    // The cards still come from the page's answer and still show in the conversation.
    expect(await screen.findByText("Ready for you (fake)")).toBeTruthy();
    expect(screen.getByText("Finding (fake)")).toBeTruthy();
    expect(screen.getByText("Decision - only you can make this (fake)")).toBeTruthy();
    expect(container.querySelector(".fmp-side")).toBeNull();
    expect(screen.queryByTestId("fmp-sec-waiting")).toBeNull();
    expect(screen.queryByTestId("fmp-notmine")).toBeNull();
    expect(screen.queryByTestId("fmp-walkthrough")).toBeNull();
    expect(screen.queryByText("Hand sessions to the Fleet Manager... (fake)")).toBeNull();
    const body = container.querySelector(".fmp-body");
    expect(body?.children).toHaveLength(1);
    expect(body?.firstElementChild?.className).toBe("fmp-convo");
  });

  it("gives the conversation the full width: the body reserves no second column", () => {
    // Vitest hands a CSS import to the test as empty, so the stylesheet is read from disk.
    const css = readFileSync(join(__dirname, "fleetmanager.css"), "utf8");
    const body = /\.fmp-body\s*\{([^}]*)\}/.exec(css)?.[1] ?? "";
    expect(body).toContain("grid-template-columns: minmax(0, 1fr);");
    const convo = /\.fmp-convo\s*\{([^}]*)\}/.exec(css)?.[1] ?? "";
    expect(convo).not.toContain("border-right");
  });

  it("an empty account shows the Gateway's no-conversation sentence", async () => {
    const none = placement("not-running");
    none.status.sessionId = null;
    api.placement.mockResolvedValue(none);
    api.page.mockResolvedValue(emptyPage());
    renderPage();

    expect(await screen.findByText("There is no Fleet Manager conversation yet. (fake)")).toBeTruthy();
  });

  it("offers Start fresh in the header in the Gateway's words, and asks before doing anything", async () => {
    api.placement.mockResolvedValue(placement("running"));
    api.page.mockResolvedValue(morningPage());
    renderPage();

    const button = await screen.findByRole("button", { name: "Start fresh (fake)" });
    expect(button.closest(".fmp-head-actions")).not.toBeNull();
    expect(screen.queryByRole("alertdialog")).toBeNull();

    fireEvent.click(button);
    const dialog = screen.getByRole("alertdialog", { name: "Start a fresh one? (fake)" });
    expect(dialog.textContent).toContain("A brand new one starts and the old one closes (fake).");
    expect(api.restart).not.toHaveBeenCalled();
  });

  it("Start fresh: cancel closes the question and restarts nothing", async () => {
    api.placement.mockResolvedValue(placement("running"));
    api.page.mockResolvedValue(morningPage());
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: "Start fresh (fake)" }));
    fireEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(api.restart).not.toHaveBeenCalled();
  });

  it("Start fresh: confirming calls the Gateway's restart once, shows its busy line, and shows the answer", async () => {
    api.placement.mockResolvedValue(placement("running"));
    api.page.mockResolvedValue(morningPage());
    let finish: (p: FleetManagerPlacement) => void = () => undefined;
    api.restart.mockReturnValue(
      new Promise<FleetManagerPlacement>((resolve) => {
        finish = resolve;
      }),
    );
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: "Start fresh (fake)" }));
    const dialog = screen.getByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Start fresh (fake)" }));

    await waitFor(() => expect(within(dialog).getByRole("button", { name: "Start fresh (fake) busy (fake)" })).toBeTruthy());
    expect(api.restart).toHaveBeenCalledTimes(1);

    const swapping = placement("running");
    swapping.status.replacement = "A new Fleet Manager has started (fake).";
    swapping.status.replacementTone = "idle";
    swapping.status.page.startFresh = startFresh(false);
    finish(swapping);

    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull());
    expect(screen.getByTestId("fmp-replacement").textContent).toBe("A new Fleet Manager has started (fake).");
    expect(screen.queryByRole("button", { name: "Start fresh (fake)" })).toBeNull();
    expect(api.restart).toHaveBeenCalledTimes(1);
  });

  it("Start fresh: a failed restart keeps the question open with the Gateway's words", async () => {
    api.placement.mockResolvedValue(placement("running"));
    api.page.mockResolvedValue(morningPage());
    api.restart.mockRejectedValue(new Error("That computer cannot be reached (fake)."));
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: "Start fresh (fake)" }));
    const dialog = screen.getByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Start fresh (fake)" }));

    await waitFor(() => expect(dialog.textContent).toContain("That computer cannot be reached (fake)."));
    expect(screen.getByRole("alertdialog")).toBe(dialog);
    expect(api.restart).toHaveBeenCalledTimes(1);
  });

  it("Start fresh: when the Gateway withdraws the offer while the question is open, the question closes and says why", async () => {
    const withdrawn = placement("running");
    withdrawn.status.page.startFresh = { ...startFresh(false), note: "A restart is already under way (fake)." };
    api.placement.mockResolvedValueOnce(placement("running")).mockResolvedValue(withdrawn);
    api.page.mockResolvedValue(morningPage());
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      renderPage();
      fireEvent.click(await screen.findByRole("button", { name: "Start fresh (fake)" }));
      expect(screen.getByRole("alertdialog", { name: "Start a fresh one? (fake)" })).toBeTruthy();

      await vi.advanceTimersByTimeAsync(9000);

      await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull());
      expect(screen.getByTestId("fmp-start-fresh-note").textContent).toBe("A restart is already under way (fake).");
      expect(screen.queryByRole("button", { name: "Start fresh (fake)" })).toBeNull();
      expect(api.restart).not.toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  });

  it("offers no Start fresh when the Gateway does not", async () => {
    api.placement.mockResolvedValue(placement("not-running"));
    api.page.mockResolvedValue(morningPage());
    renderPage();

    await screen.findByTestId("fmp-not-running");
    expect(screen.queryByRole("button", { name: "Start fresh (fake)" })).toBeNull();
  });

  it("a failed page read shows the Gateway's words, and the header still renders", async () => {
    api.placement.mockResolvedValue(placement("running"));
    api.page.mockRejectedValue(new Error("the Gateway could not read the records (fake)"));
    renderPage();

    const alerts = await screen.findAllByRole("alert");
    expect(alerts.some((a) => a.textContent === "the Gateway could not read the records (fake)")).toBe(true);
    await waitFor(() => expect(screen.getByTestId("fmp-sub").textContent).toContain("idle, watching 2 sessions (fake)"));
  });
});
