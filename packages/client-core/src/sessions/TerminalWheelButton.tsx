import { useEffect, useRef, useState } from "react";
import { GatewayError, gatewayErrorMessage, handBackWheel, takeWheel, type SessionDto } from "../api/client";
import { reportShownError } from "../errors/reportClientError";
import { readWheelAction, type WheelAction, type WheelVerb } from "./wheelAction";
import "./terminalWheelButton.css";

// THE TERMINAL TAB'S "TAKE THE WHEEL" / "HAND BACK" BUTTON - built once, mounted by the Cockpit's Terminal pane and
// the phone's Terminal page (mission #4070, phase 4; CLAUDE.md rule 8's spirit: one control, two shells).
//
// IT RENDERS THE GATEWAY'S VERDICT AND NOTHING ELSE (CLAUDE.md rule 7). Which button, its words, whether it can be
// pressed and the reason it cannot all come from readWheelAction, verbatim. A terminal-mode session shows nothing
// because that verdict says there is no button - this file never reads the connection mode or the wheel holder.
//
// PRESSING IT. The button goes busy in the same frame as the press (CLAUDE.md rule 1), calls the Gateway's verb, and:
//   - on success, clears the busy state and tells the shell (onChanged) so it can re-read the roster at once. The
//     Director reports the new holder to the Gateway BEFORE it answers (pull request #4094's contract), so the next
//     read already carries the new verdict and the button turns into its other half.
//   - on a refusal, shows the Gateway's own sentence verbatim beside the button, until the next press or until the
//     verdict changes. A failure that carries no sentence from the Gateway (the network) gets the shared wording.

export interface TerminalWheelButtonProps {
  /** The session's row as the shell's last roster read returned it; null or undefined while there is none. */
  session: SessionDto | null | undefined;
  /** Called after the Gateway accepted the switch, so the shell can re-read the session at once. */
  onChanged?: () => void;
}

const CALLS: Readonly<Record<WheelVerb, (sessionId: string) => Promise<void>>> = {
  take: (sid) => takeWheel(sid),
  handBack: (sid) => handBackWheel(sid),
};

const SURFACE = "terminal-wheel-button";

const WHAT: Readonly<Record<WheelVerb, string>> = {
  take: "take the wheel",
  handBack: "hand the wheel back",
};

/** The words to show for a failed press: the Gateway's own sentence when it sent one, verbatim. */
export function wheelFailureMessage(err: unknown, verb: WheelVerb): string {
  if (err instanceof GatewayError && err.serverReason) return err.serverReason;
  return gatewayErrorMessage(err, WHAT[verb]);
}

/** Report a verdict this build could not read. Background: the roster re-renders the button every poll, so the
 *  same sentence is reported once, not on every render. */
function reportUnreadableVerdict(message: string, sessionId: string | null): void {
  reportShownError(SURFACE, "read the wheel button", message, { background: true, sessionId: sessionId ?? undefined });
}

export function TerminalWheelButton({ session, onChanged }: TerminalWheelButtonProps) {
  const sessionId = session?.sessionId ?? null;
  const [pending, setPending] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  // The session the press was made on. A reply that lands after the screen moved to another session must not
  // write onto this one.
  const pressedFor = useRef<string | null>(null);

  let action: WheelAction | null = null;
  let unreadable: string | null = null;
  try {
    action = readWheelAction(session);
  } catch (err) {
    unreadable = err instanceof Error ? err.message : String(err);
  }
  // Reported once per sentence, as a background problem: the roster re-renders this every poll.
  useEffect(() => {
    if (unreadable !== null) reportUnreadableVerdict(unreadable, sessionId);
  }, [unreadable, sessionId]);

  const verdictKey = action ? `${action.verb}|${action.label}|${action.enabled}` : "none";
  // A new verdict, or a new session, is a new button: an old refusal no longer describes it.
  useEffect(() => {
    setRefusal(null);
  }, [verdictKey, sessionId]);
  useEffect(() => {
    setPending(false);
    pressedFor.current = null;
  }, [sessionId]);

  if (unreadable !== null) {
    return (
      <span className="twb-root">
        {/* error-reported-by: reportUnreadableVerdict */}
        <span className="twb-problem" role="alert">{unreadable}</span>
      </span>
    );
  }
  if (action === null || sessionId === null) return null;

  const verb = action.verb;
  const press = async () => {
    if (pending || !action?.enabled) return;
    pressedFor.current = sessionId;
    setRefusal(null);
    setPending(true);
    try {
      await CALLS[verb](sessionId);
      if (pressedFor.current !== sessionId) return;
      setPending(false);
      onChanged?.();
    } catch (err) {
      if (pressedFor.current !== sessionId) return;
      const message = reportShownError(SURFACE, WHAT[verb], wheelFailureMessage(err, verb), { sessionId }, err);
      setPending(false);
      setRefusal(message);
    }
  };

  const reasonId = `twb-reason-${sessionId}`;
  const shownReason = !action.enabled ? action.disabledReason : null;
  return (
    <span className="twb-root">
      <button
        type="button"
        className={`twb-button twb-${verb === "take" ? "take" : "handback"}`}
        disabled={!action.enabled || pending}
        aria-busy={pending}
        aria-describedby={shownReason ? reasonId : undefined}
        title={shownReason ?? undefined}
        onClick={() => void press()}
      >
        {pending && <span className="twb-spinner" aria-hidden="true" />}
        {action.label}
      </button>
      {shownReason && <span id={reasonId} className="twb-reason">{shownReason}</span>}
      {refusal && <span className="twb-problem" role="alert">{refusal}</span>}
    </span>
  );
}
