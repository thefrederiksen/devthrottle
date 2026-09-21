import { useCallback, useRef, useState } from "react";
import { getSessionHistory } from "../api/client";
import { useVisiblePolling } from "../polling/useVisiblePolling";
import type { HistoryBubbleFilter } from "./bubbleMapper";
import type { SessionHistoryDto } from "./types";
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
}

export function useSessionChat(sessionId: string | undefined): SessionChat {
  const [filter, setFilterState] = useState<HistoryBubbleFilter>(loadChatFilter);
  const [bubbles, setBubbles] = useState<RenderedBubble[]>([]);
  const [emptyText, setEmptyText] = useState("Waiting for the conversation to start...");
  // The Gateway's notice for a conversation that is real but no longer current (its computer is away, or
  // cannot send new turns). Shown ABOVE the bubbles; null while the session is live.
  const [staleNotice, setStaleNotice] = useState<string | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  const signatureRef = useRef("");
  const lastHistoryRef = useRef<SessionHistoryDto | null>(null);
  const filterRef = useRef(filter);
  filterRef.current = filter;

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
        const history = await getSessionHistory(sessionId, signal);
        if (signal.aborted) return;
        setLoadFailed(false);
        setLoadError(null);
        lastHistoryRef.current = history;
        renderHistory(history, false);
      } catch (err) {
        if (signal.aborted) return;
        setLoadFailed(true);
        setLoadError(err instanceof Error ? err.message : String(err));
      }
    },
    [sessionId, renderHistory],
  );
  useVisiblePolling(refresh, CHAT_POLL_MS, Boolean(sessionId));

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

  return { bubbles, emptyText, staleNotice, loadFailed, loadError, filter, setFilter };
}
