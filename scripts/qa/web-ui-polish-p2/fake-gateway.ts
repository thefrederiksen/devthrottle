// WEB UI POLISH PHASE 2 (four Cockpit fixes at 1600 pixels), for the proof screenshots. Only the network answers are
// replaced: each answer is shaped as the Gateway sends it, with the long words that made the four screens overflow
// (a long Mission button, a long status, a full build string, long run records). cockpit.tsx renders the real route
// table and frame over it. Built by build.mjs in this folder.

const now = Date.now();
const iso = (minutes: number) => new Date(now + minutes * 60_000).toISOString();

export const sessionId = "5e551011-0000-4000-8000-000000000001";

const sessions = Array.from({ length: 6 }, (_, i) => ({
  sessionId: i === 0 ? sessionId : `5e551011-0000-4000-8000-00000000000${i + 1}`,
  name: i === 0 ? "Licenses - Delivery Lead - always ready, loud when not" : `Test seat ${i + 1}`,
  number: String(115 + i),
  createdAt: iso(-240),
  sortOrder: i,
  directorId: "test-director",
  machineName: "SOREN_NORTH",
  repoPath: "D:/test/repository",
  effectiveColor: "green",
  effectiveColorHex: "#22C55E",
  stateLabel: "Working",
  triageBucket: "active",
  agent: "ClaudeCode",
  agentType: "ClaudeCode",
  modelDisplay: { text: "opus-5-5", tooltip: "The model this session runs.", isAbsent: false },
  missionButton:
    i === 0
      ? {
          verdict: "mission-with-document",
          label: "Licenses - Always Ready, Loud When Not",
          colorKey: "mission",
          iconKey: "document",
          tooltip: "Licenses - Always Ready, Loud When Not",
          documentAddress: "/r/a24e87a1-fd9e-4f8b-87e6-04013cf1e75f",
        }
      : null,
}));

function cronJob(id: string, name: string, extra: Record<string, unknown>) {
  return {
    id,
    name,
    enabled: true,
    scheduleKind: "recurring",
    cronExpression: "0 9 * * 0",
    runAt: null,
    timeZoneId: "America/Toronto",
    target: { machine: "SOREN_NORTH" },
    action: { repoPath: "D:/ReposFred/devthrottle", seed: "It is Sunday. Write the five posts for the week.", workListName: null },
    preventOverlap: true,
    notifyOn: "none",
    notifyWebhookUrl: null,
    nextRunUtc: iso(60 * 20),
    lifecycle: "active",
    stateLabel: "Active",
    toggleLabel: "Pause",
    shiftText: "Morning shift",
    lastFiredUtc: iso(-60 * 24 * 6),
    lastStatus: "started",
    runRecord: { verdict: "ok", text: "closes itself - 1 of 1, about 1h 11m" },
    ...extra,
  };
}

const jobs = [
  cronJob("cj_1", "Blog factory: five posts a week", {}),
  cronJob("cj_2", "M-Studio - Weekly Usage Report for the whole team", {
    cronExpression: "0 1 * * 1",
    shiftText: "Night shift",
    runRecord: { verdict: "bad", text: "2 of 3 closed itself - 1 stopped by another session" },
  }),
  cronJob("cj_3", "Monday budget run", {
    cronExpression: "0 7 * * 1",
    shiftText: "Night shift",
    lastFiredUtc: null,
    lastStatus: null,
    runRecord: { verdict: "none", text: "no runs yet" },
  }),
  cronJob("cj_4", "DevThrottle Factory - Podcast episode notes", {
    lastFiredUtc: iso(-60 * 24),
    lastStatus: "failed",
    lastRunTone: "bad",
    lastRunText:
      "Last run failed: The run's session on SOREN_NORTH ended without reporting how the run went. Open the session to see what it did.",
    runRecord: { verdict: "bad", text: "3 of 5 closed itself - 2 stopped by you" },
  }),
  cronJob("cj_5", "Terminal Parity - resume the comparison", {
    scheduleKind: "once",
    runAt: iso(60 * 24 * 11),
    cronExpression: null,
    target: { machine: "devlinux" },
    action: { repoPath: "/home/soren/ReposFred/devthrottle", seed: "You were started by a schedule.", workListName: null },
    shiftText: "No run in the next 7 days",
    lastFiredUtc: null,
    lastStatus: null,
    runRecord: { verdict: "none", text: "no runs yet" },
  }),
  cronJob("cj_6", "Paused clean-up of old worktrees", {
    enabled: false,
    lifecycle: "paused",
    stateLabel: "Paused",
    toggleLabel: "Resume",
  }),
];

const longStatus =
  "This machine has sent no resource summary in the last 15 minutes. Its Director may be older than the resource monitor, or its connection to the Gateway may be down.";

const DASH = "\u2014";

function capacity(reporting: boolean, why: string | null) {
  return reporting
    ? {
        reporting,
        processorLabel: "6%",
        processorDetail: "peak at least 84%",
        processorTone: "ok",
        memoryLabel: "57%",
        memoryDetail: "8.9 / 16 GB",
        memoryTone: "ok",
        spareLabel: "at most 4.0 GB",
        spareBytes: 4 * 1_073_741_824,
        statusLabel: null,
        statusTone: "ok",
        why,
        readingWindowStartUtc: iso(-5),
      }
    : {
        reporting,
        processorLabel: DASH,
        processorDetail: null,
        processorTone: "idle",
        memoryLabel: DASH,
        memoryDetail: null,
        memoryTone: "idle",
        spareLabel: DASH,
        spareBytes: null,
        statusLabel: null,
        statusTone: "idle",
        why,
        readingWindowStartUtc: null,
      };
}

const fullBuild = "2.37.0+9ac50e5674b937a70a342cd81a6db9a42eb1513d";
const none = { offered: false, label: "", reason: null };
const noLauncher = "No launcher is registered for this machine, so it cannot be started, restarted or updated from here.";

const machines = {
  newestRelease: { version: "2.37.0", label: "2.37.0", detail: null, checkedAtUtc: iso(-10) },
  highlights: [{ text: "2 of 4 Directors behind", tone: "warn" }],
  machines: [
    {
      machine: "devlinux",
      launcherVersion: null,
      launcherLastSeenUtc: null,
      reach: "NoLauncher",
      reachLabel: "No launcher",
      reachTone: "idle",
      reachDetail: noLauncher,
      launcherVersionState: { label: "", tone: "idle", behind: false },
      canReportUpdateStatus: false,
      upToDate: false,
      update: { offered: false, label: "Update now", reason: noLauncher },
      restart: none,
      start: none,
      capacity: capacity(false, longStatus),
      directors: [
        {
          directorId: "a10a8cf0-0000-4000-8000-000000000001",
          name: "devlinux",
          version: "2.35.1",
          sessions: 10,
          stateLabel: "Running",
          stateTone: "ok",
          versionState: { label: "behind", tone: "warn", behind: true },
        },
      ],
    },
    {
      machine: "SORENLAPTOP",
      launcherVersion: fullBuild,
      launcherLastSeenUtc: iso(-0.25),
      reach: "Connected",
      reachLabel: "Launcher connected",
      reachTone: "ok",
      reachDetail: null,
      launcherVersionState: { label: "current", tone: "ok", behind: false },
      canReportUpdateStatus: true,
      upToDate: true,
      update: none,
      restart: { offered: true, label: "Restart", reason: null },
      start: none,
      capacity: capacity(
        true,
        "Only 45 of the last 60 minutes were measured, so the hour's peak processor use is at least what is shown, not exactly it.",
      ),
      directors: [
        {
          directorId: "136af82d-0000-4000-8000-000000000002",
          name: "SORENLAPTOP",
          version: fullBuild,
          sessions: 0,
          stateLabel: "Running",
          stateTone: "ok",
          versionState: { label: "current", tone: "ok", behind: false },
        },
      ],
    },
    {
      machine: "SOREN_NORTH",
      launcherVersion: fullBuild,
      launcherLastSeenUtc: iso(-0.25),
      reach: "Connected",
      reachLabel: "Launcher connected",
      reachTone: "ok",
      reachDetail: null,
      launcherVersionState: { label: "current", tone: "ok", behind: false },
      canReportUpdateStatus: true,
      upToDate: false,
      update: {
        offered: false,
        label: "Update when empty",
        reason:
          "41 sessions running. Smart Restart on that Director updates it once the sessions are put away, or wait until they finish.",
      },
      restart: none,
      start: none,
      capacity: capacity(false, longStatus),
      directors: [
        {
          directorId: "6d4523e2-0000-4000-8000-000000000003",
          name: "DevThrottle_1",
          version: "2.33.0",
          sessions: 41,
          stateLabel: "Running",
          stateTone: "ok",
          versionState: { label: "behind", tone: "warn", behind: true },
        },
      ],
    },
  ],
};

const recordings = Array.from({ length: 6 }, (_, i) => ({
  recordingId: `rec-${i}`,
  title: `Recording 2026-11-0${4 - Math.min(i, 3)} 1${6 - i}:2${i}`,
  startedAt: iso(-60 * 24 * (i + 1)),
  state: "transcribed",
  segments: 10 - i,
  durationMs: (9 * 60 + 31) * 1000,
  hasTranscript: true,
  transcriptPath: `C:/vault/rec-${i}.md`,
  inVault: false,
}));

const cronLoad = { machines: [], capacity: 6, summary: "", hours: [] };

export function installFakeGateway(): void {
  window.fetch = async (input, init) => {
    const url = typeof input === "string" ? input : input instanceof Request ? input.url : input.toString();
    const path = new URL(url, "http://test.invalid").pathname;
    const method = init?.method ?? "GET";
    let answer: unknown;
    if (path === "/sessions") answer = { sessions, directors: [] };
    else if (path === "/directors") answer = [];
    else if (path === "/machines") answer = machines;
    else if (path === "/cron/jobs" && method === "GET") answer = { jobs };
    else if (path === "/cron/load") answer = cronLoad;
    else if (path === "/cron/pause") answer = { paused: false };
    else if (path === "/ingest/recordings") answer = recordings;
    else if (path === "/teams") answer = { teams: [], start: { where: "own-account" } };
    else if (path === "/gateway/alerts") answer = { alerts: [], needsAnswerCount: 0 };
    else if (path.endsWith("/licenses/alarms")) answer = { asOfUtc: iso(0), count: 0, summary: "", linkText: "", alarms: [] };
    else if (path.includes("demo-mode")) answer = { enabled: false };
    else if (path.includes("factory-agents/switch")) answer = { enabled: true };
    else if (path.endsWith("/queue")) answer = [];
    else
      return new Response(JSON.stringify({ error: "Not part of this capture", message: "Not part of this capture" }), {
        status: 503,
        headers: { "Content-Type": "application/json" },
      });
    return new Response(JSON.stringify(answer), { headers: { "Content-Type": "application/json" } });
  };
}
