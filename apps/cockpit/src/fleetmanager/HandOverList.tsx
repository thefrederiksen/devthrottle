import { useState } from "react";
import { Link } from "react-router-dom";
import type { FleetNotMine, FleetPanelItem } from "@devthrottle/client-core/fleetmanager/pageClient";
import { runHandOver } from "@devthrottle/client-core/fleetmanager/handOverClient";

// The sessions that still ask the owner directly, and a way to hand each one to the Fleet Manager (the Fleet Manager
// mission, step 8; design section 3.1). The count, the list, its order, every word and whether a row offers the hand
// over are the Gateway's (FleetNotMine). A hand over asks the Gateway, shows its sentence whichever way it went, and
// then refreshes the page, so the row leaves the list only when the Gateway says it has.

function HandOverRow({ item, onChanged }: { item: FleetPanelItem; onChanged: () => void }) {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; text: string } | null>(null);
  const action = item.action ?? null;

  const hand = async () => {
    if (action === null || !item.sessionId) return;
    setBusy(true);
    setResult(null);
    const outcome = await runHandOver(item.sessionId, action.to);
    setBusy(false);
    setResult(outcome.ok ? { ok: true, text: outcome.sentence } : { ok: false, text: outcome.error });
    if (outcome.ok) onChanged();
  };

  return (
    <div className="fmp-ho-row" data-testid={`fmp-ho-${item.id}`}>
      <span className={`fmp-dot fmp-dot-${item.dot}`} />
      <div className="fmp-item-text">
        {item.sessionId ? (
          <Link className="fmp-ho-title" to={`/session/${encodeURIComponent(item.sessionId)}`}>
            {item.title}
          </Link>
        ) : (
          <div className="fmp-ho-title">{item.title}</div>
        )}
        {item.meta.length > 0 && <div className="fmp-item-meta">{item.meta}</div>}
        {result !== null && (
          <div className={result.ok ? "fmp-ho-done" : "fmp-ho-error"} role={result.ok ? "status" : "alert"}>
            {result.text}
          </div>
        )}
      </div>
      {action !== null && (result === null || !result.ok) && (
        <button type="button" className="fmp-ho-btn" title={action.title} disabled={busy} onClick={() => void hand()}>
          {busy ? action.busyLabel : action.label}
        </button>
      )}
    </div>
  );
}

export function HandOverList({ notMine, onChanged }: { notMine: FleetNotMine; onChanged: () => void }) {
  return (
    <section className="fmp-ho" aria-label={notMine.listTitle} data-testid="fmp-handover-list">
      <h3 className="fmp-sec-title">
        {notMine.listTitle} <span className="fmp-sec-count">{notMine.count}</span>
      </h3>
      <div className="fmp-ho-note">{notMine.listNote}</div>
      {notMine.sessions.map((item) => (
        <HandOverRow key={item.id} item={item} onChanged={onChanged} />
      ))}
    </section>
  );
}
