import { useEffect, useState } from "react";
import { GatewayError, gatewayErrorMessage, type SessionDto } from "../api/client";
import { answerTurnVerdict, type TurnVerdict, type TurnVerdictRow } from "./verdictAnswer";
import "./verdictPanel.css";

// ---- The verdict panel: what the Wingman read at this stop, and the owner's answer to it ----------------
//
// The Wingman-on-every-turn mission, slice E. ONE copy, mounted by the Cockpit session view and the phone
// session screen; the desktop does not mount it (the Director wire carries colour and label only).
//
// In order: the risk, when there is one; the receipt - the agent's own words, open; the label; the summary;
// the options as buttons wired to the answer route; and "this is wrong", which slice G wires.
//
// THE CLIENT IS DUMB. Everything shown is the Gateway's stamp, verbatim. The panel never decides whether an
// option is still safe to press: the answer route re-reads the screen, compares it with the one the verdict
// was formed on, and either writes or refuses with a sentence - and that sentence is shown as it came, never
// swallowed and never reworded. What the panel does decide is layout only: a multiple-select collects its
// picks and sends them as ONE request, and a menu with no options (the parked reply) gets one confirm button.

export interface VerdictPanelProps {
  session: SessionDto;
  /** The "this is wrong" action. Slice G wires it; until then the action is shown and cannot be pressed. */
  onReportWrong?: (verdict: TurnVerdict) => void;
}

export function VerdictPanel({ session, onReportWrong }: VerdictPanelProps) {
  const row = session as TurnVerdictRow;
  const verdict = row.verdictState === "judged" ? row.turnVerdict ?? null : null;
  const verdictId = verdict?.verdictId ?? "";

  const [picked, setPicked] = useState<number[]>([]);
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [sent, setSent] = useState<string | null>(null);

  // A new stop is a new question: nothing picked, answered or refused carries over from the last one.
  useEffect(() => {
    setPicked([]);
    setRefusal(null);
    setSent(null);
  }, [row.sessionId, verdictId]);

  if (verdict === null) return null;

  const menu = verdict.menu ?? null;
  const options = verdict.options ?? [];
  const multiple = menu?.selectionMode === "multiple";
  const parkedReply = verdict.answerVia === "keys" && menu !== null && options.length === 0;

  const send = async (indexes: readonly number[]) => {
    if (busy || !row.sessionId) return;
    setBusy(true);
    setRefusal(null);
    setSent(null);
    try {
      const result = await answerTurnVerdict(row.sessionId, verdict.verdictId, indexes);
      setSent(result.reason);
      setPicked([]);
    } catch (err) {
      setRefusal(err instanceof GatewayError && err.serverReason ? err.serverReason : gatewayErrorMessage(err, "answer that stop"));
    } finally {
      setBusy(false);
    }
  };

  const toggle = (index: number) =>
    setPicked((cur) => (cur.includes(index) ? cur.filter((i) => i !== index) : [...cur, index]));

  return (
    <section className="verdict-panel" aria-label="Wingman verdict">
      {verdict.risk && verdict.risk !== "none" && (
        <div className="verdict-risk" role="note">
          Risk: {verdict.risk}
        </div>
      )}

      {verdict.evidence && (
        <details className="verdict-receipt" open>
          <summary>Claude said</summary>
          <blockquote className="verdict-evidence">{verdict.evidence}</blockquote>
        </details>
      )}

      <div className="verdict-label">{verdict.label}</div>
      {verdict.summary && <p className="verdict-summary">{verdict.summary}</p>}

      {menu?.question && <div className="verdict-question">{menu.question}</div>}

      {options.length > 0 && (
        <ul className="verdict-options">
          {options.map((option, index) => (
            <li className="verdict-option" key={index}>
              <button
                type="button"
                className={`verdict-option-button${option.recommended ? " recommended" : ""}`}
                disabled={busy}
                aria-pressed={multiple ? picked.includes(index) : undefined}
                onClick={() => (multiple ? toggle(index) : void send([index]))}
              >
                {option.key}
              </button>
              {option.recommended && <span className="verdict-recommended">Recommended</span>}
              {option.note && <span className="verdict-note">{option.note}</span>}
            </li>
          ))}
        </ul>
      )}

      {multiple && options.length > 0 && (
        <button
          type="button"
          className="verdict-send"
          disabled={busy || picked.length === 0}
          onClick={() => void send(picked)}
        >
          Send the chosen options
        </button>
      )}

      {parkedReply && (
        <button type="button" className="verdict-send" disabled={busy} onClick={() => void send([])}>
          Confirm
        </button>
      )}

      {busy && <div className="verdict-busy" role="status">Sending...</div>}
      {refusal !== null && (
        <div className="verdict-refusal" role="alert">
          {refusal}
        </div>
      )}
      {sent !== null && (
        <div className="verdict-sent" role="status">
          {sent}
        </div>
      )}

      <div className="verdict-actions">
        <button
          type="button"
          className="verdict-wrong"
          disabled={onReportWrong === undefined}
          onClick={() => onReportWrong?.(verdict)}
        >
          This is wrong
        </button>
      </div>
    </section>
  );
}
