import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { getSessionHistory, listSessions } from "../api/client";
import type { SessionHistoryDto } from "../history/types";
import {
  anyHidden,
  mapHistory,
  type HistoryBubble,
  type HistoryBubbleFilter,
} from "../history/bubbleMapper";
import { cleanForReading } from "../history/historyText";
import { markdownToHtml } from "../history/historyMarkdown";
import { extractLinks, type HistoryLink } from "../history/historyLinks";
import { SessionControls } from "../components/SessionControls";
import { SessionManageBar } from "../components/SessionManageBar";
import { ViewTabs } from "../components/ViewTabs";

// Session Chat mode (issue #811): the SAME screen and SAME controls as the Terminal (#817), with
// ONLY the display different - instead of the raw PTY mirror it shows the cleaned conversation
// HISTORY, a faithful 1:1 translation of the desktop History tab (HistoryPane.razor + the existing
// Blazor Mobile.razor twin):
//
//   * Renders GET /sessions/{sid}/history through the ported HistoryBubbleMapper (part filtering +
//     truncation caps), the ported HistoryText.CleanForReading (ANSI + wrapper-tag strip), Markdown
//     with HTML DISABLED, and link detection (URLs + absolute paths with Copy buttons). Gemini
//     raw-text history is rendered verbatim (isRawText).
//   * DEFAULT = owner prompts ("You") + assistant responses only. Tool calls, tool results, and
//     thinking are HIDDEN by default, each revealed by a "Show:" toggle (the three desktop
//     categories), persisted per browser in localStorage (ccHistoryFilter), exactly like the
//     desktop tab.
//   * Live poll every 2.5s with a signature change-detector (count + total chars + tail + state +
//     filter) so an unchanged poll never re-renders or yanks the scroll; sticky-bottom auto-follow
//     ONLY when the reader is at the bottom.
//   * The SAME controls as Terminal via the shared SessionControls (input + Speak + Send + Enter +
//     Esc + Stop + arrows) - a Send or dictation from Chat reaches the SAME session.
//
// Every history and control call carries the Bearer token, so Chat works with global Gateway auth
// on or off.

const STATUS_BASE = "Conversation history (live)";
const FILTER_STORAGE_KEY = "ccHistoryFilter";
const BOTTOM_THRESHOLD_PX = 40;

interface RenderedBubble {
  bubble: HistoryBubble;
  html: string;
  links: HistoryLink[];
}

// Read the persisted "Show:" filter; defaults to all hidden (the desktop HistoryFilterConfig
// default - just the conversation). Format matches the desktop's comma-joined booleans.
function loadFilter(): HistoryBubbleFilter {
  const fallback: HistoryBubbleFilter = { showToolCalls: false, showToolResults: false, showThinking: false };
  try {
    const raw = window.localStorage.getItem(FILTER_STORAGE_KEY);
    if (!raw) return fallback;
    const parts = raw.split(",");
    if (parts.length === 3) {
      return {
        showToolCalls: parts[0] === "true",
        showToolResults: parts[1] === "true",
        showThinking: parts[2] === "true",
      };
    }
  } catch {
    /* localStorage unavailable - fall back to the hidden-machinery default */
  }
  return fallback;
}

function persistFilter(filter: HistoryBubbleFilter): void {
  try {
    window.localStorage.setItem(
      FILTER_STORAGE_KEY,
      `${filter.showToolCalls},${filter.showToolResults},${filter.showThinking}`,
    );
  } catch {
    /* localStorage unavailable - the choice simply will not persist this session */
  }
}

// Cheap change signature: count + total chars + last bubble tail + history state + filter. Mirrors
// the desktop HistoryPane.BuildSignature so an unchanged poll never disturbs a scrolled-up reader.
function buildSignature(bubbles: HistoryBubble[], state: string | null | undefined, filter: HistoryBubbleFilter): string {
  const f = `${filter.showToolCalls}${filter.showToolResults}${filter.showThinking}`;
  if (bubbles.length === 0) return `0|${state ?? ""}|${f}`;
  let total = 0;
  for (const b of bubbles) total += b.body.length;
  const last = bubbles[bubbles.length - 1].body;
  const tail = last.length <= 64 ? last : last.slice(-64);
  return `${bubbles.length}|${total}|${tail}|${state ?? ""}|${f}`;
}

// A link label, truncated in the middle for long URLs/paths (mirrors HistoryPane.LinkLabel).
function linkLabel(text: string): string {
  return text.length <= 60 ? text : text.slice(0, 28) + "..." + text.slice(-28);
}

export function Chat() {
  const { sessionId } = useParams<{ sessionId: string }>();

  const [name, setName] = useState<string | null>(null);
  const [filter, setFilter] = useState<HistoryBubbleFilter>(loadFilter);
  const [bubbles, setBubbles] = useState<RenderedBubble[]>([]);
  const [emptyText, setEmptyText] = useState("Waiting for the conversation to start...");
  const [loadFailed, setLoadFailed] = useState(false);
  const [status, setStatus] = useState(STATUS_BASE);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState<string | null>(null);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  const atBottomRef = useRef(true);
  const signatureRef = useRef("");
  const lastHistoryRef = useRef<SessionHistoryDto | null>(null);
  const filterRef = useRef(filter);
  filterRef.current = filter;

  // One-shot fetch of the session's display name for the header.
  useEffect(() => {
    const controller = new AbortController();
    listSessions(controller.signal)
      .then((all) => {
        const match = all.find((s) => s.sessionId === sessionId) ?? null;
        if (match?.name && match.name.trim()) setName(match.name.trim());
      })
      .catch(() => { /* header label is best-effort */ });
    return () => controller.abort();
  }, [sessionId]);

  // Map the given history through the current filter, clean + render each bubble, and commit it -
  // unless the signature is unchanged (the guard that keeps a steady poll from yanking the scroll).
  const renderHistory = useCallback((history: SessionHistoryDto | null, force: boolean) => {
    const f = filterRef.current;
    const mapped = mapHistory(history, f);

    if (history && history.isSupported === false) setEmptyText("History is not available for this agent yet.");
    else if (mapped.length === 0 && anyHidden(f) && history && history.messages.length > 0)
      setEmptyText("No messages match the current filters.");
    else setEmptyText("Waiting for the conversation to start...");

    const signature = buildSignature(mapped, history?.historyState, f);
    if (!force && signature === signatureRef.current) return; // unchanged - do not re-render
    signatureRef.current = signature;

    const rendered: RenderedBubble[] = [];
    for (const b of mapped) {
      // Raw terminal scrollback (Gemini) is shown verbatim; everything else is cleaned of transcript
      // machinery (command wrapper tags, system-reminder blocks, ANSI codes) before Markdown.
      if (b.isRawText) {
        rendered.push({ bubble: b, html: "", links: [] });
        continue;
      }
      const clean = cleanForReading(b.body);
      if (clean.length === 0) continue; // the whole message was machinery - drop the empty bubble
      rendered.push({ bubble: { ...b, body: clean }, html: markdownToHtml(clean), links: extractLinks(clean) });
    }
    setBubbles(rendered);
  }, []);

  // Live poll every 2.5s. AbortController cancels the in-flight fetch on unmount/session switch.
  useEffect(() => {
    if (!sessionId) return;
    const controller = new AbortController();
    let cancelled = false;

    const refresh = async () => {
      try {
        const history = await getSessionHistory(sessionId, controller.signal);
        if (cancelled) return;
        setLoadFailed(false);
        lastHistoryRef.current = history;
        renderHistory(history, false);
      } catch (err) {
        if (cancelled || controller.signal.aborted) return;
        setLoadFailed(true);
      }
    };

    void refresh();
    const timer = window.setInterval(() => void refresh(), 2500);
    return () => {
      cancelled = true;
      controller.abort();
      window.clearInterval(timer);
    };
  }, [sessionId, renderHistory]);

  // Sticky-bottom: after the bubble list changes, stick to the bottom ONLY if the reader is already
  // at the bottom. A scrolled-up reader is never moved.
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (el && atBottomRef.current) el.scrollTop = el.scrollHeight;
  }, [bubbles]);

  const onScroll = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < BOTTOM_THRESHOLD_PX;
  }, []);

  // A "Show:" checkbox flipped: remember the choice and re-render the cached history immediately
  // through the new filter (force, since the filter is part of the signature).
  const onFilterChange = useCallback((next: HistoryBubbleFilter) => {
    setFilter(next);
    filterRef.current = next;
    persistFilter(next);
    renderHistory(lastHistoryRef.current, true);
  }, [renderHistory]);

  const flash = useCallback((message: string) => {
    setStatus(message);
    window.setTimeout(() => setStatus((cur) => (cur === message ? STATUS_BASE : cur)), 1500);
  }, []);

  const copyLink = useCallback(async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(text);
      window.setTimeout(() => setCopied((cur) => (cur === text ? null : cur)), 1500);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Copy failed");
    }
  }, []);

  return (
    <div className="terminal-screen">
      <header className="app-bar">
        <Link className="back-link" to="/">&larr; Roster</Link>
        <h1 className="term-title">{name ?? "Session"}</h1>
      </header>

      <ViewTabs sessionId={sessionId} active="chat" />

      {/* Hold/Resume + Remove management buttons on the session screen (issue #812). */}
      <SessionManageBar sessionId={sessionId} />

      <div className="term-statusbar">
        <span className="term-status" role="status">{status}</span>
        <span className="chat-live">live</span>
      </div>

      {/* Compact "Show:" filter, mirroring the desktop History tab: reveal tool calls, tool results,
          or thinking. All three hidden by default; the choice is remembered per browser. */}
      <div className="chat-filter">
        <span className="chat-filter-label">Show:</span>
        <label>
          <input
            type="checkbox"
            checked={filter.showToolCalls}
            onChange={(e) => onFilterChange({ ...filter, showToolCalls: e.target.checked })}
          />{" "}
          Tool calls
        </label>
        <label>
          <input
            type="checkbox"
            checked={filter.showToolResults}
            onChange={(e) => onFilterChange({ ...filter, showToolResults: e.target.checked })}
          />{" "}
          Results
        </label>
        <label>
          <input
            type="checkbox"
            checked={filter.showThinking}
            onChange={(e) => onFilterChange({ ...filter, showThinking: e.target.checked })}
          />{" "}
          Thinking
        </label>
      </div>

      {error !== null && <div className="banner banner-error" role="alert">{error}</div>}

      <div className="chat-stage">
        {loadFailed && bubbles.length === 0 ? (
          <div className="chat-empty">Could not read this session's history right now. Retrying...</div>
        ) : bubbles.length === 0 ? (
          <div className="chat-empty">{emptyText}</div>
        ) : (
          <div className="chat-scroll" ref={scrollRef} onScroll={onScroll}>
            {bubbles.map((r, i) => (
              <div className={`chat-bubble ${r.bubble.kind}`} key={i}>
                <div className="chat-speaker">{r.bubble.speaker}</div>
                {r.bubble.isRawText ? (
                  <pre className="chat-body raw">{r.bubble.body}</pre>
                ) : (
                  <div className="chat-body md" dangerouslySetInnerHTML={{ __html: r.html }} />
                )}
                {r.links.length > 0 && (
                  <div className="chat-links">
                    {r.links.map((link, j) => (
                      <span className={`chat-link ${link.isUrl ? "url" : "path"}`} key={j}>
                        {link.isUrl ? (
                          <a
                            className="chat-link-open"
                            href={link.text}
                            target="_blank"
                            rel="noopener noreferrer"
                            title={link.text}
                          >
                            {linkLabel(link.text)}
                          </a>
                        ) : (
                          <span className="chat-link-text" title={link.text}>{linkLabel(link.text)}</span>
                        )}
                        <button type="button" className="chat-link-copy" onClick={() => copyLink(link.text)}>
                          {link.isUrl ? "Copy URL" : "Copy path"}
                        </button>
                        {copied === link.text && <span className="chat-link-copied">copied</span>}
                      </span>
                    ))}
                  </div>
                )}
              </div>
            ))}
          </div>
        )}
      </div>

      {/* The SAME controls as Terminal (shared SessionControls). The input row + Speak + Send is
          always visible for replying; a Keys toggle reveals the Enter/Esc/Stop + arrow rows. */}
      <SessionControls sessionId={sessionId} onFlash={flash} onError={setError} showKeyRows />
    </div>
  );
}
