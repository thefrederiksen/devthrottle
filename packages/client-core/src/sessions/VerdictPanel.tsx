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
// picks and sends them as ONE request, and a menu with no options (the parked reply) gets one button that sends the
// typed reply.
//
// The receipt is headed with the session's own agent, "<agent> said", and the name is the Gateway's
// agentToolDisplay stamp, the one fold that turns the row's agent kind into a name (AgentToolDisplayFold). The
// panel keeps no table of agent names, so a Codex session can never be headed with another agent's name. A row
// with no stamp reads the same loud words the phone's roster card shows for it.

export interface VerdictPanelProps {
  /** The session the SCREEN is showing - the route's id. The answer is posted to it, and a row for any other session
   *  renders nothing, so a remembered row can never answer from another session's screen. */
  sessionId: string;
  session: SessionDto;
  /** The "this is wrong" action. Slice G wires it; until then the action is shown and cannot be pressed. */
  onReportWrong?: (verdict: TurnVerdict) => void;
  /**
   * Render for a screen with no height to spare: the receipt starts COLLAPSED instead of expanded.
   *
   * It exists for the phone's Chat tab, where the receipt is the agent's last reply and the agent's last
   * reply is also the top of the conversation immediately below - so expanded, the panel spent the
   * scarcest space on that screen restating what the reader could already see. Nothing is removed and the
   * summary line still says whose words they are; it is one tap to open.
   *
   * The default is false, so the Cockpit and the phone's other screens are unchanged.
   */
  compact?: boolean;
}

export function VerdictPanel({ sessionId, session, onReportWrong, compact = false }: VerdictPanelProps) {
  const row = session as TurnVerdictRow;
  const verdict = row.verdictState === "judged" ? row.turnVerdict ?? null : null;
  const verdictId = verdict?.verdictId ?? "";
  const agentName = (session.agentToolDisplay ?? "").trim() || "Agent tool not reported";

  const [picked, setPicked] = useState<number[]>([]);
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [sent, setSent] = useState<string | null>(null);

  // A new stop is a new question: nothing picked, answered or refused carries over from the last one.
  useEffect(() => {
    setPicked([]);
    setRefusal(null);
    setSent(null);
  }, [sessionId, verdictId]);

  if (verdict === null || row.sessionId !== sessionId) return null;

  const menu = verdict.menu ?? null;
  const options = verdict.options ?? [];
  const multiple = menu?.selectionMode === "multiple";
  const parkedReply = verdict.answerVia === "keys" && menu !== null && options.length === 0;

  const send = async (indexes: readonly number[]) => {
    if (busy || !sessionId) return;
    setBusy(true);
    setRefusal(null);
    setSent(null);
    try {
      const result = await answerTurnVerdict(sessionId, verdict.verdictId, indexes);
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
        <details className="verdict-receipt" open={!compact}>
          <summary>{agentName} said</summary>
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
          Send the typed reply
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
