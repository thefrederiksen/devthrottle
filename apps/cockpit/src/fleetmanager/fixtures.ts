import type { FleetManagerPage, FleetOutcomeCard } from "@devthrottle/client-core/fleetmanager/pageClient";
import type { FleetStanding } from "@devthrottle/client-core/fleetmanager/standingClient";
import type { FleetManagerPlacement } from "@devthrottle/client-core/settings/fleetManagerClient";

// Synthetic Gateway answers for the Fleet Manager page's tests. Neutral names only: this repository is public.

export const FM_SESSION = "80000000-0000-4000-8000-000000000001";

function act(label: string, offered: boolean, extra: { note?: string | null; confirmTitle?: string; confirmMessage?: string } = {}) {
  return { offered, label, busyLabel: `${label} busy (fake)`, note: extra.note ?? null, ...extra };
}

/** The owner's lessons and preferences as the Gateway folds them (issue #3559): one lesson the Fleet Manager kept that
 *  waits for the owner, one the owner kept, and one preference. Every word is fake, so a test proves it is rendered. */
export function standing(): FleetStanding {
  const remove = (what: string) =>
    act("Remove (fake)", true, { confirmTitle: `Remove this ${what}? (fake)`, confirmMessage: "No Fleet Manager is given it again. (fake)" });
  return {
    mistake: act("That was a mistake (fake)", true),
    boxTitle: "What should it never do again? (fake)",
    boxNote: "Kept in your words, exactly. (fake)",
    boxPlaceholder: "For example... (fake)",
    maxLessonLength: 500,
    keepLabel: "Keep (fake)",
    keepBusyLabel: "Keeping... (fake)",
    keptSentence: "Kept. The Fleet Manager is told. (fake)",
    saveLabel: "Save (fake)",
    saveBusyLabel: "Saving... (fake)",
    cancelLabel: "Cancel (fake)",
    showLabel: "Lessons and preferences (3) (fake)",
    hideLabel: "Hide lessons and preferences (fake)",
    waitingNote: "1 lesson waits for you to confirm. (fake)",
    lessons: {
      title: "Lessons (fake)",
      count: 2,
      note: "Only confirmed lessons are given to the Fleet Manager. (fake)",
      emptyText: null,
      rows: [
        {
          id: "lesson-waiting",
          kind: "lesson",
          text: "Ask before a release.",
          mistakeLine: null,
          keptLine: "Kept 6 October 2026 by the Fleet Manager (fake)",
          maxLength: 500,
          removeAction: "remove this lesson (fake)",
          statusLine: "Kept by the Fleet Manager - confirm? (fake)",
          tone: "waiting",
          confirm: act("Confirm (fake)", true),
          edit: act("Edit (fake)", true),
          remove: remove("lesson"),
        },
        {
          id: "lesson-confirmed",
          kind: "lesson",
          text: "Never message every session when none is stuck.\nCheck first.",
          mistakeLine: "What went wrong: messaged all 36 sessions (fake)",
          keptLine: "Kept 5 October 2026 by you (fake)",
          maxLength: 500,
          removeAction: "remove this lesson (fake)",
          statusLine: "Confirmed 5 October 2026 (fake)",
          tone: "confirmed",
          confirm: act("", false),
          edit: act("Edit (fake)", true, { note: "The Fleet Manager is told the new words. (fake)" }),
          remove: remove("lesson"),
        },
      ],
    },
    preferences: {
      title: "Standing preferences (fake)",
      count: 1,
      note: null,
      emptyText: null,
      rows: [
        {
          id: "pref-1",
          kind: "preference",
          text: "Merge docs on green.",
          mistakeLine: null,
          keptLine: "Kept 2 October 2026 by the Fleet Manager (fake)",
          maxLength: 2000,
          removeAction: "remove this preference (fake)",
          statusLine: null,
          tone: "preference",
          confirm: act("", false),
          edit: act("Edit (fake)", true),
          remove: remove("preference"),
        },
      ],
    },
  };
}

export const READY_CARD: FleetOutcomeCard = {
  id: "80000000-0000-4000-8000-0000000000a1",
  kind: "ready",
  tone: "ready",
  kindLabel: "Ready for you (fake)",
  title: "Fix the flaky list test",
  filedAtUtc: "2026-09-16T14:14:00Z",
  whoLine: "Fleet Manager - 10:14 (fake)",
  ready: {
    riskLabel: "Risk low (fake)",
    riskTone: "low",
    facts: [
      { label: "Checks", value: "passed" },
      { label: "Tested", value: "3 of 3 live" },
      { label: "Reviewed by", value: "a second reviewer, 1 finding fixed" },
    ],
    change: "The list now waits for the redraw itself. Nothing a user sees changes.",
    pullRequest: { label: "Open pull request #2934", url: "/example/acme/widgets/pull/2934" },
  },
  answered: false,
  actions: [
    { label: "Merge", style: "primary", words: "Merge: Fix the flaky list test", asksForWords: false, busyLabel: "Recording... (fake)" },
    {
      label: "Send it back...",
      style: "ghost",
      words: null,
      asksForWords: true,
      wordsPrefix: "Send it back: Fix the flaky list test. ",
      placeholder: "What should change? (fake)",
      sendLabel: "Send it back",
      cancelLabel: "Cancel (fake)",
      busyLabel: "Recording... (fake)",
    },
  ],
  answerRefusedLead: "Your answer was not recorded (fake):",
};

export const FINDING_CARD: FleetOutcomeCard = {
  id: "80000000-0000-4000-8000-0000000000a2",
  kind: "finding",
  tone: "finding",
  kindLabel: "Finding (fake)",
  title: "Build our own. Keep their rules, not their tools.",
  filedAtUtc: "2026-09-16T14:31:00Z",
  whoLine: "Fleet Manager - 10:31 (fake)",
  finding: {
    answer: "Both reviews say the same thing from different sides.",
    reason: "Their tools run agents we cannot see, and they stop short of merged.",
    links: [
      { label: "Open tool-a-review.md", url: "/example/reports/tool-a-review.md" },
      { label: "Open tool-b-review.md", url: "/example/reports/tool-b-review.md" },
    ],
  },
  answered: false,
  actions: [
    {
      label: "Got it",
      style: "secondary",
      words: "Got it: Build our own. Keep their rules, not their tools.",
      asksForWords: false,
      busyLabel: "Recording... (fake)",
    },
  ],
  answerRefusedLead: "Your answer was not recorded (fake):",
};

export const DECISION_CARD: FleetOutcomeCard = {
  id: "80000000-0000-4000-8000-0000000000a3",
  kind: "decision",
  tone: "decision",
  kindLabel: "Decision - only you can make this (fake)",
  title: "When our own check runs, does it replace the second reviewer, or add to it?",
  filedAtUtc: "2026-09-16T14:32:00Z",
  whoLine: "Fleet Manager - 10:32 (fake)",
  decision: {
    question: null,
    options: [
      { text: "Replace it for ordinary changes. Keep the reviewer only for missions.", recommended: true },
      { text: "Always run both.", recommended: false },
    ],
    recommendedLabel: "Recommended (fake)",
    why: "Why: running both doubled the time on the last four changes and found nothing extra.",
  },
  answered: false,
  actions: [
    {
      label: "Replace it for ordinary changes. Keep the reviewer only for missions.",
      style: "primary",
      words: "Replace it for ordinary changes. Keep the reviewer only for missions.",
      asksForWords: false,
      busyLabel: "Recording... (fake)",
    },
    { label: "Always run both.", style: "secondary", words: "Always run both.", asksForWords: false, busyLabel: "Recording... (fake)" },
  ],
  answerRefusedLead: "Your answer was not recorded (fake):",
};

export const ANSWERED_CARD: FleetOutcomeCard = {
  ...DECISION_CARD,
  id: "80000000-0000-4000-8000-0000000000a4",
  answered: true,
  answerLabel: "Answered 10:40 (fake)",
  answer: "Always run both, for now.",
  answerDelivery: "Waiting to reach the Fleet Manager (fake).",
  actions: [],
};

export function morningPage(): FleetManagerPage {
  return {
    generatedAtUtc: "2026-09-16T14:40:00Z",
    fleetManagerSessionId: FM_SESSION,
    noConversationText: null,
    waitingCount: 3,
    walkthroughLabel: "Take me through them (fake)",
    quickPrompts: [{ label: "What did I miss?", words: "What did I miss?" }],
    cards: [READY_CARD, FINDING_CARD, DECISION_CARD],
    waiting: {
      title: "Waiting on you (fake)",
      count: 3,
      tone: "attention",
      items: [
        {
          id: DECISION_CARD.id,
          title: "When our own check runs, does it replace the second reviewer?",
          meta: "Decision (fake)",
          label: "Asks which check to keep (fake Wingman label)",
          age: "8m",
          dot: "red",
          attention: true,
          sessionId: null,
        },
        {
          id: READY_CARD.id,
          title: "Fix the flaky list test",
          meta: "Ready - risk low - checks passed - Fix the flaky list test",
          label: null,
          age: "26m",
          dot: "red",
          attention: true,
          sessionId: "80000000-0000-4000-8000-000000000011",
        },
        {
          id: FINDING_CARD.id,
          title: "Build our own. Keep their rules, not their tools.",
          meta: "Finding",
          label: null,
          age: "9m",
          dot: "red",
          attention: true,
          sessionId: null,
        },
      ],
      emptyText: null,
      note: null,
    },
    underWay: {
      title: "Under way (fake)",
      count: 2,
      tone: "plain",
      items: [
        {
          id: "80000000-0000-4000-8000-000000000012",
          title: "Review of tool C",
          meta: "widgets-internal - 1h 29m",
          age: null,
          dot: "blue",
          attention: false,
          sessionId: "80000000-0000-4000-8000-000000000012",
        },
        {
          id: "80000000-0000-4000-8000-000000000011",
          title: "Checking: Fix the flaky list test",
          meta: "widgets - 26m",
          age: null,
          dot: "grey",
          attention: false,
          sessionId: "80000000-0000-4000-8000-000000000011",
        },
      ],
      emptyText: null,
      note: null,
    },
    landed: {
      title: "Answered today (fake)",
      count: 1,
      tone: "plain",
      items: [
        {
          id: "80000000-0000-4000-8000-0000000000a9",
          title: "Session tree on the web",
          meta: "You said \"Merge: Session tree on the web\" at 01:52",
          age: null,
          dot: "green",
          attention: false,
          sessionId: null,
        },
      ],
      emptyText: null,
      note: "What landed today needs the merge itself, which the Gateway does not record yet. (fake)",
    },
    notMine: {
      count: 2,
      lead: "2 sessions are not the Fleet Manager's. (fake)",
      rest: "They still ask you directly. (fake)",
      showLabel: "Hand sessions to the Fleet Manager... (fake)",
      hideLabel: "Hide the list (fake)",
      listTitle: "Sessions that ask you directly (fake)",
      listNote: "Hand a session over and the Fleet Manager owns it. (fake)",
      sessions: [
        {
          id: "sess-loose-1",
          title: "Invoice export - waiting on a question",
          meta: "Needs you - started 2h (fake)",
          age: null,
          dot: "red",
          attention: true,
          sessionId: "sess-loose-1",
          action: {
            to: "fleet-manager",
            label: "Hand to the Fleet Manager (fake)",
            title: "The Fleet Manager owns it from now on. (fake)",
            busyLabel: "Handing it over... (fake)",
          },
        },
        {
          id: "sess-loose-2",
          title: "Docs site - working",
          meta: "docs - started 40m (fake)",
          age: null,
          dot: "blue",
          attention: false,
          sessionId: "sess-loose-2",
          action: {
            to: "fleet-manager",
            label: "Hand to the Fleet Manager (fake)",
            title: "The Fleet Manager owns it from now on. (fake)",
            busyLabel: "Handing it over... (fake)",
          },
        },
      ],
    },
  };
}

export function emptyPage(): FleetManagerPage {
  return {
    generatedAtUtc: "2026-09-16T14:40:00Z",
    fleetManagerSessionId: null,
    noConversationText: "There is no Fleet Manager conversation yet. (fake)",
    waitingCount: 0,
    quickPrompts: [{ label: "What did I miss?", words: "What did I miss?" }],
    cards: [],
    waiting: { title: "Waiting on you", count: 0, tone: "plain", items: [], emptyText: "Nothing is waiting on you. (fake)", note: null },
    underWay: { title: "Under way", count: 0, tone: "plain", items: [], emptyText: "No Fleet Manager is marked. (fake)", note: null },
    landed: { title: "Answered today", count: 0, tone: "plain", items: [], emptyText: "No Ready card was answered today. (fake)", note: null },
    notMine: {
      count: 0,
      lead: "No session asks you directly. (fake)",
      rest: "Every live session is owned. (fake)",
      showLabel: null,
      hideLabel: "Hide the list (fake)",
      listTitle: "Sessions that ask you directly (fake)",
      listNote: "There is no running Fleet Manager. (fake)",
      sessions: [],
    },
  };
}

function placementAction(label: string, offered: boolean, note: string | null = null) {
  return { offered, label, note, busyLabel: `${label} busy (fake)` };
}

export function startFresh(offered: boolean) {
  return {
    ...placementAction("Start fresh (fake)", offered),
    confirmTitle: offered ? "Start a fresh one? (fake)" : null,
    confirmMessage: offered ? "A brand new one starts and the old one closes (fake)." : null,
  };
}

/** The Fleet Manager setting's answer in each state, with the page decisions the Gateway makes for it. */
export function placement(state: "running" | "not-running" | "unreachable", thinking = false): FleetManagerPlacement {
  const running = state === "running";
  // What the Gateway decides for the page in each state (fake words, so a test proves they are rendered as sent).
  const page = {
    where: "Claude Code on WORKSTATION-A",
    changeLabel: "(change)",
    composerUsable: running,
    composerPlaceholder: "Tell the Fleet Manager (fake)...",
    composerOffText: running ? null : "not running (fake off text)",
    composerHint: "Enter sends (fake hint).",
    quickPromptsUsable: running,
    quickPromptBusyLabel: "Sending (fake)...",
    thinkingShown: running && thinking,
    notRunningBarShown: !running,
    settingsLabel: "Move it in Settings",
    startFresh: startFresh(running),
  };
  return {
    agent: "ClaudeCode",
    agentLabel: "Claude Code",
    machine: "WORKSTATION-A",
    isDefault: false,
    agentNote: "",
    agents: [],
    machineNote: "",
    machines: [],
    save: placementAction("Save", true),
    generatedAtUtc: "2026-09-16T14:40:00Z",
    status: {
      state,
      sentence: running ? "Running now (fake)." : "The Fleet Manager is not running. Start it where the setting says (fake).",
      line: running ? (thinking ? "thinking, watching 2 sessions (fake)" : "idle, watching 2 sessions (fake)") : "not running (fake)",
      thinking,
      tone: running ? "ok" : state === "unreachable" ? "bad" : "idle",
      sessionId: FM_SESSION,
      watching: running ? 2 : 0,
      open: placementAction("Open it", running),
      start: placementAction("Start it", state === "not-running", state === "not-running" ? "Up to 90 seconds (fake)." : null),
      restart: placementAction("Restart it", running),
      page,
    },
  };
}
