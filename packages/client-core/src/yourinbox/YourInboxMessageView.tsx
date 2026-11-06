import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { describeAndReport } from "../errors/reportClientError";
import { formatInboxTime } from "../factoryinbox/formatInboxTime";
import {
  getYourInboxMessage,
  setYourInboxState,
  type YourInboxLink,
  type YourInboxMessage,
} from "./yourInboxClient";
import { yourInboxChanged } from "./useYourInboxCount";
import { YOUR_INBOX_SURFACE } from "./YourInboxListView";
import { YourInboxReplyComposer } from "./YourInboxReplyComposer";
import "../factoryinbox/factoryInbox.css";
import "./yourInbox.css";

// READING ONE MESSAGE (Factory Inbox mission, step C; the Cockpit's reading pane, the phone's message screen): the full
// body, who sent it, the ways back to where it came from (its session - "Open the session" - its report, its factory's
// thread), the Gateway's own buttons (Archive, Mark unread, Move to inbox), and Reply. Opening an unread message marks it
// Read. Every word but the box's own "Reply" is the Gateway's (rule 7).

export interface YourInboxMessageViewProps {
  id: string;
  surface: "cockpit" | "mobile";
  /**
   * The shell's address for a link: a session, a report, or a factory's thread. Each shell has its own addresses, so it
   * is passed in (rule 8); a link the shell has no address for (null) is not shown, so no surface renders a dead link.
   */
  hrefFor: (link: YourInboxLink) => string | null;
  /** Whether to print the subject as a heading (the phone's app bar may already show it). */
  showSubject?: boolean;
  /** The message was read: its subject, for a shell's app bar. */
  onSubject?: (subject: string) => void;
}

export function YourInboxMessageView({ id, surface, hrefFor, showSubject = true, onSubject }: YourInboxMessageViewProps) {
  const [message, setMessage] = useState<YourInboxMessage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [acting, setActing] = useState(false);
  const [replying, setReplying] = useState(false);
  // The latest onSubject, without making the read depend on a callback the shell rebuilds every render.
  const onSubjectRef = useRef(onSubject);
  onSubjectRef.current = onSubject;

  useEffect(() => {
    const controller = new AbortController();
    setMessage(null);
    setError(null);
    setReplying(false);
    (async () => {
      const read = await getYourInboxMessage(id, controller.signal);
      if (controller.signal.aborted) return;
      setMessage(read);
      onSubjectRef.current?.(read.subject);
      if (!read.unread) return;
      // OPENING MARKS IT READ. The Gateway answers with the message as it now is; the replies come from the read above,
      // which is the only answer that carries them.
      const marked = await setYourInboxState(id, "read", controller.signal);
      if (controller.signal.aborted) return;
      setMessage({ ...marked, replies: read.replies });
      yourInboxChanged();
    })().catch((err: unknown) => {
      if (controller.signal.aborted) return;
      setError(describeAndReport(YOUR_INBOX_SURFACE, "read the message", err));
    });
    return () => controller.abort();
  }, [id]);

  const act = useCallback(
    async (state: string) => {
      setActing(true);
      setError(null);
      try {
        const changed = await setYourInboxState(id, state);
        setMessage((m) => ({ ...changed, replies: m?.replies ?? null }));
        yourInboxChanged();
      } catch (err: unknown) {
        setError(describeAndReport(YOUR_INBOX_SURFACE, "change the message", err));
      } finally {
        setActing(false);
      }
    },
    [id],
  );

  if (message === null) {
    return (
      <section className="yi-message" data-testid="yi-message">
        {error !== null ? (
          <div className="fi-composer-error" role="alert">
            {error}
          </div>
        ) : (
          <p role="status">Loading...</p>
        )}
      </section>
    );
  }

  const links = message.links
    .map((l) => ({ link: l, href: hrefFor(l) }))
    .filter((x): x is { link: YourInboxLink; href: string } => x.href !== null);

  return (
    <section className="yi-message" data-testid="yi-message">
      {showSubject && <h2 className="fi-thread-title yi-subject">{message.subject}</h2>}
      <div className="yi-message-meta">
        <span className="yi-from" data-testid="yi-from">
          {message.from.label}
        </span>
        <span className="yi-row-time">{formatInboxTime(message.sentAtUtc)}</span>
        <span className="fi-badge" data-tone={message.tone} data-testid="yi-state">
          {message.stateLabel}
        </span>
      </div>
      <div className="yi-body" data-testid="yi-body">
        {message.body}
      </div>
      {links.length > 0 && (
        <div className="yi-links">
          {links.map(({ link, href }) => (
            <Link key={`${link.kind}-${link.target}`} className="yi-link" data-testid="yi-link" data-kind={link.kind} to={href}>
              {link.label}
            </Link>
          ))}
        </div>
      )}
      <div className="yi-actions">
        <button
          type="button"
          className="yi-action yi-action-reply"
          aria-expanded={replying}
          onClick={() => setReplying((r) => !r)}
        >
          Reply
        </button>
        {message.actions.map((a) => (
          <button
            key={a.state}
            type="button"
            className="yi-action"
            data-testid="yi-action"
            disabled={acting}
            onClick={() => void act(a.state)}
          >
            {a.label}
          </button>
        ))}
      </div>
      {error !== null && (
        <div className="fi-composer-error" role="alert">
          {error}
        </div>
      )}
      {message.replies !== null && message.replies.length > 0 && (
        <ol className="yi-replies" data-testid="yi-replies">
          {message.replies.map((r) => (
            <li key={r.id} className="yi-sent-reply">
              <div className="fi-bubble">{r.text}</div>
              <p className="fi-trail">
                {r.label} {formatInboxTime(r.sentAtUtc)}
              </p>
            </li>
          ))}
        </ol>
      )}
      {replying && (
        <YourInboxReplyComposer
          message={message}
          surface={surface}
          hrefFor={hrefFor}
          onSent={(m) => setMessage(m)}
        />
      )}
    </section>
  );
}
