// The restart-request card (issue #2725), rendered against a fake Gateway.
//
// What these prove that a Gateway test cannot: the card SHOWS the Gateway's sentences verbatim, offers
// the two actions only when the Gateway says an accept would be honoured, and SENDS the accept to the
// right route only after the in-card confirmation. A card that paraphrased a refusal, or accepted on one
// tap, would leave every Gateway test green while the owner saw something else.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { RestartRequestCard, RestartRequestsPanel } from "./RestartRequestsPanel";
import { visibleRestartRequests, type DirectorRestartRequest } from "./restartRequests";

const HERE = dirname(fileURLToPath(import.meta.url));

function request(overrides: Partial<DirectorRestartRequest> = {}): DirectorRestartRequest {
  return {
    id: "req-1",
    machine: "SOREN_NORTH",
    directorId: "d-1",
    directorName: "DevThrottle_1",
    requestedBySessionId: "11111111-2222-3333-4444-555555555555",
    requestedBySessionName: "Rig Alpha - Architect (11111111)",
    reason: "the launcher has a staged update",
    requestedAtUtc: "2026-09-07T12:00:00Z",
    expiresAtUtc: "2026-09-07T12:30:00Z",
    state: "Pending",
    stateReason: "",
    progress: "",
    liveSessionCount: 7,
    liveSessionsSentence: "That Director holds 7 live sessions right now; each will be asked to write a handover and close, leaf-first.",
    capability: {
      verdict: "CanRestart",
      reason: "SOREN_NORTH can be restarted: its launcher (version 2.1.0) holds a live command stream and declares director/restart.",
      guardedRestart: "Available",
      guardedRestartReason: "the launcher on SOREN_NORTH (version 2.1.0) declares director/restart:only-if-empty, so it will refuse a restart while its Director still holds live sessions and say how many.",
    },
    title: "Restart the Director on SOREN_NORTH?",
    askedBySentence: "Rig Alpha - Architect (11111111) asks: the launcher has a staged update",
    acceptSentence: "Accept once, and nothing more is asked of you: the Director asks its 7 live sessions to write a handover and close, records them in a workspace, checks the machine again, asks its launcher for a restart only if it is empty, and reports here.",
    canAccept: true,
    ...overrides,
  };
}

let calls: { url: string; method: string }[] = [];

function fakeGateway(list: DirectorRestartRequest[], acceptStatus = 200, acceptBody: unknown = null) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? "GET";
      calls.push({ url, method });
      if (url === "/gateway/director-restart-requests") {
        return new Response(JSON.stringify({ requests: list }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      if (url.endsWith("/accept")) {
        return new Response(JSON.stringify(acceptBody ?? { ...list[0], state: "Accepted", canAccept: false }), {
          status: acceptStatus,
          headers: { "Content-Type": "application/json" },
        });
      }
      if (url.endsWith("/decline")) {
        return new Response(JSON.stringify({ ...list[0], state: "Declined", canAccept: false }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      throw new Error(`unexpected request: ${method} ${url}`);
    }),
  );
}

beforeEach(() => {
  calls = [];
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("RestartRequestCard", () => {
  it("renders the Gateway's sentences verbatim, including Phase 1's reason", () => {
    render(<RestartRequestCard request={request()} />);
    expect(screen.getByText("Restart the Director on SOREN_NORTH?")).toBeTruthy();
    expect(screen.getByText("Rig Alpha - Architect (11111111) asks: the launcher has a staged update")).toBeTruthy();
    expect(screen.getByText(/holds 7 live sessions right now/)).toBeTruthy();
    expect(screen.getByText(/declares director\/restart:only-if-empty/)).toBeTruthy();
    expect(screen.getByText("Pending")).toBeTruthy();
  });

  it("keeps the capability check's sentences and the launcher version inside a closed Details", () => {
    const { container } = render(<RestartRequestCard request={request({ capability: { ...request().capability!, launcherVersion: "2.1.0" } })} />);
    const details = container.querySelector("details.restart-request-details") as HTMLDetailsElement;
    expect(details).not.toBeNull();
    expect(details.open).toBe(false);
    expect(details.querySelector("summary")?.textContent).toBe("Details");
    expect(details.textContent).toContain("can be restarted");
    expect(details.textContent).toContain("director/restart:only-if-empty");
    expect(details.textContent).toContain("Launcher version: 2.1.0");
    // The main lines outside Details carry none of the diagnostics.
    const outside = Array.from(container.querySelectorAll("article > .restart-request-line")).map((p) => p.textContent).join(" ");
    expect(outside).not.toContain("can be restarted");
    expect(outside).not.toContain("only-if-empty");
  });

  it("renders the longest real strings - a 46-character version and a 300-character reason - in full", () => {
    const version = "2.24.0-preview.20261024.215301+sha.3e47021cb0a";
    expect(version).toHaveLength(46);
    const reason = "x".repeat(300);
    const { container } = render(
      <RestartRequestCard
        request={request({
          askedBySentence: `Rig Alpha - Architect (11111111) asks: ${reason}`,
          capability: { ...request().capability!, reason, guardedRestartReason: `the launcher on SOREN_NORTH (version ${version}) will refuse a restart`, launcherVersion: version },
        })}
      />,
    );
    const details = container.querySelector("details.restart-request-details") as HTMLDetailsElement;
    expect(details.open).toBe(false);
    expect(details.textContent).toContain(`Launcher version: ${version}`);
    expect(details.textContent).toContain(reason);
    expect(screen.getByText(`Rig Alpha - Architect (11111111) asks: ${reason}`)).toBeTruthy();
    // Every line is a wrapping line: the class the shells give overflow-wrap: anywhere and min-width: 0.
    for (const p of Array.from(container.querySelectorAll("p"))) expect(p.classList.contains("restart-request-line")).toBe(true);
  });

  it("gives every card line overflow-wrap: anywhere and min-width: 0 in both apps' styles", () => {
    for (const app of ["mobile", "cockpit"]) {
      const css = readFileSync(join(HERE, `../../../../apps/${app}/src/styles.css`), "utf-8");
      const rule = css.split(/\r?\n/).find((l) => l.includes(".restart-request-line") && l.includes("overflow-wrap: anywhere"));
      expect(rule, `${app} styles.css`).toBeTruthy();
      expect(rule).toContain("min-width: 0");
    }
  });

  it("offers Accept and Decline only while the Gateway says an accept would be honoured", () => {
    const { unmount } = render(<RestartRequestCard request={request()} />);
    expect(screen.getByText("Accept")).toBeTruthy();
    expect(screen.getByText("Decline")).toBeTruthy();
    unmount();

    render(<RestartRequestCard request={request({ canAccept: false, state: "Expired", stateReason: "nobody accepted within 30 minutes" })} />);
    expect(screen.queryByText("Accept")).toBeNull();
    expect(screen.queryByText("Decline")).toBeNull();
    expect(screen.getByText("nobody accepted within 30 minutes")).toBeTruthy();
  });

  // The accept (two steps), the refusal sentence and the decline are the shared action block's, tested with it in
  // alerts/actions/RestartRequestActions.test.tsx; this proves the card renders that block.
  it("renders the shared answer block, so Accept leads to the in-place confirmation", () => {
    render(<RestartRequestCard request={request()} />);
    fireEvent.click(screen.getByText("Accept"));
    expect(screen.getByText("Yes, restart it")).toBeTruthy();
  });

  it("shows the Director's progress line while the cycle runs and its reason when it stopped", () => {
    const { unmount } = render(<RestartRequestCard request={request({ state: "Accepted", canAccept: false, progress: "draining: every session is asked to write a handover and close, leaf-first" })} />);
    expect(screen.getByRole("status").textContent).toContain("draining");
    unmount();

    render(<RestartRequestCard request={request({ state: "Abandoned", canAccept: false, stateReason: "the drain stopped: seat e1d7291b never declared it finished", workspaceId: "soren-north-2026-09-07" })} />);
    expect(screen.getByText(/e1d7291b/)).toBeTruthy();
    expect(screen.getByText(/workspace soren-north-2026-09-07/)).toBeTruthy();
  });
});

describe("RestartRequestsPanel", () => {
  it("renders nothing when the Gateway holds no requests", async () => {
    fakeGateway([]);
    const { container } = render(<RestartRequestsPanel />);
    await waitFor(() => expect(calls.some((c) => c.url === "/gateway/director-restart-requests")).toBe(true));
    expect(container.querySelector(".restart-requests")).toBeNull();
  });

  it("renders the pending request the Gateway returns", async () => {
    fakeGateway([request()]);
    render(<RestartRequestsPanel />);
    await waitFor(() => expect(screen.getByText("Restart the Director on SOREN_NORTH?")).toBeTruthy());
  });

  it("does not render a request the owner already accepted", async () => {
    fakeGateway([
      request({ id: "accepted", state: "Accepted", canAccept: false, title: "Restart the Director on BUILD_BOX?", progress: "draining" }),
      request({ id: "pending", title: "Restart the Director on SOREN_NORTH?" }),
    ]);
    const { container } = render(<RestartRequestsPanel />);
    // The pending card proves the list arrived; only then is the accepted card's absence evidence.
    await waitFor(() => expect(screen.getByText("Restart the Director on SOREN_NORTH?")).toBeTruthy());
    expect(screen.queryByText("Restart the Director on BUILD_BOX?")).toBeNull();
    expect(container.querySelectorAll(".restart-request")).toHaveLength(1);
  });
});

describe("listRestartRequests", () => {
  it("treats a successful body without a requests array as an error, never as an empty list", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ items: [] }), { status: 200, headers: { "Content-Type": "application/json" } })));
    const { listRestartRequests } = await import("./restartRequests");
    await expect(listRestartRequests()).rejects.toThrow(/without a 'requests' array/);
  });
});

describe("visibleRestartRequests", () => {
  it("keeps only the requests still waiting for an answer - an accepted request is not a card", () => {
    const list = [
      request({ id: "open", state: "Pending" }),
      request({ id: "running", state: "Accepted", canAccept: false }),
      request({ id: "fresh", state: "Declined", canAccept: false, closedAtUtc: "2026-09-07T12:45:00Z" }),
      request({ id: "old", state: "Completed", canAccept: false, closedAtUtc: "2026-09-07T12:00:00Z" }),
      request({ id: "odd", state: "Abandoned", canAccept: false, closedAtUtc: "not a time" }),
    ];
    expect(visibleRestartRequests(list).map((r) => r.id)).toEqual(["open"]);
  });
});
