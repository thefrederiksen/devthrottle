// THE SHAPES THE GATEWAY RULES FOR AN API-MODE SESSION'S LIVE CHAT (mission #4070, phase 3).
//
// Mirrors of the Gateway contracts in src/CcDirector.Gateway.Contracts (ApiModeLiveDtos.cs, ApiModeDisplayDtos.cs,
// ApiModeReceiptDtos.cs). The live relay's items and reset are not in the generated OpenAPI schema (the stream is not
// a JSON route), so they are written out here, field for field. Every string is the Gateway's finished words: the
// client renders them and decides nothing from them (CLAUDE.md rule 7).
import type { components } from "../api/schema";

/** One thing the live view shows, ruled once on the Gateway (ApiModeLiveRuling). Found on screen by `itemId`; the
 *  later item with the same id appends its text to it or replaces it, as `mode` says. */
export interface ApiModeLiveItem {
  /** `reply:<messageId>`, `tool:<toolCallId>`, `turn:<turnId>`, `receipt:<promptId>`, `connection:<sequence>`,
   *  `permission:<requestId>`. */
  itemId: string;
  /** One of the kinds below, for layout and style lookup only. */
  kind: string;
  /** `append` or `replace`. */
  mode: string;
  /** The words, verbatim. */
  text: string;
  /** A tool card's status words; null on every other kind. */
  status?: string | null;
  /** A tool card's input, or a gone connection's exit code and standard error lines; null otherwise. */
  detail?: string | null;
  /** Colour hint only: `green`, `yellow`, `red` or `neutral`. */
  tone: string;
  turnId?: string | null;
  /** The prompt a receipt item is for; null on every other kind. */
  promptId?: string | null;
  /** On a receipt item: the prompt's own words, verbatim, as the Gateway stored them on the receipt. Null when the
   *  Gateway has none (a receipt stored before it kept the words, or a Gateway older than that change). The Delivery
   *  Lead's ruling of 16 October 2026: the user's message is shown from the receipt's text, never from /history. */
  promptText?: string | null;
  /** For logs and tests only: the client applies items in arrival order and never sorts by it. */
  sequence: number;
}

/** The values of `ApiModeLiveItem.kind` (ApiModeLiveItemKinds). */
export const LIVE_ITEM_KINDS = {
  replyText: "replyText",
  toolCard: "toolCard",
  turnEnded: "turnEnded",
  receipt: "receipt",
  connection: "connection",
  permission: "permission",
} as const;

/** The values of `ApiModeLiveItem.mode` (ApiModeLiveItemModes). */
export const LIVE_ITEM_MODES = {
  append: "append",
  replace: "replace",
} as const;

/** The relay's one explicit gap (ApiModeLiveResetDto): what the client held must not be continued. */
export interface ApiModeLiveReset {
  /** `missed_events`, `new_generation` or `fell_behind`. */
  code: string;
  /** Why, as a sentence to show as it is. */
  message: string;
  /** The routes to rebuild from, with the session id filled in. */
  rebuildFrom: string[];
}

/** The Gateway's verdict stamped on an API-mode session row (ApiModeSessionDisplay); null on a terminal-mode row. */
export type ApiModeSessionDisplay = components["schemas"]["ApiModeSessionDisplay"];
