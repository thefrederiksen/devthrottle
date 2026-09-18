// @vitest-environment jsdom
// THE WINGMAN TAB AS THE OWNER SEES IT: one message box, and nothing under his cursor that he did not come here for.
//
// The screen that shipped had a text box and a Send inside the card, and a hand's width below it the page's own
// composer with a second box, a second Send, Speak, Queue and Attach - and the one that was NOT beside the question
// was the loud blue one. Directly above that sat Stop, Interrupt, Compact, Clear context and History, on every
// state, including the ones where none of them means anything. The design kept destructive controls off Now on
// purpose and they had arrived by the back door.
//
// So this drives the REAL session page with the REAL tab, the REAL Now view and the REAL composer, and proves what
// is on the screen rather than what each piece does on its own: on the Wingman tab there is exactly one box to type
// in, it is inside the card, it carries the Gateway's words and it still has Speak and Attach - and the driver bar
// and the page composer are gone. Every other tab is untouched.
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import type { WingmanNow } from "@devthrottle/client-core/sessions/wingmanNowRead";

// The regions that cannot run in jsdom (a terminal engine, a live socket, a microphone) and are not the subject.
vi.mock("../panes/TerminalPane", () => ({ TerminalPane: () => <div /> }));
vi.mock("./SessionMenu", () => ({ SessionMenu: () => null }));
vi.mock("./ChatTab", () => ({ ChatTab: () => <div /> }));
vi.mock("./VoiceTab", () => ({ VoiceTab: () => <div /> }));
vi.mock("./SourceControlTab", () => ({ SourceControlTab: () => <div /> }));
vi.mock("./ScreenshotsPanel", () => ({ ScreenshotsPanel: () => <div /> }));

import { SessionDetail } from "./SessionDetail";
import { StopSessionProvider } from "./StopSessionProvider";

const SID = "5b8e1a40-0000-4000-8000-000000000090";
const PLACEHOLDER = "Or answer in your own words - for example: allow the merge, tag straight after it.";

const NOW: WingmanNow = {
  sessionId: SID,
  state: "needs-you",
  pillText: "Needs you",
  pillColour: "red",
  pillColourHex: "#ef4444",
  unsure: false,
  unsureTag: null,
  unsureLine: null,
  when: { lead: "Stopped at", atUtc: "2026-09-18T07:12:00Z", showAgo: true, elapsedOnly: false },
  showWhyColour: false,
  headline: "Commit and deploy the fixes?",
  story: "It has made the fixes and is waiting for your permission to commit and deploy.",
  agentSaid: null,
  wholeReply: null,
  needs: {
    heading: "What it needs from you",
    recommends: null,
    question: null,
    options: [
      { index: 0, key: "Commit and deploy", note: "It commits the fixes and deploys them.", recommended: true },
      { index: 1, key: "Do not commit", note: "Nothing is committed or deployed.", recommended: false },
    ],
  },
  canAnswerByOption: true,
  verdictId: "v-1",
  replyPlaceholder: PLACEHOLDER,
  calmCard: null,
  carryingOnDeadline: null,
  lastWords: null,
  failedHeadline: null,
  failedStory: null,
  lastGood: null,
  switchedOff: null,
  lastAsked: null,
  answered: null,
  lastStop: null,
  nextNeedsYou: null,
  voice: { kind: "none", label: null, afterTurnOnText: null },
};

/** The Gateway, answering the reads this page makes. Anything unasked-for is a failure, not an empty list. */
function fakeGateway(queue: unknown[] = []) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string) => {
      const body = url.includes("wingman-now")
        ? NOW
        : url.includes("wingman-stops")
          ? { stops: [], groups: [] }
          : url.includes("/queue")
            ? { items: queue }
            : null;
      if (body === null) return new Response("{}", { status: 404 });
      return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
    }),
  );
}

const SESSION = {
  sessionId: SID,
  name: "Cube Data and Projects - Architect",
  onHold: false,
  driverCapabilities: ["Cancel", "Interrupt", "CompactContext", "ClearContext", "History"],
} as unknown as SessionDto;

function Shell() {
  return <Outlet context={{ sessions: [SESSION] }} />;
}

function openPage() {
  return render(
    <MemoryRouter initialEntries={[`/sessions/${SID}`]}>
      <StopSessionProvider>
        <Routes>
          <Route element={<Shell />}>
            <Route path="/sessions/:sessionId" element={<SessionDetail />} />
          </Route>
        </Routes>
      </StopSessionProvider>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the Wingman tab's own screen", () => {
  it("has ONE message box, inside the card, in the Gateway's words, still carrying Speak and Attach", async () => {
    fakeGateway();
    const { container } = openPage();

    // The page as it is everywhere else: its own composer at the bottom, with its own hint in it.
    expect(screen.getAllByRole("textbox").length).toBe(1);
    expect((screen.getByRole("textbox") as HTMLTextAreaElement).placeholder).toContain("Type a message");

    fireEvent.click(screen.getByRole("tab", { name: "Wingman" }));
    await waitFor(() => expect(screen.getByText("Commit and deploy the fixes?")).toBeTruthy());

    // Exactly one box and exactly one Send on the whole screen.
    const boxes = screen.getAllByRole("textbox") as HTMLTextAreaElement[];
    expect(boxes.length).toBe(1);
    expect(screen.getAllByRole("button", { name: "Send" }).length).toBe(1);

    // It is the one beside the question, and the words in it are the Gateway's.
    expect(boxes[0].placeholder).toBe(PLACEHOLDER);
    const card = container.querySelector(".wnow-card-needs")!;
    expect(within(card as HTMLElement).getByRole("textbox")).toBe(boxes[0]);

    // He dictates. A reply box with no Speak is a box he will not use - and nothing the page's composer offered is
    // left without a home.
    const bar = boxes[0].closest(".composer")!;
    expect([...bar.querySelectorAll("button")].map((b) => b.textContent)).toEqual([
      "Send",
      "Speak",
      "Queue",
      "Attach",
    ]);
  });

  it("does not put Stop, Interrupt, Compact, Clear context or History under the answers", async () => {
    fakeGateway();
    openPage();

    // They are all there on the Terminal tab, where they belong.
    for (const verb of ["Stop", "Interrupt", "Compact", "Clear context", "History"]) {
      expect(screen.getByRole("button", { name: verb })).toBeTruthy();
    }

    fireEvent.click(screen.getByRole("tab", { name: "Wingman" }));
    await waitFor(() => expect(screen.getByText("Commit and deploy the fixes?")).toBeTruthy());

    for (const verb of ["Stop", "Interrupt", "Compact", "Clear context", "History"]) {
      expect(screen.queryByRole("button", { name: verb })).toBeNull();
    }

    // And going back to the Terminal brings the whole bar back - this tab is the only one that hides it.
    fireEvent.click(screen.getByRole("tab", { name: "Terminal" }));
    expect(screen.getByRole("button", { name: "Stop" })).toBeTruthy();
  });

  it("gives the empty queue panel's width back to the page, and takes it again the moment something is queued", async () => {
    fakeGateway();
    const { container } = openPage();
    expect(container.querySelector(".session-dock")).toBeTruthy();

    fireEvent.click(screen.getByRole("tab", { name: "Wingman" }));
    await waitFor(() => expect(screen.getByText("Commit and deploy the fixes?")).toBeTruthy());
    expect(container.querySelector(".session-dock")).toBeNull();
    expect(container.querySelector(".session-detail-wide")).toBeTruthy();

    cleanup();
    vi.unstubAllGlobals();

    // One queued prompt, and the panel is worth its width again.
    fakeGateway([{ id: "q1", text: "and tag it afterwards" }]);
    const second = openPage();
    fireEvent.click(screen.getByRole("tab", { name: "Wingman" }));
    await waitFor(() => expect(second.container.querySelector(".session-dock")).toBeTruthy());
    expect(second.container.querySelector(".session-detail-wide")).toBeNull();
  });
});
