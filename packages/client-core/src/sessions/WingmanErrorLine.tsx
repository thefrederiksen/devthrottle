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
import { useEffect, useSyncExternalStore } from "react";
import type { SessionDto } from "../api/client";
import { askWingmanAgain, gatewayErrorMessage } from "../api/client";
import { useNow } from "../polling/useNow";
import { wingmanErrorOf, wingmanRetryLine, type WingmanErrorDisplay } from "./wingmanError";
import "./wingmanError.css";

// THE PRESS OUTLIVES THE STAMP, AND THE ROW. Asking again makes the row "being read", and while it is being read the
// Gateway stamps no error - rightly, there is none to show yet. A roster also moves a row between its groups as its
// state changes, which unmounts it. If the press lived in component state it would vanish under the finger that
// pressed it and take the Gateway's answer with it. So it lives here, per session, outside any one mounted row.
type Press = { asking: boolean; answer: string | null; lastStamped: WingmanErrorDisplay | null };
const IDLE: Press = { asking: false, answer: null, lastStamped: null };
const presses = new Map<string, Press>();
const listeners = new Set<() => void>();

function pressOf(sid: string): Press {
  return presses.get(sid) ?? IDLE;
}

function setPress(sid: string, change: Partial<Press>): void {
  presses.set(sid, { ...pressOf(sid), ...change });
  listeners.forEach((l) => l());
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** For tests: forget every press, so one test's answer is never on the next test's card. */
export function resetWingmanErrorPressesForTest(): void {
  presses.clear();
}

async function askAgain(sid: string): Promise<void> {
  if (sid === "" || pressOf(sid).asking) return;
  setPress(sid, { asking: true, answer: null });
  try {
    const result = await askWingmanAgain(sid);
    setPress(sid, { asking: false, answer: result.message });
  } catch (err) {
    setPress(sid, { asking: false, answer: gatewayErrorMessage(err, "ask the Wingman again") });
  }
}

export function WingmanErrorLine({ session }: { session: SessionDto }) {
  const sid = session.sessionId ?? "";
  const stamped = wingmanErrorOf(session);
  const now = useNow();
  const press = useSyncExternalStore(subscribe, () => pressOf(sid), () => pressOf(sid));

  // Remember the last error the Gateway stamped, to draw the block while a press is in flight; and once the error
  // is gone with no press running, the reading succeeded - forget the press, its answer has nothing left to explain.
  useEffect(() => {
    if (stamped !== null) {
      if (pressOf(sid).lastStamped !== stamped) presses.set(sid, { ...pressOf(sid), lastStamped: stamped });
    } else if (!pressOf(sid).asking && presses.has(sid)) {
      presses.delete(sid);
    }
  }, [sid, stamped]);

  // No error stamped and no press in flight: nothing to show. The card itself now carries what the Wingman read.
  const error = stamped ?? (press.asking ? press.lastStamped : null);
  if (error === null) return null;

  // While the press is in flight and nothing is stamped, no time is shown: the booked time is the Gateway's to
  // restate when it stamps the error again.
  const retryLine = stamped === null ? "" : wingmanRetryLine(stamped, now);
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
        <button type="button" className="wingman-error-ask" onClick={() => void askAgain(sid)} disabled={press.asking}>
          {press.asking ? "Asking..." : error.askAgainLabel}
        </button>
        {press.answer !== null && press.answer !== "" && <span className="wingman-error-answer">{press.answer}</span>}
      </div>
    </div>
  );
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
