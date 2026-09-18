// Now, rendered against the settled contract - one test per drawn state (the Wingman tab, version 3, item 3).
//
// What these prove: each of the ten states draws the words it was handed and nothing else; the view invents no
// wording of its own; a piece the Gateway did not send is not drawn; and the timed sentences are finished with the
// reader's local clock. A view that composed its own sentence for a state, dropped a Gateway string, or branched on
// what a state means goes red here.
//
// The wording below is the approved mockup's (devthrottle_internal
// docs/design/wingman-inspector/wingman-tab-v3.html), so these tests also say what the owner asked to see.
//
// They also prove what the view does with a write that FAILED: the Gateway's own sentence is shown, unedited, beside
// the control that caused it, and the owner's typed words survive anything short of an accepted send.
//
// THE SHAPES BELOW ARE THE MERGED CONTRACT'S, field for field (src/CcDirector.Gateway.Contracts/WingmanNowDto.cs).
// They were the design document's until 18 September 2026, and the route had diverged from it - so every test here
// was green while the screen could not answer a stop. That a real Gateway answer really has these names is proven
// somewhere else on purpose, in wingmanNowWireSample.test.tsx, which renders this view from a file the Gateway's own
// test writes. These prove what the view DOES with the shape; that one proves the shape is what arrives.
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { formatAgo, formatClockTime, formatElapsed, formatWhen, WingmanNow } from "./WingmanNow";
import type { WingmanNow as WingmanNowDto } from "./wingmanNowRead";

afterEach(() => cleanup());

// A fixed reading moment, so "8 minutes ago" is the same sentence on every machine.
const AT = new Date("2026-09-17T11:20:00Z");
const STOPPED = "2026-09-17T11:12:00Z";

// The two shapes a shell handler can answer in. The Cockpit's handlers never reject - they read the Gateway's answer
// and hand back one of these - so the stand-ins here answer the same way.
const accepts = (message = "") => vi.fn(async () => ({ accepted: true, message }));
const refuses = (message: string) => vi.fn(async () => ({ accepted: false, message }));

// THE SHELL'S MESSAGE BOX, stood in for. The real one is the Cockpit's composer, with Send, Speak, Queue and Attach,
// and it is proven where it is wired (apps/cockpit wingmanReplyBox.test.tsx). This view is responsible for one thing
// about it: that it is drawn exactly where the Gateway offered a placeholder, carrying the Gateway's own words.
const replyBox = (placeholder: string) => (
  <textarea aria-label="Your reply to this session" placeholder={placeholder} readOnly />
);

function base(overrides: Partial<WingmanNowDto> = {}): WingmanNowDto {
  return {
    sessionId: "11111111-1111-1111-1111-111111111111",
    sessionLine: "112 - Cube Data and Projects - Architect",
    state: "other",
    pillText: "Working",
    pillColour: "blue",
    pillColourHex: "#3b82f6",
    unsure: false,
    unsureTag: null,
    unsureLine: null,
    when: null,
    showWhyColour: true,
    headline: null,
    story: null,
    agentSaid: null,
    wholeReply: null,
    needs: null,
    canAnswerByOption: false,
    verdictId: null,
    replyPlaceholder: null,
    replyHint: null,
    snoozedUntil: null,
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
    ...overrides,
  };
}

const NEEDS_YOU = base({
  state: "needs-you",
  pillText: "Needs you",
  pillColour: "red",
  pillColourHex: "#ef4444",
  when: { lead: "Stopped at", atUtc: STOPPED, showAgo: true, elapsedOnly: false },
  headline: "Merge pull request #3002, or allow me to merge it",
  story: "The release notes are fixed and pushed to pull request #3002, rebased onto the current main.",
  agentSaid: {
    who: "Claude Code said",
    text: "Either merge #3002 yourself, or allow that command and I'll do it.",
  },
  wholeReply: "The Fable review is done, and the notes are fixed and pushed to pull request #3002.",
  needs: {
    heading: "What it needs from you",
    recommends: "It recommends: allow the merge - the notes have been reviewed.",
    question: null,
    options: [
      {
        index: 1,
        number: 1,
        key: "Allow the merge",
        note: "It runs the merge itself. This lands the release notes on main and cannot be undone by the session.",
        recommended: true,
      },
      { index: 2, number: 2, key: "I will merge it myself", note: "It waits for you. Nothing changes until you merge.", recommended: false },
    ],
  },
  canAnswerByOption: true,
  // THE VERDICT IDENTIFIER IS AT THE ROOT of the answer, which is where the route puts it.
  verdictId: "v-3002",
  replyPlaceholder: "Or answer in your own words - for example: allow the merge, tag straight after it.",
  voice: { kind: "play", label: "Play", afterTurnOnText: null },
});

describe("Now - the live stop", () => {
  it("draws the everyday needs-you stop: the pill, when it stopped, the story, the agent's sentence and every option", () => {
    render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onAnswerOption: accepts() }} />);

    expect(screen.getByText("Needs you")).toBeTruthy();
    expect(screen.getByText(`Stopped at ${formatClockTime(STOPPED)}, 8 minutes ago`)).toBeTruthy();
    expect(screen.getByText("Merge pull request #3002, or allow me to merge it")).toBeTruthy();
    expect(screen.getByText(/The release notes are fixed and pushed/)).toBeTruthy();
    expect(screen.getByText("Claude Code said")).toBeTruthy();
    expect(screen.getByText(/Either merge #3002 yourself/)).toBeTruthy();
    expect(screen.getByText("What it needs from you")).toBeTruthy();
    expect(screen.getByText("It recommends: allow the merge - the notes have been reviewed.")).toBeTruthy();
    expect(screen.getByText("Allow the merge")).toBeTruthy();
    expect(screen.getByText("RECOMMENDED")).toBeTruthy();
    expect(screen.getByText(/This lands the release notes on main/)).toBeTruthy();
    expect(screen.getByText("I will merge it myself")).toBeTruthy();
    expect(screen.getByText("The whole reply, word for word")).toBeTruthy();
    // The unsure tag and line belong to the next state, not this one.
    expect(screen.queryByText("The Wingman is not sure")).toBeNull();
  });

  it("answers by option with the index the Gateway gave, and refuses when the Gateway says the stop cannot be answered that way", async () => {
    const onAnswerOption = accepts();
    const { rerender } = render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onAnswerOption }} />);
    fireEvent.click(screen.getByText("Allow the merge"));
    expect(onAnswerOption).toHaveBeenCalledWith(NEEDS_YOU.needs!.options[0], "v-3002");
    await waitFor(() => expect((screen.getByText("Allow the merge").closest("button"))!.disabled).toBe(false));

    rerender(<WingmanNow now={base({ ...NEEDS_YOU, canAnswerByOption: false })} at={AT} actions={{ onAnswerOption }} />);
    fireEvent.click(screen.getByText("Allow the merge"));
    expect(onAnswerOption).toHaveBeenCalledTimes(1);
  });

  it("shows the answer route's refusal sentence unedited, so an answer that did nothing never looks like one that landed", async () => {
    const onAnswerOption = refuses("The screen has moved on since that question was asked.");
    render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onAnswerOption }} />);

    fireEvent.click(screen.getByText("Allow the merge"));
    await waitFor(() =>
      expect(screen.getByRole("alert").textContent).toBe("The screen has moved on since that question was asked."),
    );
  });

  it("shows what the answer route said when it accepted the answer, as a quiet line rather than an alert", async () => {
    const onAnswerOption = accepts("Sent option 1 to the session.");
    render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onAnswerOption }} />);

    fireEvent.click(screen.getByText("Allow the merge"));
    await waitFor(() => expect(screen.getByText("Sent option 1 to the session.")).toBeTruthy());
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("draws the shell's one message box inside the card, with the Gateway's words in it", () => {
    const seen = vi.fn(replyBox);
    render(<WingmanNow now={NEEDS_YOU} at={AT} replyBox={seen} />);

    const box = screen.getByLabelText("Your reply to this session") as HTMLTextAreaElement;
    expect(box.placeholder).toBe(NEEDS_YOU.replyPlaceholder);
    // The Gateway's placeholder AND the Gateway's state word: the shell offers one sending button per state, so it
    // is handed the state it is drawing a box for rather than working it out from the placeholder's wording.
    expect(seen).toHaveBeenCalledWith(NEEDS_YOU.replyPlaceholder, "needs-you");
    // Inside the card, beside the question - not a second box further down the page.
    expect(box.closest(".wnow-card-needs")).toBeTruthy();
  });

  it("draws no message box at all when the shell wired none, however the Gateway worded the placeholder", () => {
    render(<WingmanNow now={NEEDS_YOU} at={AT} />);
    expect(screen.queryByLabelText("Your reply to this session")).toBeNull();
  });

  it("draws needs-you, not sure: the same screen with the Wingman's own warning tag and line", () => {
    const now = base({
      ...NEEDS_YOU,
      unsure: true,
      unsureTag: "The Wingman is not sure",
      unsureLine: "The Wingman is not sure this is a question for you. Check the reply above before answering.",
      story: "It may be asking you to merge, or it may only be reporting that the merge was refused.",
    });
    render(<WingmanNow now={now} at={AT} />);

    expect(screen.getByText("The Wingman is not sure")).toBeTruthy();
    expect(screen.getByText(/Check the reply above before answering/)).toBeTruthy();
  });

  it("draws the reading state: the Wingman's own sentence, the session's last words, and no voice control", () => {
    const now = base({
      state: "reading",
      pillText: "Stopped - the Wingman is reading it",
      pillColour: "grey",
      pillColourHex: "#64748b",
      when: { lead: "Stopped at", atUtc: "2026-09-17T11:19:56Z", showAgo: true, elapsedOnly: false },
      headline: "The session stopped. The Wingman is reading its screen...",
      story: "This usually takes a few seconds.",
      lastWords: { who: "Its last words", text: "I need help with three things: The merge ..." },
      replyPlaceholder: "You can answer now without waiting.",
    });
    render(<WingmanNow now={now} at={AT} actions={{ onPlayVoice: accepts() }} replyBox={replyBox} />);

    expect(screen.getByText("Stopped - the Wingman is reading it")).toBeTruthy();
    expect(screen.getByText(`Stopped at ${formatClockTime("2026-09-17T11:19:56Z")}, 4 seconds ago`)).toBeTruthy();
    expect(screen.getByText("The session stopped. The Wingman is reading its screen...")).toBeTruthy();
    expect(screen.getByText("Its last words")).toBeTruthy();
    expect(screen.getByLabelText("Your reply to this session")).toBeTruthy();
    expect(screen.queryByText("Play")).toBeNull();
  });

  it("draws the working state: what it was last asked, by whom and when, and the last stop marked as past", () => {
    const now = base({
      state: "working",
      pillText: "Working",
      when: { lead: "Working for", atUtc: "2026-09-17T11:14:00Z", showAgo: true, elapsedOnly: true },
      headline: "Working on what you asked at 11:14 AM",
      lastAsked: {
        heading: "What it was last asked",
        text: "Allow the merge, tag straight after it, and retry the changelog message.",
        atUtc: "2026-09-17T11:14:00Z",
        by: "You",
        whenLead: "You, at",
      },
      replyPlaceholder: "Send it something while it works - it is queued until it is ready.",
      lastStop: {
        lead: "Last stop",
        atUtc: STOPPED,
        text: "Needs you - merge pull request #3002, or allow me to merge it.",
      },
    });
    render(<WingmanNow now={now} at={AT} replyBox={replyBox} />);

    expect(screen.getByText("What it was last asked")).toBeTruthy();
    expect(screen.getByText(/Allow the merge, tag straight after it/)).toBeTruthy();
    expect(screen.getByText(`You, at ${formatClockTime("2026-09-17T11:14:00Z")}`)).toBeTruthy();
    // The elapsed time ALONE, with no clock time in it - the shape the Gateway named with elapsedOnly.
    expect(screen.getByText("Working for 6 minutes")).toBeTruthy();
    expect(screen.getByText(`Last stop, ${formatClockTime(STOPPED)}:`)).toBeTruthy();
    expect(screen.getByText(/merge pull request #3002, or allow me to merge it\./)).toBeTruthy();
  });

  it("says nothing about who asked when the Gateway did not say", () => {
    const now = base({
      state: "working",
      lastAsked: {
        heading: "What it was last asked",
        text: "Carry on with the next slice.",
        atUtc: "2026-09-17T11:14:00Z",
        by: null,
        whenLead: "at",
      },
    });
    render(<WingmanNow now={now} at={AT} />);
    // The Gateway's own lead-in, capital and punctuation included. The view used to write "At " itself.
    expect(screen.getByText(`at ${formatClockTime("2026-09-17T11:14:00Z")}`)).toBeTruthy();
  });

  it("draws just answered: what was sent, that it is working again, and the next session that needs you", () => {
    const onGoToSession = vi.fn();
    const now = base({
      state: "just-answered",
      pillText: "Working again",
      when: { lead: "You answered", atUtc: "2026-09-17T11:19:40Z", showAgo: true, elapsedOnly: true },
      answered: {
        headline: "You answered: allow the merge",
        text: "allow the merge",
        sentLead: "Sent at",
        atUtc: "2026-09-17T11:19:40Z",
        workingAgainAfterText: "The session started working again 2 seconds later.",
      },
      nextNeedsYou: {
        heading: "Next that needs you",
        sessionId: "3f2b19c0-0000-4000-8000-000000000044",
        name: "Dev Reports - Architect",
        label: "Should I open an issue for the dev Gateway?",
        linkText: "Go there",
      },
      lastStop: {
        lead: "The stop you answered",
        atUtc: STOPPED,
        text: "Merge pull request #3002, or allow me to merge it.",
      },
    });
    render(<WingmanNow now={now} at={AT} actions={{ onGoToSession }} />);

    // The Gateway's whole finished first line, not a heading this file wrote.
    expect(screen.getByText("You answered: allow the merge")).toBeTruthy();
    expect(screen.queryByText("What you answered")).toBeNull();
    expect(
      screen.getByText(
        `Sent at ${formatClockTime("2026-09-17T11:19:40Z")} - The session started working again 2 seconds later.`,
      ),
    ).toBeTruthy();
    expect(screen.getByText("Next that needs you")).toBeTruthy();
    expect(screen.getByText("Dev Reports - Architect")).toBeTruthy();
    expect(screen.getByText("Should I open an issue for the dev Gateway?")).toBeTruthy();
    expect(screen.getByText(`The stop you answered, ${formatClockTime(STOPPED)}:`)).toBeTruthy();

    fireEvent.click(screen.getByText("Go there"));
    expect(onGoToSession).toHaveBeenCalledWith("3f2b19c0-0000-4000-8000-000000000044");
  });

  it("draws carrying on: nothing needed, and when it turns red, in the Gateway's words with the local clock", () => {
    const now = base({
      state: "carrying-on",
      pillText: "Carrying on",
      pillColour: "purple",
      pillColourHex: "#a855f7",
      when: { lead: "Stopped at", atUtc: "2026-09-17T11:19:00Z", showAgo: true, elapsedOnly: false },
      headline: "Waiting for its Worker to finish the slice J test run",
      story: "The Worker is running the full test gate on pull request 2977.",
      agentSaid: { who: "Claude Code said", text: "The Worker is seated and running." },
      calmCard: { heading: "Nothing needed from you", body: null, tone: "purple" },
      carryingOnDeadline: {
        before: "If it has not worked again by",
        atUtc: "2026-09-17T12:05:00Z",
        after: ", and none of the sessions it owns is still working, this turns red and says so.",
      },
    });
    render(<WingmanNow now={now} at={AT} />);

    expect(screen.getByText("Nothing needed from you")).toBeTruthy();
    expect(
      screen.getByText(
        `If it has not worked again by ${formatClockTime("2026-09-17T12:05:00Z")}, and none of the sessions it owns is still working, this turns red and says so.`,
      ),
    ).toBeTruthy();
  });

  it("draws the carrying-on sentence that has no clock from the card's own body, with no deadline to show", () => {
    // ONE SENTENCE, NEVER TWO TO CHOOSE BETWEEN. While a session it owns is still running no clock is counting, so
    // the Gateway sends NO deadline at all and puts the sentence that says so in the card's body.
    const now = base({
      state: "carrying-on",
      calmCard: {
        heading: "Nothing needed from you",
        body: "It turns red if it stops working and none of the sessions it owns is still working.",
        tone: "purple",
      },
      carryingOnDeadline: null,
    });
    render(<WingmanNow now={now} at={AT} />);
    expect(
      screen.getByText("It turns red if it stops working and none of the sessions it owns is still working."),
    ).toBeTruthy();
  });

  it("draws done: the work is complete, and nothing to answer", () => {
    const now = base({
      state: "done",
      pillText: "Done",
      pillColour: "cyan",
      pillColourHex: "#06b6d4",
      when: { lead: "Stopped at", atUtc: "2026-09-17T12:40:00Z", showAgo: false, elapsedOnly: false },
      headline: "Release v2.5.0 is tagged and published",
      story: "The merge landed, the full test run passed, v2.5.0 was tagged and the release built.",
      calmCard: {
        heading: "The work is complete",
        body: "Nothing is needed from you. You can close this session when you are ready.",
        tone: "cyan",
      },
    });
    render(<WingmanNow now={now} at={AT} replyBox={replyBox} />);

    expect(screen.getByText("Done")).toBeTruthy();
    expect(screen.getByText(`Stopped at ${formatClockTime("2026-09-17T12:40:00Z")}`)).toBeTruthy();
    expect(screen.getByText("The work is complete")).toBeTruthy();
    expect(screen.getByText(/You can close this session when you are ready\./)).toBeTruthy();
    // Done sent no placeholder, so there is no reply box even though the shell offered to send one.
    expect(screen.queryByLabelText("Your reply to this session")).toBeNull();
  });

  it("gives done a box the moment the Gateway sends a placeholder for it", () => {
    // THE STATE HE IS MOST LIKELY TO ANSWER FROM, and the one with nowhere to type (the review's item N2).
    // Removing the page's bottom box was right; done never had a box of its own, so it was left with none at all.
    // The view needs no rule for this - it draws a box wherever a placeholder arrived - and this is the guard that
    // says so, against the words the Gateway is adding.
    const now = base({
      state: "done",
      pillText: "Done",
      calmCard: { heading: "The work is complete", body: "Nothing is needed from you.", tone: "cyan" },
      replyPlaceholder: "Give it something else to do.",
      replyHint: "Sent to the session as your message.",
    });
    const { container } = render(<WingmanNow now={now} at={AT} replyBox={replyBox} />);

    const box = screen.getByLabelText("Your reply to this session") as HTMLTextAreaElement;
    expect(box.placeholder).toBe("Give it something else to do.");
    // Under the card, which is where the sentence above it invites him to type.
    const card = container.querySelector(".wnow-card-calm")!;
    const reply = container.querySelector(".wnow-reply")!;
    expect(card.compareDocumentPosition(reply) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    // And what sending it does, in the Gateway's words rather than in silence.
    expect(screen.getByText("Sent to the session as your message.")).toBeTruthy();
  });

  it("draws report: only telling you, the work is not finished, and the reply box stays open", () => {
    const now = base({
      state: "report",
      pillText: "Report",
      pillColour: "cyan",
      pillColourHex: "#06b6d4",
      headline: "The hosted Gateway is already running the latest changes",
      calmCard: {
        heading: "Only telling you",
        body: "Nothing is needed from you, and the release is not finished yet.",
        tone: "cyan",
      },
      replyPlaceholder: "Reply if you want it to do something about this.",
    });
    render(<WingmanNow now={now} at={AT} replyBox={replyBox} />);

    expect(screen.getByText("Only telling you")).toBeTruthy();
    expect(screen.getByText(/the release is not finished yet\./)).toBeTruthy();
    expect(screen.getByLabelText("Your reply to this session")).toBeTruthy();
  });

  it("draws a failed judgement in the Gateway's words, with the session's last words and the last good explanation", () => {
    const now = base({
      state: "failed",
      pillText: "Needs you",
      pillColour: "red",
      pillColourHex: "#ef4444",
      when: { lead: "Stopped at", atUtc: "2026-09-17T11:18:00Z", showAgo: true, elapsedOnly: false },
      failedHeadline: "The Wingman could not explain this stop",
      failedStory:
        "Its answer was thrown away: it said the session needs you but also marked the work as finished. The row stays red because the session stopped.",
      lastWords: { who: "Claude Code's last words", text: "Two more fixes for steps 7 to 9 are in ..." },
      replyPlaceholder: "Answer the session directly.",
      lastGood: {
        lead: "Last good explanation",
        atUtc: "2026-09-17T09:55:00Z",
        text: "Carrying on - fixes and inspections in progress.",
      },
    });
    render(<WingmanNow now={now} at={AT} replyBox={replyBox} />);

    expect(screen.getByText("The Wingman could not explain this stop")).toBeTruthy();
    expect(screen.getByText(/The row stays red because the session stopped\./)).toBeTruthy();
    expect(screen.getByText("Claude Code's last words")).toBeTruthy();
    expect(screen.getByText(`Last good explanation, ${formatClockTime("2026-09-17T09:55:00Z")}:`)).toBeTruthy();
    expect(screen.getByText("Carrying on - fixes and inspections in progress.")).toBeTruthy();
  });

  it("draws switched off: one sentence, the way back to Settings, and no colour explanation", () => {
    const onOpenSettings = vi.fn();
    const now = base({
      state: "switched-off",
      pillText: "Stopped",
      pillColour: "red",
      pillColourHex: "#ef4444",
      showWhyColour: false,
      switchedOff: {
        headline: "The Wingman is switched off for your account",
        story: "Nothing reads this session's stops.",
        settingsLinkText: "Switch it on in Settings",
      },
      lastWords: { who: "Its last words", text: "I need help with three things: The merge ..." },
      replyPlaceholder: "Answer the session directly.",
    });
    render(
      <WingmanNow now={now} at={AT} actions={{ onOpenSettings, onWhyColour: vi.fn() }} replyBox={replyBox} />,
    );

    expect(screen.getByText("The Wingman is switched off for your account")).toBeTruthy();
    expect(screen.getByText(/Nothing reads this session's stops\./)).toBeTruthy();
    expect(screen.queryByText("Why this colour?")).toBeNull();

    fireEvent.click(screen.getByText("Switch it on in Settings"));
    expect(onOpenSettings).toHaveBeenCalled();
  });

  it("draws a state none of the others describe with the row's own words, so Now is never blank", () => {
    const now = base({
      state: "other",
      pillText: "Snoozed until 2:00 PM",
      pillColour: "grey",
      pillColourHex: "#64748b",
      headline: "Snoozed until 2:00 PM",
      lastWords: { who: "Its last words", text: "Waiting on the owner." },
    });
    render(<WingmanNow now={now} at={AT} />);

    expect(screen.getAllByText("Snoozed until 2:00 PM").length).toBe(2);
    expect(screen.getByText("Waiting on the owner.")).toBeTruthy();
  });
});

describe("Now - the fields the contract may legitimately send as nothing", () => {
  it("ends the answered sentence at the time when the Gateway cannot tell whether the session went back to work", () => {
    // workingAgainAfterText is NULL whenever this Gateway cannot tell, and nothing may be claimed about it. The
    // view used to write "Sent at 7:19 a.m. - " and stop, leaving a dash pointing at nothing.
    const now = base({
      state: "just-answered",
      answered: {
        headline: "You answered: allow the merge",
        text: "allow the merge",
        sentLead: "Sent at",
        atUtc: "2026-09-17T11:19:40Z",
        workingAgainAfterText: null,
      },
    });
    render(<WingmanNow now={now} at={AT} />);

    expect(screen.getByText(`Sent at ${formatClockTime("2026-09-17T11:19:40Z")}`)).toBeTruthy();
    expect(screen.queryByText(/ - $/)).toBeNull();
  });

  it("draws no option list at all rather than dying, if a Gateway ever sends needs with no options", () => {
    // The contract initialises options and the fold always writes them, so this is a belt on a rule kept elsewhere -
    // but the Cockpit has no error boundary, so an absent list would take the whole screen down with it.
    const now = base({
      state: "needs-you",
      needs: { heading: "What it needs from you", recommends: null, question: null } as never,
      replyPlaceholder: "Answer in your own words.",
    });
    render(<WingmanNow now={now} at={AT} actions={{ onAnswerOption: accepts() }} replyBox={replyBox} />);

    expect(screen.getByText("What it needs from you")).toBeTruthy();
    expect(screen.getByLabelText("Your reply to this session")).toBeTruthy();
  });

  it("draws a stop that takes typed words only, with no option list and nothing to tap", () => {
    const now = base({
      state: "needs-you",
      needs: { heading: "What it needs from you", recommends: null, question: null, options: [] },
      replyPlaceholder: "Answer in your own words.",
    });
    render(<WingmanNow now={now} at={AT} actions={{ onAnswerOption: accepts() }} replyBox={replyBox} />);

    expect(screen.getByText("What it needs from you")).toBeTruthy();
    expect(screen.getByLabelText("Your reply to this session")).toBeTruthy();
    expect(screen.queryByText("RECOMMENDED")).toBeNull();
  });

  it("draws the screen without a pill colour, and without a next label, when the row carries neither", () => {
    const now = base({
      state: "just-answered",
      pillColour: null,
      pillColourHex: null,
      nextNeedsYou: {
        heading: "Next that needs you",
        sessionId: "3f2b19c0-0000-4000-8000-000000000044",
        name: "Dev Reports - Architect",
        label: null,
        linkText: "Go there",
      },
    });
    render(<WingmanNow now={now} at={AT} actions={{ onGoToSession: vi.fn() }} />);

    expect(screen.getByText("Working")).toBeTruthy();
    expect(screen.getByText("Dev Reports - Architect")).toBeTruthy();
    expect(screen.getByText("Go there")).toBeTruthy();
  });

  it("draws no voice control at all rather than dying, if a Gateway ever sends none", () => {
    // The contract says voice is never null and the fold always writes it, so this is a belt on a rule kept
    // elsewhere - but the Cockpit has no error boundary, so the whole screen would go with it.
    const now = { ...base({ headline: "Release v2.5.0 is tagged and published" }), voice: undefined } as never;
    render(<WingmanNow now={now} at={AT} actions={{ onPlayVoice: accepts() }} />);

    expect(screen.getByText("Release v2.5.0 is tagged and published")).toBeTruthy();
    expect(screen.queryByText("Play")).toBeNull();
  });
});

describe("Now - the voice control", () => {
  it("offers Play with the Gateway's label when the narration is ready", () => {
    const onPlayVoice = accepts();
    render(
      <WingmanNow now={base({ voice: { kind: "play", label: "Play", afterTurnOnText: null } })} at={AT} actions={{ onPlayVoice }} />,
    );
    fireEvent.click(screen.getByText("Play"));
    expect(onPlayVoice).toHaveBeenCalled();
  });

  it("says the audio is being prepared, and offers nothing to click", () => {
    render(
      <WingmanNow
        now={base({ voice: { kind: "preparing", label: "Preparing audio...", afterTurnOnText: null } })}
        at={AT}
        actions={{ onPlayVoice: accepts() }}
      />,
    );
    expect(screen.getByText("Preparing audio...")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Preparing audio..." })).toBeNull();
  });

  it("offers to turn voice on, and then says what that did - including whether this stop will be read", async () => {
    const onTurnOnVoice = accepts();
    const now = base({
      voice: {
        kind: "turn-on",
        label: "Read this session aloud from now on",
        afterTurnOnText: "Voice is on for this session. This stop will be read aloud in a few seconds.",
      },
    });
    render(<WingmanNow now={now} at={AT} actions={{ onTurnOnVoice }} />);

    expect(screen.queryByText(/This stop will be read aloud/)).toBeNull();
    fireEvent.click(screen.getByText("Read this session aloud from now on"));
    expect(onTurnOnVoice).toHaveBeenCalled();
    await waitFor(() =>
      expect(
        screen.getByText("Voice is on for this session. This stop will be read aloud in a few seconds."),
      ).toBeTruthy(),
    );
  });

  it("keeps the sentence about turning voice on when the next refresh flips the voice control to Play", async () => {
    const actions = { onTurnOnVoice: accepts(), onPlayVoice: accepts() };
    const off = base({
      voice: {
        kind: "turn-on" as const,
        label: "Read this session aloud from now on",
        afterTurnOnText: "Voice is on for this session. This stop will be read aloud in a few seconds.",
      },
    });
    const { rerender } = render(<WingmanNow now={off} at={AT} actions={actions} />);
    fireEvent.click(screen.getByText("Read this session aloud from now on"));
    await waitFor(() => expect(screen.getByText(/Voice is on for this session/)).toBeTruthy());

    // The next read of the same session: voice mode is on now, so the Gateway sends Play and writes no
    // after-turn-on sentence any more. What the owner was told must not vanish from under him.
    rerender(
      <WingmanNow
        now={base({ voice: { kind: "play", label: "Play", afterTurnOnText: null } })}
        at={AT}
        actions={actions}
      />,
    );
    expect(screen.getByText(/Voice is on for this session/)).toBeTruthy();
    expect(screen.getByText("Play")).toBeTruthy();
  });

  it("says what turning voice on did when the Gateway had something to add", async () => {
    const onTurnOnVoice = accepts("There is nothing to read out yet - the next turn will be narrated.");
    const now = base({
      voice: { kind: "turn-on", label: "Read this session aloud from now on", afterTurnOnText: null },
    });
    render(<WingmanNow now={now} at={AT} actions={{ onTurnOnVoice }} />);

    fireEvent.click(screen.getByText("Read this session aloud from now on"));
    await waitFor(() =>
      expect(screen.getByText("There is nothing to read out yet - the next turn will be narrated.")).toBeTruthy(),
    );
  });

  it("says so when the narration could not be played, rather than leaving a silent button", async () => {
    const onPlayVoice = refuses("The narration could not be played on this computer.");
    render(
      <WingmanNow now={base({ voice: { kind: "play", label: "Play", afterTurnOnText: null } })} at={AT} actions={{ onPlayVoice }} />,
    );

    fireEvent.click(screen.getByText("Play"));
    await waitFor(() =>
      expect(screen.getByRole("alert").textContent).toBe("The narration could not be played on this computer."),
    );
  });

  it("draws no voice control at all when there is nothing to read", () => {
    render(
      <WingmanNow
        now={base({ voice: { kind: "none", label: null, afterTurnOnText: null } })}
        at={AT}
        actions={{ onPlayVoice: accepts(), onTurnOnVoice: accepts() }}
      />,
    );
    expect(screen.queryByText("Play")).toBeNull();
    expect(screen.queryByText("Read this session aloud from now on")).toBeNull();
  });
});

describe("Now - the quick actions", () => {
  it("says what snoozing this session did, in the words the rest of the product uses", async () => {
    const onSnooze = accepts("Snoozing when it finishes");
    render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onSnooze }} />);

    fireEvent.click(screen.getByText("Snooze this session"));
    await waitFor(() => expect(screen.getByText("Snoozing when it finishes")).toBeTruthy());
  });

  it("shows the Gateway's sentence when the snooze was refused", async () => {
    const onSnooze = refuses("This session is on another account.");
    render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onSnooze }} />);

    fireEvent.click(screen.getByText("Snooze this session"));
    await waitFor(() => expect(screen.getByRole("alert").textContent).toBe("This session is on another account."));
  });

  it("draws no snooze at all when the shell did not wire one", () => {
    render(<WingmanNow now={NEEDS_YOU} at={AT} replyBox={replyBox} />);
    expect(screen.queryByText("Snooze this session")).toBeNull();
  });
});

describe("Now - the options are buttons, and they say so", () => {
  it("says a click sends, while the Gateway says the stop can still be answered that way", () => {
    const { rerender } = render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onAnswerOption: accepts() }} />);
    expect(screen.getByText("Click an option to send it as your answer.")).toBeTruthy();

    // Nothing to click: the options are there to READ, so the sentence that promises a click would be a lie.
    rerender(
      <WingmanNow now={base({ ...NEEDS_YOU, canAnswerByOption: false })} at={AT} actions={{ onAnswerOption: accepts() }} />,
    );
    expect(screen.queryByText("Click an option to send it as your answer.")).toBeNull();

    // And nothing wired to send them either.
    rerender(<WingmanNow now={NEEDS_YOU} at={AT} />);
    expect(screen.queryByText("Click an option to send it as your answer.")).toBeNull();
  });

  it("draws the option the GATEWAY recommended as the first choice, and only that one", () => {
    render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onAnswerOption: accepts() }} />);

    expect(screen.getByText("Allow the merge").closest("button")!.className).toContain("wnow-option-recommended");
    expect(screen.getByText("I will merge it myself").closest("button")!.className).not.toContain(
      "wnow-option-recommended",
    );
  });

  it("asks once before sending an answer the Gateway says cannot be undone, in the Gateway's own words", async () => {
    const onAnswerOption = accepts();
    const now = base({
      ...NEEDS_YOU,
      needs: {
        ...NEEDS_YOU.needs!,
        riskFlag: "This cannot be undone.",
        riskLine: "This cannot be undone.",
        confirmBeforeSending: true,
      },
    });
    render(<WingmanNow now={now} at={AT} actions={{ onAnswerOption }} />);

    // The warning is on the card, once, before anything is clicked - not repeated on every option.
    expect(screen.getAllByText("This cannot be undone.").length).toBe(1);

    // The first click asks instead of sending.
    fireEvent.click(screen.getByText("Allow the merge"));
    expect(onAnswerOption).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("This cannot be undone.");

    // Cancel sends nothing at all.
    fireEvent.click(screen.getByText("Cancel"));
    expect(onAnswerOption).not.toHaveBeenCalled();
    expect(screen.queryByRole("alert")).toBeNull();

    // The second click, once asked, sends.
    fireEvent.click(screen.getByText("Allow the merge"));
    fireEvent.click(screen.getByText("Send it anyway"));
    await waitFor(() => expect(onAnswerOption).toHaveBeenCalledWith(expect.objectContaining({ index: 1 }), "v-3002"));
  });

  it("asks nothing, and warns about nothing, on an option the Gateway did not mark - one click still sends", () => {
    const onAnswerOption = accepts();
    render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onAnswerOption }} />);

    fireEvent.click(screen.getByText("I will merge it myself"));
    expect(onAnswerOption).toHaveBeenCalledTimes(1);
    expect(screen.queryByText("Send it anyway")).toBeNull();
  });

  it("shows the number the Gateway folded for the reader, never the answer route's own index", () => {
    const { container, rerender } = render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onAnswerOption: accepts() }} />);
    expect([...container.querySelectorAll(".wnow-option-index")].map((n) => n.textContent)).toEqual(["1", "2"]);

    const numbered = base({
      ...NEEDS_YOU,
      needs: {
        ...NEEDS_YOU.needs!,
        options: [
          { index: 0, number: 1, key: "Commit and deploy", note: null, recommended: true },
          { index: 1, number: 2, key: "Do not commit", note: null, recommended: false },
        ],
      },
    });
    rerender(<WingmanNow now={numbered} at={AT} actions={{ onAnswerOption: accepts() }} />);
    // The route still counts from zero; the reader never sees it.
    expect([...container.querySelectorAll(".wnow-option-index")].map((n) => n.textContent)).toEqual(["1", "2"]);
  });
});

describe("Now - the head line", () => {
  it("tints the pill with the row's own colour rather than drawing a thin ring", () => {
    const { container } = render(<WingmanNow now={NEEDS_YOU} at={AT} />);
    const pill = container.querySelector(".wnow-pill") as HTMLElement;

    // One colour, the Gateway's, used three ways. Nothing here chooses a second one.
    // jsdom re-prints the hex the Gateway sent as rgb() inside the mix; it is the same one colour.
    expect(pill.style.background).toBe("color-mix(in srgb, rgb(239, 68, 68) 15%, transparent)");
    expect(pill.style.borderColor).toBe("rgb(239, 68, 68)");
    expect(pill.style.color).toBe("rgb(239, 68, 68)");
  });

  it("leaves the pill plain when the row carries no colour, rather than inventing one", () => {
    const { container } = render(
      <WingmanNow now={base({ ...NEEDS_YOU, pillColour: null, pillColourHex: null })} at={AT} />,
    );
    expect((container.querySelector(".wnow-pill") as HTMLElement).style.background).toBe("");
  });

  it("puts the voice control at the end of the pill line beside the time, with a speaker on it", () => {
    const { container } = render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onPlayVoice: accepts() }} />);
    const head = container.querySelector(".wnow-head")!;
    const when = head.querySelector(".wnow-when")!;
    const play = head.querySelector(".wnow-voice-play")!;

    // Beside the time in the same line, and nothing pushing it to the far edge of the page.
    expect(play.parentElement).toBe(head);
    expect(when.compareDocumentPosition(play) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(head.querySelector(".wnow-spacer")).toBeNull();
    expect(play.querySelector("svg.wnow-voice-icon")).toBeTruthy();
    // The words on it are still the Gateway's, whatever they are.
    expect(play.textContent).toBe("Play");
  });
});

describe("Now - the quick actions fit the state", () => {
  it("offers the wake the shell wired and no snooze beside it", async () => {
    const onUnsnooze = accepts("Unsnoozed.");
    render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onUnsnooze }} />);

    expect(screen.queryByText("Snooze this session")).toBeNull();
    // WAKE, not "unsnooze". On a snoozed session this is the first button and the one thing he came here to do.
    fireEvent.click(screen.getByText("Wake this session"));
    await waitFor(() => expect(onUnsnooze).toHaveBeenCalled());
    expect(screen.getByText("Unsnoozed.")).toBeTruthy();
  });

  it("offers a close that hands the question up to the shell, and none when the shell wired none", () => {
    const onClose = vi.fn();
    const { rerender } = render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onClose }} />);
    fireEvent.click(screen.getByText("Close this session"));
    expect(onClose).toHaveBeenCalled();

    rerender(<WingmanNow now={NEEDS_YOU} at={AT} actions={{}} />);
    expect(screen.queryByText("Close this session")).toBeNull();
  });
});

describe("Now - the calm card's colour", () => {
  it("takes the tone the Gateway named, and stays neutral when it named none", () => {
    const { container, rerender } = render(
      <WingmanNow
        now={base({ state: "done", calmCard: { heading: "The work is complete", body: null, tone: "cyan" } })}
        at={AT}
      />,
    );
    expect(container.querySelector(".wnow-card-calm-cyan")).toBeTruthy();

    rerender(
      <WingmanNow
        now={base({ state: "carrying-on", calmCard: { heading: "Nothing needed from you", body: null, tone: "purple" } })}
        at={AT}
      />,
    );
    expect(container.querySelector(".wnow-card-calm-purple")).toBeTruthy();
    expect(container.querySelector(".wnow-card-calm-cyan")).toBeNull();

    // No tone from the Gateway is not a licence to pick one here.
    rerender(<WingmanNow now={base({ calmCard: { heading: "Only telling you", body: null } })} at={AT} />);
    expect(container.querySelector(".wnow-card-calm-cyan")).toBeNull();
    expect(container.querySelector(".wnow-card-calm-purple")).toBeNull();
    expect(container.querySelector(".wnow-card-calm")).toBeTruthy();
  });
});

describe("Now - finishing the Gateway's timed sentences", () => {
  it("counts whole units down from days, and says one of a unit in the singular", () => {
    const at = new Date("2026-09-17T12:00:00Z");
    expect(formatAgo("2026-09-17T11:59:59Z", at)).toBe("1 second ago");
    expect(formatAgo("2026-09-17T11:59:30Z", at)).toBe("30 seconds ago");
    expect(formatAgo("2026-09-17T11:59:00Z", at)).toBe("1 minute ago");
    expect(formatAgo("2026-09-17T11:52:00Z", at)).toBe("8 minutes ago");
    expect(formatAgo("2026-09-17T11:00:00Z", at)).toBe("1 hour ago");
    expect(formatAgo("2026-09-15T12:00:00Z", at)).toBe("2 days ago");
  });

  it("never counts backwards when the Gateway's clock is a moment ahead of the reader's", () => {
    expect(formatAgo("2026-09-17T12:00:05Z", new Date("2026-09-17T12:00:00Z"))).toBe("0 seconds ago");
  });

  it("adds the ago only when the Gateway asked for it", () => {
    const when = { lead: "Stopped at", atUtc: STOPPED, showAgo: false, elapsedOnly: false };
    expect(formatWhen(when, AT)).toBe(`Stopped at ${formatClockTime(STOPPED)}`);
    expect(formatWhen({ ...when, showAgo: true }, AT)).toBe(`Stopped at ${formatClockTime(STOPPED)}, 8 minutes ago`);
  });

  it("says the elapsed time alone, and names no clock time, when the Gateway said that is the shape", () => {
    // The state a session is in for most of its life. Ignoring the flag rendered it as "Working for 7:14 a.m.".
    const when = { lead: "Working for", atUtc: STOPPED, showAgo: true, elapsedOnly: true };
    expect(formatWhen(when, AT)).toBe("Working for 8 minutes");
    expect(formatWhen(when, AT)).not.toContain(formatClockTime(STOPPED));
    expect(formatElapsed(STOPPED, AT)).toBe("8 minutes");
    expect(formatElapsed("2026-09-17T11:19:00Z", AT)).toBe("1 minute");
  });
});


describe("Now - which session this is", () => {
  // ROUND ONE'S TOP ITEM. The Gateway has sent this line since the route merged and the view dropped it, so the
  // pane never said whose stop was on it - while the owner moves between a dozen sessions on this one screen and
  // the box on it sends his answer to whichever one it is.
  it("puts the session's number and name on the first line, above the pill, in every state", () => {
    const states: Array<Partial<WingmanNowDto>> = [
      { state: "needs-you", pillText: "Needs you" },
      { state: "working", pillText: "Working" },
      { state: "snoozed", pillText: "Snoozed" },
      { state: "done", pillText: "Done" },
      { state: "report", pillText: "Telling you" },
      { state: "failed", pillText: "Stopped", failedHeadline: "The Wingman could not explain this stop" },
      { state: "switched-off", pillText: "Stopped" },
      { state: "other", pillText: "Exited" },
    ];
    for (const over of states) {
      const { container, unmount } = render(<WingmanNow now={base(over)} at={AT} />);
      const line = container.querySelector(".wnow-session")!;
      expect(line.textContent).toBe("112 - Cube Data and Projects - Architect");
      // FIRST. Before the pill, which is the next thing on the page.
      const pill = container.querySelector(".wnow-pill")!;
      expect(line.compareDocumentPosition(pill) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      unmount();
    }
  });

  it("draws nothing at all rather than an empty line, if a Gateway ever sends none", () => {
    const { container } = render(<WingmanNow now={base({ sessionLine: "" })} at={AT} />);
    expect(container.querySelector(".wnow-session")).toBeNull();
  });
});

describe("Now - the snoozed session", () => {
  const SNOOZED = base({
    state: "snoozed",
    pillText: "Snoozed",
    pillColour: "grey",
    pillColourHex: "#6b7280",
    when: { lead: "Stopped at", atUtc: STOPPED, showAgo: true, elapsedOnly: false },
    snoozedUntil: { lead: "Snoozed until", atUtc: "2026-09-17T15:12:00Z", showAgo: false, elapsedOnly: false },
    story: "The release notes are pushed and the merge command was refused by a permission check.",
    lastStop: {
      lead: "The stop you snoozed over",
      atUtc: STOPPED,
      text: "Needs you - Merge pull request #3002, or allow me to merge it",
    },
    replyPlaceholder: "Answer the session directly.",
  });

  it("says when it comes back, and what he snoozed over", () => {
    // THE WEAKEST PAGE ON THE SCREEN, and it was weak because the view read neither of these fields: the page said
    // the word "Snoozed" twice and nothing else, so "Answer the session directly" had nothing to refer to.
    render(<WingmanNow now={SNOOZED} at={AT} replyBox={replyBox} />);

    expect(screen.getByText(`Snoozed until ${formatClockTime("2026-09-17T15:12:00Z")}`)).toBeTruthy();
    expect(screen.getByText(`The stop you snoozed over, ${formatClockTime(STOPPED)}:`)).toBeTruthy();
    expect(screen.getByText(/Merge pull request #3002/)).toBeTruthy();
  });

  it("says what ends the snooze from the headline when the Gateway named no moment", () => {
    // A hold that has not landed yet, or one that waits for him. The Gateway sends one or the other, never both.
    const noDeadline = base({ ...SNOOZED, snoozedUntil: null, headline: "Snoozed until you wake it" });
    render(<WingmanNow now={noDeadline} at={AT} />);

    expect(screen.getByText("Snoozed until you wake it")).toBeTruthy();
    expect(screen.queryByText(/Snoozed until \d/)).toBeNull();
  });

  it("offers Wake first, and never a second snooze on a session that is already snoozed", async () => {
    const onUnsnooze = accepts("Unsnoozed.");
    const { container } = render(
      <WingmanNow now={SNOOZED} at={AT} actions={{ onUnsnooze, onOpenTerminal: () => {} }} />,
    );

    const buttons = [...container.querySelectorAll(".wnow-quick button")].map((b) => b.textContent);
    expect(buttons).toEqual(["Wake this session", "Open the terminal"]);
    fireEvent.click(screen.getByText("Wake this session"));
    await waitFor(() => expect(onUnsnooze).toHaveBeenCalled());
  });
});

describe("Now - the one button that destroys something", () => {
  it("puts Close last in the row, set apart from the harmless buttons", () => {
    // It sat between two buttons that do nothing irreversible, drawn exactly like both of them.
    const { container } = render(
      <WingmanNow
        now={base({ state: "done", pillText: "Done" })}
        at={AT}
        actions={{ onSnooze: accepts(), onClose: () => {}, onOpenTerminal: () => {} }}
      />,
    );

    const row = [...container.querySelectorAll(".wnow-quick button")].map((b) => b.textContent);
    expect(row).toEqual(["Snooze this session", "Open the terminal", "Close this session"]);
    expect(container.querySelector(".wnow-btn-apart")!.textContent).toBe("Close this session");
  });
});

describe("Now - a long ask is folded, never shortened", () => {
  // Eight lines of a scheduled prompt were the whole page, and the reply box slid off the bottom of it with every
  // long ask. The WORDS are not touched: they are what was really sent.
  const LONG = [
    "skill /youtube-channel-manager - the daily morning report. Unattended - nobody is watching when you start.",
    "Read .claude/skills/youtube-channel-manager/SKILL.md in full and follow Steps 1-6 exactly;",
    "memory/feedback.md outranks it. Read the channel in YouTube Studio through the browser profile,",
    "scan the niche with outliers.py, read comments, then email Soren ONCE as HTML so it lands by 07:00.",
    "Change nothing on the channel. ASCII only. Do not commit.",
  ].join("\n");

  const asked = (text: string) =>
    base({
      state: "working",
      pillText: "Working",
      lastAsked: { heading: "What it was last asked", text, atUtc: STOPPED, by: null, whenLead: "at" },
    });

  it("holds a long ask to three lines, with all of it one click away", () => {
    const { container } = render(<WingmanNow now={asked(LONG)} at={AT} />);

    const text = container.querySelector(".wnow-asked-text")!;
    expect(text.className).toContain("wnow-asked-clamped");
    // Every word is still there - folded, not summarised.
    expect(text.textContent).toBe(LONG);

    fireEvent.click(screen.getByText("Show all of it"));
    expect(container.querySelector(".wnow-asked-text")!.className).not.toContain("wnow-asked-clamped");
    expect(container.querySelector(".wnow-asked-text")!.textContent).toBe(LONG);

    fireEvent.click(screen.getByText("Show less of it"));
    expect(container.querySelector(".wnow-asked-text")!.className).toContain("wnow-asked-clamped");
  });

  it("offers no control on an ask that already fits", () => {
    const { container } = render(<WingmanNow now={asked("Carry on with the next slice.")} at={AT} />);

    expect(container.querySelector(".wnow-asked-clamped")).toBeNull();
    expect(screen.queryByText("Show all of it")).toBeNull();
  });
});

describe("Now - what sending the box does", () => {
  it("says it in the Gateway's words, under the box, wherever a box is offered", () => {
    const now = base({
      state: "needs-you",
      pillText: "Needs you",
      replyPlaceholder: "Or answer in your own words.",
      replyHint: "Sent to the session as your message. You can answer more than one question in one reply.",
    });
    render(<WingmanNow now={now} at={AT} replyBox={replyBox} />);

    expect(
      screen.getByText("Sent to the session as your message. You can answer more than one question in one reply."),
    ).toBeTruthy();
  });

  it("says nothing about a box that is not there", () => {
    const now = base({ state: "done", pillText: "Done", replyPlaceholder: null, replyHint: null });
    const { container } = render(<WingmanNow now={now} at={AT} replyBox={replyBox} />);

    expect(container.querySelector(".wnow-reply-hint")).toBeNull();
  });
});
