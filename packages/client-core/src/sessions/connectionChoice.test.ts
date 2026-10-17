import { describe, expect, it } from "vitest";
import type { AgentChoice } from "../api/client";
import { connectionSummary, connectionToSend, offersConnectionChoice, TERMINAL_CONNECTION } from "./connectionChoice";

// The New Session connection rules (API mode mission #4070, phase 5). Which connections an agent has is the Gateway's
// answer on the agent; these only read it.

function agent(type: string, modes: Array<[string, string]>): AgentChoice {
  return { type, displayName: type, defaultModel: "", modelLabel: "", connections: modes.map(([mode, label]) => ({ mode, label })) };
}

const PI = agent("Pi", [["terminal", "Terminal"], ["api", "API mode"]]);
const CODEX = agent("Codex", [["terminal", "Terminal"]]);

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
});

describe("connectionSummary", () => {
  it("says the Gateway's words for API mode, and nothing for terminal", () => {
    expect(connectionSummary(PI, "api")).toBe("API mode");
    expect(connectionSummary(PI, "terminal")).toBeNull();
    expect(connectionSummary(CODEX, "api")).toBeNull();
    expect(connectionSummary(null, "api")).toBeNull();
  });
});
