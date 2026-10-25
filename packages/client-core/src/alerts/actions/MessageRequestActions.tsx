import { useEffect, useRef, useState } from "react";
import { describeAndReport } from "../../errors/reportClientError";
import {
  answerMessageLinkRequest,
  requestedAmountWords,
  type MessageLinkRequest,
} from "../../fleet/messageLinksClient";
import "../alerts.css";

const SURFACE = "link-requests";
const ANSWER = "answer the request for a message link";

// THE MESSAGE REQUEST'S ANSWER (issue #4373, moved here from the message request card by the Alerts mission). A
// session that may not message another asks the owner for a link; this says how much talking it asked for and offers
// Approve and Deny while the request waits. It answers through the same Gateway call as the Director's flyout and the
// Cockpit's popover, so the asking session hears the answer in its inbox exactly as it would from either. A refusal
// ("This request was already allowed. Nothing changed.") is shown in the Gateway's words.
//
// The alert detail and the old message request card both render this one block.

export interface MessageRequestActionsProps {
  request: MessageLinkRequest;
  /** Called the moment an answer is sent, before it lands. */
  onAnswering?: (request: MessageLinkRequest) => void;
  /** Called after the answer landed, with the request as the Gateway now holds it and the Gateway's sentence saying
   *  what the answer did. */
  onAnswered?: (request: MessageLinkRequest, note: string) => void;
}

/** "Asks for: one message and a reply", then Approve and Deny while the request waits. */
export function MessageRequestActions({ request: r, onAnswering, onAnswered }: MessageRequestActionsProps) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);
  useEffect(() => () => { mounted.current = false; }, []);
  const waiting = r.status === "pending";

  const answer = async (choice: { approve: true } | { decline: true }) => {
    setBusy(true);
    setError(null);
    onAnswering?.(r);
    try {
      const result = await answerMessageLinkRequest(r.requestId, choice);
      onAnswered?.(result.request, result.note);
    } catch (err) {
      // The Gateway's own sentence, verbatim.
      if (mounted.current) setError(describeAndReport(SURFACE, ANSWER, err));
    } finally {
      if (mounted.current) setBusy(false);
    }
  };

  return (
    <>
      <p className="alert-line alert-dim">Asks for: {requestedAmountWords(r)}</p>
      {error ? <p className="alert-line alert-error" role="alert">{error}</p> : null}
      {waiting ? (
        <div className="alert-actions">
          <button type="button" className="alert-btn alert-btn-primary" disabled={busy} onClick={() => void answer({ approve: true })}>
            Approve
          </button>
          <button type="button" className="alert-btn" disabled={busy} onClick={() => void answer({ decline: true })}>
            Deny
          </button>
        </div>
      ) : null}
    </>
  );
}
