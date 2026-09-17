import { useCallback, useState } from "react";
import type { FleetCardAction, FleetOutcomeCard } from "@devthrottle/client-core/fleetmanager/pageClient";
import { answerCard, type CardAnswerDeps } from "@devthrottle/client-core/fleetmanager/answerCard";
import { reportClientError } from "@devthrottle/client-core/errors/reportClientError";
import { Button } from "../components";

// One of the three cards - Ready for you, Finding, Decision - drawn from an outcome record (the Fleet Manager
// mission, step 6). Every field, label, tone and button (with the exact words it sends) comes from the Gateway;
// this renders them as sent. A button is ONE Gateway call that records the owner's answer; the Gateway passes it to the
// Fleet Manager (see answerCard). The card then shows the Gateway's sentence for how far the answer has got.

const SURFACE = "cockpit-fleet-manager-card";

export interface OutcomeCardProps {
  card: FleetOutcomeCard;
  /** Called once the record is answered, so the page re-reads the Gateway at once. */
  onAnswered: () => void;
  /** For tests: the one call a button makes. */
  deps?: CardAnswerDeps;
}

function variantOf(style: FleetCardAction["style"]) {
  return style === "primary" ? "primary" : style === "ghost" ? "ghost" : "secondary";
}

export function OutcomeCard({ card, onAnswered, deps }: OutcomeCardProps) {
  // The action whose answer is being recorded, so its button shows that action's busy label.
  const [busy, setBusy] = useState<{ words: string; label: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [asking, setAsking] = useState<FleetCardAction | null>(null);
  const [typed, setTyped] = useState("");

  const send = useCallback(
    async (words: string, action: FleetCardAction) => {
      setBusy({ words, label: action.busyLabel });
      setError(null);
      const result = await answerCard(card.id, words, deps);
      setBusy(null);
      if (result.kind === "refused") {
        reportClientError(SURFACE, "fleet-manager", `answer ${card.id}: ${result.error}`);
        setError(result.error);
        return;
      }
      setAsking(null);
      setTyped("");
      onAnswered();
    },
    [card.id, deps, onAnswered],
  );

  const onAction = (action: FleetCardAction) => {
    if (action.asksForWords) {
      setError(null);
      setAsking(action);
      return;
    }
    if (action.words) void send(action.words, action);
  };

  return (
    <div className={`fmp-card fmp-card-${card.tone}`} data-testid={`fmp-card-${card.id}`}>
      <div className="fmp-card-kind">{card.kindLabel}</div>
      <div className="fmp-card-title">{card.title}</div>

      {card.ready && (
        <>
          <div className="fmp-card-facts">
            <span className={`fmp-risk fmp-risk-${card.ready.riskTone}`}>{card.ready.riskLabel}</span>
            {card.ready.facts.map((f) => (
              <span key={f.label}>
                {f.label} <b>{f.value}</b>
              </span>
            ))}
          </div>
          <div className="fmp-card-text">{card.ready.change}</div>
        </>
      )}

      {card.finding && (
        <>
          <div className="fmp-card-text fmp-card-answer">{card.finding.answer}</div>
          {card.finding.reason && <div className="fmp-card-text">{card.finding.reason}</div>}
        </>
      )}

      {card.decision && (
        <>
          {card.decision.question && <div className="fmp-card-text">{card.decision.question}</div>}
          {card.decision.options.map((o) => (
            <div key={o.text} className={o.recommended ? "fmp-opt fmp-opt-rec" : "fmp-opt"}>
              {o.text}
              {o.recommended && <span className="fmp-opt-rec-label">{card.decision?.recommendedLabel}</span>}
            </div>
          ))}
          {card.decision.why && <div className="fmp-card-why">{card.decision.why}</div>}
        </>
      )}

      {card.answered && (
        <div className="fmp-card-answered">
          <span className="fmp-card-answered-label">{card.answerLabel}</span>
          <span className="fmp-card-answered-text">{card.answer}</span>
          {card.answerDelivery && <span className="fmp-card-delivery">{card.answerDelivery}</span>}
        </div>
      )}

      {(card.actions.length > 0 || card.ready || (card.finding && card.finding.links.length > 0)) && (
        <div className="fmp-card-row">
          {card.actions
            .filter((a) => a.style === "primary")
            .map((a) => (
              <Button key={a.label} variant={variantOf(a.style)} disabled={busy !== null} onClick={() => onAction(a)}>
                {busy !== null && busy.words === a.words ? busy.label : a.label}
              </Button>
            ))}
          {card.ready && (
            <a className="ui-btn ui-btn-secondary fmp-card-link" href={card.ready.pullRequest.url} target="_blank" rel="noopener noreferrer">
              {card.ready.pullRequest.label}
            </a>
          )}
          {card.finding?.links.map((l) => (
            <a key={l.url} className="ui-btn ui-btn-secondary fmp-card-link" href={l.url} target="_blank" rel="noopener noreferrer">
              {l.label}
            </a>
          ))}
          {card.actions
            .filter((a) => a.style !== "primary")
            .map((a) => (
              <Button key={a.label} variant={variantOf(a.style)} disabled={busy !== null} onClick={() => onAction(a)}>
                {busy !== null && busy.words === a.words ? busy.label : a.label}
              </Button>
            ))}
        </div>
      )}

      {asking && (
        <div className="fmp-card-ask">
          <textarea
            className="fmp-card-ask-input"
            rows={2}
            autoFocus
            placeholder={asking.placeholder ?? ""}
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
          />
          <div className="fmp-card-row">
            <Button
              variant="primary"
              disabled={busy !== null || typed.trim().length === 0}
              onClick={() => void send(`${asking.wordsPrefix ?? ""}${typed}`, asking)}
            >
              {busy !== null ? busy.label : asking.sendLabel ?? asking.label}
            </Button>
            {asking.cancelLabel && (
              <Button variant="ghost" disabled={busy !== null} onClick={() => setAsking(null)}>
                {asking.cancelLabel}
              </Button>
            )}
          </div>
        </div>
      )}

      {error !== null && (
        <div className="fmp-card-error" role="alert">
          {card.answerRefusedLead} {error}
        </div>
      )}
    </div>
  );
}
