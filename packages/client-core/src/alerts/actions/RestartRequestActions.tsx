import { useEffect, useRef, useState } from "react";
import { describeAndReport } from "../../errors/reportClientError";
import { acceptRestartRequest, declineRestartRequest, type DirectorRestartRequest } from "../../restart/restartRequests";
import "../alerts.css";

const SURFACE = "restart-requests";

// THE RESTART REQUEST'S ANSWER (issue #2725, moved here from the restart card by the Alerts mission). Accept and
// Decline, offered only while the Gateway says an accept would be honoured right now (`canAccept`). The accept goes
// through an in-place confirmation step rather than a browser dialog, because accepting closes every session on that
// Director and a single tap must not be enough; the confirmation shows the Gateway's own sentence for what accepting
// does. A refusal is shown in the Gateway's words.
//
// It calls the restart's existing accept and decline routes, which keep every check they have. The alert detail and
// the old restart card both render this one block.

export interface RestartRequestActionsProps {
  request: DirectorRestartRequest;
  /** Called after an accept or a decline landed, or was refused, so the list is read again without waiting for the poll.
   *  A refused accept is an answer too: the Gateway re-checks the machine on accept and, when it can no longer be
   *  restarted, closes the request with its own sentence - the alert must leave "Needs you" at once, not after a poll. */
  onAnswered?: () => void;
}

/** Accept (two steps) and Decline. Draws nothing while `canAccept` is false, except a failure from an earlier try. */
export function RestartRequestActions({ request, onAnswered }: RestartRequestActionsProps) {
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState<"accept" | "decline" | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const mounted = useRef(true);
  useEffect(() => () => { mounted.current = false; }, []);

  const act = async (which: "accept" | "decline") => {
    setBusy(which);
    setFailure(null);
    try {
      if (which === "accept") await acceptRestartRequest(request.machine, request.id);
      else await declineRestartRequest(request.machine, request.id);
      if (mounted.current) setConfirming(false);
      onAnswered?.();
    } catch (err) {
      // The Gateway's own sentence, verbatim - a refusal on accept names what changed on the machine.
      if (mounted.current) {
        setFailure(describeAndReport(SURFACE, which === "accept" ? "accept the restart" : "decline the restart", err));
        setConfirming(false);
      }
      onAnswered?.();
    } finally {
      if (mounted.current) setBusy(null);
    }
  };

  return (
    <>
      {failure && <p className="alert-line alert-error" role="alert">{failure}</p>}
      {request.canAccept && !confirming && (
        <div className="alert-actions">
          <button type="button" className="alert-btn alert-btn-primary" onClick={() => setConfirming(true)} disabled={busy !== null}>
            Accept
          </button>
          <button type="button" className="alert-btn" onClick={() => void act("decline")} disabled={busy !== null}>
            {busy === "decline" ? "Declining..." : "Decline"}
          </button>
        </div>
      )}
      {request.canAccept && confirming && (
        <div className="alert-actions alert-confirm">
          <p className="alert-line">{request.acceptSentence || request.title}</p>
          <button type="button" className="alert-btn alert-btn-danger" onClick={() => void act("accept")} disabled={busy !== null}>
            {busy === "accept" ? "Accepting..." : "Yes, restart it"}
          </button>
          <button type="button" className="alert-btn" onClick={() => setConfirming(false)} disabled={busy !== null}>
            Not now
          </button>
        </div>
      )}
    </>
  );
}
