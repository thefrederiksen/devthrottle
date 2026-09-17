// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { useState } from "react";
import { render, screen, waitFor, cleanup, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import type { MissionDto } from "@devthrottle/client-core/missions/missions";

// The WHY now travels ON the mission record, keyed by its id, and is written back through
// setMissionWhy (PATCH /missions/{id}). It used to live in a separate store keyed by the mission's
// lower-cased NAME - and when the grouping moved to ids, the card kept looking the WHY up by the group's
// key, which had become an id, so every WHY the owner had written silently vanished from the board.
// Nothing threw; the text was simply gone. These tests render the real board so that cannot recur.
vi.mock("@devthrottle/client-core/missions/missions", async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  setMissionWhy: vi.fn(),
}));

import { setMissionWhy } from "@devthrottle/client-core/missions/missions";
import { MissionsBoard } from "./MissionsBoard";

const M_RELEASE: MissionDto = { missionId: "45e28f0c", missionName: "Release 2.0.1" };

// The Gateway stamps the color and the label; the shared ordering module THROWS rather than re-deriving
// them (the client-is-dumb rule), so a fixture has to carry them exactly as the roster would.
function session(fields: Record<string, unknown>): SessionDto {
  return {
    effectiveColor: "blue",
    stateLabel: "Working",
    dotHex: "#4a9eff",
    ...fields,
  } as unknown as SessionDto;
}

const ARCHITECT = session({
  name: "Release 2.0.1 - Architect",
  number: 124,
  sessionId: "s-124",
  missionId: "45e28f0c",
  sessionRole: "Architect",
});

const WORKER = session({
  name: "fix: 2481 delete bias path",
  number: 113,
  sessionId: "s-113",
  missionId: "45e28f0c",
  sessionRole: "Worker",
});

function renderBoard(props: Parameters<typeof MissionsBoard>[0]) {
  return render(
    <MemoryRouter>
      <MissionsBoard {...props} />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.mocked(setMissionWhy).mockReset();
});

afterEach(() => {
  cleanup();
});

describe("MissionsBoard", () => {
  it("shows the mission's WHY, read off the mission record", async () => {
    renderBoard({
      sessions: [ARCHITECT],
      missions: [{ ...M_RELEASE, why: "So we can get the Video Competition started" }],
    });

    expect(screen.getByText("So we can get the Video Competition started")).toBeTruthy();
    // ...and therefore NOT the "no why set" flag.
    expect(screen.queryByText(/No why set/i)).toBeNull();
  });

  // The regression this file exists for: the WHY must be keyed to the mission by ID, so a mission whose
  // NAME has changed still shows it. Under the old name-keyed store this silently rendered the flag.
  it("keeps the WHY when the mission's name is nothing like its old one", async () => {
    renderBoard({
      sessions: [],
      missions: [{ missionId: "45e28f0c", missionName: "Renamed To Something Else", why: "the reason" }],
    });

    expect(screen.getByText("the reason")).toBeTruthy();
    expect(screen.queryByText(/No why set/i)).toBeNull();
  });

  it("shows the loud flag when a mission genuinely has no WHY", async () => {
    renderBoard({ sessions: [ARCHITECT], missions: [M_RELEASE] });
    await waitFor(() => expect(screen.getByText(/No why set/i)).toBeTruthy());
  });

  it("lists every attached session by NAME, with its role as a badge", async () => {
    renderBoard({ sessions: [ARCHITECT, WORKER], missions: [M_RELEASE] });

    // The name is what tells one worker from another - the role alone would repeat down the card.
    expect(screen.getByText("fix: 2481 delete bias path")).toBeTruthy();
    expect(screen.getByText("Release 2.0.1 - Architect")).toBeTruthy();
    expect(screen.getByText("Architect")).toBeTruthy();
    expect(screen.getByText("Worker")).toBeTruthy();
    expect(screen.getByText("2 sessions")).toBeTruthy();
  });

  // The owner's screenshot, 16 September: session 144 read "Proof Worker running safety checks" in purple on
  // the right and "needs you" under its name. The line under the name was the Director's pre-Wingman reason.
  it("never shows the Director's 'needs you' under a row the Gateway says is carrying on or snoozed", async () => {
    renderBoard({
      sessions: [
        session({
          name: "cc-worktrees - Architect",
          number: 144,
          sessionId: "s-144",
          missionId: "45e28f0c",
          effectiveColor: "purple",
          stateLabel: "Proof Worker running safety checks",
          lastStatusReason: "needs you",
        }),
        session({
          name: "cc-worktrees - Manager",
          number: 133,
          sessionId: "s-133",
          missionId: "45e28f0c",
          effectiveColor: "grey",
          stateLabel: "Snoozed",
          lastStatusReason: "needs you",
        }),
      ],
      missions: [M_RELEASE],
    });

    expect(screen.getByText("Proof Worker running safety checks")).toBeTruthy();
    expect(screen.getByText("Snoozed")).toBeTruthy();
    expect(screen.queryByText(/needs you/i)).toBeNull();
  });

  it("renders a mission nobody is on yet", async () => {
    renderBoard({ sessions: [], missions: [M_RELEASE] });

    expect(screen.getByText("Release 2.0.1")).toBeTruthy();
    expect(screen.getByText("0 sessions")).toBeTruthy();
    expect(screen.getByText("no sessions yet")).toBeTruthy();
  });

  it("marks a mission the Gateway's mission list does not contain", async () => {
    renderBoard({
      sessions: [
        session({
          name: "worker one",
          number: 140,
          sessionId: "s-140",
          missionId: "ghost",
          missionName: "BPM Studio QA cleanup",
          sessionRole: "Worker",
        }),
      ],
      missions: [M_RELEASE],
    });

    expect(screen.getByText("BPM Studio QA cleanup")).toBeTruthy();
    expect(screen.getByText(/not in the mission list/i)).toBeTruthy();
  });

  it("hides empty missions when asked - and SAYS how many, with a way back", async () => {
    const onShowEmpty = vi.fn();
    renderBoard({
      sessions: [ARCHITECT],
      missions: [M_RELEASE, { missionId: "m-dead", missionName: "Remove the network port" }],
      hideEmpty: true,
      onShowEmpty,
    });

    // The staffed mission is drawn; the empty one is not.
    expect(screen.getByText("Release 2.0.1")).toBeTruthy();
    expect(screen.queryByText("Remove the network port")).toBeNull();

    // ...but the board never hides silently: the count is on screen and it is the way back.
    const note = screen.getByText(/1 mission with no sessions is hidden/i);
    expect(note).toBeTruthy();
    note.click();
    expect(onShowEmpty).toHaveBeenCalled();
  });

  it("draws every mission when not hiding, and shows no hidden-count note", async () => {
    renderBoard({
      sessions: [ARCHITECT],
      missions: [M_RELEASE, { missionId: "m-dead", missionName: "Remove the network port" }],
      hideEmpty: false,
    });

    expect(screen.getByText("Release 2.0.1")).toBeTruthy();
    expect(screen.getByText("Remove the network port")).toBeTruthy();
    expect(screen.queryByText(/hidden/i)).toBeNull();
  });

  it("pluralises the hidden-count note", async () => {
    renderBoard({
      sessions: [],
      missions: [
        { missionId: "m1", missionName: "One" },
        { missionId: "m2", missionName: "Two" },
      ],
      hideEmpty: true,
      onShowEmpty: vi.fn(),
    });

    expect(screen.getByText(/2 missions with no sessions are hidden/i)).toBeTruthy();
  });

  // The callback firing is not the promise; the card COMING BACK is. This drives the real round trip
  // through a stateful parent, so a wiring mistake between the notice and the board cannot pass.
  it("brings the hidden missions back when the note is clicked", async () => {
    function Harness() {
      const [hide, setHide] = useState(true);
      const props = hide
        ? ({ hideEmpty: true, onShowEmpty: () => setHide(false) } as const)
        : ({ hideEmpty: false } as const);
      return (
        <MissionsBoard
          sessions={[ARCHITECT]}
          missions={[M_RELEASE, { missionId: "m-dead", missionName: "Remove the network port" }]}
          {...props}
        />
      );
    }

    render(
      <MemoryRouter>
        <Harness />
      </MemoryRouter>,
    );

    expect(screen.queryByText("Remove the network port")).toBeNull();
    fireEvent.click(screen.getByText(/1 mission with no sessions is hidden/i));

    expect(screen.getByText("Remove the network port")).toBeTruthy();
    expect(screen.queryByText(/hidden/i)).toBeNull();
  });

  it("says so loudly when the mission list could not be loaded", async () => {
    renderBoard({ sessions: [], missions: [], error: "Gateway said 503." });

    expect(screen.getByText(/could not be loaded/i)).toBeTruthy();
    expect(screen.getByText(/Gateway said 503\./)).toBeTruthy();
  });
});
