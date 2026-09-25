import { useCallback, useEffect, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { getFactoryMap, type FactoryMapView } from "@devthrottle/client-core/factory/factoryAgentsClient";
import { EmptyState, ErrorBanner, LoadingState } from "../components";
import { FactoryMapDrawing, FactoryMapLegend, FactoryMapSpec } from "./FactoryMap";
import { AgentTable } from "./FactoryParts";
import "./factory.css";

// One factory's page (issue #3383). It opens on the Map tab: how the factory's agents connect, drawn from the map
// the factory published, with each agent's status from the Gateway. The Agents tab is the same rows its card shows.
// Read-only (rule 7): the only way to change a factory is the button that asks the Fleet Manager, which sends nothing
// by itself.
export function FactoryPageView() {
  const { factory = "" } = useParams();
  const [params, setParams] = useSearchParams();
  const tab = params.get("tab") === "agents" ? "agents" : "map";
  const [view, setView] = useState<FactoryMapView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  const reload = useCallback(() => setNonce((n) => n + 1), []);

  useEffect(() => {
    const ctrl = new AbortController();
    setError(null);
    getFactoryMap(factory, ctrl.signal).then(setView, (err: unknown) => {
      if (!ctrl.signal.aborted) setError(gatewayErrorMessage(err, "load this factory's map"));
    });
    return () => ctrl.abort();
  }, [factory, nonce]);

  if (error !== null) return <ErrorBanner message={error} onRetry={reload} />;
  if (view === null) return <LoadingState />;

  const picked = view.nodes.find((n) => n.id === selected) ?? null;
  return (
    <div className="fa-page" data-testid="factory-page">
      <nav className="fa-crumbs">
        <Link to="/factory-agents">Factory Agents</Link> / <span>{view.title}</span>
      </nav>
      <header className="fa-agent-head">
        <h1 className="ui-page-title">{view.title}</h1>
        <div className="fa-agent-actions">
          <Link className="fa-link" to={view.waitingHref}>
            Waiting for you
          </Link>
          <Link className="ui-btn ui-btn-secondary" to={view.changeHref} data-testid="fa-change">
            {view.changeLabel}
          </Link>
        </div>
      </header>

      <div className="fa-tabs" role="tablist" aria-label="Factory view">
        {view.tabs.map((t) => (
          <button
            key={t.key}
            type="button"
            role="tab"
            aria-selected={tab === t.key}
            className={`fa-tab${tab === t.key ? " active" : ""}`}
            onClick={() => setParams(t.key === "map" ? {} : { tab: t.key }, { replace: true })}
          >
            {t.label}
          </button>
        ))}
      </div>

      {tab === "agents" ? (
        <AgentTable rows={view.agents} showFactory={false} />
      ) : view.emptyText !== null ? (
        <EmptyState message={view.emptyText} />
      ) : (
        <section className="fa-panel">
          <div className="fa-map-grid">
            <div>
              <FactoryMapDrawing view={view} selected={selected} onSelect={setSelected} />
              <FactoryMapLegend view={view} />
              <p className="fa-dim">{view.statusNote}</p>
              {view.sourceText !== null && <p className="fa-dim" data-testid="fa-map-source">{view.sourceText}</p>}
            </div>
            <FactoryMapSpec node={picked} />
          </div>
        </section>
      )}
    </div>
  );
}
