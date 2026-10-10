import { describe, expect, it } from "vitest";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { dotColor } from "@devthrottle/client-core/sessions/ordering";
import { needsYouCount, pickerSession } from "./ScheduleEditor";

// Defect 9: the schedule page's Director picker ran an entire parallel triage fold (sessState /
// sessClass), deriving "needs you" from the needsYouSince timestamp instead of the Gateway's stamped
// bucket, and painting a working session GREEN. These tests pin the picker to the one fold.
//
// The picker dot reads the Gateway-stamped effectiveColorHex, so a fixture models the REAL /sessions
// payload: name AND its canonical hex. This helper stamps the hex from the colour name (the same value
// the Gateway stamps), unless a test overrides effectiveColorHex explicitly.
function session(fields: Partial<SessionDto> = {}): SessionDto {
  const name = (fields as { effectiveColor?: string }).effectiveColor;
  return {
    sessionId: "s1",
    createdAt: "2026-07-08T00:00:00Z",
    sortOrder: 0,
    ...(name ? { effectiveColorHex: dotColor(name) } : {}),
    ...fields,
  } as unknown as SessionDto;
}

// A snoozed session that needed you an hour ago, was parked, and still carries the stamp. This is the
// exact shape the old fold got wrong: needsYouSince != null, so it read "needs you" in attention-red
// while every other screen showed it parked.
const snoozed = session({
  sessionId: "snoozed",
  effectiveColor: "grey",
  stateLabel: "Snoozed",
  triageBucket: "onHold",
  needsYouSince: "2026-07-14T09:00:00Z",
  onHold: true,
} as Partial<SessionDto>);

describe("the schedule Director picker renders the Gateway's fold", () => {
  it("does not say a snoozed session needs you", () => {
    // THE DEFECT: `if (s.needsYouSince != null) return "needs you"`.
    expect(pickerSession(snoozed).state).toBe("Snoozed");
    expect(pickerSession(snoozed).dot).toBe("#6B7280"); // parked grey, not attention red
  });

  it("does not count a snoozed session in the NEEDS YOU chip", () => {
    // THE DEFECT: `machineSessions.filter((s) => s.needsYouSince != null).length` - the third fold in
    // this file, feeding the card's "{n} NEEDS YOU" badge.
    const machine = [
      snoozed,
      session({ sessionId: "working", effectiveColor: "blue", stateLabel: "Working", triageBucket: "active" } as Partial<SessionDto>),
      session({
        sessionId: "waiting",
        effectiveColor: "red",
        stateLabel: "Needs you",
        triageBucket: "needsYou",
        needsYouSince: "2026-07-14T10:00:00Z",
      } as Partial<SessionDto>),
    ];

    // Two sessions carry a needsYouSince stamp; only one of them actually needs you.
    expect(needsYouCount(machine)).toBe(1);
  });

  it("paints a WORKING session blue, never green", () => {
    // THE DEFECT, and a law violation: sessClass returned "run" for a working session and
    // .sched-sdot.run painted --sched-green (#22c55e). Green already means "ready - parked at its
    // prompt" in the shared vocabulary, so this screen used one colour to mean the opposite of what it
    // means everywhere else.
    const working = session({
      effectiveColor: "blue",
      stateLabel: "Working",
      triageBucket: "active",
      activityState: "Working",
    } as Partial<SessionDto>);

    expect(pickerSession(working).dot).toBe("#3B82F6");
    expect(pickerSession(working).dot).not.toBe("#22C55E");
  });

  it("paints a working session blue even while it is still flagged on hold", () => {
    // Nothing outranks working - including a hold that has not been cleared yet.
    const woken = session({
      effectiveColor: "blue",
      stateLabel: "Working",
      triageBucket: "active",
      onHold: true,
      needsYouSince: "2026-07-14T09:00:00Z",
    } as Partial<SessionDto>);

    expect(pickerSession(woken).dot).toBe("#3B82F6");
    expect(pickerSession(woken).state).toBe("Working");
  });

  it("fails loudly when the Gateway did not stamp the session", () => {
    expect(() => pickerSession(session({ activityState: "Working" } as Partial<SessionDto>)))
      .toThrow("Gateway /sessions missing");
    expect(() => needsYouCount([session({ effectiveColor: "red" } as Partial<SessionDto>)]))
      .toThrow("Gateway /sessions missing triageBucket");
  });
});
