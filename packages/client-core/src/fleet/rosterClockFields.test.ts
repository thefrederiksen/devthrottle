import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionDto } from "../api/client";
import {
  GATEWAY_TIME_HEADER,
  ROSTER_PATH,
  getSessionsEnvelope,
  isoToEpochSeconds,
  reachabilityLastSeen,
  resetRosterHeld,
  restoreRosterClockFields,
  type DirectorReachability,
  type SessionsEnvelope,
} from "./fleetClient";
import { supervisionStats } from "../sessions/supervision";

// Traffic optimization, phase 2: the roster is read WITHOUT its clock fields, so an unchanged roster is a 304, and
// the two fields are rebuilt here from absolute timestamps and the Gateway's own time for that answer. What is
// pinned: the rebuilt envelope is the old envelope, field for field and value for value; the fetch asks the new
// way and reuses what it holds on a 304, with the 304's own time; a Gateway that ignored the parameter is taken
// as it is.
//
// Revert-proof: compute the ages from this device's clock (Date.now()) instead of the header and the skew test goes
// red; ignore the 304 and the held-body test goes red; restore idleSeconds for a session that had no
// lastActivityAt and the keep test goes red.

const GATEWAY_NOW = "2026-09-21T12:00:07.2503114Z";
const NOW_S = Date.UTC(2026, 8, 21, 12, 0, 7) / 1000 + 0.2503114;

function session(id: string, lastActivityAt: string | null, extra: Partial<SessionDto> = {}): SessionDto {
  return {
    sessionId: id,
    agent: "claude",
    statusColor: "blue",
    lastActivityAt,
    createdAt: "2026-09-21T10:00:00Z",
    ...extra,
  } as SessionDto;
}

// The envelope as the Gateway sends it in the OLD form, with its clock fields computed at GATEWAY_NOW exactly the
// way the Gateway computes them.
function oldForm(): Partial<SessionsEnvelope> {
  const at = (iso: string) => NOW_S - isoToEpochSeconds(iso)!;
  return {
    sessions: [
      session("a", "2026-09-21T12:00:02.1111111Z", { idleSeconds: at("2026-09-21T12:00:02.1111111Z") } as Partial<SessionDto>),
      session("b", "2026-09-21T11:58:07.9999999Z", { idleSeconds: at("2026-09-21T11:58:07.9999999Z") } as Partial<SessionDto>),
      // Pushed 0.5 s AFTER the instant the answer was measured at - the Gateway clamps idle at 0.
      session("c", "2026-09-21T12:00:07.7503114Z", { idleSeconds: 0 } as Partial<SessionDto>),
      // No lastActivityAt: the Gateway never recomputes this one, and it is not left out.
      session("d", null, { idleSeconds: 42 } as Partial<SessionDto>),
    ],
    machineErrors: [],
    directors: [
      { directorId: "d1", state: "online", lastSeenUtc: "2026-09-21T12:00:03.2503114Z", lastSeenAgeSeconds: 4 },
      { directorId: "d2", state: "offline", lastSeenUtc: "2026-09-21T11:30:07.2503114Z", lastSeenAgeSeconds: 1800 },
      { directorId: "d3", state: "offline", lastSeenUtc: null, lastSeenAgeSeconds: null },
    ] as DirectorReachability[],
    unreachableBanner: null,
  };
}

// The same envelope in the NEW form: what the Gateway sends for clockFields=absolute.
function newForm(): Partial<SessionsEnvelope> {
  const env = oldForm();
  return {
    ...env,
    sessions: env.sessions!.map((s) => {
      if (s.lastActivityAt == null) return s;
      const { idleSeconds: _drop, ...rest } = s as SessionDto & { idleSeconds?: number };
      return rest as SessionDto;
    }),
    directors: env.directors!.map(({ lastSeenAgeSeconds: _drop, ...rest }) => rest as DirectorReachability),
  };
}

function closeTo(actual: unknown, expected: unknown): void {
  if (typeof expected === "number") {
    expect(typeof actual).toBe("number");
    expect(Math.abs((actual as number) - expected)).toBeLessThan(1e-6);
    return;
  }
  if (expected !== null && typeof expected === "object") {
    expect(actual !== null && typeof actual === "object").toBe(true);
    expect(Object.keys(actual as object).sort()).toEqual(Object.keys(expected as object).sort());
    for (const k of Object.keys(expected as object))
      closeTo((actual as Record<string, unknown>)[k], (expected as Record<string, unknown>)[k]);
    return;
  }
  expect(actual).toEqual(expected);
}

describe("restoreRosterClockFields", () => {
  it("rebuilds the old envelope from the new one, field for field", () => {
    closeTo(restoreRosterClockFields(newForm(), GATEWAY_NOW), oldForm());
  });

  it("gives every reader the same labels and tones as the old envelope did", () => {
    const restored = restoreRosterClockFields(newForm(), GATEWAY_NOW);
    const old = oldForm();
    expect(restored.directors!.map((d) => reachabilityLastSeen(d.lastSeenAgeSeconds)))
      .toEqual(old.directors!.map((d) => reachabilityLastSeen(d.lastSeenAgeSeconds)));
    const now = NOW_S * 1000;
    expect(restored.sessions!.map((s) => supervisionStats(s, now)))
      .toEqual(old.sessions!.map((s) => supervisionStats(s, now)));
  });

  it("does not depend on this device's clock", () => {
    // A phone ten minutes fast: the answer is the same, because only the Gateway's time is used.
    vi.useFakeTimers();
    vi.setSystemTime(new Date(Date.UTC(2026, 8, 21, 12, 10, 7)));
    try {
      closeTo(restoreRosterClockFields(newForm(), GATEWAY_NOW), oldForm());
    } finally {
      vi.useRealTimers();
    }
  });

  it("takes an answer with no Gateway time (a Gateway that ignored the parameter) exactly as it came", () => {
    const old = oldForm();
    expect(restoreRosterClockFields(old, null)).toBe(old);
  });

  it("refuses, loudly, an answer that left the clock fields out but carries no time to rebuild them from", () => {
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      expect(() => restoreRosterClockFields(newForm(), null)).toThrow(/X-Gateway-Time/);
      expect(() => restoreRosterClockFields(newForm(), "not a time")).toThrow(/unreadable/);
      expect(error).toHaveBeenCalledTimes(2);
    } finally {
      error.mockRestore();
    }
  });

  it("never changes the body it was given", () => {
    const body = newForm();
    const before = JSON.stringify(body);
    restoreRosterClockFields(body, GATEWAY_NOW);
    expect(JSON.stringify(body)).toBe(before);
  });
});

describe("isoToEpochSeconds", () => {
  it("keeps the Gateway's full fraction, reads a zone, and treats no zone as UTC", () => {
    expect(isoToEpochSeconds("2026-09-21T12:00:07.2503114Z")! - Date.UTC(2026, 8, 21, 12, 0, 7) / 1000).toBeCloseTo(0.2503114, 7);
    expect(isoToEpochSeconds("2026-09-21T14:00:07+02:00")).toBe(Date.UTC(2026, 8, 21, 12, 0, 7) / 1000);
    expect(isoToEpochSeconds("2026-09-21T12:00:07")).toBe(Date.UTC(2026, 8, 21, 12, 0, 7) / 1000);
    expect(isoToEpochSeconds("not a time")).toBeNull();
    expect(isoToEpochSeconds(null)).toBeNull();
  });
});

describe("getSessionsEnvelope, conditional", () => {
  interface Answer { status: number; body?: unknown; etag?: string; time?: string }
  let answers: Answer[];
  let requests: { url: string; init: RequestInit }[];

  beforeEach(() => {
    resetRosterHeld();
    answers = [];
    requests = [];
    vi.stubGlobal("fetch", vi.fn(async (url: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      requests.push({ url: String(url), init: init ?? {} });
      const a = answers.shift()!;
      const headers = new Headers();
      if (a.etag) headers.set("ETag", a.etag);
      if (a.time) headers.set(GATEWAY_TIME_HEADER, a.time);
      return { ok: a.status >= 200 && a.status < 300, status: a.status, headers, json: async () => a.body } as unknown as Response;
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    resetRosterHeld();
  });

  it("asks for the roster without its clock, sends back the tag it holds, and on a 304 reuses the body with the 304's own time", async () => {
    answers.push({ status: 200, body: newForm(), etag: '"t1"', time: GATEWAY_NOW });
    const first = await getSessionsEnvelope();
    expect(requests[0].url).toBe(ROSTER_PATH);
    expect(new Headers(requests[0].init.headers).get("If-None-Match")).toBeNull();
    expect(requests[0].init.cache).toBe("no-store");
    closeTo(first.directors[0].lastSeenAgeSeconds, 4);

    answers.push({ status: 304, etag: '"t1"', time: "2026-09-21T12:00:09.2503114Z" });
    const second = await getSessionsEnvelope();
    expect(new Headers(requests[1].init.headers).get("If-None-Match")).toBe('"t1"');
    // Same body, two seconds later by the GATEWAY's clock: the ages moved on exactly as the old poll's would have.
    closeTo(second.directors[0].lastSeenAgeSeconds, 6);
    expect(second.sessions.map((s) => s.sessionId)).toEqual(["a", "b", "c", "d"]);
  });

  it("a changed roster replaces what is held", async () => {
    answers.push({ status: 200, body: newForm(), etag: '"t1"', time: GATEWAY_NOW });
    await getSessionsEnvelope();
    const changed = newForm();
    changed.sessions = changed.sessions!.slice(0, 1);
    answers.push({ status: 200, body: changed, etag: '"t2"', time: GATEWAY_NOW });
    expect((await getSessionsEnvelope()).sessions).toHaveLength(1);

    answers.push({ status: 304, etag: '"t2"', time: GATEWAY_NOW });
    expect(new Headers((await getSessionsEnvelope(), requests[2].init.headers)).get("If-None-Match")).toBe('"t2"');
  });

  it("a 200 without the Gateway time, when the fields were left out, fails the read instead of showing blank ages", async () => {
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      answers.push({ status: 200, body: newForm(), etag: '"t1"' });
      await expect(getSessionsEnvelope()).rejects.toThrow(/X-Gateway-Time/);
    } finally {
      error.mockRestore();
    }
  });

  it("still fails loud on an error", async () => {
    answers.push({ status: 503 });
    await expect(getSessionsEnvelope()).rejects.toThrow(/503/);
  });
});
