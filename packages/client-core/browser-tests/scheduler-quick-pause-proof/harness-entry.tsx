// Scheduler quick pause proof harness (issue #3910): mounts the REAL Cockpit Schedule page, Factories screen and a
// factory's Seats tab against a fake Gateway.
//
// What is REAL: the shipping ScheduleView, ScheduleEditor, FactoriesView, FactoryView and SchedulerPause components,
// the shipping client-core calls they make, and the Cockpit's own stylesheets - the whole path a tap takes in the
// browser.
//
// What is SIMULATED: the Gateway. A fetch shim keeps the pause-everything switch and two schedules in memory, answers
// the routes the pages read, applies each write the way the Gateway does, and records every request so the driver can
// assert what a tap actually sent. Its words carry "(fake)" so a screenshot shows the page rendered the Gateway's
// words rather than composing its own.
//
// The Gateway's own behaviour - nothing fires while paused, no schedule is written, the switch survives a restart -
// is proven separately in C#: CronPauseEverythingTests and CronPauseEverythingHostTests.
import "../../../../apps/cockpit/src/styles.css";
import "../../../../apps/cockpit/src/components/components.css";
// The Schedule page's stylesheet is loaded by the Cockpit's entry point (main.tsx), not by the page itself.
import "../../../../apps/cockpit/src/schedule/schedule.css";
import { createRoot } from "react-dom/client";
import { HashRouter, Navigate, Route, Routes } from "react-router-dom";
import { ScheduleView } from "../../../../apps/cockpit/src/schedule/ScheduleView";
import { FactoriesView } from "../../../../apps/cockpit/src/factory/FactoriesView";
import { FactoryView } from "../../../../apps/cockpit/src/factory/FactoryView";
import { FACTORY_LIST, FACTORY_PAGE, FACTORY_SEATS } from "../../../../apps/cockpit/src/factory/fixtures";

interface FakeJob {
  id: string;
  name: string;
  enabled: boolean;
  cronExpression: string;
  factory: string | null;
  seat: string | null;
}

interface FakeState {
  pausedSince: string | null;
  jobs: FakeJob[];
}

declare global {
  interface Window {
    __gateway: FakeState;
    __requests: Array<{ method: string; path: string; body: unknown }>;
  }
}

window.__requests = [];
window.__gateway = {
  pausedSince: null,
  jobs: [
    { id: "cj_boss", name: "WarmForward Factory - Boss", enabled: true, cronExpression: "0 9 * * *", factory: "warmforward", seat: "nora-hale" },
    { id: "cj_own", name: "Morning issue sweep", enabled: true, cronExpression: "0 7 * * *", factory: null, seat: null },
    { id: "cj_tidy", name: "Evening tidy", enabled: false, cronExpression: "0 21 * * *", factory: null, seat: null },
  ],
};

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function pauseDto() {
  const paused = window.__gateway.pausedSince !== null;
  return {
    paused,
    sinceUtc: window.__gateway.pausedSince,
    banner: paused ? "Everything is paused. No schedule will start until you resume. Each schedule keeps its own setting (fake)." : null,
    actionLabel: paused ? "Resume everything" : "Pause everything",
    actionTitle: paused ? "Let schedules start again, each exactly as it was (fake)." : "Stop every schedule from starting (fake).",
  };
}

function jobDto(j: FakeJob) {
  return {
    id: j.id,
    name: j.name,
    enabled: j.enabled,
    scheduleKind: "recurring",
    cronExpression: j.cronExpression,
    runAt: null,
    timeZoneId: "America/New_York",
    target: { machine: "SOREN_NORTH" },
    action: { repoPath: "D:\\ReposFred\\repo", seed: "/help", workListName: null },
    preventOverlap: true,
    notifyOn: "none",
    notifyWebhookUrl: null,
    nextRunUtc: new Date(Date.now() + 3 * 60 * 60 * 1000).toISOString(),
    lastFiredUtc: null,
    lastStatus: null,
    lifecycle: j.enabled ? "active" : "paused",
    stateLabel: j.enabled ? "Active" : "Paused",
    toggleLabel: j.enabled ? "Pause" : "Resume",
    factory: j.factory,
    seat: j.seat,
    factoryTitle: j.factory === null ? null : "WarmForward",
    factoryHref: j.factory === null ? null : `/factories/${j.factory}/seats`,
    shortName: j.factory === null ? j.name : "Boss",
    runRecord: { verdict: "none", text: "no runs yet", runs: 0 },
  };
}

// The seats view, folded from the fake schedule the way FactoriesScreenFold does: the words say (paused) and the
// button says what a tap does.
function seatsView() {
  const boss = window.__gateway.jobs.find((j) => j.id === "cj_boss")!;
  const when = `Daily 09:00 (America/New_York)${boss.enabled ? "" : " (paused)"}`;
  return {
    ...FACTORY_SEATS,
    rows: FACTORY_SEATS.rows.map((row) =>
      row.seatId !== "nora-hale"
        ? row
        : {
            ...row,
            whenText: when,
            schedules: [{ jobId: boss.id, whenText: when, editLabel: "Edit schedule", paused: !boss.enabled, toggleLabel: boss.enabled ? "Pause" : "Resume" }],
          },
    ),
  };
}

window.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const raw = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
  const path = new URL(raw, window.location.origin).pathname;
  const method = (init?.method ?? "GET").toUpperCase();
  const body = typeof init?.body === "string" ? JSON.parse(init.body) : null;
  window.__requests.push({ method, path, body });
  const state = window.__gateway;

  if (path === "/cron/pause") {
    if (method === "PUT") state.pausedSince = body.paused ? (state.pausedSince ?? new Date().toISOString()) : null;
    return json(pauseDto());
  }
  if (path === "/cron/jobs" && method === "GET") return json({ jobs: state.jobs.map(jobDto) });
  if (path === "/cron/load") return json({ generatedUtc: new Date().toISOString(), capacity: 6, machines: [] });
  if (path === "/cron/seat-choices")
    return json({
      noneLabel: "No factory (Personal)",
      factories: [{ factory: "warmforward", title: "WarmForward", seats: [{ id: "nora-hale", label: "Boss (nora-hale)" }] }],
    });
  const jobMatch = /^\/cron\/jobs\/([^/]+)(\/runs)?$/.exec(path);
  if (jobMatch !== null) {
    const stored = state.jobs.find((j) => j.id === jobMatch[1]);
    if (stored === undefined) return json({ error: "no such cron job" }, 404);
    if (jobMatch[2] !== undefined) return json({ jobId: stored.id, runs: [] });
    if (method === "PUT") {
      stored.enabled = body.enabled;
      stored.name = body.name;
      stored.cronExpression = body.cronExpression;
    }
    return json(jobDto(stored));
  }
  if (path === "/gateway/factories") return json(FACTORY_LIST);
  if (path === "/gateway/factories/warmforward") return json(FACTORY_PAGE);
  if (path === "/gateway/factories/warmforward/seats") return json(seatsView());
  if (path === "/directors") return json([]);
  // Anything else the pages read on the side (settings, the session list) is not part of this proof.
  return json({ error: "not served by the fake Gateway" }, 404);
}) as typeof window.fetch;

createRoot(document.getElementById("root")!).render(
  <HashRouter>
    <Routes>
      <Route path="/schedule" element={<ScheduleView />} />
      <Route path="/factories" element={<FactoriesView />} />
      <Route path="/factories/:factory" element={<FactoryView />} />
      <Route path="/factories/:factory/:tab" element={<FactoryView />} />
      <Route path="*" element={<Navigate to="/schedule" replace />} />
    </Routes>
  </HashRouter>,
);
