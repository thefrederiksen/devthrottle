// THE LIVE CHAT OF AN API-MODE SESSION (mission #4070, phase 3). Built once here; the Cockpit's Chat tab and the
// phone's Chat screen both mount it in place of the history view when the session row carries the Gateway's API-mode
// verdict. A terminal-mode session never reaches this component.
//
// THIS VIEW DECIDES NOTHING (CLAUDE.md rule 7). Every word on it is the Gateway's: the status line is the session's
// verdict (ApiModeSessionDisplay, folded by ApiModeSessionFold), and every item in the conversation is ruled by
// ApiModeLiveRuling - the reply text, the tool card's title, input and status words, the receipt's words (Delivered,
// Refused with the reason, Queued with its place, a failed or stopped turn beside Delivered), the line that ends a
// turn, and the connection line. The view chooses only the LAYOUT for each kind, and a colour from the Gateway's tone.
//
// The layout is the existing Chat's: the agent's reply takes the whole column on the left with no card around it;
// a tool call is a small card with its status words; a receipt sits on the right, where the owner's words sit.
import { useCallback, useLayoutEffect, useRef } from "react";
import { markdownToHtml } from "../history/historyMarkdown";
import type { LiveEntry } from "./liveConversation";
import type { LiveStreamDeps } from "./liveStream";
import { LIVE_ITEM_KINDS, type ApiModeSessionDisplay } from "./liveTypes";
import { useApiModeLiveChat } from "./useApiModeLiveChat";
import "./apiModeLiveChat.css";

const BOTTOM_THRESHOLD_PX = 40;

/** The Gateway's colour hint as a class. Only the four documented words become a colour; anything else is neutral,
 *  which changes no words - the hint is a colour and nothing more. */
function toneClass(tone: string | null | undefined): string {
  switch (tone) {
    case "green":
    case "yellow":
    case "red":
      return `live-tone-${tone}`;
    default:
      return "live-tone-neutral";
  }
}

export interface ApiModeLiveChatProps {
  sessionId: string | undefined;
  /** The Gateway's verdict on the session (SessionDto.apiMode), from the shell's roster read. */
  display: ApiModeSessionDisplay | null | undefined;
  /** Test seam: the network and the clock the live stream uses. */
  streamDeps?: LiveStreamDeps;
}

export function ApiModeLiveChat({ sessionId, display, streamDeps }: ApiModeLiveChatProps) {
  const { conversation, stream, problem } = useApiModeLiveChat(sessionId, streamDeps);
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const atBottomRef = useRef(true);

  // Sticky bottom, as the history Chat: follow new text only while the reader is already at the bottom.
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (el && atBottomRef.current) el.scrollTop = el.scrollHeight;
  }, [conversation]);

  const onScroll = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < BOTTOM_THRESHOLD_PX;
  }, []);

  return (
    <div className="live-chat">
      {display && <LiveStatus display={display} />}

      {/* error-reported-by: useApiModeLiveChat */}
      {problem !== null && <div className="live-chat-notice live-tone-red" role="alert">{problem}</div>}
      {stream.message !== null && <div className="live-chat-notice" role="status">{stream.message}</div>}
      {/* After a reset the view holds only what came after it, so the Gateway's sentence stays above the thread. */}
      {conversation.reset !== null && (
        <div className="live-chat-notice live-tone-yellow" role="status">{conversation.reset.message}</div>
      )}

      <div className="live-chat-scroll" ref={scrollRef} onScroll={onScroll}>
        {conversation.entries.length === 0 ? (
          stream.open && <div className="live-chat-empty">Nothing to show yet.</div>
        ) : (
          <div className="live-chat-thread" role="log" aria-label="Live conversation">
            {conversation.entries.map((e) => <LiveEntryView entry={e} key={e.itemId} />)}
          </div>
        )}
      </div>
    </div>
  );
}

/** The session's verdict in one line: how it is connected, working or idle with the turn number, the queue, the last
 *  turn's stopped or failed ending, and the agent process. All of it the Gateway's words. */
function LiveStatus({ display }: { display: ApiModeSessionDisplay }) {
  return (
    <div className="live-chat-status" role="status" aria-label="Session status">
      {/* The design report's order: what the agent is doing first, in the Gateway's tone, then how it is connected. */}
      {display.activityLabel && <span className={`live-chat-pill live-chat-activity ${toneClass(display.tone)}`}>{display.activityLabel}</span>}
      {display.connectionLabel && <span className="live-chat-pill live-chat-connection">{display.connectionLabel}</span>}
      {display.queueLabel && <span className="live-chat-status-part">{display.queueLabel}</span>}
      {/* The ending takes the row's tone as the Gateway folded it: red for a failed last turn, neutral for a stopped
          one. The verdict carries no tone of its own for the ending, so the view never picks one (phase 3 review,
          finding 1). */}
      {display.lastTurnEnding && <span className={`live-chat-status-part ${toneClass(display.tone)}`}>{display.lastTurnEnding}</span>}
      {display.agentLabel && <span className="live-chat-status-part live-chat-agent">{display.agentLabel}</span>}
    </div>
  );
}

function LiveEntryView({ entry }: { entry: LiveEntry }) {
  switch (entry.kind) {
    case LIVE_ITEM_KINDS.replyText:
      return (
        <article className="live-chat-reply" data-item-id={entry.itemId}>
          <div className="live-chat-body md" dangerouslySetInnerHTML={{ __html: markdownToHtml(entry.text) }} />
        </article>
      );
    case LIVE_ITEM_KINDS.toolCard:
      return (
        <article
          className={`live-chat-tool ${toneClass(entry.tone)}`}
          data-item-id={entry.itemId}
          aria-label={entry.status ? `${entry.text}: ${entry.status}` : entry.text}
        >
          <div className="live-chat-tool-head">
            <span className="live-chat-tool-title">{entry.text}</span>
            {entry.status && <span className="live-chat-tool-status">{entry.status}</span>}
          </div>
          {entry.detail && (
            <details className="live-chat-tool-detail">
              <summary>Input</summary>
              <pre>{entry.detail}</pre>
            </details>
          )}
        </article>
      );
    case LIVE_ITEM_KINDS.receipt:
      // The owner's own words on the right, with the receipt's words under them. Without the words (the Gateway has
      // none for this prompt) the receipt stands alone on the same side.
      return (
        <article className="live-chat-prompt" data-item-id={entry.itemId}>
          {entry.promptText !== null && <div className="live-chat-prompt-text">{entry.promptText}</div>}
          <div className={`live-chat-receipt ${toneClass(entry.tone)}`}>{entry.text}</div>
        </article>
      );
    case LIVE_ITEM_KINDS.turnEnded:
      return (
        <div className={`live-chat-turn ${toneClass(entry.tone)}`} data-item-id={entry.itemId}>
          {entry.text}
        </div>
      );
    default:
      // The connection line, a permission request, or a kind added on the Gateway later: its words, as a line.
      return (
        <div className={`live-chat-line ${toneClass(entry.tone)}`} data-item-id={entry.itemId}>
          <span>{entry.text}</span>
          {entry.detail && <pre className="live-chat-line-detail">{entry.detail}</pre>}
        </div>
      );
  }
}
