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
// NOT PROVEN HERE: that any Gateway ever sends these objects. The route does not exist yet. These prove the view
// renders the settled shape, not that the shape is produced.
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { formatAgo, formatClockTime, formatWhen, WingmanNow } from "./WingmanNow";
import type { WingmanNow as WingmanNowDto } from "./wingmanNowRead";

afterEach(() => cleanup());

// A fixed reading moment, so "8 minutes ago" is the same sentence on every machine.
const AT = new Date("2026-09-17T11:20:00Z");
const STOPPED = "2026-09-17T11:12:00Z";

function base(overrides: Partial<WingmanNowDto> = {}): WingmanNowDto {
  return {
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
    replyPlaceholder: null,
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
  when: { lead: "Stopped at", atUtc: STOPPED, showAgo: true },
  headline: "Merge pull request #3002, or allow me to merge it",
  story: "The release notes are fixed and pushed to pull request #3002, rebased onto the current main.",
  agentSaid: {
    who: "Claude Code said",
    sentence: "Either merge #3002 yourself, or allow that command and I'll do it.",
  },
  wholeReply: "The Fable review is done, and the notes are fixed and pushed to pull request #3002.",
  needs: {
    heading: "What it needs from you",
    recommends: "It recommends: allow the merge - the notes have been reviewed.",
    question: null,
    verdictId: "v-3002",
    options: [
      {
        index: 1,
        key: "Allow the merge",
        note: "It runs the merge itself. This lands the release notes on main and cannot be undone by the session.",
        recommended: true,
      },
      { index: 2, key: "I will merge it myself", note: "It waits for you. Nothing changes until you merge.", recommended: false },
    ],
  },
  canAnswerByOption: true,
  replyPlaceholder: "Or answer in your own words - for example: allow the merge, tag straight after it.",
  voice: { kind: "play", label: "Play", afterTurnOnText: null },
});

describe("Now - the live stop", () => {
  it("draws the everyday needs-you stop: the pill, when it stopped, the story, the agent's sentence and every option", () => {
    render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onAnswerOption: vi.fn() }} />);

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

  it("answers by option with the index the Gateway gave, and refuses when the Gateway says the stop cannot be answered that way", () => {
    const onAnswerOption = vi.fn();
    const { rerender } = render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onAnswerOption }} />);
    fireEvent.click(screen.getByText("Allow the merge"));
    expect(onAnswerOption).toHaveBeenCalledWith(NEEDS_YOU.needs!.options[0]);

    rerender(<WingmanNow now={base({ ...NEEDS_YOU, canAnswerByOption: false })} at={AT} actions={{ onAnswerOption }} />);
    fireEvent.click(screen.getByText("Allow the merge"));
    expect(onAnswerOption).toHaveBeenCalledTimes(1);
  });

  it("sends the owner's own words to the session and clears the box", () => {
    const onSendReply = vi.fn();
    render(<WingmanNow now={NEEDS_YOU} at={AT} actions={{ onSendReply }} />);
    const box = screen.getByLabelText("Your reply to this session") as HTMLTextAreaElement;
    expect(box.placeholder).toBe(NEEDS_YOU.replyPlaceholder);

    const send = screen.getByText("Send") as HTMLButtonElement;
    expect(send.disabled).toBe(true);
    fireEvent.change(box, { target: { value: "allow the merge" } });
    fireEvent.click(send);
    expect(onSendReply).toHaveBeenCalledWith("allow the merge");
    expect(box.value).toBe("");
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
      when: { lead: "Stopped at", atUtc: "2026-09-17T11:19:56Z", showAgo: true },
      headline: "The session stopped. The Wingman is reading its screen...",
      story: "This usually takes a few seconds.",
      lastWords: { who: "Its last words", text: "I need help with three things: The merge ..." },
      replyPlaceholder: "You can answer now without waiting.",
    });
    render(<WingmanNow now={now} at={AT} actions={{ onSendReply: vi.fn(), onPlayVoice: vi.fn() }} />);

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
      when: { lead: "Working since", atUtc: "2026-09-17T11:14:00Z", showAgo: true },
      headline: "Working on what you asked at 11:14 AM",
      lastAsked: {
        text: "Allow the merge, tag straight after it, and retry the changelog message.",
        atUtc: "2026-09-17T11:14:00Z",
        by: "You",
      },
      replyPlaceholder: "Send it something while it works - it is queued until it is ready.",
      lastStop: {
        lead: "Last stop",
        atUtc: STOPPED,
        text: "Needs you - merge pull request #3002, or allow me to merge it.",
      },
    });
    render(<WingmanNow now={now} at={AT} actions={{ onSendReply: vi.fn() }} />);

    expect(screen.getByText("What it was last asked")).toBeTruthy();
    expect(screen.getByText(/Allow the merge, tag straight after it/)).toBeTruthy();
    expect(screen.getByText(`You, at ${formatClockTime("2026-09-17T11:14:00Z")}`)).toBeTruthy();
    expect(screen.getByText(`Last stop, ${formatClockTime(STOPPED)}:`)).toBeTruthy();
    expect(screen.getByText(/merge pull request #3002, or allow me to merge it\./)).toBeTruthy();
  });

  it("says nothing about who asked when the Gateway did not say", () => {
    const now = base({
      state: "working",
      lastAsked: { text: "Carry on with the next slice.", atUtc: "2026-09-17T11:14:00Z", by: null },
    });
    render(<WingmanNow now={now} at={AT} />);
    expect(screen.getByText(`At ${formatClockTime("2026-09-17T11:14:00Z")}`)).toBeTruthy();
  });

  it("draws just answered: what was sent, that it is working again, and the next session that needs you", () => {
    const onGoToSession = vi.fn();
    const now = base({
      state: "just-answered",
      pillText: "Working again",
      when: { lead: "You answered at", atUtc: "2026-09-17T11:19:40Z", showAgo: true },
      answered: {
        text: "allow the merge",
        atUtc: "2026-09-17T11:19:40Z",
        workingAgainAfterText: "The session started working again 2 seconds later.",
      },
      nextNeedsYou: {
        sessionId: "3f2b19c0-0000-4000-8000-000000000044",
        name: "Dev Reports - Architect",
        label: "Should I open an issue for the dev Gateway?",
      },
      lastStop: {
        lead: "The stop you answered",
        atUtc: STOPPED,
        text: "Merge pull request #3002, or allow me to merge it.",
      },
    });
    render(<WingmanNow now={now} at={AT} actions={{ onGoToSession }} />);

    expect(screen.getByText("What you answered")).toBeTruthy();
    expect(screen.getByText("allow the merge")).toBeTruthy();
    expect(screen.getByText(/The session started working again 2 seconds later\./)).toBeTruthy();
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
      when: { lead: "Stopped at", atUtc: "2026-09-17T11:19:00Z", showAgo: true },
      headline: "Waiting for its Worker to finish the slice J test run",
      story: "The Worker is running the full test gate on pull request 2977.",
      agentSaid: { who: "Claude Code said", sentence: "The Worker is seated and running." },
      calmCard: { heading: "Nothing needed from you", body: null },
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

  it("draws the carrying-on sentence that has no deadline, exactly as the Gateway wrote it", () => {
    const now = base({
      state: "carrying-on",
      calmCard: { heading: "Nothing needed from you", body: null },
      carryingOnDeadline: {
        before: "It turns red if it stops working and none of the sessions it owns is still working.",
        atUtc: null,
        after: null,
      },
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
      when: { lead: "Stopped at", atUtc: "2026-09-17T12:40:00Z", showAgo: false },
      headline: "Release v2.5.0 is tagged and published",
      story: "The merge landed, the full test run passed, v2.5.0 was tagged and the release built.",
      calmCard: {
        heading: "The work is complete",
        body: "Nothing is needed from you. You can close this session when you are ready.",
      },
    });
    render(<WingmanNow now={now} at={AT} actions={{ onSendReply: vi.fn() }} />);

    expect(screen.getByText("Done")).toBeTruthy();
    expect(screen.getByText(`Stopped at ${formatClockTime("2026-09-17T12:40:00Z")}`)).toBeTruthy();
    expect(screen.getByText("The work is complete")).toBeTruthy();
    expect(screen.getByText(/You can close this session when you are ready\./)).toBeTruthy();
    // Done sent no placeholder, so there is no reply box even though the shell offered to send one.
    expect(screen.queryByLabelText("Your reply to this session")).toBeNull();
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
      },
      replyPlaceholder: "Reply if you want it to do something about this.",
    });
    render(<WingmanNow now={now} at={AT} actions={{ onSendReply: vi.fn() }} />);

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
      when: { lead: "Stopped at", atUtc: "2026-09-17T11:18:00Z", showAgo: true },
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
    render(<WingmanNow now={now} at={AT} actions={{ onSendReply: vi.fn() }} />);

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
      <WingmanNow now={now} at={AT} actions={{ onOpenSettings, onSendReply: vi.fn(), onWhyColour: vi.fn() }} />,
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

describe("Now - the voice control", () => {
  it("offers Play with the Gateway's label when the narration is ready", () => {
    const onPlayVoice = vi.fn();
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
        actions={{ onPlayVoice: vi.fn() }}
      />,
    );
    expect(screen.getByText("Preparing audio...")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Preparing audio..." })).toBeNull();
  });

  it("offers to turn voice on, and then says what that did - including whether this stop will be read", () => {
    const onTurnOnVoice = vi.fn();
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
    expect(screen.getByText("Voice is on for this session. This stop will be read aloud in a few seconds.")).toBeTruthy();
  });

  it("draws no voice control at all when there is nothing to read", () => {
    render(
      <WingmanNow
        now={base({ voice: { kind: "none", label: null, afterTurnOnText: null } })}
        at={AT}
        actions={{ onPlayVoice: vi.fn(), onTurnOnVoice: vi.fn() }}
      />,
    );
    expect(screen.queryByText("Play")).toBeNull();
    expect(screen.queryByText("Read this session aloud from now on")).toBeNull();
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
    const when = { lead: "Stopped at", atUtc: STOPPED, showAgo: false };
    expect(formatWhen(when, AT)).toBe(`Stopped at ${formatClockTime(STOPPED)}`);
    expect(formatWhen({ ...when, showAgo: true }, AT)).toBe(`Stopped at ${formatClockTime(STOPPED)}, 8 minutes ago`);
  });
});
