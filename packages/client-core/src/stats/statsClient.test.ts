import { afterEach, describe, it, expect, vi } from "vitest";
import {
  getThrottle,
  throttleWindowQuery,
  throttleWindowFromSearch,
  hourlyChartEnd,
  summarizeThrottle,
  formatPercent,
  last24HourKeys,
  windowSeries,
  emptyInputHour,
  localHourLabel,
  safeTimeZone,
  type Surface,
  type ThrottleFigure,
  type ThrottleHeadline,
  type ThrottleShare,
  type InputHour,
} from "./statsClient";

// The pure "Your Throttle" summary math. The network read (getThrottle) is exercised through the app; here
// we lock the honest share arithmetic that both shells render over the figure the Gateway serves.
//
// There is no character volume anywhere in these shapes (mission "Clean up Your Throttle", ruling R16): the
// figure comes from the submission ledger, which counts turns and nothing else.

const CHOICES = [
  { days: 1, label: "Last 24 hours" },
  { days: 7, label: "Last 7 days" },
  { days: 14, label: "Last 14 days" },
  { days: 30, label: "Last 30 days" },
];

// A headline built EXPLICITLY, never derived from the buckets: the pages render the Gateway's headline
// verbatim (final inspection finding F-01), so a test that derived the expected headline from the buckets
// would be asserting the arithmetic this client no longer does.
function shareOf(turns: number, denominator: number): ThrottleShare {
  if (denominator === 0) return { turns, share: null, percent: null };
  const share = turns / denominator;
  return { turns, share, percent: Math.floor(share * 100 + 0.5) };
}

function headline(denominator: number, voice: number, typed: number, bySurface: Partial<Record<Surface, number>>): ThrottleHeadline {
  const labels: Record<Surface, string> = { desktop: "Desktop", cockpit: "Cockpit", phone: "Phone", unknown: "Unknown" };
  return {
    denominator,
    hasData: denominator > 0,
    voice: shareOf(voice, denominator),
    typed: shareOf(typed, denominator),
    phone: { ...shareOf(bySurface.phone ?? 0, denominator), remainder: denominator - (bySurface.phone ?? 0) },
    surfaces: (["desktop", "cockpit", "phone", "unknown"] as Surface[]).map((surface) => ({
      surface, label: labels[surface], remainder: denominator - (bySurface[surface] ?? 0), ...shareOf(bySurface[surface] ?? 0, denominator),
    })),
  };
}

function figure(buckets: ThrottleFigure["buckets"], head?: ThrottleHeadline): ThrottleFigure {
  const bySurface: Partial<Record<Surface, number>> = {};
  for (const b of buckets) bySurface[b.surface] = (bySurface[b.surface] ?? 0) + b.turns;
  return {
    definition: "The shared figure is computed over activity_events rows where EventType is turn-submitted and InputOrigin is present, in sessions a person started, grouped by the origin's modality and surface.",
    unit: "submitted turns",
    window: {
      fromUtc: "2026-08-29T00:00:00Z",
      toUtc: "2026-09-05T00:00:00Z",
      isDefault: true,
      label: "Last 7 days",
      kind: "default",
      days: 7,
      week: null,
      choices: CHOICES,
    },
    ledger: { retentionDays: 30, earliestUtc: "2026-08-06T04:00:00Z" },
    headline: head ?? headline(
      buckets.reduce((t, b) => t + b.turns, 0),
      buckets.filter((b) => b.modality === "voice").reduce((t, b) => t + b.turns, 0),
      buckets.filter((b) => b.modality === "typed").reduce((t, b) => t + b.turns, 0),
      bySurface,
    ),
    turns: buckets.reduce((t, b) => t + b.turns, 0),
    voiceTurns: buckets.filter((b) => b.modality === "voice").reduce((t, b) => t + b.turns, 0),
    typedTurns: buckets.filter((b) => b.modality === "typed").reduce((t, b) => t + b.turns, 0),
    sessions: 1,
    buckets,
    hourlyTurns: [],
    agents: [],
    repos: [],
    agentsSummary: { agentCount: 0, totalTurns: 0, totalSessions: 0, voiceTurns: 0, voiceShare: null, voicePercent: null, topAgentName: null, topShare: null, topPercent: null, agentDrivenTurns: 0, leverage: null, leverageText: null, hasData: false },
    reposSummary: { repoCount: 0, totalTurns: 0, totalSessions: 0, voiceTurns: 0, voiceShare: null, voicePercent: null, topRepoName: null, topShare: null, topPercent: null, hasData: false },
    reposUnattributedTurns: 0,
    excluded: { noInputOrigin: 0, agentDriven: 0, framework: 0, unresolved: 0, notStartedByYou: 0 },
    agentDrivenTurns: 0,
    starters: { sessions: 1, turns: 4, hasData: true, humanShare: 1, humanPercent: 100, groups: [{ kind: "human", label: "Started by you", sessions: 1, turns: 4, sessionShare: 1, sessionPercent: 100 }, { kind: "agent", label: "Run by other sessions", sessions: 0, turns: 0, sessionShare: 0, sessionPercent: 0 }, { kind: "schedule", label: "Run by a schedule", sessions: 0, turns: 0, sessionShare: 0, sessionPercent: 0 }, { kind: "notRecorded", label: "Starter not recorded", sessions: 0, turns: 0, sessionShare: 0, sessionPercent: 0 }] },
  };
}

describe("summarizeThrottle", () => {
  it("lays the Gateway's headline out verbatim: every count, share and percent is the headline's", () => {
    const s = summarizeThrottle(
      figure([
        { modality: "voice", surface: "phone", turns: 3 },
        { modality: "typed", surface: "desktop", turns: 1 },
      ]),
    );
    expect(s.totalTurns).toBe(4);
    expect(s.voiceTurns).toBe(3);
    expect(s.typedTurns).toBe(1);
    expect(s.turnsBySurface.phone).toBe(3);
    expect(s.turnsBySurface.desktop).toBe(1);
    expect(s.voiceShare).toBeCloseTo(0.75);
    expect(s.voicePercent).toBe(75);
    expect(s.phoneShare).toBeCloseTo(0.75);
    expect(s.phonePercent).toBe(75);
    expect(s.surfaces.map((x) => x.label)).toEqual(["Desktop", "Cockpit", "Phone", "Unknown"]);
    expect(s.hasData).toBe(true);
  });

  // THE POINT OF F-01. The headline says 57 per cent spoken and 14 from the phone; the buckets say 80 and
  // 100. The summary is the headline's, and nothing on it can be reached by dividing the buckets.
  it("renders the headline even when the counts and buckets disagree with it - it never recomputes", () => {
    const head = headline(1786, 1015, 771, { desktop: 1531, phone: 248, unknown: 7 });
    const s = summarizeThrottle(
      figure([
        { modality: "voice", surface: "phone", turns: 8 },
        { modality: "typed", surface: "desktop", turns: 2 },
      ], head),
    );
    expect(s.totalTurns).toBe(1786);
    expect(s.voiceTurns).toBe(1015);
    expect(s.voicePercent).toBe(57);
    expect(s.phonePercent).toBe(14);
    expect(s.turnsBySurface.phone).toBe(248);
    expect(s.turnsBySurface.desktop).toBe(1531);
  });

  it("prints the Gateway's percent field, not its own rounding of the share", () => {
    const head = headline(8, 3, 5, { phone: 3, desktop: 5 });
    head.voice.percent = 99;
    const s = summarizeThrottle(figure([], head));
    expect(s.voiceShare).toBeCloseTo(0.375);
    expect(s.voicePercent).toBe(99);
    expect(formatPercent(s.voicePercent)).toBe("99%");
  });

  it("reports null shares and percents (not 0%) when the Gateway says nothing is counted", () => {
    const s = summarizeThrottle(figure([]));
    expect(s.totalTurns).toBe(0);
    expect(s.voiceShare).toBeNull();
    expect(s.voicePercent).toBeNull();
    expect(s.phoneShare).toBeNull();
    expect(s.phonePercent).toBeNull();
    expect(s.hasData).toBe(false);
    expect(formatPercent(s.voicePercent)).toBe("n/a");
  });

  // The unknown surface is a recorded answer ("typed, somewhere we could not name"), kept as its own entry
  // rather than folded into a real surface - and it is still a counted turn in the voice share.
  it("keeps an unknown surface as its own entry and inside the totals", () => {
    const s = summarizeThrottle(figure([{ modality: "typed", surface: "unknown", turns: 7 }]));
    expect(s.totalTurns).toBe(7);
    expect(s.turnsBySurface.unknown).toBe(7);
    expect(s.voiceShare).toBe(0);
    expect(s.voicePercent).toBe(0);
  });
});

// The Agents and Repos tabs' headline cards used to be totalled and divided here (summarizeAgents,
// summarizeRepos, formatShare). They are the Gateway's now (fix-round finding F-01): the tabs print
// figure.agentsSummary and figure.reposSummary, and the contract tests hold the rendered tabs to the
// fixtures' recorded values.

describe("last24HourKeys", () => {
  it("returns 24 consecutive UTC hour keys ending at the current hour, oldest first", () => {
    const keys = last24HourKeys(new Date("2026-07-13T17:42:00Z"));
    expect(keys).toHaveLength(24);
    expect(keys[23]).toBe("2026-07-13T17"); // the hour containing "now"
    expect(keys[22]).toBe("2026-07-13T16");
    expect(keys[0]).toBe("2026-07-12T18"); // 23 hours earlier, across the day boundary
  });
});

describe("windowSeries", () => {
  it("aligns a sparse series onto the window and zero-fills the gaps", () => {
    const keys = last24HourKeys(new Date("2026-07-13T02:00:00Z"));
    const sparse: InputHour[] = [{ hour: "2026-07-13T01", turns: 5, voiceTurns: 4, typedTurns: 1, voiceShare: 0.8, typedShare: 0.2 }];
    const windowed = windowSeries(sparse, keys, emptyInputHour);
    expect(windowed).toHaveLength(24);
    // The one populated hour lands in its slot; every other hour is a real zero entry.
    expect(windowed[23]).toEqual({ hour: "2026-07-13T02", turns: 0, voiceTurns: 0, typedTurns: 0, voiceShare: null, typedShare: null });
    expect(windowed[22]).toEqual(sparse[0]);
    expect(windowed[0].turns).toBe(0);
  });

  it("gives two different series the SAME aligned window so charts line up", () => {
    const keys = last24HourKeys(new Date("2026-07-13T10:00:00Z"));
    const a = windowSeries([{ hour: "2026-07-13T02", turns: 3, voiceTurns: 3, typedTurns: 0, voiceShare: 1, typedShare: 0 }], keys, emptyInputHour);
    const b = windowSeries([{ hour: "2026-07-13T09", turns: 7, voiceTurns: 1, typedTurns: 6, voiceShare: 1 / 7, typedShare: 6 / 7 }], keys, emptyInputHour);
    expect(a.map((h) => h.hour)).toEqual(b.map((h) => h.hour)); // identical hour axis -> aligned
  });
});

describe("localHourLabel", () => {
  it("formats a UTC hour key as the local 2-digit hour in the given zone", () => {
    // 17:00 UTC on a July date is 13:00 in New York (EDT, UTC-4) and 17:00 in UTC.
    expect(localHourLabel("2026-07-13T17", "UTC")).toBe("17");
    expect(localHourLabel("2026-07-13T17", "America/New_York")).toBe("13");
  });
});

describe("safeTimeZone", () => {
  it("passes a usable zone through", () => {
    expect(safeTimeZone("UTC")).toBe("UTC");
    expect(safeTimeZone("America/New_York")).toBe("America/New_York");
  });

  it("falls back to a usable zone for a bad or empty id (never throws)", () => {
    const fallback = safeTimeZone("Not/AZone");
    // Whatever it resolves to, it must be a zone Intl can actually format with.
    expect(() => new Intl.DateTimeFormat("en-US", { timeZone: fallback })).not.toThrow();
    expect(safeTimeZone("").length).toBeGreaterThan(0);
  });
});

// ---- the network read: what the three request shapes send, and what a served window must carry ------
//
// The Gateway decides the window (mission "Clean up Your Throttle", rulings R4 and R5); this client sends
// what was chosen as the matching query and reads back the kind, the length or week, and the selector's
// choices. An answer WITHOUT the choices is refused rather than defaulted: a selector that offered lengths
// the Gateway did not serve would be the client ruling for itself (CLAUDE.md rule 7).

vi.mock("../api/client", () => ({
  authHeaders: () => ({}),
  GatewayError: class GatewayError extends Error {
    constructor(public status: number, message: string) {
      super(message);
    }
  },
}));

function servedWindow(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    fromUtc: "2026-08-29T16:00:00Z",
    toUtc: "2026-09-05T16:00:00Z",
    isDefault: true,
    label: "Last 7 days",
    kind: "default",
    days: 7,
    week: null,
    choices: CHOICES,
    ...over,
  };
}

function servedBody(window: Record<string, unknown>): Record<string, unknown> {
  return {
    available: true,
    generatedAtUtc: "2026-09-05T16:00:00Z",
    timeZone: "UTC",
    throttle: { ...figure([]), window },
    concurrency: null,
    statisticsUnavailableReason: "no store",
    notCaptured: [],
  };
}

function stubFetch(body: Record<string, unknown>) {
  const fetchMock = vi.fn(async (_input: RequestInfo | URL) =>
    new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } }),
  );
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("throttleWindowQuery and getThrottle", () => {
  it("sends no query for the default, and the matching query for each of the three request shapes", async () => {
    expect(throttleWindowQuery(undefined)).toBe("");
    expect(throttleWindowQuery({ days: 14 })).toBe("?days=14");
    expect(throttleWindowQuery({ week: "2026-W35" })).toBe("?week=2026-W35");
    expect(throttleWindowQuery({ fromUtc: "2026-08-24T04:00:00Z", toUtc: "2026-08-31T04:00:00Z" })).toBe(
      "?from=2026-08-24T04%3A00%3A00Z&to=2026-08-31T04%3A00%3A00Z",
    );

    const fetchMock = stubFetch(servedBody(servedWindow()));
    await getThrottle(undefined);
    await getThrottle(undefined, { days: 14 });
    await getThrottle(undefined, { week: "2026-W35" });
    await getThrottle(undefined, { fromUtc: "2026-08-24T04:00:00Z", toUtc: "2026-08-31T04:00:00Z" });
    const urls = fetchMock.mock.calls.map((call) => String(call[0]));
    expect(urls).toEqual([
      "/stats/data",
      "/stats/data?days=14",
      "/stats/data?week=2026-W35",
      "/stats/data?from=2026-08-24T04%3A00%3A00Z&to=2026-08-31T04%3A00%3A00Z",
    ]);
  });

  it("reads the kind, the length or week, and the choices exactly as served", async () => {
    stubFetch(servedBody(servedWindow({
      kind: "week",
      week: "2026-W35",
      days: null,
      isDefault: false,
      label: "Week 35 of 2026, Monday 24 August to Sunday 30 August (America/Toronto)",
    })));
    const data = await getThrottle(undefined, { week: "2026-W35" });
    if (!data.available) throw new Error("expected a served figure");
    expect(data.throttle.window.kind).toBe("week");
    expect(data.throttle.window.week).toBe("2026-W35");
    expect(data.throttle.window.days).toBeNull();
    expect(data.throttle.window.isDefault).toBe(false);
    expect(data.throttle.window.label).toBe("Week 35 of 2026, Monday 24 August to Sunday 30 August (America/Toronto)");
    expect(data.throttle.window.choices).toEqual(CHOICES);

    stubFetch(servedBody(servedWindow({ kind: "days", days: 14, isDefault: false, label: "Last 14 days" })));
    const chosen = await getThrottle(undefined, { days: 14 });
    if (!chosen.available) throw new Error("expected a served figure");
    expect(chosen.throttle.window.kind).toBe("days");
    expect(chosen.throttle.window.days).toBe(14);
  });

  it("refuses a served answer without the choices, rather than defaulting a list of its own", async () => {
    const { choices: _dropped, ...withoutChoices } = servedWindow();
    void _dropped;
    stubFetch(servedBody(withoutChoices));
    await expect(getThrottle(undefined)).rejects.toThrow(/without the window choices/);
  });

  it("refuses a window kind it does not know, and a choice without a length and a label", async () => {
    stubFetch(servedBody(servedWindow({ kind: "fortnight" })));
    await expect(getThrottle(undefined)).rejects.toThrow(/window kind this client does not know: fortnight/);

    stubFetch(servedBody(servedWindow({ choices: [{ days: 7 }] })));
    await expect(getThrottle(undefined)).rejects.toThrow(/without a length and a label/);
  });

  it("refuses a served answer without the headline, rather than computing one of its own", async () => {
    const throttle = { ...figure([]), window: servedWindow() } as Record<string, unknown>;
    delete throttle.headline;
    stubFetch({ ...servedBody(servedWindow()), throttle });
    await expect(getThrottle(undefined)).rejects.toThrow(/without the headline/);
  });

  it("refuses a headline surface, a bucket modality, or a bucket surface it does not know", async () => {
    const head = headline(1, 1, 0, { phone: 1 });
    const badSurface = { ...head, surfaces: [...head.surfaces, { surface: "watch", label: "Watch", turns: 0, share: 0, percent: 0 }] };
    stubFetch({ ...servedBody(servedWindow()), throttle: { ...figure([], head), window: servedWindow(), headline: badSurface } });
    await expect(getThrottle(undefined)).rejects.toThrow(/headline surface this client does not know: watch/);

    stubFetch({ ...servedBody(servedWindow()), throttle: { ...figure([], head), window: servedWindow(), buckets: [{ modality: "spoken", surface: "phone", turns: 1 }] } });
    await expect(getThrottle(undefined)).rejects.toThrow(/bucket modality this client does not know: spoken/);

    stubFetch({ ...servedBody(servedWindow()), throttle: { ...figure([], head), window: servedWindow(), buckets: [{ modality: "voice", surface: "Phone", turns: 1 }] } });
    await expect(getThrottle(undefined)).rejects.toThrow(/bucket surface this client does not know: Phone/);
  });

  it("still passes the self-hosted sentence through untouched", async () => {
    stubFetch({ available: false, reason: "Your Throttle works only on the hosted DevThrottle Gateway." });
    const data = await getThrottle(undefined);
    expect(data).toEqual({ available: false, reason: "Your Throttle works only on the hosted DevThrottle Gateway." });
  });
});

describe("throttleWindowFromSearch", () => {
  it("reads a week, else a length, else nothing", () => {
    expect(throttleWindowFromSearch(new URLSearchParams("week=2026-W35"))).toEqual({ week: "2026-W35" });
    expect(throttleWindowFromSearch(new URLSearchParams("days=14"))).toEqual({ days: 14 });
    expect(throttleWindowFromSearch(new URLSearchParams("week=2026-W35&days=14"))).toEqual({ week: "2026-W35" });
    expect(throttleWindowFromSearch(new URLSearchParams(""))).toBeUndefined();
    expect(throttleWindowFromSearch(new URLSearchParams("days=soon"))).toBeUndefined();
    expect(throttleWindowFromSearch(new URLSearchParams("tab=repos"))).toBeUndefined();
  });
});

describe("hourlyChartEnd", () => {
  const now = new Date("2026-09-05T16:00:00Z");

  it("ends the 24-hour charts at the served window's end when that is in the past", () => {
    const w = { fromUtc: "2026-08-24T04:00:00Z", toUtc: "2026-08-31T04:00:00Z", isDefault: false, label: "", kind: "week" as const, days: null, week: "2026-W35", choices: CHOICES };
    expect(hourlyChartEnd(w, now).toISOString()).toBe("2026-08-31T04:00:00.000Z");
    expect(last24HourKeys(hourlyChartEnd(w, now))[23]).toBe("2026-08-31T04");
  });

  it("clamps to now when the window is still open", () => {
    const w = { fromUtc: "2026-08-31T04:00:00Z", toUtc: "2026-09-07T04:00:00Z", isDefault: false, label: "", kind: "week" as const, days: null, week: "2026-W36", choices: CHOICES };
    expect(hourlyChartEnd(w, now).toISOString()).toBe("2026-09-05T16:00:00.000Z");
  });
});
