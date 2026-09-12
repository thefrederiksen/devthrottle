// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import {
  getSessionsEnvelope,
  REACHABILITY_OFFLINE,
  REACHABILITY_ONLINE,
  type SessionsEnvelope,
} from "@devthrottle/client-core/fleet/fleetClient";
import { emptyRetentionCache, mergeRosterRetention } from "@devthrottle/client-core/fleet/rosterRetention";
import { SessionRow } from "./Home";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function session(overrides: Partial<SessionDto> = {}): SessionDto {
  return {
    sessionId: "session-one",
    directorId: "director-one",
    machineName: "SOREN_NORTH",
    name: "testing pi",
    agent: "Pi",
    agentToolDisplay: "Pi",
    currentModel: "gpt-5.6-sol",
    modelDisplay: {
      kind: "reported",
      text: "gpt-5.6-sol",
      modelId: "gpt-5.6-sol",
      tooltip: "gpt-5.6-sol",
      isAbsent: false,
    },
    activityState: "Working",
    status: "Running",
    effectiveColor: "blue",
    effectiveColorHex: "#3b82f6",
    triageBucket: "active",
    stateLabel: "Working",
    ...overrides,
  } as SessionDto;
}

function mockFetch(body: unknown) {
  return vi.fn(async (): Promise<Response> => ({
    ok: true,
    status: 200,
    json: async () => body,
  }) as Response);
}

function renderRow(value: SessionDto) {
  return render(
    <MemoryRouter>
      <SessionRow session={value} />
    </MemoryRouter>,
  );
}

describe("mobile roster card agent tool", () => {
  it("carries the Gateway tool label through the roster read, retained state, and card", async () => {
    vi.stubGlobal("fetch", mockFetch({
      sessions: [session()],
      machineErrors: [],
      directors: [{ directorId: "director-one", state: REACHABILITY_ONLINE }],
      unreachableBanner: null,
    }));

    const envelope = await getSessionsEnvelope();
    const live = mergeRosterRetention(emptyRetentionCache(), envelope);
    const unavailable: SessionsEnvelope = {
      sessions: [],
      machineErrors: [],
      directors: [{ directorId: "director-one", state: REACHABILITY_OFFLINE }],
      unreachableBanner: null,
    };
    const retained = mergeRosterRetention(live.cache, unavailable);
    expect(retained.roster.sessions).toHaveLength(1);
    expect(retained.roster.sessions[0].agentToolDisplay).toBe("Pi");
    const { container } = renderRow(retained.roster.sessions[0]);

    const tool = screen.getByTitle("Agent tool");
    expect(tool.textContent).toBe("Pi");
    expect(container.textContent).not.toContain("gpt-5.6-sol");
  });

  it("renders the Gateway display spelling instead of the raw token or the model", () => {
    const { container } = renderRow(session({
      agent: "ClaudeCode",
      agentToolDisplay: "Claude Code",
      currentModel: "claude-fable-5",
    }));

    expect(screen.getByTitle("Agent tool").textContent).toBe("Claude Code");
    expect(container.textContent).not.toContain("ClaudeCode");
    expect(container.textContent).not.toContain("claude-fable-5");
  });

  it("keeps an absent Gateway display stamp visible without deriving from the raw token", () => {
    renderRow(session({ agent: "Pi", agentToolDisplay: undefined }));

    expect(screen.getByTitle("Agent tool").textContent).toBe("Agent tool not reported");
  });

  it("keeps the existing session name visible beside the added tool metadata", () => {
    renderRow(session());

    expect(screen.getByText("testing pi")).toBeTruthy();
  });
});
