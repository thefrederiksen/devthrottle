import type { AgentChoice } from "../api/client";

// CHOOSING HOW A NEW SESSION IS CONNECTED (API mode mission #4070, phase 5) - the rules the shared Connection row and
// both New Session screens read. Which connections an agent has is the Gateway's ruling, carried on the agent as
// `connections`; nothing here decides which agents have API mode, or whether API mode is offered at all.

/** The connection a create sends nothing for: terminal, the default (absent means terminal on the wire). */
export const TERMINAL_CONNECTION = "terminal";

/** Whether the New Session screen draws a Connection row for this agent: only when the Gateway offers it more than one. */
export function offersConnectionChoice(agent: AgentChoice | null): boolean {
  return agent !== null && agent.connections.length > 1;
}

/**
 * The connection a create sends for this agent: the one the person chose when the agent offers it, else terminal. A
 * choice made for one agent is never carried to another that does not offer it - switching from Pi in API mode to an
 * agent without it starts that agent in its terminal, not refused.
 */
export function connectionToSend(agent: AgentChoice | null, chosen: string): string {
  if (agent === null) return TERMINAL_CONNECTION;
  return agent.connections.some((c) => c.mode === chosen) ? chosen : TERMINAL_CONNECTION;
}

/** The Gateway's words for the connection a create will send, or null when it is terminal (nothing to say). */
export function connectionSummary(agent: AgentChoice | null, chosen: string): string | null {
  const mode = connectionToSend(agent, chosen);
  if (mode === TERMINAL_CONNECTION || agent === null) return null;
  return agent.connections.find((c) => c.mode === mode)?.label ?? null;
}
