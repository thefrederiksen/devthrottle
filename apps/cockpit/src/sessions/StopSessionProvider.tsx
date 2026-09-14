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
} from "@devthrottle/client-core/api/client";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { useDismissOnBackdrop } from "../components";

// The surface label on every client-error report from this dialog, so the Gateway log and
// GET /client-errors/recent name where the user was standing (issue #2189).
const SURFACE = "cockpit-stop-session";

// What is recorded when the user writes no note (issue internal#1992). The Gateway requires a reason on
// every stop under Ruling 4 - that requirement exists because an AGENT key may stop any OTHER session and
// the trail must say why. It was never meant for the owner's own hand: a human clicking Stop in his own
// Cockpit is the strongest authorisation there is. So the note is optional and this supplies the reason
// instead, naming the thing the Gateway cannot otherwise see - WHERE the stop came from. The Gateway's
// contract is untouched and the audit row is still complete. The phone does exactly this (#2816).
export const STOP_REASON_FROM_THE_COCKPIT = "Stopped by the owner from the Cockpit";

/** The note as written, trimmed - or the derived reason when nothing was written. */
export function stopReasonToRecord(note: string): string {
  const trimmed = note.trim();
  return trimmed.length === 0 ? STOP_REASON_FROM_THE_COCKPIT : trimmed;
}

// THE STOP IS ONE CONFIRMATION, NOT AN INTERROGATION (issue internal#1992).
//
// This dialog used to explain the audit trail in two sentences, demand a written reason before it would
// enable its own button, and then hold an answer card open reporting what had happened to a session the
// user had just watched leave the roster. It now asks one question - Stop <name>? - with Cancel, Stop,
// and an optional note; the button is live from the moment it opens.
//
// A SUCCESS IS SILENT. The session goes and the page returns to the roster, which is the answer. The
// Gateway still folds its sentences and the command line still prints them - nothing about the route
// changed - this dialog just stops making the owner read a report about something he asked for and
// watched happen. Nothing here reads the verdict word: there are four today and a fifth would be one
// edit on the Gateway and none here, and every one of them is a session that is no longer running.
//
// ONLY A FAILURE SPEAKS, and it speaks inside this dialog, above the button it explains, with the note
// still in the box so a retry costs one click.
//
// THE REQUEST STILL OUTLIVES THE ROW IT DESCRIBES (inspection finding I4, and it survives the change).
// The stop used to live inside SessionMenu, and BOTH places that mount SessionMenu exist only while the
// session exists: one per roster card (SessionRoster) and one behind `selected && ...` on the session
// page (SessionDetail). A successful stop removes that row and the shared roster poll refreshes every two
// seconds (rosterStore.ROSTER_POLL_MS), so the menu - and the outstanding request with it - was unmounted
// mid-flight. A failure that arrived after the refresh was never seen at all. So the whole stop lives
// HERE, in one owner mounted in AppShell, above the roster and above the session page: the row it was
// started from can be removed at any moment and neither the request nor the failure goes with it.
//
// One stop at a time, deliberately. There is one dialog on screen, so a second stop started while one is
// unanswered would have to either queue or overwrite; see the busy guard on the action below.

/** What the menu hands up when the user chooses Stop session. */
export interface StopSessionApi {
  /**
   * Open the stop dialog for this session. `onClosed` is called once the session HAS BEEN STOPPED - so a
   * page can navigate away only when there is nothing left to look at. It is not called when the dialog
   * is dismissed without a stop, and it is not called on a failure, because on a failure the session may
   * well still be running.
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
      "A stop control was mounted outside StopSessionProvider. The stop request has to outlive the "
      + "roster row it was started from, so the provider belongs above the roster and the session page.",
    );
  }
  return api;
}

interface StopTarget {
  sessionId: string;
  /** What to call the session in the question. */
  label: string;
}

export function StopSessionProvider({ children }: { children: ReactNode }) {
  const [target, setTarget] = useState<StopTarget | null>(null);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const noteId = useId();
  // What to tell the page that opened this, once the session has actually been stopped. A ref, not
  // state: it is read by the close, never rendered, and it must not re-render the dialog when it is set.
  const onClosedRef = useRef<(() => void) | undefined>(undefined);
  // Whether a stop request is outstanding. A REF and not the busy flag: two Enter presses in the same
  // tick both read the same stale busy, so a state check lets the second one through.
  const inFlight = useRef(false);

  const openStop = useCallback((session: SessionDto, onClosed?: () => void) => {
    const sid = session.sessionId ?? "";
    // A stop already waiting for its answer is not thrown away to start another one.
    if (inFlight.current) return;
    onClosedRef.current = onClosed;
    setTarget({ sessionId: sid, label: session.name || sid });
    setNote("");
    setError(null);
  }, []);

  // Take the dialog off the screen. `stopped` says whether the session actually went, and it is the only
  // thing that lets the page navigate away - a dismissal without a stop, or after a failure, leaves the
  // page exactly where it is, because the session is still there.
  const dismiss = useCallback((stopped: boolean) => {
    const onClosed = onClosedRef.current;
    onClosedRef.current = undefined;
    setTarget(null);
    setNote("");
    setError(null);
    if (stopped) onClosed?.();
  }, []);

  // Cancel, and the backdrop. Refused while a request is outstanding: closing mid-flight would leave a
  // failure with nowhere to land, and the user would read the vanished dialog as a stop that worked.
  const closeDialog = useCallback(() => {
    if (inFlight.current) return;
    dismiss(false);
  }, [dismiss]);

  const dismissDialog = useDismissOnBackdrop(closeDialog);

  // Send the stop. On SUCCESS the dialog closes and the page is told it may leave - the session is gone,
  // and that is the whole report. On failure the dialog stays open with the note still in the box, so a
  // retry does not begin by making the user write their sentence again.
  //
  // THE BUSY GUARD IS ON THE ACTION, NOT ONLY ON THE BUTTON (inspection finding I7). The button disables
  // while a request is outstanding, but the note box stays active and Enter is not a button - a disabled
  // attribute does not block a key press. Without this, two Enter presses sent two stops and two rows in
  // the audit trail for one intention.
  const send = useCallback(async () => {
    if (target === null) return;
    if (target.sessionId.length === 0) return;
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setError(null);
    try {
      await stopSession(target.sessionId, stopReasonToRecord(note));
      inFlight.current = false;
      dismiss(true);
    } catch (err) {
      setError(describeAndReport(SURFACE, "stop the session", err));
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  }, [target, note, dismiss]);

  const api = useMemo<StopSessionApi>(() => ({ openStop }), [openStop]);

  return (
    <StopSessionContext.Provider value={api}>
      {children}
      {target !== null && (
        <div className="session-dialog-overlay" {...dismissDialog}>
          <div className="session-dialog" role="dialog" aria-modal="true">
            <h3 className="session-dialog-title">Stop {target.label}?</h3>
            <p className="session-dialog-text">
              This ends the session on its machine and takes it off the roster. Files in its worktree are
              left exactly as they are.
            </p>
            <label className="session-dialog-label" htmlFor={noteId}>
              Note (optional)
            </label>
            <input
              id={noteId}
              className="session-dialog-input"
              value={note}
              onChange={(e) => setNote(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") void send();
              }}
              placeholder="Spawned into the wrong mode"
              autoFocus
            />
            {/* The failure, in the Gateway's own words, above the button it explains. */}
            {error !== null && <div className="session-dialog-error">{error}</div>}
            <div className="session-dialog-actions">
              <button type="button" className="session-dialog-btn" onClick={closeDialog} disabled={busy}>
                Cancel
              </button>
              <button
                type="button"
                className="session-dialog-btn danger"
                onClick={() => void send()}
                disabled={busy}
              >
                {busy ? "Stopping..." : "Stop session"}
              </button>
            </div>
          </div>
        </div>
      )}
    </StopSessionContext.Provider>
  );
}
