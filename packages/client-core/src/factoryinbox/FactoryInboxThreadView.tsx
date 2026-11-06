import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import {
  getFactoryInboxSummary,
  getFactoryInboxThread,
  type FactoryInboxMessage,
  type FactoryInboxSummary,
  type FactoryInboxThread,
} from "./factoryInboxClient";
import { FACTORY_INBOX_SURFACE, FactoryComposer } from "./FactoryComposer";
import { formatInboxTime } from "./formatInboxTime";
import "./factoryInbox.css";

// ONE FACTORY'S THREAD (Factory Inbox mission, phase 4; mockup B, and the Cockpit's reading pane in mockup D). Each
// message carries its own trail - Sent, Received, with the Boss, the decision - so "it was listened to" can be seen at a
// glance. Every word under a message, and the tone it is coloured by, is the Gateway's; this view prints them.
//
// The decision arrives minutes later, from the Boss, so the thread is read again every 15 seconds while it is open.

const REFRESH_MS = 15_000;

export interface FactoryInboxThreadViewProps {
  factory: string;
  surface: "cockpit" | "mobile";
  /** Rendered beside the title, for a shell's own extras. */
  actions?: ReactNode;
  /** Whether to print the factory's title as a heading (the phone's app bar already shows it). */
  showTitle?: boolean;
  /** The thread was read: its title, for a shell's app bar. */
  onTitle?: (title: string) => void;
}

export function FactoryInboxThreadView({ factory, surface, actions, showTitle = true, onTitle }: FactoryInboxThreadViewProps) {
  const [thread, setThread] = useState<FactoryInboxThread | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [summary, setSummary] = useState<FactoryInboxSummary | null>(null);
  const [summaryOpen, setSummaryOpen] = useState(false);
  const [summaryError, setSummaryError] = useState<string | null>(null);
  const [reads, setReads] = useState(0);
  // The latest onTitle, without making the read depend on a callback the shell rebuilds every render.
  const onTitleRef = useRef(onTitle);
  onTitleRef.current = onTitle;

  const reload = useCallback(() => setReads((n) => n + 1), []);

  useEffect(() => {
    setThread(null);
    setSummary(null);
    setSummaryOpen(false);
    setError(null);
  }, [factory]);

  useEffect(() => {
    const controller = new AbortController();
    const background = reads > 0;
    getFactoryInboxThread(factory, controller.signal).then(
      (t) => {
        if (controller.signal.aborted) return;
        setThread(t);
        setError(null);
        onTitleRef.current?.(t.title);
        backgroundRecovered(FACTORY_INBOX_SURFACE, "read the factory's messages");
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        setError(describeAndReport(FACTORY_INBOX_SURFACE, "read the factory's messages", err, background ? { background: true } : undefined));
      },
    );
    const timer = setTimeout(reload, REFRESH_MS);
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [factory, reads, reload]);

  const toggleSummary = useCallback(() => {
    if (summaryOpen) {
      setSummaryOpen(false);
      return;
    }
    setSummaryOpen(true);
    setSummaryError(null);
    getFactoryInboxSummary(factory).then(setSummary, (err: unknown) =>
      setSummaryError(describeAndReport(FACTORY_INBOX_SURFACE, "read the factory's summary", err)),
    );
  }, [factory, summaryOpen]);

  // The Gateway answers newest first; a thread reads oldest at the top, like a conversation.
  const messages = thread === null ? [] : [...thread.messages].reverse();

  return (
    <section className="fi-thread" data-testid="fi-thread">
      <div className="fi-thread-head">
        {showTitle && <h2 className="fi-thread-title">{thread?.title ?? factory}</h2>}
        <div className="fi-thread-actions">
          {actions}
          <button type="button" className="fi-summary-button" aria-expanded={summaryOpen} onClick={toggleSummary}>
            Summary
          </button>
        </div>
      </div>
      {summaryOpen && (
        <div className="fi-summary" data-testid="fi-summary">
          {summaryError !== null && (
            <div className="fi-composer-error" role="alert">
              {summaryError}
            </div>
          )}
          {summaryError === null && summary === null && <p role="status">Loading...</p>}
          {summary !== null && (
            <>
              <p className="fi-summary-line">{summary.line}</p>
              <p className="fi-summary-window">
                Since {formatInboxTime(summary.sinceUtc)}
              </p>
            </>
          )}
        </div>
      )}
      {error !== null && (
        <div className="fi-composer-error" role="alert">
          {error}
        </div>
      )}
      {thread === null && error === null && <p role="status">Loading...</p>}
      {thread !== null && messages.length === 0 && <p className="fi-empty">No messages yet.</p>}
      <ol className="fi-messages">
        {messages.map((m) => (
          <ThreadMessage key={m.id} message={m} />
        ))}
      </ol>
      <FactoryComposer factory={factory} factoryTitle={thread?.title} surface={surface} onLeft={reload} />
    </section>
  );
}

function ThreadMessage({ message }: { message: FactoryInboxMessage }) {
  return (
    <li className="fi-message" data-testid="fi-message" data-tone={message.tone}>
      <div className="fi-bubble">{message.text}</div>
      <div className="fi-meta">
        {message.noteLabel !== "" && (
          <span className="fi-note-label" data-note={message.note}>
            {message.noteLabel}
          </span>
        )}
        <span className="fi-source">{message.sourceLabel}</span>
        <span className="fi-state" data-tone={message.tone}>
          {message.stateLabel}
        </span>
      </div>
      <p className="fi-trail" data-testid="fi-trail">
        {message.trail.map((s, i) => (
          <span key={`${s.step}-${i}`} className="fi-trail-step" data-step={s.step}>
            {i > 0 && " - "}
            {s.label}
            {s.atUtc !== null && ` ${formatInboxTime(s.atUtc)}`}
          </span>
        ))}
      </p>
    </li>
  );
}
