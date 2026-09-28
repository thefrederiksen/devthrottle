// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent } from "@testing-library/react";

// The Cockpit New Session dialog's factory field (Factory Memory mission, phase 3b). It is the only way a person
// starts a session into a factory by hand, so what is held down is exactly what reaches createSession: nothing
// when the field is left empty (it is empty on every open, never remembered), and the typed factory when it is
// filled. The summary line says the factory before the session is started.
//
// Only the Gateway calls are replaced; the dialog is the real one.
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
    Promise.resolve([{ type: "ClaudeCode", displayName: "Claude Code", defaultModel: "", modelLabel: "Opus" }]),
  createSession: (...args: unknown[]) => createSessionMock(...args),
  addRepo: () => Promise.resolve({ added: true, name: "", path: "" }),
  gatewayErrorMessage: (err: unknown) => String(err),
}));

import { NewSessionDialog } from "./NewSessionDialog";

async function openAndPickTheRepository() {
  render(<NewSessionDialog onClose={() => {}} onCreated={() => {}} />);
  fireEvent.click((await screen.findByText("/repositories/site")).closest("button") as HTMLElement);
  await screen.findByRole("button", { name: "Claude Code" });
}

function factoryBox(): HTMLInputElement {
  return screen.getByLabelText("Factory (optional)") as HTMLInputElement;
}

describe("the Cockpit New Session dialog's factory field", () => {
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

  it("is empty when the dialog opens, and a session started with it empty names no factory", async () => {
    await openAndPickTheRepository();

    expect(factoryBox().value).toBe("");
    expect(screen.getByText("Claude Code . Opus . skips permission prompts")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Create session" }));

    await waitFor(() => expect(createSessionMock).toHaveBeenCalledTimes(1));
    expect(createSessionMock.mock.calls[0][2].factory).toBe("");
  });

  it("starts the session into the factory typed, and says so before it is started", async () => {
    await openAndPickTheRepository();

    fireEvent.change(factoryBox(), { target: { value: "website-factory" } });
    expect(screen.getByText("Claude Code . Opus . skips permission prompts . in factory website-factory")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Create session" }));

    await waitFor(() => expect(createSessionMock).toHaveBeenCalledTimes(1));
    expect(createSessionMock.mock.calls[0][0]).toBe("north-1");
    expect(createSessionMock.mock.calls[0][1]).toBe("/repositories/site");
    expect(createSessionMock.mock.calls[0][2].factory).toBe("website-factory");
  });

  it("does not remember the factory: the next open starts empty again", async () => {
    await openAndPickTheRepository();
    fireEvent.change(factoryBox(), { target: { value: "website-factory" } });
    fireEvent.click(screen.getByRole("button", { name: "Create session" }));
    await waitFor(() => expect(createSessionMock).toHaveBeenCalledTimes(1));
    cleanup();

    await openAndPickTheRepository();

    expect(factoryBox().value).toBe("");
  });
});
