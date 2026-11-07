import { useCallback, useEffect, useRef, useState } from "react";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import { getFactoryInboxList, type FactoryInboxList } from "./factoryInboxClient";
import { FACTORY_INBOX_SURFACE, FactoryComposer } from "./FactoryComposer";
import { startOneReadAtATime } from "./oneReadAtATime";
import "./factoryInbox.css";

// THE FACTORY INBOX LIST (Factory Inbox mission, phase 4; mockup A, and the Cockpit's list column in mockup D): every
// factory with a Boss, in the Gateway's order, each row with the Gateway's own line, waiting line, badge and tone. The
// composer is on the list itself, so the owner can pick a factory, speak and send in one place without opening a
// thread - "never wait for anything to start". The list is read again 15 seconds after each read ends - one read at a
// time, each with its own time limit (oneReadAtATime.ts), the same loop as the thread.

const REFRESH_MS = 15_000;

export interface FactoryInboxListViewProps {
  surface: "cockpit" | "mobile";
  /** Open one factory's thread. */
  onOpen: (factory: string) => void;
  /** The factory whose thread is open beside the list (the Cockpit), marked on its row. */
  selected?: string | null;
  /** Whether to mount the composer under the list (the phone and the Cockpit's list without a pane open). */
  showComposer?: boolean;
}

export function FactoryInboxListView({ surface, onOpen, selected = null, showComposer = true }: FactoryInboxListViewProps) {
  const [list, setList] = useState<FactoryInboxList | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [picked, setPicked] = useState<string | null>(null);
  // Ask for a read now: at once when none runs, or as soon as the running one ends.
  const readNowRef = useRef<() => void>(() => {});

  const reload = useCallback(() => readNowRef.current(), []);

  useEffect(() => {
    const loop = startOneReadAtATime(
      (signal, background) =>
        getFactoryInboxList(signal).then(
          (l) => {
            if (signal.aborted) return;
            setList(l);
            setError(null);
            backgroundRecovered(FACTORY_INBOX_SURFACE, "read the factory inbox");
          },
          (err: unknown) => {
            if (signal.aborted) return;
            setError(describeAndReport(FACTORY_INBOX_SURFACE, "read the factory inbox", err, background ? { background: true } : undefined));
          },
        ),
      REFRESH_MS,
    );
    readNowRef.current = loop.readNow;
    return () => {
      readNowRef.current = () => {};
      loop.stop();
    };
  }, []);

  // The composer's factory: the one picked, else the open one, else the first in the Gateway's order.
  const choices = list?.factories.map((f) => ({ factory: f.factory, title: f.title })) ?? [];
  const target = picked ?? selected ?? choices[0]?.factory ?? null;

  return (
    <section className="fi-list" data-testid="fi-list">
      {error !== null && (
        <div className="fi-composer-error" role="alert">
          {error}
        </div>
      )}
      {list === null && error === null && <p role="status">Loading...</p>}
      {list !== null && list.count === 0 && (
        <p className="fi-empty">No factory has a Boss yet, so there is no factory inbox to leave a message in.</p>
      )}
      <ul className="fi-rows">
        {list?.factories.map((f) => (
          <li key={f.factory}>
            <button
              type="button"
              className="fi-row"
              data-testid="fi-row"
              aria-current={selected === f.factory ? "true" : undefined}
              onClick={() => onOpen(f.factory)}
            >
              <span className="fi-row-text">
                <span className="fi-row-title">{f.title}</span>
                <span className="fi-row-line">{f.row.line}</span>
                {f.row.waitingLine !== null && <span className="fi-row-waiting">{f.row.waitingLine}</span>}
                {f.row.reasonLine !== null && (
                  <span className="fi-row-reason" data-tone={f.row.tone}>
                    {f.row.reasonLine}
                  </span>
                )}
              </span>
              <span className="fi-badge" data-tone={f.row.tone}>
                {f.row.badge}
              </span>
            </button>
          </li>
        ))}
      </ul>
      {showComposer && list !== null && list.count > 0 && (
        <FactoryComposer
          factory={target}
          choices={choices}
          onFactoryChange={setPicked}
          surface={surface}
          onLeft={reload}
        />
      )}
    </section>
  );
}
