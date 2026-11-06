import type { FactoryInboxList, FactoryInboxMessage, FactoryInboxSummary, FactoryInboxThread } from "./factoryInboxClient";

// Gateway answers for the factory inbox screens' tests. Every word is deliberately odd - no word a screen could write
// by itself - so a screen that prints anything but the Gateway's words fails the test that reads it (rule 7).

export function oddMessage(over: Partial<FactoryInboxMessage> = {}): FactoryInboxMessage {
  return {
    id: "m-1",
    factory: "zeta-works",
    text: "Quokka ledger needs a gentler tone",
    rawText: null,
    note: "bad",
    noteLabel: "Bad-ish (gateway)",
    source: "typed",
    sourceLabel: "Keyed in (gateway)",
    surface: "phone",
    sentAtUtc: "2026-11-05T09:02:00Z",
    state: "decided",
    stateLabel: "Handed to a writer (gateway)",
    tone: "ok",
    mergedInto: null,
    links: [],
    bundleId: "b-1",
    decision: null,
    trail: [
      { step: "sent", label: "Left from the pocket (gateway)", atUtc: "2026-11-05T09:02:00Z" },
      { step: "received", label: "Filed by the clerk (gateway)", atUtc: "2026-11-05T09:04:00Z" },
      { step: "with-boss", label: "On the big desk (gateway)", atUtc: null },
    ],
    ...over,
  };
}

export function oddList(): FactoryInboxList {
  return {
    count: 2,
    factories: [
      {
        factory: "zeta-works",
        title: "Zeta Works (gateway)",
        row: { line: "Quokka ledger needs a gentler tone", waitingLine: "Two still pondering (gateway)", badge: "Mulling (gateway)", tone: "amber" },
        waiting: 2,
        lastMessage: oddMessage(),
      },
      {
        factory: "alpha-mill",
        title: "Alpha Mill (gateway)",
        row: { line: "Silence so far (gateway)", waitingLine: null, badge: "Empty-ish (gateway)", tone: "grey" },
        waiting: 0,
        lastMessage: null,
      },
    ],
  };
}

export function oddThread(): FactoryInboxThread {
  return {
    factory: "zeta-works",
    title: "Zeta Works (gateway)",
    count: 2,
    // Newest first, as the Gateway answers.
    messages: [
      oddMessage({
        id: "m-2",
        text: "Second thought about the wombat",
        sentAtUtc: "2026-11-05T09:10:00Z",
        noteLabel: "",
        note: "none",
        state: "decided",
        stateLabel: "Turned down (gateway)",
        tone: "red",
        trail: [{ step: "decided", label: "Turned down: the contract says no (gateway)", atUtc: "2026-11-05T09:12:00Z" }],
      }),
      oddMessage(),
    ],
  };
}

export function oddSummary(): FactoryInboxSummary {
  return {
    factory: "zeta-works",
    sinceUtc: "2026-11-04T09:00:00Z",
    untilUtc: "2026-11-05T09:00:00Z",
    count: 2,
    line: "Two arrived; one waved through, one turned down (gateway).",
    byDecision: [],
    items: [],
  };
}
