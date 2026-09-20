// The "Wingman error" on a session card: the tag, the retry line, and the "Ask again" button (mission "Wingman
// error and retry", 2026-09-19).
//
// IT LIVES ONCE, HERE, and the Cockpit roster and the mobile roster both mount it, so the two surfaces cannot say
// the same failure two different ways. It shows whenever the Gateway stamped an error on the row - it does not
// look at voice mode, because a reading fails the same way whether or not anybody is listening.
//
// EVERY WORD IS THE GATEWAY'S except the countdown, which is the Gateway's absolute time turned into "in 40
// seconds" against the shared one-second clock. When nothing is booked the Gateway sends its own sentence and
// this renders it verbatim - the component has no sentence of its own that promises an attempt.
//
// THE BUTTON REPORTS. A press makes one attempt now; while it runs the button says so and cannot be pressed
// twice, and the Gateway's answer is shown under it. A button that reports nothing reads as broken.
import { useCallback, useEffect, useRef, useState } from "react";
import type { SessionDto } from "../api/client";
import { askWingmanAgain, gatewayErrorMessage } from "../api/client";
import { useNow } from "../polling/useNow";
import { wingmanErrorOf, wingmanRetryLine, type WingmanErrorDisplay } from "./wingmanError";
import "./wingmanError.css";

export function WingmanErrorLine({ session }: { session: SessionDto }) {
  const error = wingmanErrorOf(session);
  if (error === null) return null;
  return <WingmanErrorBody session={session} />;
}

/**
 * The retry line alone, ticking: for the voice screens, whose card already carries the Gateway's label, message
 * and button and only lacks WHEN. Renders nothing when the display carries no Wingman error.
 */
export function WingmanRetryCountdown({ error }: { error: WingmanErrorDisplay | null | undefined }) {
  const now = useNow();
  if (error === null || error === undefined) return null;
  // With nothing booked the voice card's own message already is the Gateway's "nothing more is scheduled".
  if (error.exhausted === true || !error.nextRetryAtUtc) return null;
  return <div className="wingman-error-retry wingman-error-retry-voice">{wingmanRetryLine(error, now)}</div>;
}

function WingmanErrorBody({ session }: { session: SessionDto }) {
  const error = wingmanErrorOf(session)!;
  const now = useNow();
  const [asking, setAsking] = useState(false);
  const [answer, setAnswer] = useState<string | null>(null);
  const alive = useRef(true);
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);

  const sid = session.sessionId ?? "";
  const askAgain = useCallback(async () => {
    if (asking || sid === "") return;
    setAsking(true);
    setAnswer(null);
    try {
      const result = await askWingmanAgain(sid);
      if (alive.current) setAnswer(result.message);
    } catch (err) {
      if (alive.current) setAnswer(gatewayErrorMessage(err, "ask the Wingman again"));
    } finally {
      if (alive.current) setAsking(false);
    }
  }, [asking, sid]);

  const retryLine = wingmanRetryLine(error, now);
  return (
    <div className="wingman-error" role="status">
      <div className="wingman-error-head">
        <span className="wingman-error-tag" title={error.reason ?? undefined}>
          {error.tag}
        </span>
        {retryLine !== "" && <span className="wingman-error-retry">{retryLine}</span>}
      </div>
      {error.reason !== undefined && error.reason !== "" && <div className="wingman-error-reason">{error.reason}</div>}
      <div className="wingman-error-actions">
        <button type="button" className="wingman-error-ask" onClick={askAgain} disabled={asking}>
          {asking ? "Asking..." : error.askAgainLabel}
        </button>
        {answer !== null && answer !== "" && <span className="wingman-error-answer">{answer}</span>}
      </div>
    </div>
  );
}
