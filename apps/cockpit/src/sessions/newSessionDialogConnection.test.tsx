// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent } from "@testing-library/react";

// The Cockpit New Session dialog's Connection row (API mode mission #4070, phase 5). Which agents have API mode is the
// Gateway's answer on each agent (`connections`), so the dialog is fed one agent offered both and one offered the
// terminal alone, and what is held down is the row, the default, the summary line and what reaches createSession.
//
// Only the Gateway calls are replaced; the dialog and the shared row are the real ones.
const createSessionMock = vi.fn();
vi.mock("@devthrottle/client-core/api/client", () => ({
  getDirectors: () =>
    Promise.resolve([
      {
        directorId: "north-1",
        machineName: "SOREN_NORTH",
        displayName: "North",
        version: "2.6.0",
        startedAt: "2026-09-19T08:00:00Z",
        lastSeen: "2026-09-20T08:00:00Z",
        controlEndpoint: "http://127.0.0.1:7801",
        sessions: 0,
      },
    ]),
  getKnownRepositories: () =>
    Promise.resolve([{ name: "Site", path: "/repositories/site", lastUsed: "2026-09-19T09:00:00Z", neverOpened: false }]),
  getRepos: () => Promise.resolve([]),
  getAgents: () =>
    Promise.resolve([
      {
        type: "Pi",
        displayName: "Pi",
        defaultModel: "",
        modelLabel: "GLM",
        connections: [
          { mode: "terminal", label: "Terminal", description: null },
          { mode: "api", label: "API mode", description: "Prompts are handed to Pi as messages, and each one is confirmed." },
        ],
      },
      {
        type: "Codex",
        displayName: "Codex",
        defaultModel: "",
        modelLabel: "GPT",
        connections: [{ mode: "terminal", label: "Terminal" }],
      },
    ]),
  createSession: (...args: unknown[]) => createSessionMock(...args),
  addRepo: () => Promise.resolve({ added: true, name: "", path: "" }),
  gatewayErrorMessage: (err: unknown) => String(err),
}));

import { NewSessionDialog, launchSummary } from "./NewSessionDialog";

async function openAndPickTheRepository() {
  render(<NewSessionDialog onClose={() => {}} onCreated={() => {}} />);
  fireEvent.click((await screen.findByText("/repositories/site")).closest("button") as HTMLElement);
  await screen.findByRole("button", { name: "Pi" });
}

describe("the Cockpit New Session dialog's Connection row", () => {
  beforeEach(() => {
    createSessionMock.mockResolvedValue({ sessionId: "new-session" });
    try {
      window.localStorage.clear();
    } catch {
      /* no storage in this environment is fine */
    }
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("offers Terminal and API mode for the agent the Gateway offers both, with Terminal chosen", async () => {
    await openAndPickTheRepository();

    expect(screen.getByRole("group", { name: "Connection" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Terminal" }).getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByText("Pi . GLM . skips permission prompts")).toBeTruthy();
    expect(screen.queryByTestId("connection-choice-description")).toBeNull();
  });

  it("starts a terminal session when nothing is changed", async () => {
    await openAndPickTheRepository();

    fireEvent.click(screen.getByRole("button", { name: "Create session" }));

    await waitFor(() => expect(createSessionMock).toHaveBeenCalledTimes(1));
    expect(createSessionMock.mock.calls[0][2].connectionMode).toBe("terminal");
  });

  it("starts the session in API mode when API mode is chosen, and says so before it starts", async () => {
    await openAndPickTheRepository();

    fireEvent.click(screen.getByRole("button", { name: "API mode" }));
    expect(screen.getByText("Pi . GLM . skips permission prompts . API mode")).toBeTruthy();
    expect(screen.getByTestId("connection-choice-description").textContent).toBe(
      "Prompts are handed to Pi as messages, and each one is confirmed.",
    );
    fireEvent.click(screen.getByRole("button", { name: "Create session" }));

    await waitFor(() => expect(createSessionMock).toHaveBeenCalledTimes(1));
    expect(createSessionMock.mock.calls[0][2].agent).toBe("Pi");
    expect(createSessionMock.mock.calls[0][2].connectionMode).toBe("api");
  });

  it("draws no row for an agent offered the terminal alone, and never carries API mode to it", async () => {
    await openAndPickTheRepository();
    fireEvent.click(screen.getByRole("button", { name: "API mode" }));

    fireEvent.click(screen.getByRole("button", { name: "Codex" }));

    expect(screen.queryByRole("group", { name: "Connection" })).toBeNull();
    expect(screen.getByText("Codex . GPT . skips permission prompts")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Create session" }));
    await waitFor(() => expect(createSessionMock).toHaveBeenCalledTimes(1));
    expect(createSessionMock.mock.calls[0][2].connectionMode).toBe("terminal");
  });

  it("is Terminal again on the next open: API mode is never remembered", async () => {
    await openAndPickTheRepository();
    fireEvent.click(screen.getByRole("button", { name: "API mode" }));
    fireEvent.click(screen.getByRole("button", { name: "Create session" }));
    await waitFor(() => expect(createSessionMock).toHaveBeenCalledTimes(1));
    cleanup();

    await openAndPickTheRepository();

    expect(screen.getByRole("button", { name: "Terminal" }).getAttribute("aria-pressed")).toBe("true");
  });
});

describe("launchSummary", () => {
  it("names a connection other than the terminal before the factory", () => {
    const pi = { type: "Pi", displayName: "Pi", defaultModel: "", modelLabel: "GLM", connections: [] };

    expect(launchSummary(pi, true, "site", "API mode")).toBe("Pi . GLM . skips permission prompts . API mode . in factory site");
    expect(launchSummary(pi, true, "", null)).toBe("Pi . GLM . skips permission prompts");
  });
});
