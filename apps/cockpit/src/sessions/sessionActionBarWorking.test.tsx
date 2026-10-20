// @vitest-environment jsdom
import { describe, it, expect, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { SessionActionBar } from "./SessionActionBar";

// THE STRIP SAYS "WORKING" ONLY WHILE THE GATEWAY SAYS THE SESSION IS WORKING (API mode phase 6 QA, finding 1).
// The idle row below is the one the Gateway served for an API-mode session with no turn yet in the phase 6 dry run
// (docs/missions/api-mode-pi-2026-10-16/proofs/phase-6/linux-dry-run-20261018-122023/wire.jsonl): green, Ready, and in
// the "active" bucket - which holds every session that is not red, not only the working ones.

const SID = "7d2f9c10-0000-4000-8000-000000000043";
const CAPABILITIES = ["ClearContext", "Cancel", "ContextUsage", "ModelReport", "CompactContext"];

function row(fields: Record<string, unknown>): SessionDto {
  return { sessionId: SID, connectionMode: "api", ...fields } as unknown as SessionDto;
}

afterEach(() => cleanup());

describe("SessionActionBar", () => {
  it("SessionActionBar_IdleApiModeSessionReadyInTheActiveBucket_DoesNotSayWorking", () => {
    // Arrange
    const idle = row({ activityState: "WaitingForInput", effectiveColor: "green", stateLabel: "Ready", triageBucket: "active" });

    // Act
    render(<SessionActionBar sessionId={SID} capabilities={CAPABILITIES} session={idle} />);

    // Assert: no "Working". Stop stays, because it is drawn whenever the driver declares Cancel (the owner's ruling
    // in SessionActionBar.tsx: a Stop hidden mid-turn costs more than a Stop shown while idle).
    expect(screen.queryByText("Working")).toBeNull();
    expect(screen.getByRole("button", { name: /Stop/ })).toBeTruthy();
  });

  it("SessionActionBar_WorkingSession_SaysWorking", () => {
    const working = row({ activityState: "Working", effectiveColor: "blue", stateLabel: "Working", triageBucket: "active" });

    render(<SessionActionBar sessionId={SID} capabilities={CAPABILITIES} session={working} />);

    expect(screen.getByText("Working")).toBeTruthy();
  });

  it("SessionActionBar_RowWithNoColourStamped_StaysQuietAndDoesNotThrow", () => {
    const unstamped = row({ activityState: "Working", triageBucket: "active" });

    render(<SessionActionBar sessionId={SID} capabilities={CAPABILITIES} session={unstamped} />);

    expect(screen.queryByText("Working")).toBeNull();
    expect(screen.getByRole("button", { name: /Stop/ })).toBeTruthy();
  });
});
