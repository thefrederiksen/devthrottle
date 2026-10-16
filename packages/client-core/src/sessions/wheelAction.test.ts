import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import { readWheelAction } from "./wheelAction";

// The Terminal tab's wheel button is the GATEWAY'S verdict, read in one place (mission #4070, CLAUDE.md rule 7).
// These pin that the reader renders exactly what the Gateway said, shows nothing when the Gateway says nothing,
// never decides from the connection mode or the wheel holder, and refuses to invent a button it cannot read.

function row(apiMode: unknown, over: Partial<SessionDto> = {}): SessionDto {
  return { sessionId: "s-1", ...over, apiMode } as unknown as SessionDto;
}

function apiModeWith(terminalWheel: unknown): unknown {
  return { kind: "idle", connectionLabel: "API mode", terminalWheel };
}

describe("readWheelAction", () => {
  it("returns null when there is no session row yet", () => {
    expect(readWheelAction(null)).toBeNull();
    expect(readWheelAction(undefined)).toBeNull();
  });

  it("returns null for a row with no API-mode verdict (a terminal session, or an older Gateway)", () => {
    expect(readWheelAction(row(undefined))).toBeNull();
    expect(readWheelAction(row(null))).toBeNull();
  });

  it("returns null when the verdict has no wheel button", () => {
    expect(readWheelAction(row(apiModeWith(undefined)))).toBeNull();
    expect(readWheelAction(row(apiModeWith(null)))).toBeNull();
  });

  it("returns null when the Gateway's button is none", () => {
    expect(readWheelAction(row(apiModeWith({ kind: "none", label: "", enabled: false })))).toBeNull();
  });

  it("reads Take the wheel verbatim", () => {
    const action = readWheelAction(row(apiModeWith({ kind: "take", label: "Take the wheel", enabled: true, disabledReason: null })));
    expect(action).toEqual({ verb: "take", label: "Take the wheel", enabled: true, disabledReason: null });
  });

  it("reads Hand back verbatim", () => {
    const action = readWheelAction(row(apiModeWith({ kind: "handBack", label: "Hand back", enabled: true })));
    expect(action).toEqual({ verb: "handBack", label: "Hand back", enabled: true, disabledReason: null });
  });

  it("carries the Gateway's reason when the button is not enabled", () => {
    const reason = "The terminal is in the middle of a turn. Hand back when it has finished.";
    const action = readWheelAction(row(apiModeWith({ kind: "handBack", label: "Hand back", enabled: false, disabledReason: reason })));
    expect(action).toEqual({ verb: "handBack", label: "Hand back", enabled: false, disabledReason: reason });
  });

  it("drops a reason the Gateway sent on an enabled button", () => {
    const action = readWheelAction(row(apiModeWith({ kind: "take", label: "Take the wheel", enabled: true, disabledReason: "stale" })));
    expect(action?.disabledReason).toBeNull();
  });

  it("does not decide from the connection mode or the wheel holder", () => {
    // A row the raw fields would call API mode held by the protocol, but whose verdict says no button: nothing.
    const verdictSaysNone = row(apiModeWith({ kind: "none" }), { connectionMode: "api", wheelHolder: "protocol" } as Partial<SessionDto>);
    expect(readWheelAction(verdictSaysNone)).toBeNull();
    // And a terminal-mode row is not hidden by the reader: only the verdict is.
    const verdictSaysTake = row(apiModeWith({ kind: "take", label: "Take the wheel", enabled: true }), { connectionMode: "terminal", wheelHolder: "none" } as Partial<SessionDto>);
    expect(readWheelAction(verdictSaysTake)?.verb).toBe("take");
  });

  it("throws on a button kind it does not know, rather than guessing one", () => {
    expect(() => readWheelAction(row(apiModeWith({ kind: "grab", label: "Grab", enabled: true })))).toThrow(/does not know \("grab"\)/);
  });

  it("throws on a button with no words, rather than inventing them", () => {
    expect(() => readWheelAction(row(apiModeWith({ kind: "take", label: "  ", enabled: true })))).toThrow(/no words/);
  });
});
