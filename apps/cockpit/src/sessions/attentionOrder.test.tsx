// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// "Attention first" -> the Needs you group is a WAITING LINE, not the drag-to-reorder desktop order.
// The session that has been asking for you the longest is at the TOP; one that only just started
// needing you joins at the BOTTOM. That is the same order the phone roster uses, and it is what makes
// working the list from the top down first-in, first-handled.
//
// The distinguishing test is the second one: the desktop sortOrder is set to the EXACT OPPOSITE of the
// wait order, so a roster that fell back to the old inBucket ordering renders the reverse and fails.

vi.mock("@devthrottle/client-core/api/client", () => ({
  // The restart-requests panel polls inside this roster and reaches for gatewayErrorMessage when a
  // read fails. Without it here, every poll threw an unhandled rejection - the tests still passed, but
  // the run reported errors and exited non-zero, which is how a real failure would come to be ignored.
  gatewayErrorMessage: (err: unknown) => String(err),
  setVoiceModeAllSessions: vi.fn(async () => {}),
}));

vi.mock("./SessionMenu", () => ({
  SessionMenu: () => null,
}));

import { SessionRoster } from "./SessionRoster";

function needsYou(sessionId: string, needsYouSince: string, sortOrder: number): SessionDto {
  return {
    sessionId,
    name: sessionId,
    createdAt: "2026-07-26T08:00:00Z",
    sortOrder,
    directorId: "d1",
    machineName: "SOREN_NORTH",
    repoPath: "D:\\ReposFred\\devthrottle",
    effectiveColor: "red",
    effectiveColorHex: "#EF4444",
    stateLabel: "Needs you",
    triageBucket: "needsYou",
    needsYouSince,
  } as unknown as SessionDto;
}

function renderAttention(sessions: SessionDto[]) {
  render(
    <MemoryRouter initialEntries={["/sessions"]}>
      <SessionRoster
        sessions={sessions}
        directors={[]}
        portByDirector={new Map()}
        selectedId={undefined}
        view="attention"
        onView={() => {}}
        error={null}
        onNewSession={() => {}}
      />
    </MemoryRouter>,
  );
}

// The rendered order of the Needs you group, read off the DOM rather than off the sort function, so
// the test fails if the roster stops passing the waiting order through to the rows.
function needsYouRowOrder(): string[] {
  const bucket = Array.from(document.querySelectorAll(".roster-bucket")).find(
    (b) => b.querySelector(".roster-bucket-head.needs") !== null,
  );
  if (bucket === undefined) throw new Error("the attention view rendered no 'Needs you' bucket");
  return Array.from(bucket.querySelectorAll(".roster-name-text")).map((el) => el.textContent ?? "");
}

describe("Attention first - the Needs you waiting line", () => {
  beforeEach(() => {
    // This project runs vitest without globals, so testing-library's automatic cleanup is not
    // registered - without this, each render leaks into the next test's document.
    cleanup();
    vi.clearAllMocks();
  });

  it("puts the longest wait at the top and the newest at the bottom", () => {
    renderAttention([
      needsYou("newest", "2026-07-26T09:50:00Z", 1),
      needsYou("oldest", "2026-07-26T08:10:00Z", 2),
      needsYou("middle", "2026-07-26T09:00:00Z", 3),
    ]);

    expect(needsYouRowOrder()).toEqual(["oldest", "middle", "newest"]);
  });

  it("ignores the drag-to-reorder desktop order inside the group", () => {
    // sortOrder is the exact reverse of the wait order: the old behaviour renders newest-first.
    renderAttention([
      needsYou("oldest", "2026-07-26T08:10:00Z", 30),
      needsYou("middle", "2026-07-26T09:00:00Z", 20),
      needsYou("newest", "2026-07-26T09:50:00Z", 10),
    ]);

    expect(needsYouRowOrder()).toEqual(["oldest", "middle", "newest"]);
  });

  it("sorts a session the Gateway never stamped with a wait time to the bottom", () => {
    const unstamped = needsYou("unstamped", "2026-07-26T08:00:00Z", 1);
    delete (unstamped as unknown as Record<string, unknown>).needsYouSince;

    renderAttention([unstamped, needsYou("waiting", "2026-07-26T09:50:00Z", 2)]);

    expect(needsYouRowOrder()).toEqual(["waiting", "unstamped"]);
  });
});
