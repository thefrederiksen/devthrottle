// The Now view of the Wingman tab: the live stop of one session, and only the live stop (the Wingman tab, version 3,
// item 3). The answer to GET /sessions/{sid}/wingman-now.
//
// THE CLIENT IS DUMB. Every owner-facing word below is folded once on the Gateway by WingmanNowFold - the state, the
// pill words, the headline, the story, the agent's sentence, the recommendation, every option's words, the calm
// cards, the deadline sentence, the failure sentences and the voice label. The Now view lays them out, formats the
// UTC instants into the reader's local time, and decides nothing (product CLAUDE.md rule 7).
//
// Hand-written rather than generated, like wingmanStops.ts: the committed schema.ts predates the route. EVERY SHAPE
// BELOW IS DERIVED FROM src/CcDirector.Gateway.Contracts/WingmanNowDto.cs, field for field, and from nothing else.
// It was written from the design document first and shipped disagreeing with the route on three fields, each of
// which broke the screen: the answer by option could never succeed, the agent's decisive sentence rendered empty,
// and the working clock read "Working for 7:14 a.m.". The design document is where the contract was settled; the C#
// file is what the route actually sends, and where they differ this file follows the C# file.
//
// PROVEN AGAINST THE ROUTE'S OWN ANSWER. wingmanNow.gatewaySample.json beside this file is written by the Gateway
// test WingmanNowWireSampleTests, which serializes a real WingmanNowFold answer with the options the route
// serializes with; wingmanNowWireSample.test.tsx reads that file and renders the view from it. If the two sides
// ever disagree on a field name again, that test fails instead of every suite staying green.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/** Which of the ten drawn states this session is in. The Gateway decides; the view never infers it. */
export type WingmanNowState =
  | "needs-you"
  | "reading"
  | "working"
  | "just-answered"
  | "carrying-on"
  | "done"
  | "report"
  | "failed"
  | "switched-off"
  | "other";

/**
 * A timed sentence: a finished lead, a UTC instant, and whether to say how long ago it was. The Gateway does not know
 * the reader's time zone, so it never writes a clock time - "Stopped at" + 2026-09-17T11:12:31Z + showAgo renders
 * "Stopped at 11:12 AM, 8 minutes ago".
 */
export interface WingmanNowWhen {
  lead: string;
  atUtc: string;
  showAgo: boolean;
  /**
   * True when the sentence is the ELAPSED TIME ALONE and the clock time is not shown at all: "Working for 6
   * minutes", not "Working for 11:14 AM". `atUtc` is then the moment to measure from. The Gateway names which of
   * the two shapes the sentence is; the view never works it out from the state.
   */
  elapsedOnly: boolean;
}

/** Something that was said, and who said it: the agent's decisive sentence, or the session's own last words. */
export interface WingmanNowSaid {
  who: string;
  text: string;
}

/** One thing the owner can answer with. `index` is what POST /turn-verdict/answer takes; `key` is the option's own words. */
export interface WingmanNowOption {
  index: number;
  key: string;
  note?: string | null;
  recommended: boolean;
}

/** What the session needs from the owner: the agent's recommendation, the question it asked, and the options. */
export interface WingmanNowNeeds {
  heading: string;
  recommends?: string | null;
  question?: string | null;
  /** The ways of answering, in the verdict's own order. Empty when the stop takes typed words only. */
  options: WingmanNowOption[];
}

/** A card that says nothing is needed: done, only telling you, or carrying on. */
export interface WingmanNowCalmCard {
  heading: string;
  body?: string | null;
  /**
   * The card's colour, NAMED by the Gateway. The approved mockup tints done and report cyan and carrying on purple,
   * and that is the owner's done-versus-report distinction made visible - so the choice belongs to the same fold
   * that chose the words, never to a branch on the state name here. A card with no tone keeps the neutral card.
   */
  tone?: "cyan" | "purple" | null;
}

/**
 * When a carrying-on session turns red if it does not work again. Rendered as `before` + the local clock time +
 * `after`. While a session it owns is still working there is no clock at all: the Gateway then sends NO deadline and
 * puts the sentence that says so in the calm card's `body` instead, so the view never has both to choose between.
 */
export interface WingmanNowDeadline {
  before: string;
  atUtc: string;
  after: string;
}

/** A past stop, marked as past: the newest good explanation, or the stop that was answered. */
export interface WingmanNowPast {
  lead: string;
  atUtc: string;
  text: string;
}

/** The Wingman is switched off for this account. */
export interface WingmanNowSwitchedOff {
  headline: string;
  story: string;
  settingsLinkText: string;
}

/** What the session was last asked, and by whom when the Gateway is sure. `by` is null rather than a guess. */
export interface WingmanNowLastAsked {
  /** The card's heading, in the Gateway's words. */
  heading: string;
  text: string;
  atUtc: string;
  by?: string | null;
  /** The finished words before the time - "You, at", or just "at" when nobody is named. The view joins this and the
   *  local clock time, and chooses neither the words nor the punctuation. */
  whenLead: string;
}

/** What the owner answered, and the Gateway's sentence about the session going back to work. */
export interface WingmanNowAnswered {
  /** The whole first line, finished: "You answered: allow the merge". */
  headline: string;
  /** What he answered on its own - the same words as `headline` carries, without its lead. */
  text: string;
  /** The words before the moment he answered: "Sent at". The view adds the local clock time. */
  sentLead: string;
  atUtc: string;
  /** That the session took the answer and went back to work, in finished words. NULL when this Gateway cannot tell,
   *  and then nothing is claimed about it - so the view draws the sentence without it rather than trailing a dash. */
  workingAgainAfterText?: string | null;
}

/** The next session in the account that needs the owner, in the order the Sessions list uses. */
export interface WingmanNowNext {
  /** The words over the row: "Next that needs you". */
  heading: string;
  sessionId: string;
  name: string;
  /** What it is waiting for, in its own row's words. Null when the row carries none. */
  label?: string | null;
  /** The words on the way there: "Go there". */
  linkText: string;
}

/** The voice control for this stop. `none` means there is nothing to read, and no button is drawn. */
export interface WingmanNowVoice {
  kind: "play" | "preparing" | "turn-on" | "none";
  label?: string | null;
  /** What to say once voice mode has been turned on from here - including whether THIS stop will be read aloud. */
  afterTurnOnText?: string | null;
}

/** The answer to GET /sessions/{sid}/wingman-now: everything the Now view draws, as finished strings and flags. */
export interface WingmanNow {
  /** The session this is about. */
  sessionId: string;
  state: WingmanNowState;
  pillText: string;
  /** The colour word the row wears. Null only when the row carries none. */
  pillColour?: string | null;
  /** The canonical pixel for `pillColour`. Null only when the row carries none. */
  pillColourHex?: string | null;

  unsure: boolean;
  unsureTag?: string | null;
  unsureLine?: string | null;

  when?: WingmanNowWhen | null;
  showWhyColour: boolean;

  headline?: string | null;
  story?: string | null;
  agentSaid?: WingmanNowSaid | null;
  wholeReply?: string | null;
  needs?: WingmanNowNeeds | null;
  canAnswerByOption: boolean;
  /** The verdict the options came from, which POST /sessions/{sid}/turn-verdict/answer takes alongside an option's
   *  index. Null when there is no verdict in force. AT THE ROOT, not inside `needs`: that is where the route puts
   *  it, and reading it from inside `needs` made every option answer fail with "The answer did not say which
   *  verdict it answers, so nothing was sent." */
  verdictId?: string | null;
  replyPlaceholder?: string | null;
  calmCard?: WingmanNowCalmCard | null;
  carryingOnDeadline?: WingmanNowDeadline | null;

  lastWords?: WingmanNowSaid | null;
  failedHeadline?: string | null;
  failedStory?: string | null;
  lastGood?: WingmanNowPast | null;
  switchedOff?: WingmanNowSwitchedOff | null;

  lastAsked?: WingmanNowLastAsked | null;
  answered?: WingmanNowAnswered | null;
  lastStop?: WingmanNowPast | null;
  nextNeedsYou?: WingmanNowNext | null;

  voice: WingmanNowVoice;
}

/**
 * GET /sessions/{sid}/wingman-now - the live stop of one session, as finished strings and flags.
 *
 * Resolves with the Gateway's answer as sent. Throws a GatewayError when the read was refused, carrying the
 * Gateway's own sentence, so a failure is never drawn as a calm screen.
 *
 * A 404 arrives as a GatewayError with status 404 like any other refusal, and shows the Gateway's own sentence.
 */
export async function readWingmanNow(sessionId: string, signal?: AbortSignal): Promise<WingmanNow> {
  const sid = encodeURIComponent(sessionId);
  const res = await gatewayFetch(`/sessions/${sid}/wingman-now`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw await GatewayError.from(res, "read what this session needs now");
  }
  return (await res.json()) as WingmanNow;
}
