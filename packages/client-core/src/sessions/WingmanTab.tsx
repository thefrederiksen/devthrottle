// The Wingman tab. Its default view is NOW - the live stop of this session, and what the owner needs to do about it
// (the Wingman tab, version 3). History is the version 1 list of every stop the Wingman judged, kept as it is until
// it is redesigned.
//
// NOW IS THE ONLY VIEW OF THE LIVE STOP, AND A FAILED READ SAYS SO. Now is fed by GET /sessions/{sid}/wingman-now.
// A Gateway that cannot serve it - refused, unreachable, or too old to have the route - says so in its own sentence,
// exactly like any other failure. There is no longer a bridge that quietly showed the version 1 list instead: it was
// scaffolding for a route that had not shipped, and it went out in the same release as the route, because the
// route's OWN genuine 404s (another account's session, an unknown one) would otherwise have read as "no Now here".
// History keeps that list, reachable beside Now at all times - including while the Now read is failing.
//
// A BACKGROUND REFRESH NEVER TAKES THE SCREEN AWAY. Now is re-read every few seconds without the owner asking, on
// the screen he types into. A failure on one of those refreshes keeps the last good screen and adds one quiet line
// beside it, so a 502 during a deploy or a one-second drop cannot unmount the reply box and destroy his draft.
//
// THESE VIEWS DECIDE NOTHING. Every word, colour, group and count is the Gateway's own fold - WingmanNowFold for Now,
// WingmanStopsFold for History - rendered verbatim. The only work done here is layout, formatting the UTC instants
// into the reader's local time, and filtering the list by the group key the Gateway already stamped.
//
// Lives in client-core so the shell stays thin; only the Cockpit mounts it (the owner's ruling - not the phone).
import { useEffect, useMemo, useState } from "react";
import { gatewayErrorMessage } from "../api/client";
import { readWingmanStops, type WingmanStop, type WingmanStopsResponse } from "./wingmanStops";
import { readWingmanNow, type WingmanNow as WingmanNowDto } from "./wingmanNowRead";
import { WingmanNow, type WingmanNowActions, type WingmanNowReplyBox } from "./WingmanNow";
import "./wingmanTab.css";

/** How often Now is re-read while the tab is open. It is one session's live stop, read only while it is on screen. */
const NOW_REFRESH_MILLISECONDS = 5000;

/**
 * The Wingman tab: Now by default, History beside it.
 *
 * `actions` is everything Now can do, wired by the shell that mounts it. An action with no handler is not drawn, so
 * this tab never shows a control that would do nothing.
 */
export function WingmanTab({
  sessionId,
  actions,
  replyBox,
}: {
  sessionId: string;
  /**
   * What Now can do, DECIDED ONCE THE ANSWER IS IN HAND. It is a function of the Gateway's answer, not a fixed set,
   * because which controls belong on the screen depends on what the Gateway said about this session - a snoozed one
   * is offered a wake and not a snooze, finished work is offered a close (the review's item B3). An action the shell
   * leaves out is not drawn, so Now never offers a control that would do nothing.
   */
  actions?: (now: WingmanNowDto) => WingmanNowActions;
  /** The shell's own message box, drawn inside Now wherever the Gateway offered a reply (the review's item B1). */
  replyBox?: WingmanNowReplyBox;
}) {
  // The last screen the Gateway served, and the failure of the most recent read, kept APART. A refresh that fails
  // adds the failure; it never clears the screen, because clearing it is what destroyed the owner's half-typed
  // reply. They are cleared together, by the session changing.
  const [now, setNow] = useState<WingmanNowDto | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [view, setView] = useState<"now" | "history">("now");

  useEffect(() => {
    let live = true;
    const controller = new AbortController();
    setNow(null);
    setFailure(null);
    setView("now");

    const read = () => {
      readWingmanNow(sessionId, controller.signal)
        .then((answer) => {
          if (!live) return;
          setNow(answer);
          setFailure(null);
        })
        .catch((err: unknown) => {
          if (!live || controller.signal.aborted) return;
          setFailure(gatewayErrorMessage(err, "read what this session needs now"));
        });
    };

    read();
    const timer = setInterval(read, NOW_REFRESH_MILLISECONDS);
    return () => {
      live = false;
      clearInterval(timer);
      controller.abort();
    };
  }, [sessionId]);

  return (
    <div className="wingman-tab">
      <div className="wingman-views" role="tablist" aria-label="The Wingman's views of this session">
        <button
          type="button"
          role="tab"
          aria-selected={view === "now"}
          className={`wingman-view ${view === "now" ? "on" : ""}`}
          onClick={() => setView("now")}
        >
          Now
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={view === "history"}
          className={`wingman-view ${view === "history" ? "on" : ""}`}
          onClick={() => setView("history")}
        >
          History
        </button>
      </div>
      {view === "now" ? (
        <NowView sessionId={sessionId} now={now} failure={failure} actions={actions} replyBox={replyBox} />
      ) : (
        <WingmanStopsView sessionId={sessionId} />
      )}
    </div>
  );
}

/**
 * The Now pane: the live stop while there is one, and what went wrong with the last read.
 *
 * With a screen in hand the failure is ONE QUIET LINE beside it - the screen stays exactly where it was, and so does
 * everything typed into it. With no screen yet (the very first read failed) the failure is all there is to show, and
 * it is shown as the Gateway wrote it.
 */
function NowView({
  sessionId,
  now,
  failure,
  actions,
  replyBox,
}: {
  sessionId: string;
  now: WingmanNowDto | null;
  failure: string | null;
  actions?: (now: WingmanNowDto) => WingmanNowActions;
  replyBox?: WingmanNowReplyBox;
}) {
  if (now === null) {
    return failure === null ? (
      <p className="wingman-state" role="status">
        Loading this session's live stop...
      </p>
    ) : (
      <p className="wingman-state wingman-state-error" role="alert">
        {failure}
      </p>
    );
  }
  return (
    <>
      {failure !== null && (
        <p className="wingman-stale" role="status">
          {failure}
        </p>
      )}
      {/* Keyed on the session, so moving to another one starts its Now with an empty reply box and no voice sentence
          carried over, while a refresh of the SAME session never remounts it and never loses a draft.
          Honest note: the effect above ALSO clears `now` when the session changes, so today either one on its own
          would empty the box - removing just one keeps the test green, and removing both turns it red. The key is
          kept because it states the invariant where the reader is looking, rather than leaving it to a reset three
          screens up that a later change could reasonably drop. */}
      <WingmanNow key={sessionId} now={now} actions={actions?.(now)} replyBox={replyBox} />
    </>
  );
}

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

/**
 * The version 1 list: every stop the Wingman judged for this session - what it was shown, exactly what it was asked,
 * what the model answered word for word, and what the product did with it. Unchanged; it is what History shows until
 * History is redesigned, and what the whole tab shows while the Now route does not exist.
 */
export function WingmanStopsView({ sessionId }: { sessionId: string }) {
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
