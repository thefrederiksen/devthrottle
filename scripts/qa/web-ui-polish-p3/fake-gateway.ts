// WEB UI POLISH PHASE 3 (small consistency fixes on the phone), for the proof screenshots. Only the network answers are
// replaced, each shaped as the Gateway sends it; phone.tsx renders the phone's real route table over it. Anything not
// part of this capture answers 503, which the pages show as their ordinary error state.

const now = Date.now();
const iso = (minutes: number) => new Date(now + minutes * 60_000).toISOString();

export const sessionId = "5e551011-0000-4000-8000-000000000001";
export const reportId = "a24e87a1-fd9e-4f8b-87e6-04013cf1e75f";

const session = {
  sessionId,
  name: "Web UI Polish - Developer - phase 3 phone consistency",
  number: "139",
  createdAt: iso(-60),
  sortOrder: 0,
  directorId: "test-director",
  machineName: "SOREN_NORTH",
  repoPath: "D:/test/repository",
  effectiveColor: "blue",
  effectiveColorHex: "#3B82F6",
  stateLabel: "Working",
  triageBucket: "active",
  agent: "ClaudeCode",
  agentType: "ClaudeCode",
  status: "Running",
};

export function installFakeGateway(): void {
  window.fetch = async (input, init) => {
    const url = typeof input === "string" ? input : input instanceof Request ? input.url : input.toString();
    const path = new URL(url, "http://test.invalid").pathname;
    const method = init?.method ?? "GET";
    let answer: unknown;
    if (path === "/healthz") answer = { status: "ok", version: "2.40.0+6f25f3cc1abc", serverTime: iso(0) };
    else if (path === "/fleet/counts") answer = { directors: 3, sessions: 17 };
    else if (path === "/sessions") answer = { sessions: [session], directors: [] };
    else if (path === `/sessions/${sessionId}`) answer = session;
    else if (path === "/directors") answer = [];
    else if (path === "/machines") answer = [];
    else if (path === "/teams") answer = { teams: [], start: { where: "own-account" } };
    else if (path === "/gateway/alerts" && method === "GET") answer = { alerts: [], needsAnswerCount: 0 };
    else if (path.endsWith("/licenses/alarms")) answer = { asOfUtc: iso(0), count: 0, summary: "", linkText: "", alarms: [] };
    else if (path === "/gateway/snooze-presets") answer = { presets: [15, 60, 240, 480], defaultMinutes: 60, maximum: 5 };
    else
      return new Response(JSON.stringify({ error: "Not part of this capture", message: "Not part of this capture" }), {
        status: 503,
        headers: { "Content-Type": "application/json" },
      });
    return new Response(JSON.stringify(answer), { headers: { "Content-Type": "application/json" } });
  };
}
