import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import {
  REACHABILITY_OFFLINE,
  REACHABILITY_ONLINE,
  REACHABILITY_WOBBLY,
  type DirectorReachability,
  type SessionsEnvelope,
} from "./fleetClient";
import { needsYouBadgeCount } from "../sessions/ordering";
import { emptyRetentionCache, mergeRosterRetention, RETENTION_HORIZON_MS, type RetentionCache } from "./rosterRetention";

// Minimal SessionDto fixtures: the merge only routes sessions by id/director/machine, so the triage and
// color fields the ordering helpers need are irrelevant here.
function session(sessionId: string, directorId: string, machineName = "MACHINE_A"): SessionDto {
  return { sessionId, directorId, machineName } as unknown as SessionDto;
}

function director(directorId: string, state: DirectorReachability["state"], extra: Partial<DirectorReachability> = {}): DirectorReachability {
  return { directorId, state, machineName: "MACHINE_A", ...extra };
}

function envelope(sessions: SessionDto[], directors: DirectorReachability[] = []): SessionsEnvelope {
  return { sessions, machineErrors: [], directors, unreachableBanner: null };
}

describe("roster keep-and-mark retention merge", () => {
  it("renders live sessions of an online Director normally, with no mark", () => {
    const cache = emptyRetentionCache();
    const env = envelope([session("s1", "d1"), session("s2", "d1")], [director("d1", REACHABILITY_ONLINE)]);
    const { roster } = mergeRosterRetention(cache, env);
    expect(roster.sessions.map((s) => s.sessionId)).toEqual(["s1", "s2"]);
    expect(roster.marks.size).toBe(0);
  });

  it("treats a Director with live sessions and NO reachability entry as online (older Gateway)", () => {
    const { roster, cache } = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], []));
    expect(roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(roster.marks.size).toBe(0);
    expect(cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1"]);
  });

  it("keeps a wobbly Director's served sessions on the roster, marked wobbly", () => {
    const env = envelope([session("s1", "d1")], [director("d1", REACHABILITY_WOBBLY, { lastSeenAgeSeconds: 12 })]);
    const { roster } = mergeRosterRetention(emptyRetentionCache(), env);
    expect(roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    const mark = roster.marks.get("s1");
    expect(mark?.reachability).toBe(REACHABILITY_WOBBLY);
    expect(mark?.machineName).toBe("MACHINE_A");
    expect(mark?.lastSeenLabel).toBe("last seen 12s ago");
  });

  // Epic #1159 step A: the Gateway now SERVES an unreachable machine's sessions instead of dropping them.
  // Those rows are real Gateway data and must win over anything held here - the offline branch used to
  // ignore them and re-inject the cache, which would have thrown away current data for a stale copy.
  it("prefers the Gateway's live rows over the cache for an OFFLINE Director, and still marks them", () => {
    // Poll 1: d1 online with s1 - cached.
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    // Poll 2: d1 offline, but the Gateway serves its sessions anyway - and it now reports s2 as well,
    // which the client cache has never seen. The served set is what renders.
    const second = mergeRosterRetention(
      first.cache,
      envelope([session("s1", "d1"), session("s2", "d1")], [director("d1", REACHABILITY_OFFLINE, { lastSeenAgeSeconds: 90 })]),
    );
    expect(second.roster.sessions.map((s) => s.sessionId)).toEqual(["s1", "s2"]);
    // Served, not live: both rows are marked, so they render dimmed and dated rather than as normal rows.
    expect(second.roster.marks.get("s1")?.reachability).toBe(REACHABILITY_OFFLINE);
    expect(second.roster.marks.get("s2")?.reachability).toBe(REACHABILITY_OFFLINE);
    expect(second.roster.marks.get("s2")?.lastSeenLabel).toBe("last seen 1m ago");
    // The cache takes the served set, so a later poll that serves nothing still shows both.
    expect(second.cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1", "s2"]);
  });

  // THE COLD START - the case that showed NOTHING before. The phone's retention cache lives in memory for
  // as long as the roster page is mounted: a relaunch, a reload, or simply opening a session and coming
  // back starts it empty. With a machine already unreachable at that moment there was nothing to retain
  // and no rows in the envelope either, so the roster came up blank. Now the Gateway serves the rows and
  // an empty cache costs nothing.
  it("shows an offline Director's Gateway-served rows on a COLD START, with an empty cache", () => {
    const { roster, cache } = mergeRosterRetention(
      emptyRetentionCache(),
      envelope([session("s1", "d1"), session("s2", "d1")], [director("d1", REACHABILITY_OFFLINE, { lastSeenAgeSeconds: 3600 })]),
    );
    expect(roster.sessions.map((s) => s.sessionId)).toEqual(["s1", "s2"]);
    expect(roster.marks.get("s1")?.reachability).toBe(REACHABILITY_OFFLINE);
    expect(roster.marks.get("s1")?.lastSeenLabel).toBe("last seen 1h ago");
    expect(cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1", "s2"]);
  });

  it("keeps an offline Director's sessions from the cache after the Gateway drops them, marked offline", () => {
    // First poll: d1 online with s1 - cached.
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    // Second poll: d1 offline, its session dropped from the envelope's sessions[] (only a directors entry).
    const second = mergeRosterRetention(first.cache, envelope([], [director("d1", REACHABILITY_OFFLINE, { lastSeenAgeSeconds: 130 })]));
    expect(second.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    const mark = second.roster.marks.get("s1");
    expect(mark?.reachability).toBe(REACHABILITY_OFFLINE);
    expect(mark?.lastSeenLabel).toBe("last seen 2m ago");
  });

  // Inspection 2, finding 3, then inspection 3, finding 2 - this test has now been wrong THREE times,
  // which is worth recording because each wrong version looked like the disciplined one.
  //
  // First it demanded the card survive "indefinitely", which encoded the unbounded-retention defect.
  // Then it demanded the card be dropped the moment the envelope stopped NAMING the Director, which
  // read absence as proof of eviction - and a RESTARTED Gateway serves exactly that envelope before its
  // Directors reconnect, so the phone would have emptied itself on the first poll after any restart.
  // The wire carries no eviction tombstone, so absence proves nothing at all.
  //
  // Then it bounded the retention by when the Director was last NAMED, and tested that with a
  // FIVE-SECOND gap - which is the one gap at which the two designs cannot be told apart. A last-named
  // stamp ages during time the phone observes NOTHING, so a suspended page or a long outage could carry
  // it past the horizon and the first successful empty envelope after a restart would delete everything
  // at once. The five-second test was green for both the working design and the broken one.
  //
  // What is true, stated at exactly its real strength (inspection 4, finding 1 corrected the version of
  // this paragraph that said "counts OBSERVED ABSENCE" - it does not): the first envelope that omits a
  // Director starts the clock and can never delete, and deletion needs a second one, a horizon later,
  // still omitting it. After that first omission the clock is plain elapsed wall time, so a suspended
  // page still ages it. That is accepted because the deletion is recoverable, which the last test in this
  // group pins rather than assumes. The long-gap test below separates this design from the previous one;
  // it does not claim the suspension hole is gone.
  it("keeps the cards through a Gateway restart, when the envelope suddenly names nobody", () => {
    const t0 = 1_000_000;
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]), t0);
    // The Gateway restarted: it answers successfully, but knows nothing yet.
    const second = mergeRosterRetention(first.cache, envelope([], []), t0 + 5_000);
    expect(second.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(second.cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1"]);
  });

  // Inspection 4, finding 2. This clock does not START until the Gateway has already stopped naming the
  // Director - which is after the Gateway's own twenty-four-hour horizon - so whatever is set here runs
  // AFTER that day, not alongside it. Mirroring the Gateway's day therefore gave the phone nearly
  // forty-eight hours while every record promised twenty-four.
  //
  // The only job this clock has is telling a Gateway RESTART from a real eviction, and a restart's
  // pre-Hello window is seconds. So it must stay small enough that the total is the configured day plus a
  // rounding error. This pins the SIZE, not the exact value: ten minutes may become five or fifteen, but
  // anything approaching a day is the defect coming back and this reddens before it ships.
  it("keeps the client horizon far below the Gateway's day, so the two do not stack into two days", () => {
    expect(RETENTION_HORIZON_MS).toBeLessThanOrEqual(15 * 60 * 1000);
    expect(RETENTION_HORIZON_MS).toBeGreaterThan(60 * 1000);   // still long enough to outlast a restart
  });

  // THE TEST THAT SEPARATES THE TWO DESIGNS (inspection 3, finding 2). The phone was suspended, or its
  // polls failed, for longer than the whole horizon - so it observed NOTHING for that time - and then the
  // Gateway comes back and answers, naming nobody yet. Under the last-named stamp this single response
  // deleted every card. It must not: nothing was observed to be absent, so nothing has been learned.
  it("does NOT delete on the first empty response after a gap longer than the horizon", () => {
    const t0 = 1_000_000;
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]), t0);
    const afterTheOutage = mergeRosterRetention(first.cache, envelope([], []), t0 + RETENTION_HORIZON_MS * 3);
    expect(afterTheOutage.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(afterTheOutage.cache.byDirector.has("d1")).toBe(true);
  });

  // NAMED FOR WHAT THE BODY DOES. It used to say the Director had been "OBSERVED missing for longer than
  // the horizon", and the body supplies ONE omission and then lets wall time pass - which is exactly the
  // overclaim inspection 4 found in the source comments, reproduced in a test name. The horizon here is
  // elapsed time after a first observed omission; nothing observes the interval.
  it("drops the cards on a later empty envelope, a horizon after the first omission started the clock", () => {
    const t0 = 1_000_000;
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]), t0);
    // The first omission only starts the clock.
    const observedMissing = mergeRosterRetention(first.cache, envelope([], []), t0 + 60_000);
    expect(observedMissing.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    // A second successful envelope, still naming nobody, a horizon after the FIRST omission.
    const later = mergeRosterRetention(observedMissing.cache, envelope([], []), t0 + 60_000 + RETENTION_HORIZON_MS + 1);
    expect(later.roster.sessions).toEqual([]);
    expect(later.cache.byDirector.has("d1")).toBe(false);
  });

  // THE PROPERTY THE WHOLE DESIGN RESTS ON (inspection 4, finding 1). The client clock is allowed to be a
  // crude wall-clock timer - and to delete on time the phone did not observe - only because deleting a
  // retained card loses NOTHING. The cache is a display convenience, not a store of record: the sessions
  // are on the Gateway, and the next envelope that names the machine restores them from Gateway data,
  // which is fresher than the copy that was dropped.
  //
  // That claim was made in a comment before it was checked. It is checked here, because a recovery
  // property nobody exercises is exactly the kind of reassurance this mission keeps finding to be false.
  it("restores a deleted card from the Gateway as soon as the machine is named again", () => {
    const t0 = 1_000_000;
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]), t0);
    const observedMissing = mergeRosterRetention(first.cache, envelope([], []), t0 + 1_000);
    const deleted = mergeRosterRetention(observedMissing.cache, envelope([], []), t0 + 1_000 + RETENTION_HORIZON_MS + 1);
    expect(deleted.roster.sessions).toEqual([]);
    expect(deleted.cache.byDirector.has("d1")).toBe(false);

    // The machine comes back. The Gateway serves its rows and the card returns - from Gateway data, with
    // no help from the cache that was just emptied.
    const back = mergeRosterRetention(deleted.cache, envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]), t0 + 1_000 + RETENTION_HORIZON_MS + 2);
    expect(back.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(back.roster.marks.size).toBe(0);   // a live row, not a retained one

    // ...and it works from a genuinely empty cache too, which is what makes it a recovery property of the
    // MERGE rather than an accident of what happened to survive in this particular cache.
    const coldStart = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]), t0);
    expect(coldStart.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
  });

  // Coming back resets the clock. A machine that reappears and goes away again is starting a NEW absence,
  // not resuming the old one, so it gets a full horizon - otherwise a Director that returns for one poll
  // every day would accumulate its way to deletion while being seen constantly.
  it("restarts the horizon when the Director is named again in between", () => {
    const t0 = 1_000_000;
    let cache = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]), t0).cache;
    cache = mergeRosterRetention(cache, envelope([], []), t0 + 1_000).cache;                                    // observed missing
    cache = mergeRosterRetention(cache, envelope([], [director("d1", REACHABILITY_OFFLINE)]), t0 + 2_000).cache; // named again
    const later = mergeRosterRetention(cache, envelope([], []), t0 + 2_000 + RETENTION_HORIZON_MS);
    expect(later.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
  });

  // The other side of the boundary: while the Gateway still names the Director, the cards stay however
  // long it serves no rows. Being named clears the stamp, so this never expires.
  it("keeps retaining while the Gateway still names the Director, however long it serves no rows", () => {
    let cache = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]), 0).cache;
    for (let poll = 0; poll < 50; poll++) {
      const at = poll * RETENTION_HORIZON_MS;   // far beyond the horizon, but it is NAMED every time
      const next = mergeRosterRetention(cache, envelope([], [director("d1", REACHABILITY_OFFLINE, { lastSeenAgeSeconds: 3600 })]), at);
      expect(next.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
      cache = next.cache;
    }
  });

  // Inspection 2, finding 2. A row cached while the machine was ONLINE carries machineReachable=true.
  // Re-injecting it untouched produced a card that LOOKED unreachable - dimmed, dated - while still
  // nagging, still showing a waiting clock, and still promising it could speak, because the branch
  // deliberately moved all of those onto the Gateway's stamp.
  it("re-stamps a retained row as unreachable when its machine is offline", () => {
    const online = session("s1", "d1");
    (online as { machineReachable?: boolean }).machineReachable = true;
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([online], [director("d1", REACHABILITY_ONLINE)]), 0);
    const offline = mergeRosterRetention(first.cache, envelope([], [director("d1", REACHABILITY_OFFLINE, { lastSeenAgeSeconds: 300 })]), 1000);
    const row = offline.roster.sessions.find((s) => s.sessionId === "s1");
    expect(row).toBeDefined();
    expect((row as { machineReachable?: boolean }).machineReachable).toBe(false);
  });

  // ...and a WOBBLY machine keeps its true stamp, because its tunnel is up and a command sent to it
  // lands. Without this the re-stamp would silence exactly the machine the mission set out to keep nagging.
  it("leaves a retained row reachable when its machine is only wobbly", () => {
    const online = session("s1", "d1");
    (online as { machineReachable?: boolean }).machineReachable = true;
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([online], [director("d1", REACHABILITY_ONLINE)]), 0);
    const wobbly = mergeRosterRetention(first.cache, envelope([], [director("d1", REACHABILITY_WOBBLY, { lastSeenAgeSeconds: 45 })]), 1000);
    const row = wobbly.roster.sessions.find((s) => s.sessionId === "s1");
    expect((row as { machineReachable?: boolean }).machineReachable).toBe(true);
  });

  // Inspection 3, finding 3. The three "may nag" surfaces - the row, the voice queue, and the app-icon
  // badge - must be counting the SAME sessions. The row and the queue read the merged roster; the badge
  // was counted from the raw envelope, so in a wobbly fallback the card nagged and could enter the voice
  // queue while the badge was explicitly cleared.
  //
  // What this pins is the fact underneath that defect: in this exact state the two candidate sources give
  // DIFFERENT answers, and the merged one is the answer the other two surfaces already give. If the
  // envelope ever became an equally good source, the last assertion would go red and this test would have
  // to be rewritten rather than quietly passing.
  //
  // NOT PROVEN HERE, and it cannot be until `apps/mobile` has a test harness (issue #1171): that the Home
  // page passes the merged list to reconcileBadge. That call site is source-level only.
  it("counts the badge differently from the envelope in a wobbly fallback, and the merged list is the one that agrees", () => {
    const needsYou = session("s1", "d1");
    (needsYou as { machineReachable?: boolean; triageBucket?: string }).machineReachable = true;
    (needsYou as { machineReachable?: boolean; triageBucket?: string }).triageBucket = "needsYou";

    const online = mergeRosterRetention(emptyRetentionCache(), envelope([needsYou], [director("d1", REACHABILITY_ONLINE)]), 0);
    expect(needsYouBadgeCount(online.roster.sessions)).toBe(1);

    // The Gateway names the Director wobbly and serves NO rows for it - the fallback case.
    const wobblyEnvelope = envelope([], [director("d1", REACHABILITY_WOBBLY, { lastSeenAgeSeconds: 45 })]);
    const fallback = mergeRosterRetention(online.cache, wobblyEnvelope, 1000);

    // The row and the voice queue see the retained, still-reachable card...
    expect(fallback.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(needsYouBadgeCount(fallback.roster.sessions)).toBe(1);
    // ...while the envelope the badge used to be counted from sees nothing at all. That gap IS the defect.
    expect(needsYouBadgeCount(wobblyEnvelope.sessions)).toBe(0);
  });

  // The other side of it: an OFFLINE fallback must clear the badge on both sources, so the test above is
  // pinning a real distinction between the two states rather than "merged always counts more".
  it("counts no badge for a retained card whose machine is offline", () => {
    const needsYou = session("s1", "d1");
    (needsYou as { machineReachable?: boolean; triageBucket?: string }).machineReachable = true;
    (needsYou as { machineReachable?: boolean; triageBucket?: string }).triageBucket = "needsYou";

    const online = mergeRosterRetention(emptyRetentionCache(), envelope([needsYou], [director("d1", REACHABILITY_ONLINE)]), 0);
    const fallback = mergeRosterRetention(online.cache, envelope([], [director("d1", REACHABILITY_OFFLINE, { lastSeenAgeSeconds: 900 })]), 1000);

    expect(fallback.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);   // still SHOWN
    expect(needsYouBadgeCount(fallback.roster.sessions)).toBe(0);               // and not NAGGING
  });

  it("removes a session only on an authoritative online answer, never while unreachable", () => {
    // Poll 1: d1 online with s1 and s2.
    let cache: RetentionCache = emptyRetentionCache();
    cache = mergeRosterRetention(cache, envelope([session("s1", "d1"), session("s2", "d1")], [director("d1", REACHABILITY_ONLINE)])).cache;
    // Poll 2: d1 goes offline - both sessions retained (no authority to remove).
    const offline = mergeRosterRetention(cache, envelope([], [director("d1", REACHABILITY_OFFLINE)]));
    expect(offline.roster.sessions.map((s) => s.sessionId).sort()).toEqual(["s1", "s2"]);
    // Poll 3: d1 answers online but now only reports s1 - s2 was genuinely killed, so it drops.
    const back = mergeRosterRetention(offline.cache, envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    expect(back.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(back.roster.marks.size).toBe(0);
    expect(back.cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1"]);
  });

  it("replaces retained cards in place when the machine comes back online", () => {
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    const offline = mergeRosterRetention(first.cache, envelope([], [director("d1", REACHABILITY_OFFLINE)]));
    expect(offline.roster.marks.get("s1")?.reachability).toBe(REACHABILITY_OFFLINE);
    const online = mergeRosterRetention(offline.cache, envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    expect(online.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(online.roster.marks.size).toBe(0);
  });

  it("replaces an older retained agent-tool label with the newer live label", () => {
    const older = session("s1", "d1");
    older.agentToolDisplay = "Pi";
    const first = mergeRosterRetention(
      emptyRetentionCache(),
      envelope([older], [director("d1", REACHABILITY_ONLINE)]),
    );
    const offline = mergeRosterRetention(
      first.cache,
      envelope([], [director("d1", REACHABILITY_OFFLINE)]),
    );
    expect(offline.roster.sessions[0].agentToolDisplay).toBe("Pi");

    const newer = session("s1", "d1");
    newer.agentToolDisplay = "Codex";
    const online = mergeRosterRetention(
      offline.cache,
      envelope([newer], [director("d1", REACHABILITY_ONLINE)]),
    );

    expect(online.roster.sessions[0]).toBe(newer);
    expect(online.roster.sessions[0].agentToolDisplay).toBe("Codex");
    expect(online.cache.byDirector.get("d1")?.[0]).toBe(newer);
  });

  it("never retains a session with no owning Director id (live-only)", () => {
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "")], []));
    expect(first.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(first.cache.byDirector.has("")).toBe(false);
    // Next poll with no sessions: the no-director session is simply gone (not retained).
    const second = mergeRosterRetention(first.cache, envelope([], []));
    expect(second.roster.sessions).toHaveLength(0);
  });

  it("keeps a reachable Director's sessions while a DIFFERENT machine is offline", () => {
    const first = mergeRosterRetention(
      emptyRetentionCache(),
      envelope(
        [session("s1", "d1", "MACHINE_A"), session("s2", "d2", "MACHINE_B")],
        [director("d1", REACHABILITY_ONLINE), director("d2", REACHABILITY_ONLINE)],
      ),
    );
    // d2 goes offline; d1 stays online with a fresh session s3.
    const second = mergeRosterRetention(
      first.cache,
      envelope(
        [session("s1", "d1", "MACHINE_A"), session("s3", "d1", "MACHINE_A")],
        [director("d1", REACHABILITY_ONLINE), director("d2", REACHABILITY_OFFLINE, { machineName: "MACHINE_B" })],
      ),
    );
    const ids = second.roster.sessions.map((s) => s.sessionId).sort();
    expect(ids).toEqual(["s1", "s2", "s3"]);
    expect(second.roster.marks.get("s2")?.reachability).toBe(REACHABILITY_OFFLINE);
    expect(second.roster.marks.get("s2")?.machineName).toBe("MACHINE_B");
    expect(second.roster.marks.has("s1")).toBe(false);
    expect(second.roster.marks.has("s3")).toBe(false);
  });

  it("is idempotent: applying the same envelope twice yields the same cache and roster", () => {
    const env = envelope([session("s1", "d1")], [director("d1", REACHABILITY_WOBBLY, { lastSeenAgeSeconds: 5 })]);
    const once = mergeRosterRetention(emptyRetentionCache(), env);
    const twice = mergeRosterRetention(once.cache, env);
    expect(twice.roster.sessions.map((s) => s.sessionId)).toEqual(once.roster.sessions.map((s) => s.sessionId));
    expect([...twice.cache.byDirector.keys()]).toEqual([...once.cache.byDirector.keys()]);
    expect(twice.cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1"]);
  });

  it("does not mutate the previous cache (pure merge)", () => {
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    const snapshot = [...(first.cache.byDirector.get("d1") ?? [])];
    // A later online answer that drops s1 must not retroactively change the earlier cache object.
    mergeRosterRetention(first.cache, envelope([], [director("d1", REACHABILITY_ONLINE)]));
    expect(first.cache.byDirector.get("d1")).toEqual(snapshot);
  });
});
