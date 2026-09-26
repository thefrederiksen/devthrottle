// THE WINGMAN DEBUG VIEW - staff only, and the third view on the Wingman tab beside Now and History.
//
// It answers one question and nothing else: for a stop, WHAT WAS THE MODEL GIVEN AND WHAT DID IT ACTUALLY SAY -
// for both calls. A reading takes two model calls, and until this was built only the first was recorded at all,
// with nothing able to read even that back. The contract was shrunk from twelve fields to five without anybody
// being able to see the answers behind the measurement; this is how that is checked from now on.
//
// THE LAYOUT IS THE MOCKUP THE OWNER SAW: a stop, then for each call - what it was fed, the exact prompt, the raw
// answer - and then what the product kept out of it. The judge's half and the narration's half sit one under the
// other, in the order they happened, because the second call is given the first call's decision and reads as a
// consequence of it.
//
// NOTHING HERE DECIDES ANYTHING. The headings, the "this call was not made" sentences and the cut warnings are the
// Gateway's own words, rendered verbatim. The only work done in this file is layout and turning UTC into local time.
import { useEffect, useState } from "react";
import { gatewayErrorMessage } from "../api/client";
import { readWingmanDebug, type WingmanDebugResponse, type WingmanDebugStop } from "./wingmanDebug";
import { formatLocalInstant } from "./WingmanTab";

type Load =
  | { kind: "loading" }
  | { kind: "error"; message: string }
  | { kind: "loaded"; answer: WingmanDebugResponse };

/** What the view says when the account is staff but this session has no record yet. */
export const WINGMAN_DEBUG_EMPTY = "The Wingman has not read this session in the last seven days.";

export function WingmanDebugView({ sessionId }: { sessionId: string }) {
  const [load, setLoad] = useState<Load>({ kind: "loading" });
  const [selectedId, setSelectedId] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setLoad({ kind: "loading" });
    setSelectedId(null);
    readWingmanDebug(sessionId, controller.signal)
      .then((answer) => setLoad({ kind: "loaded", answer }))
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setLoad({ kind: "error", message: gatewayErrorMessage(err, "read the Wingman's debug record") });
      });
    return () => controller.abort();
  }, [sessionId]);

  if (load.kind === "loading") {
    return (
      <p className="wingman-state" role="status">
        Loading every prompt and answer behind this session's readings...
      </p>
    );
  }
  if (load.kind === "error") {
    // Includes the ordinary refusal for an account that is not staff. The Gateway's own sentence is shown, because
    // "you may not read this" and "there is nothing here" are different answers and must not look alike.
    return (
      <p className="wingman-state wingman-state-error" role="alert">
        {load.message}
      </p>
    );
  }
  const { answer } = load;
  if (answer.stops.length === 0) {
    return (
      <p className="wingman-state" role="status">
        {WINGMAN_DEBUG_EMPTY}
      </p>
    );
  }

  const selected = answer.stops.find((s) => s.traceId === selectedId) ?? answer.stops[0];
  return (
    <div className="wingman-split">
      <ul className="wingman-list" aria-label="Readings">
        {answer.stops.map((s) => (
          <li key={s.traceId}>
            <button
              type="button"
              className={`wingman-item ${selected.traceId === s.traceId ? "on" : ""}`}
              aria-current={selected.traceId === s.traceId}
              onClick={() => setSelectedId(s.traceId)}
            >
              <span className="wingman-item-verdict">{s.state || s.outcome}</span>
              <span className="wingman-item-time">{formatLocalInstant(s.observedAtUtc)}</span>
              <span className="wingman-item-label">{s.label ?? s.failureReason ?? s.outcome}</span>
              <span className="wingman-item-meta">
                {s.outcome} - {s.trigger}
              </span>
            </button>
          </li>
        ))}
      </ul>
      <StopDebug stop={selected} judgeTitle={answer.judgeCallTitle} narrationTitle={answer.narrationCallTitle} />
    </div>
  );
}

function StopDebug({
  stop,
  judgeTitle,
  narrationTitle,
}: {
  stop: WingmanDebugStop;
  judgeTitle: string;
  narrationTitle: string;
}) {
  return (
    <section className="wingman-detail wingman-debug" aria-label="The selected reading, both calls">
      <div className="wingman-strip">
        <div className="wingman-strip-top">
          <span className="wingman-strip-label">{stop.label ?? stop.failureReason ?? stop.outcome}</span>
        </div>
        <div className="wingman-strip-facts">
          <span className="wingman-pill">{stop.state || stop.outcome}</span>
          <span className="wingman-pill">{stop.trigger}</span>
          {stop.model && <span className="wingman-pill">{stop.model}</span>}
          {stop.contractVersion && <span className="wingman-pill">contract {stop.contractVersion}</span>}
          {stop.judgeSeconds != null && <span className="wingman-pill">judge {stop.judgeSeconds.toFixed(1)}s</span>}
          {stop.narrationSeconds != null && (
            <span className="wingman-pill">words {stop.narrationSeconds.toFixed(1)}s</span>
          )}
        </div>
        <dl className="wingman-kv">
          <dt>Stop seen</dt>
          <dd>{formatLocalInstant(stop.observedAtUtc)}</dd>
          <dt>Recorded</dt>
          <dd>{formatLocalInstant(stop.recordedAtUtc)}</dd>
          {stop.verdictId && (
            <>
              <dt>Reading</dt>
              <dd>{stop.verdictId}</dd>
            </>
          )}
          {stop.rowLabel && (
            <>
              <dt>Row</dt>
              <dd>
                {stop.rowColour ? `${stop.rowColour} - ` : ""}
                {stop.rowLabel}
              </dd>
            </>
          )}
        </dl>
        {stop.failed && stop.failureReason && (
          <p className="wingman-note wingman-debug-refusal">Refused: {stop.failureReason}</p>
        )}
        {/* A bad button list costs the buttons, not the reading: the answer was kept, and this is why it has none. */}
        {stop.optionsDroppedReason && (
          <p className="wingman-note wingman-debug-refusal">Buttons dropped: {stop.optionsDroppedReason}</p>
        )}
        {stop.failed && (
          <p className="wingman-note">
            Retries made: {String(stop.retriesMade ?? 0)}.{" "}
            {stop.nextRetryAtUtc ? `Next retry booked for ${stop.nextRetryAtUtc}.` : "No retry is booked."}
          </p>
        )}
      </div>

      <div className="wingman-blocks">
        <article className="wingman-block" aria-label="What both calls were fed">
          <h4>What the Wingman was fed</h4>
          {stop.fed != null ? (
            <pre className="wingman-text wingman-screen">{stop.fed}</pre>
          ) : (
            <p className="wingman-note">{stop.fedAbsentText}</p>
          )}
        </article>

        <CallBlock
          title={judgeTitle}
          prompt={stop.judgePrompt}
          promptCutText={stop.judgePromptCutText}
          rawReply={stop.judgeRawReply}
          rawReplyCutText={stop.judgeRawReplyCutText}
          notMadeText={stop.judgeNotAskedText}
          failureDetail={null}
        />

        <CallBlock
          title={narrationTitle}
          prompt={stop.narrationPrompt}
          promptCutText={stop.narrationPromptCutText}
          rawReply={stop.narrationRawReply}
          rawReplyCutText={stop.narrationRawReplyCutText}
          notMadeText={stop.narrationNotMadeText}
          failureDetail={stop.narrationFailureDetail}
        />

        <article className="wingman-block" aria-label="What the reading kept">
          <h4>What the reading kept</h4>
          <dl className="wingman-kv">
            <dt>State</dt>
            <dd>{stop.state || "-"}</dd>
            <dt>Label</dt>
            <dd>{stop.label ?? "-"}</dd>
          </dl>
          <h5>Narration</h5>
          {stop.narration ? (
            <pre className="wingman-text">{stop.narration}</pre>
          ) : (
            <p className="wingman-note">This reading has no narration.</p>
          )}
        </article>
      </div>
    </section>
  );
}

/** One model call: the prompt as sent and the answer as received, or the Gateway's sentence saying it was not made. */
function CallBlock({
  title,
  prompt,
  promptCutText,
  rawReply,
  rawReplyCutText,
  notMadeText,
  failureDetail,
}: {
  title: string;
  prompt?: string | null;
  promptCutText?: string | null;
  rawReply?: string | null;
  rawReplyCutText?: string | null;
  notMadeText?: string | null;
  failureDetail?: string | null;
}) {
  return (
    <article className="wingman-block" aria-label={title}>
      <h4>{title}</h4>
      {notMadeText != null && <p className="wingman-note">{notMadeText}</p>}
      {failureDetail != null && <p className="wingman-note wingman-debug-refusal">{failureDetail}</p>}
      {prompt != null && (
        <>
          <h5>The exact prompt</h5>
          {promptCutText && <p className="wingman-note">{promptCutText}</p>}
          <pre className="wingman-text">{prompt}</pre>
        </>
      )}
      {rawReply != null && (
        <>
          <h5>The raw answer</h5>
          {rawReplyCutText && <p className="wingman-note">{rawReplyCutText}</p>}
          <pre className="wingman-text">{rawReply}</pre>
        </>
      )}
    </article>
  );
}
