// THE LIVE CONVERSATION OF AN API-MODE SESSION, AS THE CLIENT HOLDS IT (mission #4070, phase 3).
//
// The Gateway rules every item (ApiModeLiveRuling); this file only applies them, exactly as each item says:
//   - an item is found by its `itemId`; two items with the same id are the same thing on screen;
//   - `append` adds the item's text to the end of what that item already shows (a new item starts empty), and takes
//     the item's other fields as they now stand;
//   - `replace` makes the item exactly the one received;
//   - a new item takes its place at the end, in the order items ARRIVE - never sorted by sequence, which starts again
//     with every Director generation.
// A reset means what is held must not be continued: everything is let go, the Gateway's own sentence is kept to show,
// and the items that follow (the stored receipts first) build the view again.
//
// Pure functions over immutable state, so React sees a new object for every change and a test needs no DOM.
import { LIVE_ITEM_MODES, type ApiModeLiveItem, type ApiModeLiveReset } from "./liveTypes";

/** One item as it stands on screen: the latest fields the Gateway sent for it, and its whole text so far. */
export interface LiveEntry {
  itemId: string;
  kind: string;
  text: string;
  status: string | null;
  detail: string | null;
  tone: string;
  turnId: string | null;
  promptId: string | null;
  promptText: string | null;
}

export interface LiveConversation {
  /** Every item on screen, in the order it first arrived. */
  entries: readonly LiveEntry[];
  /** The last reset the Gateway sent, or null when the view has never been reset. Shown above the conversation,
   *  because after a reset the view no longer holds what came before it. */
  reset: ApiModeLiveReset | null;
}

/** Thrown when an item cannot be applied as the contract says - a mode this client does not know. A contract break
 *  between this client and the Gateway, so it is reported, never guessed at. */
export class LiveItemError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "LiveItemError";
  }
}

export function emptyConversation(): LiveConversation {
  return { entries: [], reset: null };
}

function entryOf(item: ApiModeLiveItem, text: string): LiveEntry {
  return {
    itemId: item.itemId,
    kind: item.kind,
    text,
    status: item.status ?? null,
    detail: item.detail ?? null,
    tone: item.tone,
    turnId: item.turnId ?? null,
    promptId: item.promptId ?? null,
    promptText: item.promptText ?? null,
  };
}

/** Apply items in the order given. Returns the same object when `items` is empty. */
export function applyItems(state: LiveConversation, items: readonly ApiModeLiveItem[]): LiveConversation {
  if (items.length === 0) return state;
  const entries = [...state.entries];
  const positions = new Map<string, number>();
  entries.forEach((e, i) => positions.set(e.itemId, i));

  for (const item of items) {
    if (item.mode !== LIVE_ITEM_MODES.append && item.mode !== LIVE_ITEM_MODES.replace)
      throw new LiveItemError(`The Gateway sent a live item "${item.itemId}" with a mode this app does not know ("${item.mode}").`);
    const at = positions.get(item.itemId);
    if (at === undefined) {
      positions.set(item.itemId, entries.length);
      entries.push(entryOf(item, item.text));
      continue;
    }
    const text = item.mode === LIVE_ITEM_MODES.append ? entries[at].text + item.text : item.text;
    entries[at] = entryOf(item, text);
  }
  return { entries, reset: state.reset };
}

/** The Gateway said the view cannot be continued: let everything go and keep its sentence. */
export function applyReset(_state: LiveConversation, reset: ApiModeLiveReset): LiveConversation {
  return { entries: [], reset };
}
