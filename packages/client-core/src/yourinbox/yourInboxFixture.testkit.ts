import type { YourInboxCount, YourInboxList, YourInboxMessage } from "./yourInboxClient";

// Gateway answers for the Your inbox screens' tests. Every word is deliberately odd - no word a screen could write by
// itself - so a screen that prints anything but the Gateway's words fails the test that reads it (rule 7).

export function oddYourMessage(over: Partial<YourInboxMessage> = {}): YourInboxMessage {
  return {
    id: "y-1",
    subject: "Wombat ledger is ready (gateway)",
    body: "The quokka notes are filed.\nSecond line of the wombat body.",
    state: "unread",
    stateLabel: "Fresh as dew (gateway)",
    tone: "red",
    unread: true,
    from: { kind: "session", label: "#412 Wombat - Scribe (gateway)" },
    links: [
      { kind: "session", label: "Visit the scribe (gateway)", target: "11111111-1111-4111-8111-111111111111" },
      { kind: "report", label: "Peek at the scroll (gateway)", target: "99999999-9999-4999-8999-999999999999" },
    ],
    actions: [
      { state: "read", label: "Tick it off (gateway)" },
      { state: "archived", label: "Shelve it (gateway)" },
    ],
    replyTo: { target: "session", label: "Answer the scribe directly (gateway)" },
    sentAtUtc: "2026-11-05T09:02:00Z",
    readAtUtc: null,
    archivedAtUtc: null,
    replies: [],
    agentActions: [],
    agentLine: null,
    ...over,
  };
}

export function oddFactoryMessage(over: Partial<YourInboxMessage> = {}): YourInboxMessage {
  return oddYourMessage({
    id: "y-2",
    subject: "Turned down: move the moon (gateway)",
    body: "The contract fixes the moon.",
    state: "read",
    stateLabel: "Already glanced (gateway)",
    tone: "grey",
    unread: false,
    from: { kind: "factory", label: "Zeta Works - Big Desk (gateway)" },
    links: [{ kind: "factory", label: "Wander to Zeta Works (gateway)", target: "zeta-works" }],
    actions: [
      { state: "unread", label: "Make it fresh again (gateway)" },
      { state: "archived", label: "Shelve it (gateway)" },
    ],
    replyTo: { target: "factory", label: "Drop a note in the Zeta pigeonhole (gateway)" },
    ...over,
  });
}

export function oddYourList(over: Partial<YourInboxList> = {}): YourInboxList {
  return {
    view: "inbox",
    count: 2,
    unread: 1,
    unreadLabel: "one fresh (gateway)",
    filters: [
      { view: "unread", label: "Only fresh (gateway)", count: 1 },
      { view: "inbox", label: "Everything here (gateway)", count: 2 },
      { view: "archived", label: "The shelf (gateway)", count: 7 },
    ],
    empty: "Not a squeak (gateway).",
    messages: [oddYourMessage(), oddFactoryMessage()],
    ...over,
  };
}

export function oddYourCount(over: Partial<YourInboxCount> = {}): YourInboxCount {
  return { unread: 3, label: "three fresh ones (gateway)", ...over };
}
