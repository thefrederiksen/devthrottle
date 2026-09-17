import { useCallback, useState } from "react";
import type { FleetCardAction, FleetOutcomeCard } from "@devthrottle/client-core/fleetmanager/pageClient";
import { answerCard, retellFleetManager, type CardAnswerDeps } from "@devthrottle/client-core/fleetmanager/answerCard";
import { reportClientError } from "@devthrottle/client-core/errors/reportClientError";
import { Button } from "../components";

// One of the three cards - Ready for you, Finding, Decision - drawn from an outcome record (the Fleet Manager
// mission, step 6). Every field, label, tone and button (with the exact words it sends) comes from the Gateway;
// this renders them as sent. A button sends the owner's answer as if the owner had said it: see answerCard for the
// order of the two calls and what each failure shows.

const SURFACE = "cockpit-fleet-manager-card";

export interface OutcomeCardProps {
  card: FleetOutcomeCard;
  fleetManagerSessionId: string | null | undefined;
  /** Called once the record is answered, so the page re-reads the Gateway at once. */
  onAnswered: () => void;
  /** For tests: the two calls a button makes. */
  deps?: CardAnswerDeps;
}

function variantOf(style: FleetCardAction["style"]) {
  return style === "primary" ? "primary" : style === "ghost" ? "ghost" : "secondary";
}

export function OutcomeCard({ card, fleetManagerSessionId, onAnswered, deps }: OutcomeCardProps) {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [asking, setAsking] = useState<FleetCardAction | null>(null);
  const [typed, setTyped] = useState("");
  // The record was answered but the Fleet Manager was not told: the words, so they can be sent again.
  const [untold, setUntold] = useState<{ words: string; error: string } | null>(null);

  const send = useCallback(
    async (words: string) => {
      setBusy(words);
      setError(null);
      const result = await answerCard(card.id, fleetManagerSessionId, words, deps);
      setBusy(null);
      if (result.kind === "answer-failed") {
        reportClientError(SURFACE, "fleet-manager", `answer ${card.id}: ${result.error}`);
        setError(result.error);
        return;
      }
      setAsking(null);
      setTyped("");
      if (result.kind === "prompt-failed") {
        reportClientError(SURFACE, "fleet-manager", `tell the Fleet Manager about ${card.id}: ${result.error}`);
        setUntold({ words, error: result.error });
      }
      onAnswered();
    },
    [card.id, fleetManagerSessionId, deps, onAnswered],
  );

  const retell = useCallback(async () => {
    if (untold === null) return;
    setBusy(untold.words);
    const result = await retellFleetManager(fleetManagerSessionId, untold.words, deps);
    setBusy(null);
    if (result.kind === "sent") setUntold(null);
    else setUntold({ words: untold.words, error: result.error });
  }, [untold, fleetManagerSessionId, deps]);

  const onAction = (action: FleetCardAction) => {
    if (action.asksForWords) {
      setError(null);
      setAsking(action);
      return;
    }
    if (action.words) void send(action.words);
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
        </div>
      )}

      {(card.actions.length > 0 || card.ready || (card.finding && card.finding.links.length > 0)) && (
        <div className="fmp-card-row">
          {card.actions
            .filter((a) => a.style === "primary")
            .map((a) => (
              <Button key={a.label} variant={variantOf(a.style)} disabled={busy !== null} onClick={() => onAction(a)}>
                {busy !== null && busy === a.words ? "Sending..." : a.label}
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
                {busy !== null && busy === a.words ? "Sending..." : a.label}
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
              onClick={() => void send(`${asking.wordsPrefix ?? ""}${typed}`)}
            >
              {busy !== null ? "Sending..." : asking.sendLabel ?? asking.label}
            </Button>
            <Button variant="ghost" disabled={busy !== null} onClick={() => setAsking(null)}>
              Cancel
            </Button>
          </div>
        </div>
      )}

      {error !== null && (
        <div className="fmp-card-error" role="alert">
          Your answer was not recorded, and nothing was sent to the Fleet Manager: {error}
        </div>
      )}
      {untold !== null && (
        <div className="fmp-card-error" role="alert">
          Your answer was recorded, but it did not reach the Fleet Manager: {untold.error}{" "}
          <Button variant="ghost" disabled={busy !== null} onClick={() => void retell()}>
            {busy !== null ? "Sending..." : "Send it to the Fleet Manager again"}
          </Button>
        </div>
      )}
    </div>
  );
}
