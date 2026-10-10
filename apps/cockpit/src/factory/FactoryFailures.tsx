import { useEffect, useState } from "react";
import { Link, useLocation } from "react-router-dom";
import {
  markFactoryFailureHandled,
  type FactoryFailureItem,
  type FactoryPageFailures,
} from "@devthrottle/client-core/factory/factoriesScreenClient";
import { Button } from "../components";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-factory-failures";

// What is failing on a factory's page (Factories screen round 2): where the list's FAILING word links to
// (#failing). Every word is the Gateway's (FactoriesScreenFold); "Handled" appends a NEW row that corrects the
// failure, and the failed row itself is never changed.

export function FailuresCard({ factory, failures, onChanged }: { factory: string; failures: FactoryPageFailures; onChanged: () => void }) {
  return (
    <section className="fa-panel" id="failing" data-testid="fa-page-failing">
      <h2 className="fa-section-title">{failures.heading}</h2>
      <p className="fa-dim">{failures.note}</p>
      <div className="fa-waiting">
        {failures.items.map((item, i) => (
          <FailureItem key={item.id ?? `schedule-${i}`} factory={factory} item={item} onHandled={onChanged} />
        ))}
      </div>
    </section>
  );
}

function FailureItem({ factory, item, onHandled }: { factory: string; item: FactoryFailureItem; onHandled: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const id = item.id;
  return (
    <div className="fa-waiting-item" data-testid={id !== null ? `fa-failure-${id}` : "fa-failure-schedule"}>
      {item.subject !== null && (
        <div className="fa-waiting-head">
          <strong>{item.subject}</strong>
        </div>
      )}
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
        {item.note !== null && <span className="fa-dim">{item.note}</span>}
        {id !== null && item.handledLabel !== null && (
          <Button
            variant="primary"
            disabled={busy}
            onClick={async () => {
              setBusy(true);
              setError(null);
              try {
                await markFactoryFailureHandled(factory, id);
                onHandled();
              } catch (err) {
                setError(describeAndReport(SURFACE, "mark it handled", err));
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

/** Opening "/factories/x#failing" scrolls to that card once the page has loaded. */
export function useScrollToHash(ready: boolean) {
  const { hash } = useLocation();
  useEffect(() => {
    if (!ready || hash.length < 2) return;
    const target = document.getElementById(decodeURIComponent(hash.slice(1)));
    // jsdom has no scrollIntoView; a real browser always does.
    if (target !== null && typeof target.scrollIntoView === "function") target.scrollIntoView({ block: "start" });
  }, [ready, hash]);
}
