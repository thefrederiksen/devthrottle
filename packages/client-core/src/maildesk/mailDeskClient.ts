// The Mail Desk page's read (Mail Desk mission, phase 7): the typed, same-origin read of GET /gateway/mail-desk/channels,
// the active mailboxes only. The shape is docs/qa/mail-desk/phase-1/CONTRACT.md section 5 (the channel row) plus
// docs/qa/mail-desk/phase-7/CONTRACT.md section 2 (the words and tones the page shows); every field here is one of
// theirs.
//
// CRITICAL RULE 7 - THE GATEWAY DECIDES EVERY WORD. "Outlook", "Kept for you", "3 emails decided today", which machine
// is tried first, and which tone a sign-in carries are all folded once on the Gateway (MailDeskChannelFold). This
// client carries them verbatim; it never turns a provider, a mode or a decision word into words of its own.
//
// THE ANSWER HAS TWO KINDS, and neither is a failure:
//   - `channels`: the Gateway answered 200 with the envelope (possibly no channels);
//   - `off`: 404 - the Mail Desk switch is off for this account (MailDeskGate answers 404 with no body, so an account
//     that is off cannot tell Mail Desk exists) - or the app's own HTML shell, which is what a Gateway from before the
//     Mail Desk answers for a route it never mapped.
// Anything else (a fault, a body that is not the contract) is thrown, so a failure can never look like "switched off".
//
// AN ANSWER THAT BREAKS THE CONTRACT IS THROWN, NEVER DRAWN AS A GUESS: a missing label, a tone the contract does not
// list, a count that is not a number. Each would otherwise reach the screen as something plausible.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/** The Mail Desk routes' prefix (phase 1 contract, section 5). */
export const MAIL_DESK_CHANNELS_PATH = "/gateway/mail-desk/channels";

/** The only thing a client turns into a colour on a sign-in or a check. */
export type MailDeskTone = "good" | "bad" | "neutral";
/** The mode's colour: live mode acts on the mailbox, so it stands out. */
export type MailDeskModeTone = "neutral" | "attention";

/** Whether the mailbox is signed in on the machine that screens it, as the Gateway folded it from the last check. */
export interface MailDeskSignedIn {
  state: "unknown" | "yes" | "failing";
  label: string;
  tone: MailDeskTone;
}

/** The Mail Desk start's last check of the mailbox. `utc` and `outcome` are null before the first check. */
export interface MailDeskLastCheck {
  utc: string | null;
  outcome: string | null;
  label: string;
  tone: MailDeskTone;
}

/** One decision word with its label and today's count. */
export interface MailDeskDecisionCount {
  decision: string;
  label: string;
  count: number;
}

/** The channel's decisions since local midnight in the account's own time zone. */
export interface MailDeskToday {
  total: number;
  byDecision: Record<string, number>;
  /** "No emails decided today", "1 email decided today", "N emails decided today". */
  label: string;
  /** Every decision word in the Gateway's order, zeros included. */
  counts: MailDeskDecisionCount[];
}

/** One mailbox on the Mail Desk: the phase 1 channel row with phase 7's words and tones. */
export interface MailDeskChannel {
  id: string;
  kind: string;
  provider: string;
  providerLabel: string;
  address: string;
  /** In order of preference. */
  machines: string[];
  machinesLabel: string;
  mode: "observe" | "live";
  modeLabel: string;
  modeTone: MailDeskModeTone;
  factory: string;
  seat: string;
  signedIn: MailDeskSignedIn;
  lastCheck: MailDeskLastCheck;
  today: MailDeskToday;
  createdUtc: string;
  createdBy: string;
  changedUtc: string;
  changedBy: string;
  removedUtc: string | null;
}

/** The 200 answer of GET /gateway/mail-desk/channels. */
export interface MailDeskChannels {
  count: number;
  channels: MailDeskChannel[];
  title: string;
  summary: string;
  /** Shown in place of the cards when there is no channel. */
  emptyLabel: string;
}

export type MailDeskAnswer = { kind: "channels"; envelope: MailDeskChannels } | { kind: "off" };

const UNREADABLE = "The Gateway sent a Mail Desk list this app cannot read";

/** Read the active channels. 404 is the switch being off; every other failure is thrown. */
export async function getMailDeskChannels(signal?: AbortSignal): Promise<MailDeskAnswer> {
  const res = await gatewayFetch(MAIL_DESK_CHANNELS_PATH, {
    headers: { ...authHeaders(), Accept: "application/json" },
    signal,
  });

  if (res.status === 404) return { kind: "off" };
  if (!res.ok) throw await GatewayError.from(res, "read the Mail Desk");

  // A Gateway from before the Mail Desk: the route is not mapped, so the read reached the page fallback.
  if (contentType(res) === "text/html") return { kind: "off" };
  if (contentType(res) !== "application/json") {
    throw new GatewayError(
      502,
      `The Gateway answered the Mail Desk list with ${contentType(res) || "an unlabelled body"} instead of its mailboxes.`,
    );
  }

  // The Gateway WAS reached; a body that is not JSON is its fault and is said so, never left to surface as a
  // SyntaxError, which the error wording reads as "cannot reach the Gateway".
  const body = await res.text();
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    throw new GatewayError(502, `${UNREADABLE}: its answer is not valid JSON.`);
  }
  return { kind: "channels", envelope: readEnvelope(parsed) };
}

function contentType(res: Response): string {
  return (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** A label that says something: a string with at least one character that is not a space. */
function isLabel(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function isCount(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= 0;
}

function fail(what: string): never {
  throw new GatewayError(502, `${UNREADABLE}: ${what}.`);
}

function label(source: Record<string, unknown>, field: string, where: string): string {
  const value = source[field];
  if (!isLabel(value)) fail(`${where} has no ${field}`);
  return value;
}

const TONES: ReadonlyArray<MailDeskTone> = ["good", "bad", "neutral"];
const MODE_TONES: ReadonlyArray<MailDeskModeTone> = ["neutral", "attention"];

function tone<T extends string>(value: unknown, allowed: ReadonlyArray<T>, where: string): T {
  if (typeof value !== "string" || !(allowed as ReadonlyArray<string>).includes(value)) {
    fail(`${where} has the tone ${JSON.stringify(value)}, which is not one of ${allowed.join(", ")}`);
  }
  return value as T;
}

function readEnvelope(value: unknown): MailDeskChannels {
  if (!isObject(value)) fail("its answer is not an object");
  if (!Array.isArray(value.channels)) fail("its answer has no list of channels");
  if (!isCount(value.count)) fail("its answer has no count");
  const channels = value.channels.map((row, index) => readChannel(row, `channel ${index + 1}`));
  return {
    count: value.count,
    channels,
    title: label(value, "title", "its answer"),
    summary: label(value, "summary", "its answer"),
    emptyLabel: label(value, "emptyLabel", "its answer"),
  };
}

function readChannel(value: unknown, where: string): MailDeskChannel {
  if (!isObject(value)) fail(`${where} is not an object`);
  const address = label(value, "address", where);
  const at = `the channel ${address}`;
  const signedIn = value.signedIn;
  const lastCheck = value.lastCheck;
  const today = value.today;
  if (!isObject(signedIn)) fail(`${at} has no signedIn`);
  if (!isObject(lastCheck)) fail(`${at} has no lastCheck`);
  if (!isObject(today)) fail(`${at} has no today`);
  if (!Array.isArray(today.counts)) fail(`${at} has no today.counts`);
  if (!isCount(today.total)) fail(`${at} has no today.total`);
  const counts = today.counts.map((entry, index): MailDeskDecisionCount => {
    const place = `${at}, decision count ${index + 1}`;
    if (!isObject(entry)) fail(`${place} is not an object`);
    if (!isCount(entry.count)) fail(`${place} has no count`);
    return { decision: label(entry, "decision", place), label: label(entry, "label", place), count: entry.count };
  });
  // The fields the page does not show are carried as the Gateway sent them; only what reaches the screen is checked.
  return {
    ...(value as unknown as MailDeskChannel),
    address,
    providerLabel: label(value, "providerLabel", at),
    machinesLabel: label(value, "machinesLabel", at),
    modeLabel: label(value, "modeLabel", at),
    modeTone: tone(value.modeTone, MODE_TONES, `${at}'s mode`),
    signedIn: {
      ...(signedIn as unknown as MailDeskSignedIn),
      label: label(signedIn, "label", `${at}'s signedIn`),
      tone: tone(signedIn.tone, TONES, `${at}'s signedIn`),
    },
    lastCheck: {
      ...(lastCheck as unknown as MailDeskLastCheck),
      label: label(lastCheck, "label", `${at}'s lastCheck`),
      tone: tone(lastCheck.tone, TONES, `${at}'s lastCheck`),
    },
    today: {
      ...(today as unknown as MailDeskToday),
      total: today.total,
      label: label(today, "label", `${at}'s today`),
      counts,
    },
  };
}
