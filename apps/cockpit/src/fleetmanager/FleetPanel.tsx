import { Link } from "react-router-dom";
import type { FleetManagerPage, FleetPanelItem, FleetPanelSection } from "@devthrottle/client-core/fleetmanager/pageClient";
import type { PollState } from "@devthrottle/client-core/polling/pollingStore";

// The live right panel of the Fleet Manager page (the Fleet Manager mission, step 6). Facts from the Gateway, not
// the model: every heading, count, age, dot and sentence is folded there and rendered here as sent. It fails on its
// own: a failed read shows the Gateway's words above whatever was last read, and never an invented value.

function PanelItem({ item }: { item: FleetPanelItem }) {
  const body = (
    <>
      <span className={`fmp-dot fmp-dot-${item.dot}`} />
      <div className="fmp-item-text">
        <div className="fmp-item-title">{item.title}</div>
        {item.meta.length > 0 && <div className="fmp-item-meta">{item.meta}</div>}
        {item.label && <div className="fmp-item-label">{item.label}</div>}
      </div>
      {item.age && <span className={item.attention ? "fmp-item-age" : "fmp-item-age fmp-item-age-dim"}>{item.age}</span>}
    </>
  );
  const className = item.attention ? "fmp-item fmp-item-attention" : "fmp-item";
  return item.sessionId ? (
    <Link className={className} to={`/session/${encodeURIComponent(item.sessionId)}`} data-testid={`fmp-item-${item.id}`}>
      {body}
    </Link>
  ) : (
    <div className={className} data-testid={`fmp-item-${item.id}`}>
      {body}
    </div>
  );
}

function Section({ section, name, walkthroughLabel }: { section: FleetPanelSection; name: string; walkthroughLabel?: string | null }) {
  return (
    <section className="fmp-sec" aria-label={section.title} data-testid={`fmp-sec-${name}`}>
      <h3 className={section.tone === "attention" ? "fmp-sec-title fmp-sec-title-attention" : "fmp-sec-title"}>
        {section.title} <span className="fmp-sec-count">{section.count}</span>
      </h3>
      {/* The way into the walkthrough (step 7). The Gateway offers it only when something is waiting. */}
      {walkthroughLabel && (
        <Link className="ui-btn ui-btn-primary fmp-walkthrough" to="/fleet-manager/walkthrough" data-testid="fmp-walkthrough">
          {walkthroughLabel}
        </Link>
      )}
      {section.items.map((item) => (
        <PanelItem key={item.id} item={item} />
      ))}
      {section.emptyText && <div className="fmp-sec-empty">{section.emptyText}</div>}
      {section.note && <div className="fmp-sec-note">{section.note}</div>}
    </section>
  );
}

export function FleetPanel({ state }: { state: PollState<FleetManagerPage> }) {
  const page = state.data;
  return (
    <aside className="fmp-side" aria-label="What the Gateway knows">
      {state.error !== null && (
        <div className="fmp-region-error" role="alert">
          {state.error}
        </div>
      )}
      {page === null ? (
        state.loading ? (
          <div className="fmp-loading" role="status">
            Loading what is waiting, under way and answered...
          </div>
        ) : null
      ) : (
        <>
          <Section section={page.waiting} name="waiting" walkthroughLabel={page.walkthroughLabel} />
          <Section section={page.underWay} name="under-way" />
          <Section section={page.landed} name="landed" />
          <div className="fmp-notmine" data-testid="fmp-notmine">
            <b>{page.notMine.lead}</b> {page.notMine.rest}
          </div>
        </>
      )}
    </aside>
  );
}
