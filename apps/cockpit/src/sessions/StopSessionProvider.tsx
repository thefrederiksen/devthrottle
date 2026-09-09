import {
  createContext,
  useCallback,
  useContext,
  useId,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import {
  stopSession,
  type SessionDto,
  type SessionStopOutcome,
} from "@devthrottle/client-core/api/client";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { useDismissOnBackdrop } from "../components";

// The surface label on every client-error report from this dialog, so the Gateway log and
// GET /client-errors/recent name where the user was standing (issue #2189).
const SURFACE = "cockpit-stop-session";

// THE STOP ANSWER OUTLIVES THE ROW IT DESCRIBES (mission "Stop a session", inspection finding I4).
//
// The whole point of this control is that it says what happened. It used to hold the question, the
// request and the Gateway's answer inside SessionMenu - and BOTH places that mount SessionMenu exist
// only while the session exists: one per roster card (SessionRoster) and one behind `selected && ...`
// on the session page (SessionDetail). A successful stop removes that row, the shared roster poll
// refreshes every two seconds (rosterStore.ROSTER_POLL_MS), and the next refresh unmounted the menu
// and destroyed the answer - with no Done, no backdrop click, and nothing read. A refresh that landed
// before the response arrived meant the answer never became visible at all.
//
// So the whole stop lives HERE instead, in one owner mounted in AppShell, above the roster and above
// the session page. SessionMenu asks this provider to start a stop and then owns nothing about it:
// the row it sits in can be removed at any moment, mid-request or mid-answer, and neither the request
// nor the answer goes with it. Dismissing the answer is the only thing that clears it, and dismissal
// is also the moment the page that opened it is told it may navigate away.
//
// One stop at a time, deliberately. There is one dialog on screen, so a second stop started while one
// is unanswered would have to either queue or overwrite - and overwriting is the defect above wearing
// a different hat. The menu item that starts a stop is only reachable from a dropdown, so the case is
// a keyboard race rather than an ordinary flow; see the busy guard on the action below.
//
// This view composes no sentence about a stop and never branches on the verdict word. There are four
// verdict words today and there may be five tomorrow; adding one is an edit on the Gateway, and this
// file must not need to know it happened.

/** What the menu hands up when the user chooses Stop session. */
export interface StopSessionApi {
  /**
   * Open the stop dialog for this session. `onClosed` is called once the user has DISMISSED an
   * answer - never when the request returns - so a page can navigate away only after the answer has
   * been read. It is not called when the dialog is closed without a stop having happened.
   */
  openStop: (session: SessionDto, onClosed?: () => void) => void;
}

const StopSessionContext = createContext<StopSessionApi | null>(null);

/**
 * The stop control's one owner. Mounted in AppShell so it is an ancestor of every surface that can
 * start a stop.
 */
export function useStopSession(): StopSessionApi {
  const api = useContext(StopSessionContext);
  if (api === null) {
    throw new Error(
      "A stop control was mounted outside StopSessionProvider. The stop answer has to outlive the "
      + "roster row it describes, so the provider belongs above the roster and the session page.",
    );
  }
  return api;
}

interface StopTarget {
  sessionId: string;
  /** What to call the session in the question. The Gateway's own words carry the ANSWER. */
  label: string;
}

export function StopSessionProvider({ children }: { children: ReactNode }) {
  const [target, setTarget] = useState<StopTarget | null>(null);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<SessionStopOutcome | null>(null);
  const reasonId = useId();
  // What to tell the page that opened this, once an answer has been dismissed. A ref, not state: it is
  // read by the dismissal, never rendered, and it must not re-render the dialog when it is set.
  const onClosedRef = useRef<(() => void) | undefined>(undefined);
  // Whether a stop request is outstanding. A REF and not the busy flag: two Enter presses in the same
  // tick both read the same stale busy, so a state check lets the second one through.
  const inFlight = useRef(false);

  const openStop = useCallback((session: SessionDto, onClosed?: () => void) => {
    const sid = session.sessionId ?? "";
    // A stop already waiting for its answer is not thrown away to start another one. The answer on
    // screen is the record of something that has already happened to a session.
    if (inFlight.current) return;
    onClosedRef.current = onClosed;
    setTarget({ sessionId: sid, label: session.name || sid });
    setReason("");
    setError(null);
    setOutcome(null);
  }, []);

  // Dismissing the dialog. When an ANSWER is on screen, dismissing it is also the moment the page is
  // told it may navigate away - so the answer is never destroyed before it has been read, whichever
  // way the user dismisses it (the button or the backdrop).
  //
  // This is not a view deciding what a verdict means: it does not look at the verdict word at all.
  // Every verdict the Gateway can send is a session that is no longer running, so every one of them
  // leaves the page in the same place.
  const closeDialog = useCallback(() => {
    // A dismissal while the request is still outstanding would put the answer nowhere to land. The
    // dialog offers no dismissal in that state; this is the same rule stated where it is enforced.
    if (inFlight.current) return;
    const answered = outcome !== null;
    const onClosed = onClosedRef.current;
    onClosedRef.current = undefined;
    setTarget(null);
    setReason("");
    setError(null);
    setOutcome(null);
    if (answered) onClosed?.();
  }, [outcome]);

  const dismissDialog = useDismissOnBackdrop(closeDialog);

  // Send the stop. On success the dialog KEEPS the answer and stays open; the page is told nothing
  // until the user dismisses it. On failure the dialog also stays open, with the typed reason still in
  // the box, so a retry does not begin by making the user write their sentence again.
  //
  // THE BUSY GUARD IS ON THE ACTION, NOT ONLY ON THE BUTTON (inspection finding I7). The button
  // disables while a request is outstanding, but the reason box stays active and Enter is not a
  // button - a disabled attribute does not block a key press. Without this, two Enter presses sent two
  // stops, and the two independently completing handlers could overwrite the first answer with the
  // second's or clear busy while a request was still outstanding.
  const send = useCallback(async () => {
    if (target === null) return;
    const trimmed = reason.trim();
    if (target.sessionId.length === 0 || trimmed.length === 0) return;
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setError(null);
    try {
      setOutcome(await stopSession(target.sessionId, trimmed));
    } catch (err) {
      setError(describeAndReport(SURFACE, "stop the session", err));
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  }, [target, reason]);

  const api = useMemo<StopSessionApi>(() => ({ openStop }), [openStop]);

  return (
    <StopSessionContext.Provider value={api}>
      {children}
      {target !== null && (
        <div className="session-dialog-overlay" {...dismissDialog}>
          <div className="session-dialog" role="dialog" aria-modal="true">
            {/* The stop dialog is one dialog in two states: the QUESTION until the Gateway has
                answered, and then the ANSWER. It never shows both, and it never shows neither. */}
            {outcome === null && (
              <>
                <h3 className="session-dialog-title">Stop session</h3>
                <p className="session-dialog-text">
                  Stop <strong>{target.label}</strong>? This ends the session on its machine and
                  removes it from the roster. Files in its worktree are left exactly as they are.
                </p>
                <label className="session-dialog-label" htmlFor={reasonId}>
                  Why are you stopping it? A reason is required, and it is recorded with the stop so
                  anyone reading the trail later knows what happened.
                </label>
                <input
                  id={reasonId}
                  className="session-dialog-input"
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") void send();
                  }}
                  placeholder="Spawned into the wrong mode"
                  autoFocus
                />
                {error !== null && <div className="session-dialog-error">{error}</div>}
                <div className="session-dialog-actions">
                  <button type="button" className="session-dialog-btn" onClick={closeDialog} disabled={busy}>
                    Cancel
                  </button>
                  {/* Disabled on an empty or whitespace-only reason: the Gateway would refuse that
                      stop, and a control must not offer a click that can only come back refused. */}
                  <button
                    type="button"
                    className="session-dialog-btn danger"
                    onClick={() => void send()}
                    disabled={busy || reason.trim().length === 0}
                  >
                    {busy ? "Stopping..." : "Stop session"}
                  </button>
                </div>
              </>
            )}

            {/* The answer, rendered VERBATIM: the Gateway's headline, then each of its detail lines in
                the order it sent them. Nothing here is composed, and nothing here reads the verdict
                word. */}
            {outcome !== null && (
              <>
                <h3 className="session-dialog-title">Stop session</h3>
                <p className="session-dialog-headline">{outcome.headline}</p>
                {outcome.details.length > 0 && (
                  <ul className="session-dialog-details">
                    {outcome.details.map((line, i) => (
                      <li key={i}>{line}</li>
                    ))}
                  </ul>
                )}
                <div className="session-dialog-actions">
                  <button type="button" className="session-dialog-btn primary" onClick={closeDialog}>
                    Done
                  </button>
                </div>
              </>
            )}
          </div>
        </div>
      )}
    </StopSessionContext.Provider>
  );
}
