import { useCallback, useEffect, useRef, useState } from "react";
import { sendEscape, sendInterrupt, type SessionDto } from "@devthrottle/client-core/api/client";
import { classifyOrNull } from "@devthrottle/client-core/sessions/ordering";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

// The surface label on every client-error report from this bar, so the Gateway log and
// GET /client-errors/recent name where the user was standing (issue #2189).
const SURFACE = "cockpit-session-actions";

// THE STRIP UNDER THE COMPOSER (owner ruling, 2026-09-20). This was a row of five driver buttons drawn
// at the same weight as Send, sitting under every conversation whether or not any of them could do
// anything. Three of them have moved to the session menu, under a "Context" heading, because they are
// rare and one of them destroys the conversation:
//
//   Compact (CompactContext cap)     -> SessionMenu
//   Clear context (ClearContext cap) -> SessionMenu
//   History (History cap)            -> SessionMenu
//
// What is left here is the one verb that is urgent, and its escalation:
//
//   Stop (Cancel cap)          -> POST /sessions/{sid}/escape    (the driver's soft cancel - Esc)
//   Interrupt (Interrupt cap)  -> POST /sessions/{sid}/interrupt (hard Ctrl+C, stronger than Stop)
//
// STOP IS DRAWN WHENEVER THE DRIVER DECLARES IT, not only while the Gateway says the session is
// working. The design called for it to appear only mid-turn, and that is the nicer screen - but the
// two failures are not the same size. A Stop shown while nothing is running costs a wasted Esc; a Stop
// hidden while something IS running leaves a person watching a runaway session with no button. The
// Gateway's bucket decides the WORDS beside it and whether the harder verb is offered, never whether
// the button exists.
//
// INTERRUPT IS AN ESCALATION, NOT A SYNONYM. It is not drawn until Stop has been pressed and the
// session is still working, which is the only moment the difference between the two means anything.

export interface SessionActionBarProps {
  sessionId: string | undefined;
  /** The selected session's SessionDto.driverCapabilities; a button shows only if its verb is listed. */
  capabilities: string[] | undefined;
  /** The selected session, for the Gateway's own working/waiting ruling. Undefined on a cold deep link. */
  session: SessionDto | undefined;
}

export function SessionActionBar({ sessionId, capabilities, session }: SessionActionBarProps) {
  const [acting, setActing] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Set when Stop has been pressed on this session. It is what turns the harder verb on - see the note
  // above. Reset whenever the session changes, so an escalation never carries over to a different one.
  const [stopTried, setStopTried] = useState(false);
  const statusTimer = useRef<number | null>(null);

  useEffect(() => {
    setStopTried(false);
  }, [sessionId]);

  useEffect(() => {
    return () => {
      if (statusTimer.current !== null) window.clearTimeout(statusTimer.current);
    };
  }, []);

  const flash = useCallback((message: string) => {
    setStatus(message);
    if (statusTimer.current !== null) window.clearTimeout(statusTimer.current);
    statusTimer.current = window.setTimeout(() => setStatus(null), 5000);
  }, []);

  // `action` names what the user pressed, in their terms ("stop the turn"). It shapes the sentence the
  // user reads AND labels the report that goes to the Gateway (issue #2189), so a failed button press is
  // never a bare status number on screen and never invisible on the server.
  const act = useCallback(
    async (verb: () => Promise<void>, done: string, action: string) => {
      if (!sessionId || acting) return;
      setActing(true);
      setError(null);
      try {
        await verb();
        flash(done);
      } catch (err) {
        setError(describeAndReport(SURFACE, action, err));
      } finally {
        setActing(false);
      }
    },
    [sessionId, acting, flash],
  );

  const has = (cap: string) => capabilities?.includes(cap) === true;

  // A cold deep link into a session (the roster has not arrived yet, so the selected session and its
  // declared capabilities are still undefined) used to render an empty button row. Show a small loading
  // state instead until the capabilities resolve (issue #1247). An empty array - a session that loaded
  // and genuinely declares no driver verbs - is NOT loading, so it correctly renders no strip.
  if (capabilities === undefined) {
    return (
      <div className="action-strip">
        <span className="action-loading">Loading session...</span>
      </div>
    );
  }

  // The Gateway's own ruling, read - never re-derived from a timer or a byte count here. Null means it
  // did not say, and this control renders that absence by staying quiet rather than by guessing. It is
  // deliberately the non-throwing door onto the same rule: an unstamped session must not take the whole
  // composer area down with it, which is exactly what the throwing one did.
  const working = session !== undefined && classifyOrNull(session) === "active";
  const canStop = has("Cancel");
  const canEscalate = stopTried && working && has("Interrupt");

  // Nothing to say and nothing to press: draw nothing at all rather than an empty strip.
  if (!canStop && status === null && error === null && !working) return null;

  return (
    <div className="action-strip">
      {working && <span className="action-working">Working</span>}
      {canStop && (
        <button
          type="button"
          className="act-stop"
          disabled={acting}
          onClick={() => {
            setStopTried(true);
            if (sessionId) void act(() => sendEscape(sessionId), "turn stopped", "stop the turn");
          }}
          title="Stop the current turn (the driver's soft cancel - Esc)"
        >
          <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false">
            <rect x="4.5" y="4.5" width="7" height="7" rx="1.4" fill="currentColor" />
          </svg>
          Stop
        </button>
      )}
      {canEscalate && (
        <button
          type="button"
          className="act-escalate"
          disabled={acting}
          onClick={() => sessionId && void act(() => sendInterrupt(sessionId), "interrupted", "interrupt the session")}
          title="Still going? Ctrl+C, which is stronger than Stop"
        >
          Force interrupt
        </button>
      )}
      {status !== null && <span className="action-status">{status}</span>}
      {error !== null && <span className="action-error">{error}</span>}
    </div>
  );
}
