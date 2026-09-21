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
  sessionLine: "112 - Cube Data and Projects - Architect",
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
      { index: 0, number: 1, key: "Commit and deploy", note: "It commits the fixes and deploys them.", recommended: true },
      { index: 1, number: 2, key: "Do not commit", note: "Nothing is committed or deployed.", recommended: false },
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

/**
 * A WORKING SESSION, as the Gateway folds one: no stop to answer, and a box whose own words say a message sent to
 * it waits. Every other field is the same answer, so what differs on screen can only be the state.
 */
const WORKING: WingmanNow = {
  ...NOW,
  state: "working",
  pillText: "Working",
  pillColour: "blue",
  pillColourHex: "#3b82f6",
  when: { lead: "Working for", atUtc: "2026-09-18T07:12:00Z", showAgo: false, elapsedOnly: true },
  headline: null,
  story: null,
  needs: null,
  canAnswerByOption: false,
  verdictId: null,
  replyPlaceholder: "Send it something while it works - it is queued until it is ready.",
  replyHint: "Sent to the session as your message.",
  lastAsked: {
    heading: "What it was last asked",
    text: "Carry on with the next slice.",
    atUtc: "2026-09-18T07:08:00Z",
    by: null,
    whenLead: "at",
  },
};

/** A FINISHED session, the one state where closing it is the next step - so the close is offered at all. */
const DONE: WingmanNow = {
  ...NOW,
  state: "done",
  pillText: "Done",
  pillColour: "cyan",
  pillColourHex: "#06b6d4",
  headline: "Release v2.5.0 is tagged and published",
  needs: null,
  canAnswerByOption: false,
  verdictId: null,
  replyPlaceholder: "Give it something else to do.",
  replyHint: "Sent to the session as your message.",
  calmCard: {
    heading: "The work is complete",
    body: "Nothing is needed from you. You can close this session when you are ready.",
    tone: "cyan",
  },
};

/** Every call the page made, so a test can say WHICH route a button reached rather than only that something did. */
let calls: string[] = [];

/** The Gateway, answering the reads this page makes. Anything unasked-for is a failure, not an empty list. */
function fakeGateway(queue: unknown[] = [], now: WingmanNow = NOW) {
  calls = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string) => {
      calls.push(url);
      const body = url.includes("wingman-now")
        ? now
        : url.includes("wingman-stops")
          ? { stops: [], groups: [] }
          : url.includes("/queue")
            ? { items: queue }
            : url.includes("/prompt")
              ? {}
              : url.includes("/stop")
                ? { verdict: "stopped", headline: "The session was stopped." }
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
    //
    // ONE SENDING BUTTON (the review's item N3). This session is STOPPED, so there is nothing to queue behind and
    // Queue meant nothing beside Send. Speak and Attach stay: neither of them sends.
    // The three are icons inside one pill now (owner ruling, 2026-09-20), so they are read by the name
    // they announce rather than by the words drawn on them - which is also the name a person hears.
    const bar = boxes[0].closest(".composer")!;
    expect([...bar.querySelectorAll("button")].map((b) => b.getAttribute("aria-label"))).toEqual([
      "Attach",
      "Speak",
      "Send",
    ]);
  });

  it("offers one button named for what it does on a working session, and no Send beside it", async () => {
    // The box on a working session says a message "is queued until it is ready" - and then offered a Send AND a
    // Queue. If Send queues, Queue does nothing; if it does not, the words in the box are wrong. One button.
    fakeGateway([], WORKING);
    openPage();

    fireEvent.click(screen.getByRole("tab", { name: "Wingman" }));
    await waitFor(() => expect(screen.getByText("What it was last asked")).toBeTruthy());

    const box = screen.getByRole("textbox") as HTMLTextAreaElement;
    expect(box.placeholder).toBe("Send it something while it works - it is queued until it is ready.");
    const bar = box.closest(".composer")!;
    expect([...bar.querySelectorAll("button")].map((b) => b.getAttribute("aria-label"))).toEqual([
      "Attach",
      "Speak",
      "Queue it",
    ]);
    expect(screen.queryByRole("button", { name: "Send" })).toBeNull();

    // And it DOES what it is called. A button named for the queue that reached the prompt route would be the same
    // defect wearing the other word.
    fireEvent.change(box, { target: { value: "and tag it afterwards" } });
    fireEvent.click(screen.getByRole("button", { name: "Queue it" }));
    await waitFor(() => expect(calls.some((url) => url.endsWith(`/sessions/${SID}/queue`))).toBe(true));
    expect(calls.some((url) => url.includes("/prompt"))).toBe(false);
  });

  it("does not put the driver controls under the answers", async () => {
    fakeGateway();
    openPage();

    // WHERE THEY LIVE NOW (owner ruling, 2026-09-20): Stop is the one urgent verb, on the strip under
    // the composer; Interrupt is its escalation and is not drawn until Stop has been pressed; Compact,
    // Clear context and History moved into the session menu. What this test is about is unchanged - the
    // Wingman tab is the one tab that carries NONE of them, because the owner clicks his answers here.
    expect(screen.getByRole("button", { name: "Stop" })).toBeTruthy();
    for (const gone of ["Interrupt", "Force interrupt", "Compact", "Clear context", "History"]) {
      expect(screen.queryByRole("button", { name: gone })).toBeNull();
    }

    fireEvent.click(screen.getByRole("tab", { name: "Wingman" }));
    await waitFor(() => expect(screen.getByText("Commit and deploy the fixes?")).toBeTruthy());

    for (const verb of ["Stop", "Interrupt", "Force interrupt", "Compact", "Clear context", "History"]) {
      expect(screen.queryByRole("button", { name: verb })).toBeNull();
    }

    // And going back to the Terminal brings the strip back - this tab is the only one that hides it.
    fireEvent.click(screen.getByRole("tab", { name: "Terminal" }));
    expect(screen.getByRole("button", { name: "Stop" })).toBeTruthy();
  });

  it("asks once before it closes a session, and stops nothing until he answers", async () => {
    // THE ONLY BUTTON ON NOW THAT DESTROYS ANYTHING. It sat in the middle of the row between two harmless
    // buttons, drawn exactly like both, and nothing on the screen said whether a click would just do it.
    fakeGateway([], DONE);
    const { container } = openPage();

    fireEvent.click(screen.getByRole("tab", { name: "Wingman" }));
    await waitFor(() => expect(screen.getByText("The work is complete")).toBeTruthy());

    // Last in the row, and set apart from the buttons that do nothing irreversible.
    const row = [...container.querySelectorAll(".wnow-quick button")].map((b) => b.textContent);
    expect(row[row.length - 1]).toBe("Close this session");
    expect(container.querySelector(".wnow-btn-apart")!.textContent).toBe("Close this session");

    fireEvent.click(screen.getByRole("button", { name: "Close this session" }));

    // It ASKS, by name, and the session is still running while it asks.
    expect(screen.getByRole("heading", { name: "Stop Cube Data and Projects - Architect?" })).toBeTruthy();
    expect(calls.some((url) => url.includes("/stop"))).toBe(false);

    // Cancel leaves it alone entirely.
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(calls.some((url) => url.includes("/stop"))).toBe(false);

    // And the answer is what closes it.
    fireEvent.click(screen.getByRole("button", { name: "Close this session" }));
    fireEvent.click(screen.getByRole("button", { name: "Stop session" }));
    await waitFor(() => expect(calls.some((url) => url.endsWith(`/sessions/${SID}/stop`))).toBe(true));
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
