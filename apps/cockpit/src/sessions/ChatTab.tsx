import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useSessionChat } from "@devthrottle/client-core/history/useSessionChat";
import { chatLinkLabel } from "@devthrottle/client-core/history/chatView";
import type { RenderedBubble } from "@devthrottle/client-core/history/chatView";
import { DENSITY_LABELS, filterFlagsFor, setDensity, useDensity } from "@devthrottle/client-core/sessions/density";
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

/**
 * A stable handle for one turn, used to find it again after the filter changes and the list is rebuilt.
 * The index cannot serve: it is an index into the FILTERED list, and the whole point of the jump is that
 * the filter is about to be taken off. The timestamp identifies a turn exactly when the history carries
 * one; the words are the fallback for a transcript that does not.
 */
function turnKey(r: RenderedBubble): string {
  return r.bubble.timestamp ?? r.bubble.body.slice(0, 200);
}

/** The clock on a prompt in the index, in the reader's own locale. Empty when the history carries none. */
function turnClock(r: RenderedBubble): string {
  const iso = r.bubble.timestamp;
  if (!iso) return "";
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return "";
  return at.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
}

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
  // The turn to scroll to once the list has been rebuilt without the filter - set by clicking a prompt
  // in the index, cleared the moment it is honoured. Null at every other time.
  const [pendingJump, setPendingJump] = useState<string | null>(null);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  const atBottomRef = useRef(true);

  // Sticky-bottom: stick to the bottom after the list changes ONLY if the reader is already there - and
  // never while a jump is outstanding, which is the one case where the reader has said exactly where
  // they want to be and it is not the bottom.
  useLayoutEffect(() => {
    if (pendingJump !== null) return;
    const el = scrollRef.current;
    if (el && atBottomRef.current) el.scrollTop = el.scrollHeight;
  }, [bubbles, pendingJump]);

  // Honour an outstanding jump once the unfiltered list has rendered. Runs after the list changes, finds
  // the turn by its handle and brings it to the middle of the view - the middle rather than the top,
  // because what the reader wants to see is the ANSWER that followed, and that is below it.
  useLayoutEffect(() => {
    if (pendingJump === null) return;
    const el = scrollRef.current;
    // Matched by reading the attribute rather than by building a selector out of the handle. The handle
    // is a timestamp or the reader's own words, and their words are not a thing to interpolate into a
    // selector - quotes and brackets in a prompt would break it, and CSS.escape is not somewhere to have
    // to rely on (it is absent in the test environment, and that absence threw here rather than degrading).
    const target = el
      ? [...el.querySelectorAll("[data-turn-key]")].find((n) => n.getAttribute("data-turn-key") === pendingJump)
      : undefined;
    if (target) {
      target.scrollIntoView({ block: "center" });
      // The reader is now parked mid-conversation, so a new turn arriving must not yank them away.
      atBottomRef.current = false;
    }
    // Cleared whether or not it was found: a handle that no longer matches anything (the turn aged out
    // of the transcript between the click and the render) must not leave the view pinned for ever.
    setPendingJump(null);
  }, [bubbles, pendingJump]);

  // Jump back to a prompt in the full conversation: take the filter off, then scroll to it. Finding the
  // prompt was never the goal - finding the reader's PLACE was - so the index hands them back the thread
  // rather than leaving them in a filtered view they now have to switch off themselves.
  const jumpToTurn = useCallback(
    (r: RenderedBubble) => {
      setPendingJump(turnKey(r));
      setFilter({ ...filter, myPromptsOnly: false });
    },
    [filter, setFilter],
  );

  // The switch is the source of truth for the three machinery flags; the stored filter follows it. Only
  // myPromptsOnly stays the reader's own independent choice - it takes the conversation AWAY rather than
  // adding machinery to it, which is why it was never one of the three and is not one of these.
  const density = useDensity();
  useEffect(() => {
    const flags = filterFlagsFor(density);
    if (
      filter.showToolCalls === flags.showToolCalls &&
      filter.showToolResults === flags.showToolResults &&
      filter.showThinking === flags.showThinking
    )
      return;
    setFilter({ ...filter, ...flags });
  }, [density, filter, setFilter]);

  const promptCount = useMemo(() => bubbles.filter((r) => r.bubble.kind === "user").length, [bubbles]);

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
        {/* ONE SWITCH, and it drives the session cards in the rail too (client-core/sessions/density).
            It replaces the three machinery checkboxes that used to stand here: Clean is none of them,
            Normal is tool calls, Everything is all three. The checkboxes said WHICH machinery; the
            switch says HOW MUCH, which is the question a person actually has, and it is the same
            question the rail was asking with no control at all. */}
        <span className="chat-density" role="group" aria-label="How much detail to show">
          {DENSITY_LABELS.map((d) => (
            <button
              key={d.value}
              type="button"
              className={`chat-density-btn ${density === d.value ? "on" : ""}`}
              aria-pressed={density === d.value}
              title={d.title}
              onClick={() => setDensity(d.value)}
            >
              {d.label}
            </button>
          ))}
        </span>
        {/* APART FROM THE THREE ABOVE, on purpose. Those add machinery back into the conversation; this
            takes the conversation away. Same row would read as a fourth of the same kind. */}
        <label className="chat-filter-pill chat-filter-mine">
          <input
            type="checkbox"
            checked={filter.myPromptsOnly}
            onChange={(e) => setFilter({ ...filter, myPromptsOnly: e.target.checked })}
          />
          My prompts only
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
            {filter.myPromptsOnly && (
              <p className="chat-index-lead" role="status">
                {promptCount === 1
                  ? "1 prompt of yours in this conversation. Click it to go back to it."
                  : `${promptCount} prompts of yours in this conversation. Click one to go back to it.`}
              </p>
            )}
            {/* One centred column holds every turn, and it is the column - not the bubble - that sets the
                reading measure. The turns then take their own side of it: the owner's to the right edge,
                the agent's across the whole width. */}
            <div className="chat-thread">
              {bubbles.map((r, i) => (
                <article className={`chat-bubble ${r.bubble.kind}`} key={i} data-turn-key={turnKey(r)}>
                  {filter.myPromptsOnly && <span className="chat-index-clock">{turnClock(r)}</span>}
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
                  {/* In the index every prompt is a way back into the thread. A button rather than the
                      article itself, so it is reachable by keyboard and announces what it does. */}
                  {filter.myPromptsOnly && (
                    <button
                      type="button"
                      className="chat-index-jump"
                      aria-label="Go back to this prompt in the conversation"
                      onClick={() => jumpToTurn(r)}
                    >
                      &rsaquo;
                    </button>
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
