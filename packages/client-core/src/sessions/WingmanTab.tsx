// The Wingman tab (the Wingman inspector, phase 3): every stop the Wingman judged for one session - what it was
// shown, exactly what it was asked, what the model answered word for word, and what the product did with it.
//
// THIS VIEW DECIDES NOTHING. Every word, colour, group and count is the Gateway's fold (WingmanStopsFold), rendered
// verbatim. The only work done here is layout, formatting the UTC instants into local time, and filtering the list
// by the group key the Gateway already stamped on each stop. It does not answer a stop and does not write feedback.
//
// Lives in client-core so the shell stays thin; only the Cockpit mounts it (the owner's ruling - not the phone).
import { useEffect, useMemo, useState } from "react";
import { gatewayErrorMessage } from "../api/client";
import { readWingmanStops, type WingmanStop, type WingmanStopsResponse } from "./wingmanStops";
import "./wingmanTab.css";

/** What the tab shows when the route answered with no stops, in the phase 3 ruling's words. */
export const WINGMAN_TAB_EMPTY = "The Wingman has not judged this session in the last seven days.";

/** A Gateway UTC instant, in the reader's own local date and time. */
export function formatLocalInstant(utc: string): string {
  return new Date(utc).toLocaleString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

type Load =
  | { kind: "loading" }
  | { kind: "error"; message: string }
  | { kind: "loaded"; answer: WingmanStopsResponse };

export function WingmanTab({ sessionId }: { sessionId: string }) {
  const [load, setLoad] = useState<Load>({ kind: "loading" });
  const [filter, setFilter] = useState("all");
  const [selectedId, setSelectedId] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setLoad({ kind: "loading" });
    setFilter("all");
    setSelectedId(null);
    readWingmanStops(sessionId, controller.signal)
      .then((answer) => setLoad({ kind: "loaded", answer }))
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setLoad({ kind: "error", message: gatewayErrorMessage(err, "read the Wingman's stops for this session") });
      });
    return () => controller.abort();
  }, [sessionId]);

  const answer = load.kind === "loaded" ? load.answer : null;
  const visible = useMemo(
    () => (answer === null ? [] : answer.stops.filter((s) => filter === "all" || s.group === filter)),
    [answer, filter],
  );

  if (load.kind === "loading") {
    return (
      <div className="wingman-tab">
        <p className="wingman-state" role="status">
          Loading the Wingman's stops...
        </p>
      </div>
    );
  }
  if (load.kind === "error") {
    return (
      <div className="wingman-tab">
        <p className="wingman-state wingman-state-error" role="alert">
          {load.message}
        </p>
      </div>
    );
  }
  if (answer === null || answer.stops.length === 0) {
    return (
      <div className="wingman-tab">
        <p className="wingman-state" role="status">
          {WINGMAN_TAB_EMPTY}
        </p>
      </div>
    );
  }

  const selected = visible.find((s) => s.traceId === selectedId) ?? visible[0] ?? null;
  // Following a pointer to another stop (what this one replaced, the expiry that ended its clock) shows every stop
  // again, so the stop it names is always on the list beside it.
  const select = (traceId: string) => {
    setFilter("all");
    setSelectedId(traceId);
  };

  return (
    <div className="wingman-tab">
      <div className="wingman-chips" role="toolbar" aria-label="Filter the stops">
        {answer.groups.map((g) => (
          <button
            key={g.key}
            type="button"
            className={`wingman-chip ${filter === g.key ? "on" : ""}`}
            aria-pressed={filter === g.key}
            onClick={() => setFilter(g.key)}
          >
            {g.label}
            <b>{g.count}</b>
          </button>
        ))}
      </div>
      <div className="wingman-split">
        <ul className="wingman-list" aria-label="Stops">
          {visible.map((s) => (
            <li key={s.traceId}>
              <button
                type="button"
                className={`wingman-item ${selected?.traceId === s.traceId ? "on" : ""}`}
                aria-current={selected?.traceId === s.traceId}
                onClick={() => setSelectedId(s.traceId)}
              >
                <RowDot stop={s} />
                <span className="wingman-item-verdict">{s.verdictText}</span>
                <span className="wingman-item-time">{formatLocalInstant(s.observedAtUtc)}</span>
                <span className="wingman-item-label">{s.strip.label}</span>
                <span className="wingman-item-meta">
                  {s.outcomeText} - {s.triggerText}
                </span>
              </button>
            </li>
          ))}
        </ul>
        {selected === null ? (
          <p className="wingman-state" role="status">
            No stops in this filter.
          </p>
        ) : (
          <StopDetail stop={selected} onSelect={select} />
        )}
      </div>
    </div>
  );
}

/** The colour the row wore with this stop on it, as the Gateway recorded it; an outline when it was not recorded. */
function RowDot({ stop }: { stop: WingmanStop }) {
  return (
    <span
      className={`wingman-dot ${stop.rowColourHex ? "" : "wingman-dot-none"}`}
      style={stop.rowColourHex ? { background: stop.rowColourHex } : undefined}
      title={stop.rowColour ?? stop.rowLabel}
      aria-hidden="true"
    />
  );
}

function StopDetail({ stop, onSelect }: { stop: WingmanStop; onSelect: (traceId: string) => void }) {
  const { saw, asked, answered, did } = stop;
  return (
    <section className="wingman-detail" aria-label="The selected stop">
      <div className="wingman-strip">
        <div className="wingman-strip-top">
          <RowDot stop={stop} />
          <span className="wingman-strip-label">{stop.strip.label}</span>
        </div>
        <div className="wingman-strip-facts">
          <span className="wingman-pill">{stop.strip.outcomeText}</span>
          <span className="wingman-pill">{stop.strip.replyText}</span>
          <span className="wingman-pill">{stop.verdictText}</span>
          <span className="wingman-pill">{stop.triggerText}</span>
        </div>
        <dl className="wingman-kv">
          <dt>Row</dt>
          <dd>
            {stop.rowColour ? `${stop.rowColour} - ` : ""}
            {stop.rowLabel}
          </dd>
          <dt>Stop seen</dt>
          <dd>{formatLocalInstant(stop.observedAtUtc)}</dd>
          <dt>Recorded</dt>
          <dd>{formatLocalInstant(stop.recordedAtUtc)}</dd>
        </dl>
      </div>

      <div className="wingman-blocks">
        <article className="wingman-block" aria-label="What it saw">
          <h4>What it saw</h4>
          {saw.kept ? (
            <>
              {saw.facts.length > 0 && (
                <dl className="wingman-kv">
                  {saw.facts.map((f) => (
                    <FactRow key={f.name} name={f.name} value={f.value} />
                  ))}
                </dl>
              )}
              <h5>Screen</h5>
              <pre className="wingman-text wingman-screen">{saw.screenRows.join("\n")}</pre>
              {saw.sourceHeading && saw.sourceText != null && (
                <>
                  <h5>{saw.sourceHeading}</h5>
                  <pre className="wingman-text">{saw.sourceText}</pre>
                </>
              )}
              {saw.recentTurns != null && (
                <>
                  <h5>Recent turns</h5>
                  <pre className="wingman-text">{saw.recentTurns}</pre>
                </>
              )}
            </>
          ) : (
            <p className="wingman-note">{saw.notKeptText}</p>
          )}
        </article>

        <article className="wingman-block" aria-label="What it was asked">
          <h4>What it was asked</h4>
          {asked.prompt != null ? (
            <>
              {asked.cutText && <p className="wingman-note">{asked.cutText}</p>}
              <pre className="wingman-text">{asked.prompt}</pre>
            </>
          ) : (
            <p className="wingman-note">{asked.notAskedText}</p>
          )}
        </article>

        <article className="wingman-block" aria-label="What it answered">
          <h4>
            What it answered<span className="wingman-block-aside">{stop.strip.replyText}</span>
          </h4>
          {answered.rawReply != null ? (
            <>
              {answered.cutText && <p className="wingman-note">{answered.cutText}</p>}
              <pre className="wingman-text">{answered.rawReply}</pre>
            </>
          ) : (
            <p className="wingman-note">{answered.noAnswerText}</p>
          )}
        </article>

        <article className="wingman-block" aria-label="What the product did">
          <h4>What the product did</h4>
          <p className="wingman-decision">{did.decision}</p>
          {did.reason != null && <pre className="wingman-text wingman-reason">{did.reason}</pre>}
          {(did.verdictLabel != null || did.verdictSummary != null) && (
            <dl className="wingman-kv">
              {did.verdictLabel != null && <FactRow name="Label" value={did.verdictLabel} />}
              {did.verdictSummary != null && <FactRow name="Summary" value={did.verdictSummary} />}
            </dl>
          )}
          {did.causeText != null && <p className="wingman-note">{did.causeText}</p>}
          {did.replacedText != null && (
            <p className="wingman-note">
              {did.replacedText}
              {did.replacedTraceId && (
                <button type="button" className="wingman-link" onClick={() => onSelect(did.replacedTraceId!)}>
                  Show that stop
                </button>
              )}
            </p>
          )}
          {did.clock && (
            <div className="wingman-clock">
              <p>{did.clock.text}</p>
              <dl className="wingman-kv">
                {did.clock.setToRunOutAtUtc && (
                  <FactRow name="Set to run out" value={formatLocalInstant(did.clock.setToRunOutAtUtc)} />
                )}
                {did.clock.ranOutAtUtc && <FactRow name="Ran out" value={formatLocalInstant(did.clock.ranOutAtUtc)} />}
              </dl>
              {did.clock.ranOutTraceId && (
                <button type="button" className="wingman-link" onClick={() => onSelect(did.clock!.ranOutTraceId!)}>
                  Show the stop that ended it
                </button>
              )}
            </div>
          )}
        </article>
      </div>
    </section>
  );
}

function FactRow({ name, value }: { name: string; value: string }) {
  return (
    <>
      <dt>{name}</dt>
      <dd>{value}</dd>
    </>
  );
}
