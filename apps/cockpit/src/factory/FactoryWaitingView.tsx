import { useCallback, useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import {
  getFactoryWaiting,
  markFactoryItemHandled,
  type FactoryWaitingItem,
  type FactoryWaitingView as WaitingView,
} from "@devthrottle/client-core/factory/factoryAgentsClient";
import { Button, EmptyState, ErrorBanner, LoadingState, PageHeader } from "../components";
import { ToneChip } from "./FactoryParts";
import "./factory.css";

// Waiting for you (Screen 4): every asked and escalated row nothing has corrected yet, as the Gateway folded it.
// "I have handled it" appends a NEW row that corrects the escalation; the escalation itself is never edited.
export function FactoryWaitingView() {
  const [params] = useSearchParams();
  const factory = params.get("factory") ?? undefined;
  const [view, setView] = useState<WaitingView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  const reload = useCallback(() => setNonce((n) => n + 1), []);

  useEffect(() => {
    const ctrl = new AbortController();
    setError(null);
    getFactoryWaiting(factory, ctrl.signal).then(setView, (err: unknown) => {
      if (!ctrl.signal.aborted) setError(gatewayErrorMessage(err, "load what is waiting for you"));
    });
    return () => ctrl.abort();
  }, [factory, nonce]);

  if (error !== null) return <ErrorBanner message={error} onRetry={reload} />;
  if (view === null) return <LoadingState />;

  return (
    <div className="fa-page" data-testid="factory-waiting-page">
      <nav className="fa-crumbs">
        <Link to="/factory-agents">Factory Agents</Link> /
      </nav>
      <PageHeader title={view.title} subtitle={view.summary} />
      {view.truncatedText !== null && <div className="fa-warn">{view.truncatedText}</div>}
      {view.emptyText !== null ? (
        <EmptyState message={view.emptyText} />
      ) : (
        <div className="fa-waiting">
          {view.items.map((item) => (
            <WaitingItem key={item.id} item={item} onHandled={reload} />
          ))}
        </div>
      )}
    </div>
  );
}

function WaitingItem({ item, onHandled }: { item: FactoryWaitingItem; onHandled: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <div className="fa-waiting-item" data-testid={`fa-waiting-${item.id}`}>
      <div className="fa-waiting-head">
        <ToneChip word={item.word} tone={item.tone} />
        {item.subject !== null && <strong>{item.subject}</strong>}
        <span className="fa-dim">{item.factoryTitle}</span>
      </div>
      <div>{item.what}</div>
      <div className="fa-waiting-foot">
        <span className="fa-dim">{item.by}</span>
        {item.sessionId !== null && item.sessionLabel !== null && (
          <Link className="mono" to={`/session/${encodeURIComponent(item.sessionId)}`}>
            {item.sessionLabel}
          </Link>
        )}
        {item.link !== null && (
          <a className="fa-link" href={item.link} target="_blank" rel="noopener noreferrer">
            {item.linkLabel}
          </a>
        )}
        {item.handledLabel !== null && (
          <Button
            variant="primary"
            disabled={busy}
            onClick={async () => {
              setBusy(true);
              setError(null);
              try {
                await markFactoryItemHandled(item.id);
                onHandled();
              } catch (err) {
                setError(gatewayErrorMessage(err, "mark it handled"));
              } finally {
                setBusy(false);
              }
            }}
          >
            {busy ? item.handledBusyLabel : item.handledLabel}
          </Button>
        )}
        {error !== null && <span className="fa-error">{error}</span>}
      </div>
    </div>
  );
}
