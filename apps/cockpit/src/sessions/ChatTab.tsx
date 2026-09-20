import { useCallback, useLayoutEffect, useRef, useState } from "react";
import { useSessionChat } from "@devthrottle/client-core/history/useSessionChat";
import { chatLinkLabel } from "@devthrottle/client-core/history/chatView";
import { FileViewerModal } from "../components/FileViewerModal";

// The Cockpit Chat tab (issue #1213): a thin view over the SAME shared client-core hook the mobile Chat
// page uses (useSessionChat), so the two apps render the cleaned conversation history from one source.
// The composer at the bottom of SessionDetail drives replies for every tab, so this tab is the read
// view only: the desktop-History "Show:" filter and the live bubble list. The terminal socket is never
// touched (the TerminalPane stays mounted-hidden in SessionDetail while this tab is active).
//
// THE TURN LAYOUT IS THE ONE EVERY CHAT PRODUCT USES, and it is deliberate. Your own words sit on the
// RIGHT in a narrow filled bubble; the agent's answer takes the full column on the LEFT with no card
// around it at all. Neither carries a name label. A bordered card around the agent's reply is the single
// thing that made this screen read as homemade - no major chat product draws one - and once the two
// sides are on opposite edges with different fills, a label saying "You" or "Assistant" is telling the
// reader what the layout already told them. The owner's bubble reuses the Fleet Manager page's own
// owner tokens (--owner-bubble-*) so the two conversations in this product read as the same
// conversation. Only a tool-result bubble keeps its speaker line, because "Tool result" is not a name -
// it is the one thing about that bubble the layout cannot say.

const BOTTOM_THRESHOLD_PX = 40;

export function ChatTab({ sessionId }: { sessionId: string | undefined }) {
  const { bubbles, emptyText, staleNotice, loadFailed, filter, setFilter } = useSessionChat(sessionId);
  const [copied, setCopied] = useState<string | null>(null);
  // Which turn's "Copy" was last pressed, by index, so the confirmation shows on that turn alone. Kept
  // apart from `copied` (which keys on a link's text) because two turns can carry identical words.
  const [copiedTurn, setCopiedTurn] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Local Files (Phase 2): the file path currently being shown in the viewer, or null when closed. A
  // click on a detected file-path link sets it; the FileViewerModal renders it in place.
  const [viewerPath, setViewerPath] = useState<string | null>(null);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  const atBottomRef = useRef(true);

  // Sticky-bottom: stick to the bottom after the list changes ONLY if the reader is already there.
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (el && atBottomRef.current) el.scrollTop = el.scrollHeight;
  }, [bubbles]);

  const onScroll = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < BOTTOM_THRESHOLD_PX;
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

  // Copy one whole turn. The screen has had copy buttons for the links inside a message since Local
  // Files phase 2 and none for the message itself, which is the thing people actually want to lift out
  // of a conversation. Copies the CLEANED body the reader can see - never the raw transcript behind it.
  const copyTurn = useCallback(async (index: number, text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopiedTurn(index);
      window.setTimeout(() => setCopiedTurn((cur) => (cur === index ? null : cur)), 1500);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Copy failed");
    }
  }, []);

  return (
    <div className="chat-tab">
      {/* Compact "Show:" filter, mirroring the desktop History tab and the mobile Chat page. Each choice
          is a pill that fills in when it is on; the checkbox itself is still a real checkbox, only drawn
          by the pill around it, so it keeps its keyboard behaviour and its accessible name. */}
      <div className="chat-filter">
        <span className="chat-filter-label">Show:</span>
        <label className="chat-filter-pill">
          <input
            type="checkbox"
            checked={filter.showToolCalls}
            onChange={(e) => setFilter({ ...filter, showToolCalls: e.target.checked })}
          />
          Tool calls
        </label>
        <label className="chat-filter-pill">
          <input
            type="checkbox"
            checked={filter.showToolResults}
            onChange={(e) => setFilter({ ...filter, showToolResults: e.target.checked })}
          />
          Results
        </label>
        <label className="chat-filter-pill">
          <input
            type="checkbox"
            checked={filter.showThinking}
            onChange={(e) => setFilter({ ...filter, showThinking: e.target.checked })}
          />
          Thinking
        </label>
        <span className="chat-live">live</span>
      </div>

      {error !== null && <div className="composer-error" role="alert">{error}</div>}

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
            {/* One centred column holds every turn, and it is the column - not the bubble - that sets the
                reading measure. The turns then take their own side of it: the owner's to the right edge,
                the agent's across the whole width. */}
            <div className="chat-thread">
              {bubbles.map((r, i) => (
                <article className={`chat-bubble ${r.bubble.kind}`} key={i}>
                  {/* Only a tool-result bubble names its speaker (see the note at the top of this file). */}
                  {r.bubble.kind === "tool" && <div className="chat-speaker">{r.bubble.speaker}</div>}
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
                            // Local Files (Phase 2): the file path is now a clickable control that opens
                            // the viewer for that path (the copy button below is unchanged).
                            <button
                              type="button"
                              className="chat-link-open chat-link-view"
                              title={link.text}
                              onClick={() => setViewerPath(link.text)}
                            >
                              {chatLinkLabel(link.text)}
                            </button>
                          )}
                          <button type="button" className="chat-link-copy" onClick={() => void copyLink(link.text)}>
                            {link.isUrl ? "Copy URL" : "Copy path"}
                          </button>
                          {copied === link.text && <span className="chat-link-copied">copied</span>}
                        </span>
                      ))}
                    </div>
                  )}
                  {/* Drawn only under the pointer (and on keyboard focus), so a quiet conversation stays
                      quiet. It is inside the turn, so it moves with it. */}
                  <div className="chat-turn-actions">
                    <button
                      type="button"
                      className="chat-turn-copy"
                      aria-label="Copy this message"
                      onClick={() => void copyTurn(i, r.bubble.body)}
                    >
                      {copiedTurn === i ? "Copied" : "Copy"}
                    </button>
                  </div>
                </article>
              ))}
            </div>
          </div>
        )}
      </div>

      {viewerPath !== null && sessionId && (
        <FileViewerModal sessionId={sessionId} path={viewerPath} onClose={() => setViewerPath(null)} />
      )}
    </div>
  );
}
