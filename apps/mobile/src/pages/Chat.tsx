import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { listSessions } from "@devthrottle/client-core/api/client";
import { useSessionChat } from "@devthrottle/client-core/history/useSessionChat";
import { chatLinkLabel } from "@devthrottle/client-core/history/chatView";
import { DictationStatusStrip } from "@devthrottle/client-core/dictation/DictationStatusStrip";
import { VerdictPanel } from "@devthrottle/client-core/sessions/VerdictPanel";
import { SessionAppBar } from "../components/SessionAppBar";
import { SessionControls } from "../components/SessionControls";
import { useSessionManage } from "../components/useSessionManage";
import { ViewTabs } from "../components/ViewTabs";

// Session Chat mode (issue #811): the SAME screen and SAME controls as the Terminal (#817), with ONLY
// the display different - instead of the raw PTY mirror it shows the cleaned conversation HISTORY, a
// faithful 1:1 translation of the desktop History tab. The data logic (poll, "Show:" filter, change
// signature, per-bubble clean + Markdown + link extraction) lives in the shared client-core hook
// useSessionChat (hoisted in issue #1213 so the Cockpit Chat tab renders the same code); this page is a
// thin view that owns only the DOM: the sticky-bottom scroll, copy buttons, and the shared controls.

const STATUS_BASE = "Conversation history (live)";
const BOTTOM_THRESHOLD_PX = 40;

export function Chat() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const navigate = useNavigate();

  const [name, setName] = useState<string | null>(null);
  const { bubbles, emptyText, staleNotice, loadFailed, filter, setFilter } = useSessionChat(sessionId);
  const [status, setStatus] = useState(STATUS_BASE);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState<string | null>(null);
  const manage = useSessionManage(sessionId);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  const atBottomRef = useRef(true);

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

  const flash = useCallback((message: string) => {
    setStatus(message);
    window.setTimeout(() => setStatus((cur) => (cur === message ? STATUS_BASE : cur)), 1500);
  }, []);

  // Local Files (Phase 3): a detected file path opens the full-screen file viewer for that path (the
  // copy button below is unchanged). The absolute path rides as ?path= so a reload/deep-link resolves,
  // and ?from= records THIS tab so the viewer's Back returns to Chat (not a dead history.back()).
  const openFile = useCallback((path: string) => {
    const sid = encodeURIComponent(sessionId ?? "");
    const from = encodeURIComponent(`/session/${sessionId ?? ""}/chat`);
    navigate(`/session/${sid}/file?path=${encodeURIComponent(path)}&from=${from}`);
  }, [navigate, sessionId]);

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
      {/* Snooze is in the overflow here: this screen's bottom belongs to the message composer, so
          there is no thumb-zone room for it (Voice mode, which has room, keeps it one tap). */}
      <SessionAppBar title={name ?? "Session"} manage={manage} showSnooze showSwitchToVoice />

      <ViewTabs sessionId={sessionId} active="chat" />

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
            onChange={(e) => setFilter({ ...filter, showToolCalls: e.target.checked })}
          />{" "}
          Tool calls
        </label>
        <label>
          <input
            type="checkbox"
            checked={filter.showToolResults}
            onChange={(e) => setFilter({ ...filter, showToolResults: e.target.checked })}
          />{" "}
          Results
        </label>
        <label>
          <input
            type="checkbox"
            checked={filter.showThinking}
            onChange={(e) => setFilter({ ...filter, showThinking: e.target.checked })}
          />{" "}
          Thinking
        </label>
      </div>

      {error !== null && <div className="banner banner-error" role="alert">{error}</div>}

      {/* Live dictation status so a Speak Send from Chat is never silent (#1139). */}
      <DictationStatusStrip sessionId={sessionId} />

      {/* What the Wingman read at this stop, and the owner's answer to it - the shared client-core panel, fed
          from the same roster poll as the snooze state. It renders nothing unless the row carries a judged verdict.
          The hook hands over only THIS route's row, and the panel answers with THIS route's session id. With no row
          to act on - a read pending, failed or missing this session - there is no panel, only the reason. */}
      {manage.session && sessionId && <VerdictPanel sessionId={sessionId} session={manage.session} />}
      {manage.sessionProblem !== null && <div className="chat-stale" role="status">{manage.sessionProblem}</div>}

      {/* ABOVE the scrolling conversation, not inside it. A long conversation opens at the BOTTOM, so a
          notice placed at the top of the scroll is exactly where nobody looks (found in review); and it
          must survive the empty branches too, where the reader is even more in the dark. It is non-null
          only when there IS a stored conversation, so it never doubles up with the empty-state sentence. */}
      {staleNotice !== null && <div className="chat-stale" role="status">{staleNotice}</div>}

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
                            {chatLinkLabel(link.text)}
                          </a>
                        ) : (
                          // Local Files (Phase 3): the file path is now a clickable control that opens
                          // the full-screen viewer for that path (the copy button below is unchanged).
                          <button
                            type="button"
                            className="chat-link-open chat-link-view"
                            title={link.text}
                            onClick={() => openFile(link.text)}
                          >
                            {chatLinkLabel(link.text)}
                          </button>
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
