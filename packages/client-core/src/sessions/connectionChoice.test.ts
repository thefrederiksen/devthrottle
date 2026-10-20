import { describe, expect, it } from "vitest";
import type { AgentChoice } from "../api/client";
import { connectionSummary, connectionToSend, defaultConnectionOf, offersConnectionChoice, TERMINAL_CONNECTION } from "./connectionChoice";

// The New Session connection rules (API mode mission #4070, phase 5). Which connections an agent has is the Gateway's
// answer on the agent; these only read it.

function agent(type: string, modes: Array<[string, string]>, defaultMode: string | null = "terminal"): AgentChoice {
  return {
    type, displayName: type, defaultModel: "", modelLabel: "",
    connections: modes.map(([mode, label]) => ({ mode, label, isDefault: mode === defaultMode })),
  };
}

const PI = agent("Pi", [["terminal", "Terminal"], ["api", "API mode"]]);
const CODEX = agent("Codex", [["terminal", "Terminal"]]);
// What the Gateway serves today for Pi on a Director of 2.25.0 or later: both offered, Terminal the default (the owner's
// decision, 20 October 2026). PI_API_DEFAULT is what it would serve once Pi is switched over to API mode by default.
const PI_API_DEFAULT = agent("Pi", [["terminal", "Terminal"], ["api", "API mode"]], "api");
// What an older Gateway serves: no choice is marked.
const PI_FROM_AN_OLDER_GATEWAY = agent("Pi", [["terminal", "Terminal"], ["api", "API mode"]], null);

describe("defaultConnectionOf", () => {
  it("is the choice the Gateway marks as the default", () => {
    expect(defaultConnectionOf(PI_API_DEFAULT)).toBe("api");
    expect(defaultConnectionOf(PI)).toBe("terminal");
  });

  it("is terminal when the Gateway marks none, or before an agent is chosen", () => {
    expect(defaultConnectionOf(PI_FROM_AN_OLDER_GATEWAY)).toBe(TERMINAL_CONNECTION);
    expect(defaultConnectionOf(null)).toBe(TERMINAL_CONNECTION);
  });
});

describe("offersConnectionChoice", () => {
  it("is true only for an agent the Gateway offers more than one connection", () => {
    expect(offersConnectionChoice(PI)).toBe(true);
    expect(offersConnectionChoice(CODEX)).toBe(false);
    expect(offersConnectionChoice(null)).toBe(false);
  });
});

describe("connectionToSend", () => {
  it("sends the chosen connection when the agent offers it", () => {
    expect(connectionToSend(PI, "api")).toBe("api");
  });

  it("sends terminal for an agent that does not offer the choice, so a choice never carries to another agent", () => {
    expect(connectionToSend(CODEX, "api")).toBe(TERMINAL_CONNECTION);
  });

  it("sends terminal before an agent is chosen", () => {
    expect(connectionToSend(null, "api")).toBe(TERMINAL_CONNECTION);
  });

  it("sends the agent's default before the person picks: Terminal for Pi today, API mode when the Gateway marks it", () => {
    expect(connectionToSend(PI_API_DEFAULT, null)).toBe("api");
    expect(connectionToSend(PI, null)).toBe("terminal");
    expect(connectionToSend(CODEX, null)).toBe("terminal");
  });

  it("keeps Terminal one click away: a person who picks Terminal for Pi gets Terminal", () => {
    expect(connectionToSend(PI_API_DEFAULT, "terminal")).toBe("terminal");
  });
});

describe("connectionSummary", () => {
  it("says the Gateway's words for API mode, and nothing for terminal", () => {
    expect(connectionSummary(PI, "api")).toBe("API mode");
    expect(connectionSummary(PI, "terminal")).toBeNull();
    expect(connectionSummary(CODEX, "api")).toBeNull();
    expect(connectionSummary(null, "api")).toBeNull();
  });
});
