// The Now view of the Wingman tab: the live stop of one session, and only the live stop (the Wingman tab, version 3,
// item 3). The answer to GET /sessions/{sid}/wingman-now.
//
// THE CLIENT IS DUMB. Every owner-facing word below is folded once on the Gateway by WingmanNowFold - the state, the
// pill words, the headline, the story, the agent's sentence, the recommendation, every option's words, the calm
// cards, the deadline sentence, the failure sentences and the voice label. The Now view lays them out, formats the
// UTC instants into the reader's local time, and decides nothing (product CLAUDE.md rule 7).
//
// Hand-written rather than generated, like wingmanStops.ts: the committed schema.ts predates the route. The shapes
// mirror the settled design in devthrottle_internal
// docs/missions/wingman-inspector/DESIGN-v3-item-1.md field for field, and will mirror
// src/CcDirector.Gateway.Contracts/WingmanNowDto.cs once item 1 builds it.
//
// NOT PROVEN HERE: the Gateway route does not exist yet, so nothing in this file has been checked against a real
// answer. It is the settled contract written down, not an observed one.

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
}

/** The agent's own decisive sentence, with the Gateway's name for who said it. */
export interface WingmanNowSaid {
  who: string;
  sentence: string;
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
  options: WingmanNowOption[];
  /** The verdict the options belong to. Rides alongside `index` on POST /turn-verdict/answer. */
  verdictId?: string | null;
}

/** A card that says nothing is needed: done, only telling you, or carrying on. */
export interface WingmanNowCalmCard {
  heading: string;
  body?: string | null;
}

/**
 * When a carrying-on session turns red if it does not work again. Rendered as `before` + the local clock time +
 * `after`. While a session it owns is still working there is no deadline: `atUtc` and `after` are null and `before`
 * carries the whole sentence on its own.
 */
export interface WingmanNowDeadline {
  before: string;
  atUtc?: string | null;
  after?: string | null;
}

/** The session's own last words, for the states where the Wingman has nothing to say about them. */
export interface WingmanNowLastWords {
  who: string;
  text: string;
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
  text: string;
  atUtc: string;
  by?: string | null;
}

/** What the owner answered, and the Gateway's sentence about the session going back to work. */
export interface WingmanNowAnswered {
  text: string;
  atUtc: string;
  workingAgainAfterText: string;
}

/** The next session in the account that needs the owner, in the order the Sessions list uses. */
export interface WingmanNowNext {
  sessionId: string;
  name: string;
  label: string;
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
  state: WingmanNowState;
  pillText: string;
  pillColour: string;
  pillColourHex: string;

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
  replyPlaceholder?: string | null;
  calmCard?: WingmanNowCalmCard | null;
  carryingOnDeadline?: WingmanNowDeadline | null;

  lastWords?: WingmanNowLastWords | null;
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
