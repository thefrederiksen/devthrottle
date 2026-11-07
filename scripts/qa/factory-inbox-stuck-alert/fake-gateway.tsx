// THE "DEALT WITH IT" CONTROL ON BOTH SURFACES (issue 4746), for the proof screenshots. Only the network answers are
// replaced: the alerts list holds one stuck factory inbox, worded exactly as the Gateway words it, and the "Dealt with
// it" post takes it away as the Gateway does. The two pages (cockpit.tsx, phone.tsx) render the real shells over this.
// Built by build.mjs in this folder.

export const stuckId = "factory-inbox-stuck:citizenship";
const summary = "A message was not received in 15 minutes: a Director is running on the Boss's computer, but no Secretary started to receive it.";

export function installFakeGateway(): void {
  let alerts: unknown[] = [
    {
      id: stuckId, kind: "factory-inbox-stuck", needsAnswer: true, title: "The Factory inbox of Citizenship is stuck",
      summary, status: "Stuck", tone: "bad", createdAtUtc: "2026-11-07T15:15:00Z", updatedAtUtc: "2026-11-07T15:15:00Z",
      expiresAtUtc: null, seen: true,
      detail: { factory: "citizenship", title: "Citizenship",
        reasons: [{ cause: "no-secretary", sentence: summary, count: 1, sinceUtc: "2026-11-07T15:15:00Z" }] },
    },
  ];
  const posts: string[] = [];
  const sessions = Array.from({ length: 6 }, (_, i) => ({
    sessionId: `test-session-${i}`, name: `Citizenship - Seat ${i + 1}`, number: String(i + 101), createdAt: "2026-11-07T08:00:00Z",
    sortOrder: i, directorId: "test-director", machineName: "SOREN_NORTH", repoPath: "D:/test/repository",
    effectiveColor: "green", effectiveColorHex: "#22C55E", stateLabel: "Working", triageBucket: "active", agent: "Test", agentType: "Test",
  }));

  window.fetch = async (input, init) => {
    const url = typeof input === "string" ? input : input instanceof Request ? input.url : input.toString();
    const path = new URL(url, "http://test.invalid").pathname;
    const method = init?.method ?? "GET";
    let answer: unknown;
    if (path === "/gateway/alerts" && method === "GET") answer = { alerts, needsAnswerCount: alerts.length };
    else if (path === "/gateway/alerts/seen") answer = {};
    else if (path === "/gateway/factory/citizenship/inbox/dealt-with" && method === "POST") {
      posts.push(path);
      alerts = [];
      answer = { factory: "citizenship", marked: 1 };
    } else if (path === "/sessions") answer = { sessions, directors: [] };
    else if (path === "/directors") answer = [];
    else if (path === "/teams") answer = { teams: [], start: { where: "own-account" } };
    else if (path.endsWith("/licenses/alarms")) answer = { asOfUtc: "2026-11-07T15:30:00Z", count: 0, summary: "", linkText: "", alarms: [] };
    else if (path.includes("demo-mode")) answer = { enabled: false };
    else if (path.includes("factory-agents/switch")) answer = { enabled: true };
    else return new Response(JSON.stringify({ error: "Not part of this capture", message: "Not part of this capture" }), { status: 503, headers: { "Content-Type": "application/json" } });
    return new Response(JSON.stringify(answer), { headers: { "Content-Type": "application/json" } });
  };
  Object.assign(window, { dealtWithPosts: posts });
}

export function rootElement(): HTMLElement {
  const host = document.getElementById("root");
  if (!host) throw new Error("No root");
  return host;
}
