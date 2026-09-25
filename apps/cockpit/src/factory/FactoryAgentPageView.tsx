import { useCallback, useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { getFactoryAgent, type FactoryAgentPage } from "@devthrottle/client-core/factory/factoryAgentsClient";
import { ErrorBanner, LoadingState } from "../components";
import { ActivityTable, NumberPill, PauseButton, ToneChip } from "./FactoryParts";
import "./factory.css";

// One factory agent (Screen 2). Read-only: the only controls are Pause / Resume and "Ask the Fleet Manager to
// change it", which opens the Fleet Manager page with the request written and sends nothing by itself.
//
// Its definition - character, what it may and never does, skills, credentials, versions - is not stored on the
// Gateway until #2177 mission 1, and the page says exactly that, in the Gateway's words.
export function FactoryAgentPageView() {
  const { factory = "", agent = "" } = useParams();
  const [page, setPage] = useState<FactoryAgentPage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  const reload = useCallback(() => setNonce((n) => n + 1), []);

  useEffect(() => {
    const ctrl = new AbortController();
    setError(null);
    getFactoryAgent(factory, agent, ctrl.signal).then(setPage, (err: unknown) => {
      if (!ctrl.signal.aborted) setError(gatewayErrorMessage(err, "load this factory agent"));
    });
    return () => ctrl.abort();
  }, [factory, agent, nonce]);

  if (error !== null) return <ErrorBanner message={error} onRetry={reload} />;
  if (page === null) return <LoadingState />;

  return (
    <div className="fa-page" data-testid="factory-agent-page">
      <nav className="fa-crumbs">
        <Link to="/factory-agents">Factory Agents</Link> / <Link to={page.factoryHref}>{page.factoryTitle}</Link> /
      </nav>
      <header className="fa-agent-head">
        <h1 className="ui-page-title">
          {page.name} <ToneChip word={page.statusWord} tone={page.statusTone} />
        </h1>
        <div className="fa-agent-actions">
          {page.pause !== null && (
            <PauseButton pause={page.pause} factory={page.factoryId} agent={page.agentId} onDone={reload} />
          )}
          <Link className="ui-btn ui-btn-secondary fa-ask" to={page.askHref} data-testid="fa-ask">
            {page.askLabel}
          </Link>
        </div>
      </header>
      {page.pauseUnavailableText !== null && <p className="fa-dim">{page.pauseUnavailableText}</p>}

      <section className="fa-panel">
        <h2 className="fa-section-title">Who it is, and what it may do</h2>
        <p className="fa-definition" data-testid="fa-definition">
          {page.definitionText}
        </p>
      </section>

      <section className="fa-panel">
        <h2 className="fa-section-title">Woken by</h2>
        {page.wokenByEmptyText !== null && <p className="fa-dim">{page.wokenByEmptyText}</p>}
        {page.wokenBy.map((w) => (
          <div key={w.triggerId} className="fa-woken">
            <div>
              {w.text} <ToneChip word={w.statusWord} tone={w.statusTone} />
            </div>
            {w.statusText !== null && <div className="fa-fault">{w.statusText}</div>}
            <div className="fa-dim">Last check: {w.lastCheck}</div>
          </div>
        ))}
        {page.lastCheck !== null && <div className="fa-dim">Last check of any trigger: {page.lastCheck}</div>}
      </section>

      <section className="fa-panel">
        <h2 className="fa-section-title">{page.last7DaysTitle}</h2>
        <div className="fa-numbers">
          {page.last7Days.map((n) => (
            <NumberPill key={n.text} n={n} />
          ))}
        </div>
      </section>

      <section className="fa-panel">
        <h2 className="fa-section-title">{page.recentTitle}</h2>
        {page.recentEmptyText !== null ? (
          <p className="fa-dim">{page.recentEmptyText}</p>
        ) : (
          <ActivityTable rows={page.recent} showFactory={false} />
        )}
      </section>
    </div>
  );
}
