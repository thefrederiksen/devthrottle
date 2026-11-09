// Contributor email choice proof harness (Team implementation piece 4.4): mounts the REAL ContributorNoticeCard - the one
// card both the Cockpit and the phone render - against a fake Gateway.
//
// REAL: the shipping card and its client (contributorNoticeClient). SIMULATED: the Gateway. A fetch shim answers
// GET /teams/notifications with a card worded exactly as the Gateway words it (ContributorNoticeCard in
// src/CcDirector.Gateway/Api/ContributorNoticeEndpoints.cs) and applies each PUT to its state, so the card re-renders from
// the server's answer. The Gateway's own fold, storage and refusals are proven in C# (ContributorNoticeTests).
import { createRoot } from "react-dom/client";
import { ContributorNoticeCard } from "../../src/settings/ContributorNoticeCard";

declare global {
  interface Window {
    __puts: Array<{ mode: string; dailyHour: number }>;
  }
}

const options = [
  ["now", "Email me right away"],
  ["daily", "Once a day, in one email"],
  ["never", "Never - I will check myself"],
] as const;
const hourText = (h: number) => String(h).padStart(2, "0") + ":00";
const saved: Record<string, (h: number) => string> = {
  now: () => "Saved. You get an email each time something is sent to you.",
  daily: (h) => "Saved. You get one email a day at " + hourText(h) + ", only when something is waiting.",
  never: () => "Saved. You get no email; what is sent to you waits in your Inbox.",
};

let state = { mode: "now", dailyHour: 8 };
window.__puts = [];

function card(savedLine: string | null) {
  return {
    applies: true,
    title: "Email when something is sent to you",
    explanation:
      "When something is sent to you in a team - a report, a question, a factory, or an answer to what you wrote - DevThrottle can tell you by email.",
    mode: state.mode,
    options: options.map(([value, label]) => ({ value, label, selected: value === state.mode })),
    showHour: state.mode === "daily",
    hourLabel: "Send the daily email at",
    dailyHour: state.dailyHour,
    hours: Array.from({ length: 24 }, (_, h) => ({ value: h, label: hourText(h) })),
    timeZoneLine: "Times are in your own time zone, America/Toronto.",
    note: "The email only says that something is waiting, and in which team, with a link. What was sent stays on DevThrottle.",
    savedLine,
  };
}

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });

window.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
  const method = (init?.method ?? "GET").toUpperCase();
  if (url.endsWith("/teams/notifications") && method === "GET") return json(card(null));
  if (url.endsWith("/teams/notifications") && method === "PUT") {
    const body = JSON.parse(String(init?.body ?? "{}")) as { mode: string; dailyHour: number };
    window.__puts.push(body);
    state = { mode: body.mode, dailyHour: body.dailyHour };
    return json(card(saved[state.mode](state.dailyHour)));
  }
  return new Response(JSON.stringify({ error: "not part of this proof" }), { status: 404 });
}) as typeof window.fetch;

createRoot(document.getElementById("root")!).render(<ContributorNoticeCard />);
