// TEST SUPPORT ONLY - never imported by product code.
//
// A GET /gateway/mail-desk/channels answer in the shape of docs/qa/mail-desk/phase-7/CONTRACT.md section 2, whose
// WORDS ARE DELIBERATELY ODD. Every sentence a page shows is one no client would write for itself: a provider called
// "outlook" whose label is not "Outlook", decision words whose labels are not the obvious ones, a count that is not
// the decision's usual count. A page that renders the Gateway's words verbatim shows them; a page that works out its
// own words from `provider`, `mode`, `decision` or `byDecision` shows something else and fails.
//
// The repository is public: addresses use email@devthrottle.com and the reserved example domains only.
import type { MailDeskChannel, MailDeskChannels } from "./mailDeskClient";

function channel(overrides: Partial<MailDeskChannel> & Pick<MailDeskChannel, "id" | "address">): MailDeskChannel {
  return {
    kind: "email",
    provider: "outlook",
    providerLabel: "Owlpost Seven",
    machines: ["kettle-03"],
    machinesLabel: "kettle-03, alone on the hill",
    mode: "observe",
    modeLabel: "Sideways - hums quietly and touches nothing",
    modeTone: "neutral",
    factory: "mail-desk",
    seat: "mail-desk",
    signedIn: { state: "yes", label: "Zebra crossing approved", tone: "good" },
    lastCheck: { utc: "2026-10-19T08:15:00Z", outcome: "ok", label: "Checked at a quarter past the moon", tone: "good" },
    today: {
      total: 0,
      byDecision: { route: 0, archive: 0, junk: 0, "keep-for-owner": 0, "mail-desk-agent": 0, failed: 0 },
      label: "Not a single pebble today",
      counts: [
        { decision: "route", label: "Sent to the lighthouse", count: 0 },
        { decision: "archive", label: "Folded into a paper swan", count: 0 },
        { decision: "junk", label: "Fed to the goat", count: 0 },
        { decision: "keep-for-owner", label: "Tucked under your pillow", count: 0 },
        { decision: "mail-desk-agent", label: "Handed to the night owl", count: 0 },
        { decision: "failed", label: "Lost in the fog", count: 0 },
      ],
    },
    createdUtc: "2026-10-18T10:00:00Z",
    createdBy: "email@devthrottle.com",
    changedUtc: "2026-10-18T10:00:00Z",
    changedBy: "email@devthrottle.com",
    removedUtc: null,
    ...overrides,
  };
}

/** Three mailboxes: signed in and live with decisions today, not signed in, and observing with no check yet. */
export function oddChannels(): MailDeskChannels {
  return structuredClone({
    count: 3,
    title: "The Desk of Many Letters",
    summary: "Every envelope the owls carry, counted twice a fortnight; nothing moves while an owl is asleep.",
    emptyLabel: "The pigeonholes are empty - whistle for an owl with: cc-devthrottle maildesk channel add <address>",
    channels: [
      channel({
        id: "ch-1",
        address: "a.very.long.mailbox.name.that.has.to.wrap.rather.than.be.cut.short@devthrottle.com",
        mode: "live",
        modeLabel: "Full gallop - files, flags and sweeps for real",
        modeTone: "attention",
        machines: ["kettle-03", "teapot.example"],
        machinesLabel: "kettle-03, then teapot.example if kettle-03 is napping",
        today: {
          total: 11,
          byDecision: { route: 4, archive: 7, junk: 0, "keep-for-owner": 0, "mail-desk-agent": 0, failed: 0 },
          label: "Eleven-ish pebbles sorted by teatime",
          counts: [
            { decision: "route", label: "Sent to the lighthouse", count: 4 },
            { decision: "archive", label: "Folded into a paper swan", count: 7 },
            { decision: "junk", label: "Fed to the goat", count: 0 },
            { decision: "keep-for-owner", label: "Tucked under your pillow", count: 0 },
            { decision: "mail-desk-agent", label: "Handed to the night owl", count: 0 },
            { decision: "failed", label: "Lost in the fog", count: 0 },
          ],
        },
      }),
      channel({
        id: "ch-2",
        address: "email@devthrottle.com",
        providerLabel: "Not Outlook At All",
        signedIn: { state: "failing", label: "The drawbridge is up on kettle-03: lower it with the brass key", tone: "bad" },
        lastCheck: { utc: "2026-10-19T08:20:00Z", outcome: "failed", label: "The last knock went unanswered", tone: "bad" },
      }),
      channel({
        id: "ch-3",
        address: "letters@post.example",
        machines: ["kettle-03", "teapot.example", "samovar.example"],
        machinesLabel: "kettle-03, then teapot.example, then samovar.example - each only when the one before it snores",
        signedIn: { state: "unknown", label: "Nobody has rung the bell yet", tone: "neutral" },
        lastCheck: { utc: null, outcome: null, label: "Never knocked - the owls are not hatched", tone: "neutral" },
      }),
    ],
  });
}

/** The same envelope with no mailbox on the desk. */
export function noChannels(): MailDeskChannels {
  return { ...oddChannels(), count: 0, channels: [] };
}

/** Every sentence a card must show for `c`, in reading order. */
export function wordsOf(c: MailDeskChannel): string[] {
  return [
    c.address,
    c.providerLabel,
    c.modeLabel,
    c.machinesLabel,
    c.signedIn.label,
    c.lastCheck.label,
    c.today.label,
    ...c.today.counts.flatMap((entry) => [entry.label, String(entry.count)]),
  ];
}
