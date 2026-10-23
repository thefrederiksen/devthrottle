import { useCallback, useRef, useState } from "react";
import { getSessionHistory } from "../api/client";
import { useVisiblePolling } from "../polling/useVisiblePolling";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import type { HistoryBubbleFilter } from "./bubbleMapper";
import type { SessionHistoryDto } from "./types";
import { applyHistoryPage, applyOlderPage, type HeldConversation } from "./conversationTail";
import {
  buildChatSignature,
  loadChatFilter,
  persistChatFilter,
  renderChatHistory,
  type RenderedBubble,
} from "./chatView";

// The shared Session Chat hook (hoisted for issue #1213). It drives the live conversation-history view
// for BOTH the mobile Chat page and the Cockpit Chat tab: it polls GET /sessions/{sid}/history every
// 2.5s while the page is visible, applies the desktop "Show:" filter (persisted per browser), and only commits a new bubble list
// when the change-signature actually changes, so a steady poll never re-renders or yanks a scrolled-up
// reader. Every call carries the Bearer token (via the shared client), so Chat works with global
// Gateway auth on or off. The view owns only the DOM: the scroll element and its sticky-bottom follow.

const CHAT_POLL_MS = 2500;

/**
 * What the empty Chat says before the Gateway has answered at all. It used to start on "Waiting for the conversation
 * to start...", so every long conversation spent its loading time telling the reader the session had not spoken yet
 * (owner, 2026-10-23, from the phone). That sentence is the Gateway's to say, about a conversation it has looked at.
 */
export const CHAT_LOADING_TEXT = "Loading the conversation...";

/**
 * How a Chat reads the conversation (issue #4372). With `turns`, it holds only the END of the conversation - the
 * last `turns` turns - and fetches the `turns` before those each time loadEarlier is called. Without it, the whole
 * conversation, as every Chat did before (the Fleet Manager page still does: it merges its own cards into the
 * conversation by time, and has no place to say there is more above).
 */
export interface SessionChatOptions {
  turns?: number;
}

export interface SessionChat {
  bubbles: RenderedBubble[];
  emptyText: string;
  /** The Gateway's sentence for a conversation that is real but no longer current - its computer is away,
   *  or too old to send new turns. Shown ABOVE the bubbles; null while the session is live. */
  staleNotice: string | null;
  loadFailed: boolean;
  /** The Gateway's own words for the last failed read, or null while reads succeed. */
  loadError: string | null;
  filter: HistoryBubbleFilter;
  /** Flip a "Show:" category; the choice is persisted and the cached history re-rendered immediately. */
  setFilter: (next: HistoryBubbleFilter) => void;
  /** True when the conversation has turns before the ones held - only ever with a window. */
  hasEarlier: boolean;
  /** True while the turns before the held ones are being fetched. */
  loadingEarlier: boolean;
  /** How many older pages have been put in front so far. It goes up in the SAME render as the bubbles they added,
   *  which is how a view knows that render grew at the top - and by how much - and keeps the reader in place. */
  earlierPages: number;
  /** The sentence for an older-page fetch that failed, or null. Cleared when the next one starts. */
  earlierError: string | null;
  /** Fetch the turns before the held ones and put them in front. Does nothing without a window, when there is
   *  nothing earlier, or while a fetch is already running. */
  loadEarlier: () => void;
}

export function useSessionChat(sessionId: string | undefined, options?: SessionChatOptions): SessionChat {
  const turns = options?.turns;
  const [filter, setFilterState] = useState<HistoryBubbleFilter>(loadChatFilter);
  const [bubbles, setBubbles] = useState<RenderedBubble[]>([]);
  const [emptyText, setEmptyText] = useState(CHAT_LOADING_TEXT);
  // The Gateway's notice for a conversation that is real but no longer current (its computer is away, or
  // cannot send new turns). Shown ABOVE the bubbles; null while the session is live.
  const [staleNotice, setStaleNotice] = useState<string | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  const signatureRef = useRef("");
  const lastHistoryRef = useRef<SessionHistoryDto | null>(null);
  // The whole conversation held for the session on screen, and the cursor that names it (traffic optimization,
  // phase 2). Keyed by session, so a switch to another session starts again from a full read.
  const heldRef = useRef<HeldConversation | null>(null);
  const filterRef = useRef(filter);
  filterRef.current = filter;
  // Issue #4372: where the held window starts, for the "there is more above" control, and whether older turns are
  // being fetched. `wholeReadsRef` counts the whole answers applied, so an older page asked against one whole read is
  // never put in front of a different one that replaced it while the page was in flight.
  const [windowFrom, setWindowFrom] = useState(0);
  const [loadingEarlier, setLoadingEarlier] = useState(false);
  const [earlierPages, setEarlierPages] = useState(0);
  const [earlierError, setEarlierError] = useState<string | null>(null);
  const loadingEarlierRef = useRef(false);
  const wholeReadsRef = useRef(0);
  // Bumped whenever the session on screen changes. An older-page fetch remembers the value it started under and does
  // nothing at all when it lands under another: the held copy still names the OLD session until the new session's
  // first answer arrives, so checking the held copy's session alone let a late page repaint the previous conversation
  // under the new session's name (found in review).
  const shownEpochRef = useRef(0);

  // A DIFFERENT SESSION STARTS EMPTY. The Cockpit keeps one session screen and changes only the address when another
  // session is selected, so this hook used to keep showing the previous session's conversation - and its empty text -
  // until the new one's first answer arrived. Reset in the render that sees the new session, so not even one frame of
  // the old conversation is drawn under the new session's name.
  const [shownFor, setShownFor] = useState(sessionId);
  if (shownFor !== sessionId) {
    setShownFor(sessionId);
    setBubbles([]);
    setEmptyText(CHAT_LOADING_TEXT);
    setStaleNotice(null);
    setLoadFailed(false);
    setLoadError(null);
    setWindowFrom(0);
    setLoadingEarlier(false);
    setEarlierPages(0);
    setEarlierError(null);
    loadingEarlierRef.current = false;
    shownEpochRef.current += 1;
    signatureRef.current = "";
    lastHistoryRef.current = null;
  }

  // Map the given history through the current filter and commit it - unless the signature is unchanged
  // (the guard that keeps a steady poll from yanking the scroll). `force` bypasses the guard for a
  // filter change (the filter is part of the signature).
  const renderHistory = useCallback((history: SessionHistoryDto | null, force: boolean) => {
    const f = filterRef.current;
    const rendered = renderChatHistory(history, f);
    setEmptyText(rendered.emptyText);
    setStaleNotice(history?.staleNotice ?? null);

    const mappedForSignature = rendered.bubbles.map((r) => r.bubble);
    const signature = buildChatSignature(mappedForSignature, history?.historyState, f);
    if (!force && signature === signatureRef.current) return; // unchanged - do not re-render
    signatureRef.current = signature;
    setBubbles(rendered.bubbles);
  }, []);

  // Live poll every 2.5s, only while the page is visible (traffic optimization, phase 1). This used to be a bare
  // setInterval, so a hidden or forgotten tab re-downloaded the whole conversation every 2.5s all night. The
  // shared visibility helper stops the timer and cancels the in-flight read when the tab is hidden, and reads
  // once at once when it is visible again. The signal it hands us is aborted on hide, unmount and session
  // switch, so a late answer for a session no longer on screen is dropped.
  const refresh = useCallback(
    async (signal: AbortSignal) => {
      if (!sessionId) return;
      try {
        // Traffic optimization, phase 2: ask only for what is new since the conversation already held, and
        // assemble the whole one here, so the renderer below is handed exactly what a full read would give it.
        const before = heldRef.current;
        const held = before !== null && before.sessionId === sessionId ? before : null;
        const sent = held?.cursor ?? "";
        const window = turns === undefined ? undefined : { turns };
        let page = await getSessionHistory(sessionId, signal, sent, window);
        let next = applyHistoryPage(held, sessionId, sent, page);
        if (signal.aborted) return;
        if (next === null) {
          // The tail does not fit what is held (it should not happen - the Gateway only tails a cursor it
          // verified). Never guess: throw away what is held and read the whole conversation.
          page = await getSessionHistory(sessionId, signal, "", window);
          next = applyHistoryPage(null, sessionId, "", page);
          if (signal.aborted) return;
          if (next === null) throw new Error("The Gateway answered a whole-conversation read with a partial one.");
        }
        // Two polls can be in flight at once on a slow link. Only the first answer built on what is held may
        // replace it; a later one built on the same, now superseded, copy is dropped - the next poll catches up.
        if (heldRef.current !== before) return;
        if ((page.tailFrom ?? 0) === 0) wholeReadsRef.current += 1;
        heldRef.current = next;
        setWindowFrom(next.windowFrom);
        const history = next.history;
        setLoadFailed(false);
        setLoadError(null);
        backgroundRecovered("session-chat", "load the conversation");
        lastHistoryRef.current = history;
        renderHistory(history, false);
      } catch (err) {
        if (signal.aborted) return;
        setLoadFailed(true);
        setLoadError(describeAndReport("session-chat", "load the conversation", err, { sessionId, background: true }));
      }
    },
    [sessionId, renderHistory, turns],
  );
  useVisiblePolling(refresh, CHAT_POLL_MS, Boolean(sessionId));

  // Issue #4372: fetch the turns before the held window and put them in front. The Gateway serves them only on a
  // cursor that proves what is held; when it cannot, it answers a fresh window, which replaces what is held.
  const loadEarlier = useCallback(() => {
    const held = heldRef.current;
    if (turns === undefined || !sessionId || held === null || held.sessionId !== sessionId || held.windowFrom === 0) return;
    if (loadingEarlierRef.current) return;
    loadingEarlierRef.current = true;
    setLoadingEarlier(true);
    setEarlierError(null);
    const before = held.windowFrom;
    const reads = wholeReadsRef.current;
    const epoch = shownEpochRef.current;
    void getSessionHistory(sessionId, undefined, held.cursor, { turns, before })
      .then((page) => {
        // Another session is on screen now: this page is nothing to it, and the reset has already cleared the flags.
        if (shownEpochRef.current !== epoch) return;
        const current = heldRef.current;
        // Asked against a whole read that has since been replaced: drop it. The reader is still at the top, so the
        // next scroll asks again against what is held now.
        if (current === null || current.sessionId !== sessionId || wholeReadsRef.current !== reads) return;
        const next = applyOlderPage(current, sessionId, before, page);
        if (next === null) {
          // It fits neither the window nor a fresh start. Never guess: the next poll reads the conversation again.
          heldRef.current = null;
          return;
        }
        const prepended = page.olderFrom !== undefined;
        if (!prepended) wholeReadsRef.current += 1;
        heldRef.current = next;
        setWindowFrom(next.windowFrom);
        // Counted in the same callback as the bubbles it adds, so React draws both in one render and the view can
        // measure exactly how much grew at the top.
        if (prepended) setEarlierPages((n) => n + 1);
        lastHistoryRef.current = next.history;
        renderHistory(next.history, false);
      })
      .catch((err) => {
        if (shownEpochRef.current !== epoch) return;
        setEarlierError(describeAndReport("session-chat", "load the earlier messages", err, { sessionId }));
      })
      .finally(() => {
        if (shownEpochRef.current !== epoch) return;
        loadingEarlierRef.current = false;
        setLoadingEarlier(false);
      });
  }, [sessionId, turns, renderHistory]);

  // A "Show:" checkbox flipped: remember the choice and re-render the cached history immediately
  // through the new filter (force, since the filter is part of the signature).
  const setFilter = useCallback(
    (next: HistoryBubbleFilter) => {
      setFilterState(next);
      filterRef.current = next;
      persistChatFilter(next);
      renderHistory(lastHistoryRef.current, true);
    },
    [renderHistory],
  );

  return {
    bubbles,
    emptyText,
    staleNotice,
    loadFailed,
    loadError,
    filter,
    setFilter,
    hasEarlier: turns !== undefined && windowFrom > 0,
    loadingEarlier,
    earlierPages,
    earlierError,
    loadEarlier,
  };
}
