import { useEffect, useRef, useState } from "react";
import { describeAndReport } from "../../errors/reportClientError";
import { answerSecretTransfer, type SecretTransfer, type SecretTransferPlace } from "../../secrets/secretTransfersClient";
import "../alerts.css";

const SURFACE = "secret-transfers";
const ANSWER = "answer the secret transfer";

// THE SECRET TRANSFER'S ANSWER (Secret Handoff, issue #2943, moved here from the transfer card by the Alerts mission).
// Approve and Deny, offered only while the Gateway says the transfer can still be answered (`canAnswer`), and sent
// with WHERE it was answered - "phone" on the phone, "cockpit" on the Cockpit, "badge" from a session's own badge - so
// the record and the asking agent say where the owner said yes. A refusal ("This transfer was already approved in the
// cc-secrets window.") is shown in the Gateway's words. A transfer never carries a value, so neither does this.
//
// It calls the transfer's existing answer route. The alert detail and the old transfer card both render this block.

export interface SecretTransferActionsProps {
  transfer: SecretTransfer;
  /** Where an answer given here is recorded. */
  where: SecretTransferPlace;
  /** Called the moment an answer is sent, before it lands. */
  onAnswering?: (transfer: SecretTransfer) => void;
  /** Called after the answer landed, with the transfer as the Gateway now holds it. */
  onAnswered?: (transfer: SecretTransfer) => void;
}

/** Approve and Deny while the transfer waits. Draws nothing else, except a refusal from the last try. */
export function SecretTransferActions({ transfer: t, where, onAnswering, onAnswered }: SecretTransferActionsProps) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);
  useEffect(() => () => { mounted.current = false; }, []);

  const answer = async (approve: boolean) => {
    setBusy(true);
    setError(null);
    onAnswering?.(t);
    try {
      const result = await answerSecretTransfer(t.transferId, approve, where);
      onAnswered?.(result.transfer);
    } catch (err) {
      // The Gateway's own sentence, verbatim.
      if (mounted.current) setError(describeAndReport(SURFACE, ANSWER, err));
    } finally {
      if (mounted.current) setBusy(false);
    }
  };

  return (
    <>
      {error ? <p className="alert-line alert-error" role="alert">{error}</p> : null}
      {t.canAnswer ? (
        <div className="alert-actions">
          <button type="button" className="alert-btn alert-btn-primary" disabled={busy} onClick={() => void answer(true)}>
            Approve
          </button>
          <button type="button" className="alert-btn" disabled={busy} onClick={() => void answer(false)}>
            Deny
          </button>
        </div>
      ) : null}
    </>
  );
}
