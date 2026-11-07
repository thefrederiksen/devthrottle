import { useEffect, useRef, useState } from "react";
import { describeAndReport } from "../../errors/reportClientError";
import { dealtWithFactoryInbox } from "../../factoryinbox/factoryInboxClient";
import type { FactoryInboxStuck } from "../alertsClient";
import "../alerts.css";

const SURFACE = "factory-inbox-stuck";
const ANSWER = "mark the factory inbox as dealt with";

// THE OWNER ENDS A STUCK INBOX ALERT (issue 4746). When no Secretary can start, nothing receives the owner's message
// and the stuck alert would stand for ever; the owner reads the message, deals with it himself, and presses "Dealt with
// it". The Gateway marks exactly what the alert reported as dealt with by the owner (its thread says who and when), and
// the alert is gone on the next read. A message that becomes stuck afterwards raises the alert again - that is the
// Gateway's ruling, not this block's. A refusal ("This factory's inbox is not stuck now...") is shown in the Gateway's
// words.
//
// One block for the Cockpit and the phone: the alert detail renders it on both.

export interface FactoryInboxStuckActionsProps {
  stuck: FactoryInboxStuck;
  /** Called after the Gateway marked the inbox dealt with, so the list is read again at once. */
  onAnswered?: () => void;
}

export function FactoryInboxStuckActions({ stuck, onAnswered }: FactoryInboxStuckActionsProps) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);
  useEffect(() => () => { mounted.current = false; }, []);

  const dealtWith = async () => {
    setBusy(true);
    setError(null);
    try {
      await dealtWithFactoryInbox(stuck.factory);
      onAnswered?.();
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
      <div className="alert-actions">
        <button type="button" className="alert-btn alert-btn-primary" disabled={busy} onClick={() => void dealtWith()}>
          Dealt with it
        </button>
      </div>
    </>
  );
}
