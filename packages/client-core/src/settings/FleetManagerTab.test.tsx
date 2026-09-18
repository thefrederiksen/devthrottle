// The Fleet Manager tab (the Fleet Manager mission, step 5), rendered against a fake Gateway.
//
// What these prove that a Gateway test cannot: the tab shows the Gateway's own sentences as they were sent, a
// computer the Gateway says cannot be chosen cannot be chosen, restart and move ask before they close anything,
// the tab sends what the owner chose, and "Open it" appears only where the surface has a session route.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { FleetManagerTab } from "./FleetManagerTab";
import type { FleetManagerAction, FleetManagerPlacement } from "./fleetManagerClient";

const SESSION = "60000000-0000-4000-8000-000000000001";

// Sentences the Gateway writes. Deliberately NOT the ones the Gateway's fold writes today: if the tab composed any
// of these itself, it could not produce these words, so the tab passing proves it renders what it was sent.
const RUNNING = "Running now: Test Agent on WORKSTATION-A, since 07:02. Watching 5 sessions. (from the fake)";
const STATE_A = "On - Director running (fake)";
const STATE_C = "Not reachable - last seen yesterday 22:40. (fake)";

function action(label: string, offered: boolean, extra: Partial<FleetManagerAction> = {}): FleetManagerAction {
  return { offered, label, busyLabel: `${label} in progress (fake)...`, ...extra };
}

function placement(running: boolean): FleetManagerPlacement {
  return {
    agent: "ClaudeCode",
    agentLabel: "Claude Code",
    machine: "WORKSTATION-A",
    isDefault: false,
    defaultNote: null,
    agentNote: "Any agent installed on the chosen computer (fake).",
    agents: [
      { value: "ClaudeCode", displayName: "Claude Code" },
      { value: "Codex", displayName: "Codex" },
    ],
    machineNote: "If no Director is running there, the launcher starts one (fake).",
    machines: [
      { machine: "WORKSTATION-A", state: "running", stateLabel: STATE_A, detail: "- Claude Code installed", tone: "ok", selectable: true },
      { machine: "WORKSTATION-B", state: "launcher-will-start", stateLabel: "On - the launcher will start one (fake)", tone: "go", selectable: true },
      { machine: "WORKSTATION-C", state: "not-reachable", stateLabel: STATE_C, detail: "It cannot run there until it is back.", tone: "bad", selectable: false },
    ],
    status: {
      state: running ? "running" : "not-running",
      sentence: running ? RUNNING : "The Fleet Manager is not running (fake).",
      line: running ? "idle, watching 5 sessions (fake)" : "not running (fake)",
      thinking: false,
      tone: running ? "ok" : "idle",
      sessionId: running ? SESSION : null,
      watching: running ? 5 : 0,
      open: action("Open it", running),
      start: action("Start it", !running, { note: running ? null : "Up to 90 seconds (fake)." }),
      restart: action("Restart it", running, {
        confirmTitle: "Restart the Fleet Manager? (fake)",
        confirmMessage: "The old one closes after its turn (fake).",
      }),
      page: {
        changeLabel: "(change)",
        composerUsable: running,
        composerPlaceholder: "",
        composerHint: "",
        quickPromptsUsable: running,
        quickPromptBusyLabel: "",
        thinkingShown: false,
        notRunningBarShown: !running,
        settingsLabel: "",
      },
    },
    save: running
      ? action("Save and move it", true, {
          verb: "move",
          note: "Saving starts a new Fleet Manager in the new place (fake).",
          confirmTitle: "Move the Fleet Manager? (fake)",
          confirmMessage: "The one running now finishes its current turn and then closes (fake).",
        })
      : action("Save", true, { verb: "save", note: "Saving records the choice (fake)." }),
    generatedAtUtc: "2026-09-16T14:30:00Z",
  };
}

let calls: { url: string; method: string; body: unknown }[] = [];

function fakeGateway(running: boolean, overrides: Record<string, () => Response> = {}) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? "GET";
      const body = init?.body === undefined ? undefined : JSON.parse(String(init.body));
      calls.push({ url, method, body });
      const key = `${method} ${url}`;
      if (overrides[key]) return overrides[key]();
      if (url.startsWith("/gateway/fleet-manager/")) return json(placement(running));
      if (url === "/gateway/settings") return json({ turnVerdictJudgeEnabled: false, turnVerdictColourEnabled: false });
      throw new Error(`unexpected request: ${key}`);
    }),
  );
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function mount(sessionHref?: (id: string) => string) {
  return render(
    <MemoryRouter>
      <FleetManagerTab sessionHref={sessionHref} />
    </MemoryRouter>,
  );
}

const writes = () => calls.filter((c) => c.method !== "GET");

beforeEach(() => {
  calls = [];
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the Fleet Manager tab", () => {
  it("shows a loading line before the Gateway answers", () => {
    vi.stubGlobal("fetch", vi.fn(() => new Promise(() => {})));
    mount();
    expect(screen.getByText("Loading the Fleet Manager setting...")).toBeTruthy();
  });

  it("renders the Gateway's sentences as they were sent", async () => {
    fakeGateway(true);
    mount((id) => `/session/${id}`);

    expect(await screen.findByText(RUNNING)).toBeTruthy();
    expect(screen.getByText(STATE_A)).toBeTruthy();
    expect(screen.getByText(STATE_C)).toBeTruthy();
    expect(screen.getByText("Any agent installed on the chosen computer (fake).")).toBeTruthy();
    expect(screen.getByText("Saving starts a new Fleet Manager in the new place (fake).")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save and move it" })).toBeTruthy();
  });

  it("while a restart or a move is under way, shows the Gateway's sentence about what it waits for", async () => {
    const waiting = placement(true);
    waiting.status.replacement = "The Fleet Manager running now is waiting for you, so it is not closed (fake).";
    waiting.status.replacementTone = "bad";
    waiting.status.restart = action("Restart it", false, { note: "A restart or a move is already under way (fake)." });
    fakeGateway(true, { "GET /gateway/fleet-manager/placement": () => json(waiting) });
    mount();

    const line = await screen.findByText("The Fleet Manager running now is waiting for you, so it is not closed (fake).");
    expect(line.className).toContain("fm-tone-bad");
    expect(screen.queryByRole("button", { name: "Restart it" })).toBeNull();
  });

  it("says nothing about a replacement when none is under way", async () => {
    fakeGateway(true);
    mount();

    await screen.findByText(RUNNING);
    expect(document.querySelector(".fm-replacement")).toBeNull();
  });

  it("will not let an unreachable computer be chosen", async () => {
    fakeGateway(true);
    mount();

    const c = (await screen.findByRole("radio", { name: /WORKSTATION-C/ })) as HTMLInputElement;
    expect(c.disabled).toBe(true);
    expect((screen.getByRole("radio", { name: /WORKSTATION-B/ }) as HTMLInputElement).disabled).toBe(false);
    expect((screen.getByRole("radio", { name: /WORKSTATION-A/ }) as HTMLInputElement).checked).toBe(true);
  });

  it("links Open it to the surface's session route, and shows no link without one", async () => {
    fakeGateway(true);
    const { unmount } = mount((id) => `/session/${id}`);
    const link = (await screen.findByRole("link", { name: "Open it" })) as HTMLAnchorElement;
    expect(link.getAttribute("href")).toBe(`/session/${SESSION}`);
    unmount();

    mount();
    await screen.findByText(RUNNING);
    expect(screen.queryByRole("link", { name: "Open it" })).toBeNull();
    expect(screen.queryByText("Open it")).toBeNull();
  });

  it("asks before restarting, and sends nothing on cancel", async () => {
    fakeGateway(true);
    mount();

    fireEvent.click(await screen.findByRole("button", { name: "Restart it" }));
    const dialog = screen.getByRole("alertdialog");
    expect(dialog.textContent).toContain("Restart the Fleet Manager? (fake)");
    expect(dialog.textContent).toContain("The old one closes after its turn (fake).");
    expect(writes()).toEqual([]);

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(writes()).toEqual([]);
  });

  it("restarts only after the owner confirms, and shows the Gateway's busy line meanwhile", async () => {
    let release: (r: Response) => void = () => {};
    fakeGateway(true);
    // The restart answer is held back so the busy line can be seen while it is outstanding.
    const base = globalThis.fetch as unknown as (u: string, i?: RequestInit) => Promise<Response>;
    vi.stubGlobal(
      "fetch",
      vi.fn((url: string, init?: RequestInit) => {
        if (url === "/gateway/fleet-manager/restart") {
          calls.push({ url, method: init?.method ?? "GET", body: undefined });
          return new Promise<Response>((resolve) => {
            release = resolve;
          });
        }
        return base(url, init);
      }),
    );
    mount();

    fireEvent.click(await screen.findByRole("button", { name: "Restart it" }));
    const dialog = screen.getByRole("alertdialog");
    fireEvent.click(Array.from(dialog.querySelectorAll("button")).find((b) => b.textContent === "Restart it")!);

    expect(await screen.findByText("Restart it in progress (fake)...")).toBeTruthy();
    expect(writes()).toEqual([{ url: "/gateway/fleet-manager/restart", method: "POST", body: undefined }]);

    release(json(placement(true)));
    await waitFor(() => expect(screen.queryByText("Restart it in progress (fake)...")).toBeNull());
  });

  it("asks before moving, then sends the chosen agent and computer", async () => {
    fakeGateway(true);
    mount();

    fireEvent.change(await screen.findByLabelText("Agent"), { target: { value: "Codex" } });
    fireEvent.click(screen.getByRole("radio", { name: /WORKSTATION-B/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save and move it" }));

    expect(screen.getByRole("alertdialog").textContent).toContain("Move the Fleet Manager? (fake)");
    expect(writes()).toEqual([]);

    const dialog = screen.getByRole("alertdialog");
    fireEvent.click(Array.from(dialog.querySelectorAll("button")).find((b) => b.textContent === "Save and move it")!);

    await waitFor(() =>
      expect(writes()).toEqual([
        { url: "/gateway/fleet-manager/move", method: "POST", body: { agent: "Codex", machine: "WORKSTATION-B" } },
      ]),
    );
  });

  it("saves without asking when nothing is running", async () => {
    fakeGateway(false);
    mount();

    fireEvent.click(await screen.findByRole("radio", { name: /WORKSTATION-B/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(screen.queryByRole("alertdialog")).toBeNull();
    await waitFor(() =>
      expect(writes()).toEqual([
        { url: "/gateway/fleet-manager/placement", method: "PUT", body: { agent: "ClaudeCode", machine: "WORKSTATION-B" } },
      ]),
    );
  });

  it("keeps save disabled until the choice differs from what is saved", async () => {
    fakeGateway(false);
    mount();

    const save = (await screen.findByRole("button", { name: "Save" })) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
    fireEvent.click(screen.getByRole("radio", { name: /WORKSTATION-B/ }));
    expect(save.disabled).toBe(false);
  });

  it("starts from the bar and shows the Gateway's own refusal", async () => {
    fakeGateway(false, {
      "POST /gateway/fleet-manager/start": () =>
        json({ error: "WORKSTATION-A cannot be reached now, so the Fleet Manager was not started. (fake)" }, 409),
    });
    mount();

    expect(await screen.findByText("Up to 90 seconds (fake).")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Start it" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("WORKSTATION-A cannot be reached now, so the Fleet Manager was not started. (fake)");
    expect(writes()).toEqual([{ url: "/gateway/fleet-manager/start", method: "POST", body: undefined }]);
  });

  it("shows the load failure instead of a blank tab", async () => {
    fakeGateway(false, {
      "GET /gateway/fleet-manager/placement": () => json({ error: "no account is bound to this request" }, 403),
    });
    mount();

    const alert = await screen.findByText(/Could not load the Fleet Manager setting/);
    expect(alert.textContent).toContain("no account is bound to this request");
  });

  it("carries the Wingman's turn verdict switches", async () => {
    fakeGateway(false);
    mount();

    expect(await screen.findByRole("heading", { name: /Turn verdicts/ })).toBeTruthy();
  });
});
