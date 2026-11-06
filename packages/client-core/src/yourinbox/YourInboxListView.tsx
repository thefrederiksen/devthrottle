import { useCallback, useEffect, useState } from "react";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import { formatInboxTime } from "../factoryinbox/formatInboxTime";
import { getYourInbox, type YourInboxList } from "./yourInboxClient";
import { onYourInboxChanged } from "./useYourInboxCount";
import "../factoryinbox/factoryInbox.css";
import "./yourInbox.css";

// YOUR INBOX, THE LIST (Factory Inbox mission, step C; mockup C, and the Cockpit's list column): newest first, each row
// who sent it, the subject, one line of the body, the time, and an unread message in bold with its tone badge. Above it
// the filters - their words, their order and their counts are the Gateway's - and, when a view holds nothing, the
// Gateway's sentence for it. This view prints; it decides nothing (rule 7).

const REFRESH_MS = 30_000;

/** Where a Your inbox failure is filed. One set of screens on both shells, so one surface. */
export const YOUR_INBOX_SURFACE = "your-inbox";

/** The view shown first: the whole inbox, as the Gateway answers when no view is named. */
export const YOUR_INBOX_FIRST_VIEW = "inbox";

export interface YourInboxListViewProps {
  /** Open one message. */
  onOpen: (id: string) => void;
  /** The message open beside the list (the Cockpit), marked on its row. */
  selected?: string | null;
}

export function YourInboxListView({ onOpen, selected = null }: YourInboxListViewProps) {
  const [view, setView] = useState(YOUR_INBOX_FIRST_VIEW);
  const [list, setList] = useState<YourInboxList | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [reads, setReads] = useState(0);

  const reload = useCallback(() => setReads((n) => n + 1), []);

  useEffect(() => onYourInboxChanged(reload), [reload]);

  useEffect(() => {
    const controller = new AbortController();
    const background = reads > 0;
    getYourInbox(view, controller.signal).then(
      (l) => {
        if (controller.signal.aborted) return;
        setList(l);
        setError(null);
        backgroundRecovered(YOUR_INBOX_SURFACE, "read your inbox");
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        setError(describeAndReport(YOUR_INBOX_SURFACE, "read your inbox", err, background ? { background: true } : undefined));
      },
    );
    const timer = setTimeout(reload, REFRESH_MS);
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [view, reads, reload]);

  const pick = (next: string) => {
    if (next === view) return;
    setList((l) => (l === null ? null : { ...l, messages: [] }));
    setView(next);
  };

  // The list shown is the one for the view picked; while another view's answer is still on screen, it waits.
  const shown = list !== null && list.view === view ? list : null;

  return (
    <section className="fi-list" data-testid="yi-list">
      {list !== null && (
        <div className="yi-filters" role="group" aria-label="Show">
          {list.filters.map((f) => (
            <button
              key={f.view}
              type="button"
              className="yi-filter"
              data-testid="yi-filter"
              aria-pressed={f.view === view}
              onClick={() => pick(f.view)}
            >
              {f.label}
              <span className="yi-filter-count">{f.count}</span>
            </button>
          ))}
        </div>
      )}
      {error !== null && (
        <div className="fi-composer-error" role="alert">
          {error}
        </div>
      )}
      {shown === null && error === null && <p role="status">Loading...</p>}
      {shown !== null && shown.messages.length === 0 && (
        <p className="fi-empty" data-testid="yi-empty">
          {shown.empty}
        </p>
      )}
      <ul className="fi-rows">
        {shown?.messages.map((m) => (
          <li key={m.id}>
            <button
              type="button"
              className="fi-row yi-row"
              data-testid="yi-row"
              data-unread={m.unread ? "true" : undefined}
              aria-current={selected === m.id ? "true" : undefined}
              onClick={() => onOpen(m.id)}
            >
              <span className="fi-row-text">
                <span className="yi-row-head">
                  <span className="fi-row-title yi-row-from">{m.from.label}</span>
                  <span className="yi-row-time">{formatInboxTime(m.sentAtUtc)}</span>
                </span>
                <span className="yi-row-subject">{m.subject}</span>
                <span className="fi-row-line">{m.body}</span>
              </span>
              <span className="fi-badge" data-tone={m.tone}>
                {m.stateLabel}
              </span>
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}
