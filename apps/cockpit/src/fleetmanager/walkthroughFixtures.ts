import type { FleetManagerWalkthrough, FleetWalkthroughItem } from "@devthrottle/client-core/fleetmanager/walkthroughClient";

// A walkthrough as the Gateway folds it (the Fleet Manager mission, step 7), for the view's tests. Invented names
// only. Every sentence here stands in for the Gateway's: the view must render each one as given.

export const FM = "80000000-0000-4000-8000-000000000001";
export const LAYOUTS = "80000000-0000-4000-8000-000000000002";
export const RELEASE = "80000000-0000-4000-8000-000000000003";

const noSnooze = { offered: false, label: "Snooze 1 hour", minutes: 60, note: null };
const noClose = {
  offered: false,
  label: "Close the session...",
  refusedText: null,
  confirmTitle: "Close this session?",
  confirmMessage: "",
  confirmLabel: "Close it",
  busyLabel: "Closing...",
};

export function doneItem(): FleetWalkthroughItem {
  return {
    id: "rec-1",
    position: 1,
    positionLabel: "1 of 3",
    kind: "decision",
    title: "Build our own worktree tool?",
    sessionName: null,
    meta: "Decision - not about one session",
    waitLabel: null,
    stepLine: "You said \"Yes.\" - 08:43",
    done: true,
    sessionId: null,
    reading: { heading: "What it needs from you - read by the Wingman", available: false, note: "Not about one session (fake)." },
    advice: { heading: "The Fleet Manager says", text: null, emptyText: "No advice (fake)." },
    screen: { heading: "The screen", offered: false, lines: 14, note: null, loadingText: "Reading (fake)..." },
    answerMode: "none",
    answer: null,
    card: null,
    snooze: noSnooze,
    skipLabel: "Skip for now",
    open: { offered: false, label: "Open the session" },
    close: noClose,
  };
}

export function menuItem(): FleetWalkthroughItem {
  return {
    id: "rec-2",
    position: 2,
    positionLabel: "2 of 3",
    kind: "decision",
    title: "Pick a fire-safety layout",
    sessionName: "Harbour Bistro - Worker - fire-safety mockups",
    meta: "harbour-web - WORKSTATION-A - Claude Code - started by the Fleet Manager yesterday 16:20",
    waitLabel: "waiting 42m",
    stepLine: "Harbour Bistro - Worker - fire-safety mockups - waiting 42m",
    done: false,
    sessionId: LAYOUTS,
    reading: {
      heading: "What it needs from you - read by the Wingman",
      available: true,
      note: null,
      label: "Which fire-safety layout to keep",
      summary: "Both layouts are built and running locally. The other one is deleted.",
      evidenceLead: "In its own words:",
      evidence: "Which layout should I keep?  The other one will be deleted.",
      riskLine: "Risk: irreversible",
    },
    advice: {
      heading: "The Fleet Manager says",
      text: "You picked the long column for the last two client pages, and the client reads on a phone. I'd pick B.",
      emptyText: null,
    },
    screen: { heading: "The screen", offered: true, lines: 14, note: null, loadingText: "Reading the session's last lines..." },
    answerMode: "session",
    answer: {
      verdictId: "verdict-layouts",
      question: "Which layout should I keep?",
      multiple: false,
      parkedReply: false,
      options: [
        { index: 0, label: "A - card grid", note: "B is deleted.", sessionPick: true, fleetManagerPick: false, markText: "its pick" },
        { index: 1, label: "B - single column", note: "A is deleted.", sessionPick: false, fleetManagerPick: true, markText: "Fleet Manager's pick" },
      ],
      pickNote: null,
      sendChosenLabel: "Send the chosen options",
      parkedReplyLabel: "Send the typed reply",
      sendingText: "Sending...",
      recordFailedLead: "The session took your answer, but the Fleet Manager's record was not updated:",
    },
    card: null,
    snooze: { offered: true, label: "Snooze 1 hour", minutes: 60, note: null },
    skipLabel: "Skip for now",
    open: { offered: true, label: "Open the session" },
    close: {
      offered: true,
      label: "Close the session...",
      refusedText: null,
      confirmTitle: "Close Harbour Bistro - Worker - fire-safety mockups?",
      confirmMessage: "Its branch main is fully in origin/main (fake). Closing stops it; it cannot be undone.",
      confirmLabel: "Close it",
      busyLabel: "Closing...",
    },
  };
}

export function cardItem(): FleetWalkthroughItem {
  return {
    id: "rec-3",
    position: 3,
    positionLabel: "3 of 3",
    kind: "ready",
    title: "Merge Release 2.0.7",
    sessionName: "Release 2.0.7",
    meta: "widgets - WORKSTATION-A - Codex - started 09:10",
    waitLabel: "waiting 9m",
    stepLine: "Release 2.0.7 - waiting 9m",
    done: false,
    sessionId: RELEASE,
    reading: {
      heading: "What it needs from you - read by the Wingman",
      available: false,
      note: "The Wingman has no reading of this session's current stop (fake).",
    },
    advice: { heading: "The Fleet Manager says", text: null, emptyText: "The Fleet Manager wrote no advice for this one." },
    screen: { heading: "The screen", offered: false, lines: 14, note: "The session's computer is not reporting (fake).", loadingText: "" },
    answerMode: "fleet-manager",
    answer: null,
    card: {
      id: "rec-3",
      kind: "ready",
      tone: "ready",
      kindLabel: "Ready for you",
      title: "Merge Release 2.0.7",
      filedAtUtc: "2026-09-16T14:21:00Z",
      whoLine: "Fleet Manager - 14:21",
      answered: false,
      actions: [{ label: "Merge", style: "primary", words: "Merge: Merge Release 2.0.7", asksForWords: false, busyLabel: "Recording..." }],
      answerRefusedLead: "Your answer was not recorded, and nothing was passed to the Fleet Manager:",
    },
    snooze: { offered: false, label: "Snooze 1 hour", minutes: 60, note: "Cannot be snoozed now (fake)." },
    skipLabel: "Skip for now",
    open: { offered: true, label: "Open the session" },
    close: { ...noClose, refusedText: "Close is not offered: this session has 3 uncommitted files." },
  };
}

export function walkthrough(items: FleetWalkthroughItem[] = [doneItem(), menuItem(), cardItem()]): FleetManagerWalkthrough {
  const open = items.filter((i) => !i.done).length;
  return {
    generatedAtUtc: "2026-09-16T14:30:00Z",
    fleetManagerSessionId: FM,
    title: "Take me through them",
    intro: "One at a time, most important first (fake).",
    backLabel: "Back to the conversation",
    roundTitle: `This round - ${items.length}`,
    roundIds: items.map((i) => i.id),
    items,
    notInRound: "Not in this round: 2 you snoozed and 4 of the Fleet Manager's sessions that are working.",
    openCount: open,
    endTitle: open > 0 ? "End of the round" : "That is the round.",
    endText: open > 0 ? `${open} items in this round are still waiting (fake).` : "Everything in this round is settled (fake).",
    againLabel: open > 0 ? "Go through the skipped ones again" : null,
    newRoundLabel: null,
    emptyText: items.length === 0 ? "Nothing is waiting on you." : null,
  };
}
