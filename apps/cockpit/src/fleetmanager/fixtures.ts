import type { FleetManagerPage, FleetOutcomeCard } from "@devthrottle/client-core/fleetmanager/pageClient";

// Synthetic Gateway answers for the Fleet Manager page's tests. Neutral names only: this repository is public.

export const FM_SESSION = "80000000-0000-4000-8000-000000000001";

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
    { label: "Merge", style: "primary", words: "Merge: Fix the flaky list test", asksForWords: false },
    {
      label: "Send it back...",
      style: "ghost",
      words: null,
      asksForWords: true,
      wordsPrefix: "Send it back: Fix the flaky list test. ",
      placeholder: "What should change? (fake)",
      sendLabel: "Send it back",
    },
  ],
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
  actions: [{ label: "Got it", style: "secondary", words: "Got it: Build our own. Keep their rules, not their tools.", asksForWords: false }],
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
    },
    { label: "Always run both.", style: "secondary", words: "Always run both.", asksForWords: false },
  ],
};

export const ANSWERED_CARD: FleetOutcomeCard = {
  ...DECISION_CARD,
  id: "80000000-0000-4000-8000-0000000000a4",
  answered: true,
  answerLabel: "Answered 10:40 (fake)",
  answer: "Always run both, for now.",
  actions: [],
};

export function morningPage(): FleetManagerPage {
  return {
    generatedAtUtc: "2026-09-16T14:40:00Z",
    fleetManagerSessionId: FM_SESSION,
    noConversationText: null,
    waitingCount: 3,
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
    notMine: { count: 26, lead: "26 sessions are not the Fleet Manager's. (fake)", rest: "They still ask you directly. (fake)" },
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
    notMine: { count: 0, lead: "No session asks you directly. (fake)", rest: "Every live session is owned. (fake)" },
  };
}
