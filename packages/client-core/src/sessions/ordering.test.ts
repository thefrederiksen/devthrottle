import { describe, expect, it, vi } from "vitest";
import type { SessionDto } from "../api/client";
import { classify, contextLine, deletionReason, dotColor, dotHex, effectiveColor, groupByDirector, inBucket, inWaitingOrder, isDeferredHold, isInCalmBand, isWorking, machineCanBeActedOn, needsYouBadgeCount, pendingDeletion, snoozeCountdown, stateLabel } from "./ordering";

function session(fields: Partial<SessionDto> & { sessionId?: string } = {}): SessionDto {
  return {
    sessionId: "s1",
    createdAt: "2026-07-08T00:00:00Z",
    sortOrder: 0,
    ...fields,
  } as unknown as SessionDto;
}

describe("Gateway-stamped session presentation state", () => {
  it("uses effectiveColor and triageBucket from /sessions", () => {
    const s = session({ effectiveColor: "yellow", triageBucket: "active" });

    expect(effectiveColor(s)).toBe("yellow");
    expect(classify(s)).toBe("active");
  });

  it("fails loudly when effectiveColor is missing", () => {
    expect(() => effectiveColor(session({ triageBucket: "active" })))
      .toThrow("Gateway /sessions missing effectiveColor");
  });

  it("fails loudly when triageBucket is missing", () => {
    expect(() => classify(session({ effectiveColor: "red" })))
      .toThrow("Gateway /sessions missing triageBucket");
  });

  it("fails loudly when triageBucket is invalid", () => {
    expect(() => classify(session({ effectiveColor: "red", triageBucket: "waiting" } as Partial<SessionDto>)))
      .toThrow("invalid triageBucket");
  });

  it("filters by the Gateway-stamped bucket", () => {
    const sessions = [
      session({ sessionId: "a", effectiveColor: "red", triageBucket: "needsYou", sortOrder: 2 }),
      session({ sessionId: "b", effectiveColor: "blue", triageBucket: "active", sortOrder: 1 }),
    ];

    expect(inBucket(sessions, "needsYou").map((s) => s.sessionId)).toEqual(["a"]);
  });

  it("fails loudly on unknown Gateway colors", () => {
    expect(() => dotColor("chartreuse")).toThrow("Unknown Gateway effectiveColor");
  });

  it("renders the 'unknown' and 'grey' Gateway colors as gray", () => {
    // "unknown" is a real Gateway effectiveColor (an indeterminate activity state) and must render, not throw.
    expect(dotColor("unknown")).toBe("#6B7280");
    expect(dotColor("grey")).toBe("#6B7280");
  });

  it("gives snoozed and exited the SAME grey - the dot draws no distinction the Gateway did not make", () => {
    // The Gateway folds BOTH a parked session and an exited one to "grey": it has no snoozed colour and
    // no exited colour. So the two must be the same pixel here. The difference between them is
    // lifecycle, and lifecycle travels on the stamped label and a badge, never on the dot.
    //
    // This is what the desktop rail got wrong: it took the one name "grey" and split it into two hexes
    // (#9CA3AF when the raw onHold flag was set, #6A6A6A otherwise), inventing a distinction the fold
    // never emitted. Same name, two pixels, from a client re-reading a raw field.
    expect(dotColor("grey")).toBe(dotColor("unknown"));
  });

  it("resolves every Gateway colour name to exactly one canonical hex", () => {
    // THE CANONICAL PALETTE (law 7: every device shows the same thing, always). These exact values are
    // the desktop rail's brushes too. A check that compares fold ANSWERS ("red" === "red") cannot see a
    // surface rendering a different red, so the agreement has to be pinned to the PIXEL here.
    expect(dotColor("red")).toBe("#EF4444");
    expect(dotColor("yellow")).toBe("#EAB308");
    expect(dotColor("orange")).toBe("#F97316");
    expect(dotColor("green")).toBe("#22C55E");
    expect(dotColor("blue")).toBe("#3B82F6");
    expect(dotColor("purple")).toBe("#A855F7");
    expect(dotColor("supporting")).toBe("#64748B");
    expect(dotColor("error")).toBe("#B91C1C");
    expect(dotColor("grey")).toBe("#6B7280");
    expect(dotColor("unknown")).toBe("#6B7280");
  });

  it("never paints a working session anything but blue", () => {
    // The law, as a pixel. The schedule picker used to render a working session GREEN (its own local
    // fold returned "run", and .sched-sdot.run painted --sched-green) - while green means "ready,
    // parked at its prompt" in the shared vocabulary. Same colour, opposite meaning.
    expect(dotColor(effectiveColor(session({ effectiveColor: "blue" })))).toBe("#3B82F6");
    expect(dotColor("blue")).not.toBe(dotColor("green"));
  });

  it("stateLabel reads the Gateway-stamped label", () => {
    // stateLabel IS in the generated schema now that it has been regenerated from the C# DTOs, so the
    // cast is no longer load-bearing - kept only because `session()` takes Partial<SessionDto> and the
    // accessor still reads through the GatewayStampedSession cast.
    expect(stateLabel(session({ stateLabel: "Needs you" } as Partial<SessionDto>))).toBe("Needs you");
  });

  it("stateLabel fails loudly when missing", () => {
    expect(() => stateLabel(session({ effectiveColor: "red" })))
      .toThrow("Gateway /sessions missing stateLabel");
  });

  it("isDeferredHold reads the Gateway hold tri-state, which onHold cannot see", () => {
    // A deferred snooze reads onHold=false, so only holdState distinguishes it from "not snoozed".
    expect(isDeferredHold(session({ holdState: "DeferredHold", onHold: false } as Partial<SessionDto>))).toBe(true);
    expect(isDeferredHold(session({ holdState: "Held" } as Partial<SessionDto>))).toBe(false);
    expect(isDeferredHold(session({ holdState: "None" } as Partial<SessionDto>))).toBe(false);
    expect(isDeferredHold(session({}))).toBe(false);
  });

  it("isWorking is exactly the Gateway's blue - nothing else gets a vote", () => {
    // THE LAW (2026-07-14): a working session is BLUE, always. So blue IS working, and the client
    // asks the Gateway and nothing else.
    //
    // Two assertions here used to encode the OLD law and were deliberately removed:
    //   - `isWorking({ effectiveColor: "yellow", activityState: "Working" })` -> true, on the theory
    //     that a working session's colour might not have "settled" yet. It cannot: the Gateway's fold
    //     returns blue for ANY working session, so yellow-while-working is a DTO it can never emit.
    //   - `isWorking({ effectiveColor: "blue", onHold: true })` -> false, commented "on-hold is never
    //     working, even if blue". That is the defect itself, written down as a requirement: it is the
    //     reason a snoozed session that woke up and started working still read as parked.

    // Blue effectiveColor is working, regardless of the raw Director statusColor.
    expect(isWorking(session({ effectiveColor: "blue", statusColor: "red" }))).toBe(true);
    // Red and not working -> not working.
    expect(isWorking(session({ effectiveColor: "red", activityState: "WaitingForInput" }))).toBe(false);
    // Blue AND snoozed is working: the Gateway stamped blue, so the session is running. Snooze is a
    // statement about a session that has stopped; it cannot un-work a running one.
    expect(isWorking(session({ effectiveColor: "blue", onHold: true }))).toBe(true);
  });

  it("contextLine renders the Gateway's stamped label instead of re-deriving one", () => {
    // The row's words come from the same fold as its dot, so they cannot contradict it.
    // stateLabel is Gateway-stamped and not in the generated schema, so each literal needs the same
    // cast the stateLabel tests above use.
    expect(contextLine(session({ effectiveColor: "blue", stateLabel: "Working", onHold: true } as Partial<SessionDto>)))
      .toBe("Working");
    expect(contextLine(session({ effectiveColor: "grey", stateLabel: "Snoozed", onHold: true } as Partial<SessionDto>)))
      .toBe("Snoozed");
    // A working session with dictation in flight reads "Working", not "Transcribing...": the local
    // ladder that produced a blue dot beside the word "Snoozed" is gone.
    expect(contextLine(session({ effectiveColor: "blue", stateLabel: "Working", transcribing: true } as Partial<SessionDto>)))
      .toBe("Working");
  });
});

describe("dotHex - the session dot renders the Gateway-stamped pixel", () => {
  it("paints the stamped effectiveColorHex verbatim", () => {
    // The Gateway resolves the name through the ONE canonical map and stamps the hex; the dot paints it.
    expect(dotHex(session({ effectiveColor: "red", effectiveColorHex: "#EF4444" } as Partial<SessionDto>))).toBe("#EF4444");
    expect(dotHex(session({ effectiveColor: "blue", effectiveColorHex: "#3B82F6" } as Partial<SessionDto>))).toBe("#3B82F6");
  });

  it("paints the magenta sentinel and logs when the stamp is missing (an old Gateway, mixed-version deploy)", () => {
    // FAIL LOUD, NEVER GUESS: no hex on the wire must NOT fall back to the local COLORS table. Magenta is
    // not a state and cannot be mistaken for one; grey would read as "parked", which would be a quiet lie.
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      expect(dotHex(session({ effectiveColor: "red" }))).toBe("#FF00FF");
      expect(spy).toHaveBeenCalledOnce();
      // It must NOT paint the real red the name would have mapped to - that is the guessed colour we refuse.
      expect(dotHex(session({ effectiveColor: "red" }))).not.toBe(dotColor("red"));
    } finally {
      spy.mockRestore();
    }
  });

  it("paints the magenta sentinel and logs when the stamp is not a hex", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      expect(dotHex(session({ effectiveColor: "red", effectiveColorHex: "reddish" } as Partial<SessionDto>))).toBe("#FF00FF");
      expect(dotHex(session({ effectiveColor: "red", effectiveColorHex: "" } as Partial<SessionDto>))).toBe("#FF00FF");
      expect(spy).toHaveBeenCalled();
    } finally {
      spy.mockRestore();
    }
  });

  it("contains a bad row instead of crashing when the stamp is a non-string JSON value", () => {
    // Optional chaining does NOT guard a number/object/array - .trim would throw a TypeError from the
    // render path and take down the whole roster. dotHex must type-guard and paint the sentinel instead.
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      expect(() => dotHex(session({ effectiveColor: "red", effectiveColorHex: 16711680 } as unknown as Partial<SessionDto>))).not.toThrow();
      expect(dotHex(session({ effectiveColor: "red", effectiveColorHex: 16711680 } as unknown as Partial<SessionDto>))).toBe("#FF00FF");
      expect(dotHex(session({ effectiveColor: "red", effectiveColorHex: { r: 255 } } as unknown as Partial<SessionDto>))).toBe("#FF00FF");
      expect(dotHex(session({ effectiveColor: "red", effectiveColorHex: ["#EF4444"] } as unknown as Partial<SessionDto>))).toBe("#FF00FF");
      expect(spy).toHaveBeenCalled();
    } finally {
      spy.mockRestore();
    }
  });
});

describe("needs-you waiting-line order", () => {
  const needsYou = (id: string, needsYouSince?: string, extra: Partial<SessionDto> = {}) =>
    session({ sessionId: id, effectiveColor: "red", triageBucket: "needsYou", needsYouSince, ...extra } as Partial<SessionDto>);

  it("puts the longest wait at the top and the newest wait at the bottom", () => {
    const sessions = [
      needsYou("new", "2026-07-09T12:00:00Z"),
      needsYou("oldest", "2026-07-09T09:00:00Z"),
      needsYou("middle", "2026-07-09T10:30:00Z"),
    ];

    expect(inWaitingOrder(sessions).map((s) => s.sessionId)).toEqual(["oldest", "middle", "new"]);
  });

  it("keeps only needs-you sessions and ignores manual desktop sortOrder", () => {
    const sessions = [
      needsYou("waited-most", "2026-07-09T08:00:00Z", { sortOrder: 99 }),
      session({ sessionId: "active", effectiveColor: "blue", triageBucket: "active" }),
      needsYou("waited-least", "2026-07-09T11:00:00Z", { sortOrder: 1 }),
    ];

    expect(inWaitingOrder(sessions).map((s) => s.sessionId)).toEqual(["waited-most", "waited-least"]);
  });

  it("sorts a session with no wait stamp to the bottom", () => {
    const sessions = [
      needsYou("no-stamp", undefined),
      needsYou("has-stamp", "2026-07-09T09:00:00Z"),
    ];

    expect(inWaitingOrder(sessions).map((s) => s.sessionId)).toEqual(["has-stamp", "no-stamp"]);
  });
});

describe("the calm band after the waiting line", () => {
  const needsYou = (id: string, needsYouSince: string) =>
    session({ sessionId: id, effectiveColor: "red", triageBucket: "needsYou", needsYouSince } as Partial<SessionDto>);
  const calm = (id: string, effectiveColor: string, createdAt: string) =>
    session({ sessionId: id, effectiveColor, triageBucket: "active", verdictState: "judged", createdAt } as Partial<SessionDto>);

  it("lists the green and purple judged rows after every red row", () => {
    const sessions = [
      calm("done", "green", "2026-07-09T08:00:00Z"),
      needsYou("red-b", "2026-07-09T10:00:00Z"),
      calm("carrying", "purple", "2026-07-09T07:00:00Z"),
      needsYou("red-a", "2026-07-09T09:00:00Z"),
    ];

    expect(inWaitingOrder(sessions).map((s) => s.sessionId)).toEqual(["red-a", "red-b", "carrying", "done"]);
  });

  it("leaves out a green row with no verdict, a judged row in any other colour, a row still being read, and a snoozed row", () => {
    const sessions = [
      session({ sessionId: "fresh", effectiveColor: "green", triageBucket: "active" }),
      session({ sessionId: "judged-yellow", effectiveColor: "yellow", triageBucket: "active", verdictState: "judged" } as Partial<SessionDto>),
      session({ sessionId: "reading", effectiveColor: "green", triageBucket: "active", verdictState: "reading" } as Partial<SessionDto>),
      session({ sessionId: "snoozed", effectiveColor: "grey", triageBucket: "onHold", verdictState: "judged" } as Partial<SessionDto>),
    ];

    expect(sessions.filter(isInCalmBand)).toEqual([]);
    expect(inWaitingOrder(sessions)).toEqual([]);
  });

  it("does not count a calm row in the needs-you badge", () => {
    const sessions = [calm("done", "green", "2026-07-09T08:00:00Z"), needsYou("red", "2026-07-09T09:00:00Z")];

    expect(needsYouBadgeCount(sessions)).toBe(1);
  });
});

describe("groupByDirector", () => {
  it("groups sessions by their owning Director and labels each with its port", () => {
    const sessions = [
      session({ sessionId: "a", directorId: "d1", machineName: "SOREN_NORTH", sortOrder: 1 }),
      session({ sessionId: "b", directorId: "d2", machineName: "Sorens-Mac-mini", sortOrder: 0 }),
      session({ sessionId: "c", directorId: "d1", machineName: "SOREN_NORTH", sortOrder: 0 }),
    ];
    const ports = new Map([
      ["d1", "7880"],
      ["d2", "7880"],
    ]);

    const groups = groupByDirector(sessions, ports);

    expect(groups.map((g) => `${g.machineName}:${g.port}`)).toEqual([
      "SOREN_NORTH:7880",
      "Sorens-Mac-mini:7880",
    ]);
    // Within the first Director, sessions come back in desktop (sortOrder) order.
    expect(groups[0].sessions.map((s) => s.sessionId)).toEqual(["c", "a"]);
  });

  it("keeps two Directors on the same machine as separate groups, ordered by port", () => {
    const sessions = [
      session({ sessionId: "a", directorId: "hi", machineName: "SOREN_NORTH" }),
      session({ sessionId: "b", directorId: "lo", machineName: "SOREN_NORTH" }),
    ];
    const ports = new Map([
      ["hi", "7885"],
      ["lo", "7880"],
    ]);

    const groups = groupByDirector(sessions, ports);

    expect(groups.map((g) => g.port)).toEqual(["7880", "7885"]);
  });

  it("degrades to the bare machine name when the port is unknown", () => {
    const sessions = [session({ sessionId: "a", directorId: "d1", machineName: "SOREN_NORTH" })];

    const groups = groupByDirector(sessions, new Map());

    expect(groups[0].port).toBe("");
    expect(groups[0].machineName).toBe("SOREN_NORTH");
  });
});

describe("hold time (snoozeCountdown) - the Gateway-owned snooze clock", () => {
  const now = Date.parse("2026-07-16T12:00:00Z");

  it("formats the remaining time to match the desktop rail's wording", () => {
    const at = (iso: string) => snoozeCountdown(session({ snoozeUntil: iso } as Partial<SessionDto>), now);
    expect(at("2026-07-16T15:48:00Z")).toBe("wakes in 3h 48m"); // 3h 48m
    expect(at("2026-07-16T15:00:00Z")).toBe("wakes in 3h");      // exact hours -> no minutes
    expect(at("2026-07-16T12:42:00Z")).toBe("wakes in 42m");     // under an hour
    expect(at("2026-07-16T12:00:30Z")).toBe("wakes in <1m");     // under a minute
    expect(at("2026-07-16T11:59:00Z")).toBe("waking up");        // already past due
  });

  it("returns null when there is no running snooze clock", () => {
    // Not snoozed, or a deferred snooze that has not landed (no deadline yet).
    expect(snoozeCountdown(session(), now)).toBeNull();
    expect(snoozeCountdown(session({ snoozeUntil: null } as Partial<SessionDto>), now)).toBeNull();
    expect(snoozeCountdown(session({ snoozeUntil: "not-a-date" } as Partial<SessionDto>), now)).toBeNull();
  });
});

describe("winding-down (pendingDeletion) - a badge, never a colour", () => {
  it("reads the Gateway flag and reason, defaulting to false / null", () => {
    expect(pendingDeletion(session())).toBe(false);
    expect(deletionReason(session())).toBeNull();

    const flagged = session({ pendingDeletion: true, deletionReason: "jobs-auto: nothing to report" } as Partial<SessionDto>);
    expect(pendingDeletion(flagged)).toBe(true);
    expect(deletionReason(flagged)).toBe("jobs-auto: nothing to report");
  });
});

describe("the needs-you BADGE count - visible is not the same question as nag", () => {
  const needsYou = (sessionId: string, fields: Partial<SessionDto> = {}): SessionDto =>
    session({ sessionId, effectiveColor: "red", triageBucket: "needsYou", ...fields });

  it("counts a needs-you session on a reachable machine", () => {
    expect(needsYouBadgeCount([needsYou("a", { machineReachable: true })])).toBe(1);
  });

  it("does NOT count a needs-you session whose machine the Gateway says is unreachable", () => {
    // The ruling: a laptop asleep overnight with red sessions must not leave the badge lit until morning.
    // The rows stay on the roster - this is only the badge.
    const sessions = [
      needsYou("awake", { machineReachable: true }),
      needsYou("asleep-1", { machineReachable: false }),
      needsYou("asleep-2", { machineReachable: false }),
    ];
    expect(needsYouBadgeCount(sessions)).toBe(1);
  });

  it("goes to zero when every waiting session is on an unreachable machine", () => {
    expect(needsYouBadgeCount([needsYou("a", { machineReachable: false })])).toBe(0);
  });

  it("COUNTS a session that carries no reachability stamp at all (older Gateway)", () => {
    // Back-compat, and it is deliberate: an unstamped field means "I was not told", not "unreachable".
    // Reading the absence as unreachable would switch the badge off for everyone on a mixed-version
    // deploy, and a missing nag is invisible - the owner would never learn the phone had gone quiet.
    expect(needsYouBadgeCount([needsYou("a")])).toBe(1);
  });

  it("COUNTS a session stamped null (a Director-local response, where the question is meaningless)", () => {
    expect(needsYouBadgeCount([needsYou("a", { machineReachable: null } as Partial<SessionDto>)])).toBe(1);
  });

  it("ignores sessions in other buckets, reachable or not", () => {
    const sessions = [
      needsYou("a", { machineReachable: true }),
      session({ sessionId: "b", effectiveColor: "blue", triageBucket: "active", machineReachable: true }),
      session({ sessionId: "c", effectiveColor: "grey", triageBucket: "onHold", machineReachable: false }),
    ];
    expect(needsYouBadgeCount(sessions)).toBe(1);
  });

  it("machineCanBeActedOn is the single rule the count is built from", () => {
    expect(machineCanBeActedOn(session({ machineReachable: true }))).toBe(true);
    expect(machineCanBeActedOn(session())).toBe(true);
    expect(machineCanBeActedOn(session({ machineReachable: false }))).toBe(false);
  });
});
