import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  closeWalkthroughSession,
  getWalkthrough,
  readSessionLines,
  type FleetManagerWalkthrough,
  type FleetWalkthroughItem,
} from "@devthrottle/client-core/fleetmanager/walkthroughClient";
import {
  answerWalkthroughItem,
  sentenceOf,
  snoozeWalkthroughItem,
  type WalkthroughActionDeps,
  type WalkthroughActResult,
} from "@devthrottle/client-core/fleetmanager/walkthroughActions";
import { reportClientError } from "@devthrottle/client-core/errors/reportClientError";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import { Button, ConfirmDialog } from "../components";
import { OutcomeCard } from "./OutcomeCard";
import { fleetManagerPageStore } from "./pageStore";

// "TAKE ME THROUGH THEM" (the Fleet Manager mission, step 7): one waiting item at a time, at /fleet-manager/walkthrough.
//
// THE CLIENT IS DUMB (CLAUDE.md rule 7). The round, its order, every heading and sentence, both picks on the answer
// buttons, and whether snooze and close are offered all come from GET /gateway/fleet-manager/walkthrough. This view
// only keeps its PLACE in the round: which item is in front of the owner, which one is next, and when the round has
// run out. Skip moves that place and changes nothing anywhere.
//
// THE ROUND IS THE GATEWAY'S. The first read starts it; every later read sends its ids back, so an item answered
// here stays in the left column as done, and a record that arrives meanwhile waits for the next round.
//
// WHAT EACH BUTTON DOES:
//   an answer button   the Wingman's one answer route, then the record (walkthroughActions.ts says why that order)
//   a card button      exactly as on the Fleet Manager page (OutcomeCard / answerCard)
//   snooze             the snooze route, then a note on the record
//   open               the session's page
//   close              the Cockpit's confirmation window, then the Gateway's close route, which decides again,
//                      stops through the one stop handler and records it; it is only shown when the Gateway offers it

const SURFACE = "cockpit-fleet-manager-walkthrough";
export const WALKTHROUGH_REFRESH_MS = 5000;
export const SCREEN_REFRESH_MS = 4000;

export interface WalkthroughViewProps {
  /** For tests: what the answer and snooze buttons call. */
  deps?: WalkthroughActionDeps;
}

/** The first item at or after `from` (in round order) that is not settled, or null. */
function nextOpen(items: FleetWalkthroughItem[], from: number): FleetWalkthroughItem | null {
  for (let i = Math.max(0, from); i < items.length; i++) if (!items[i].done) return items[i];
  return null;
}

export function WalkthroughView({ deps }: WalkthroughViewProps) {
  const [data, setData] = useState<FleetManagerWalkthrough | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  // The item in front of the owner, by record id; null with `ended` set once the round has run out.
  const [currentId, setCurrentId] = useState<string | null>(null);
  const [ended, setEnded] = useState(false);
  const [flash, setFlash] = useState<string | null>(null);
  const roundRef = useRef<string[] | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const next = await getWalkthrough(roundRef.current, signal);
      roundRef.current = next.roundIds;
      setData(next);
      setLoadError(null);
      return next;
    } catch (err) {
      if (signal?.aborted) return null;
      setLoadError(sentenceOf(err));
      reportClientError(SURFACE, "fleet-manager", `read the walkthrough: ${sentenceOf(err)}`);
      return null;
    }
  }, []);

  // The first read places the owner on the round's first open item.
  useEffect(() => {
    let live = true;
    void load().then((first) => {
      if (!live || first === null) return;
      const open = nextOpen(first.items, 0);
      setCurrentId(open?.id ?? null);
      setEnded(open === null);
    });
    return () => {
      live = false;
    };
  }, [load]);

  // Kept current while the owner reads: a reading can change, and an item can be answered elsewhere.
  useVisiblePolling(
    useCallback(
      async (signal: AbortSignal) => {
        if (roundRef.current !== null) await load(signal);
      },
      [load],
    ),
    WALKTHROUGH_REFRESH_MS,
  );

  const items = data?.items ?? [];
  const index = items.findIndex((i) => i.id === currentId);
  const current = index >= 0 ? items[index] : null;

  /** Move past the item at `from`: to the next open one after it, else to the end of the round. */
  const advanceFrom = useCallback((list: FleetWalkthroughItem[], fromId: string | null) => {
    const at = list.findIndex((i) => i.id === fromId);
    const next = nextOpen(list, at + 1);
    setCurrentId(next?.id ?? null);
    setEnded(next === null);
  }, []);

  /** After an item was acted on: read the round again, then move on from that item. */
  const settled = useCallback(
    async (fromId: string, sentence: string | null) => {
      setFlash(sentence);
      fleetManagerPageStore.refreshNow();
      const fresh = await load();
      advanceFrom(fresh?.items ?? items, fromId);
    },
    [load, advanceFrom, items],
  );

  const skip = () => {
    setFlash(null);
    advanceFrom(items, currentId);
  };

  const again = () => {
    setFlash(null);
    const open = nextOpen(items, 0);
    setCurrentId(open?.id ?? null);
    setEnded(open === null);
  };

  const newRound = async () => {
    setFlash(null);
    roundRef.current = null;
    const fresh = await load();
    const open = fresh === null ? null : nextOpen(fresh.items, 0);
    setCurrentId(open?.id ?? null);
    setEnded(open === null);
  };

  return (
    <div className="fmp-screen" data-testid="fmw-screen">
      <header className="fmp-head">
        <div className="fmp-head-text">
          <h1 className="fmp-title">{data?.title ?? ""}</h1>
          <div className="fmp-sub">{data?.intro ?? ""}</div>
        </div>
        <div className="fmp-head-actions">
          <Link className="ui-btn ui-btn-secondary" to="/fleet-manager" data-testid="fmw-back">
            {data?.backLabel ?? "Back to the conversation"}
          </Link>
        </div>
      </header>

      {loadError !== null && (
        <div className="fmp-bar fmp-bar-bad" role="alert">
          {loadError}
        </div>
      )}

      {data === null ? (
        loadError === null && (
          <div className="fmp-loading" role="status">
            Loading what is waiting on you...
          </div>
        )
      ) : (
        <div className="fmw-body">
          <aside className="fmw-steps" aria-label={data.roundTitle}>
            <h3 className="fmw-steps-title">{data.roundTitle}</h3>
            {data.items.map((item) => (
              <button
                type="button"
                key={item.id}
                className={`fmw-step${item.done ? " fmw-step-done" : ""}${item.id === currentId && !ended ? " fmw-step-current" : ""}`}
                data-testid={`fmw-step-${item.id}`}
                aria-current={item.id === currentId && !ended ? "step" : undefined}
                disabled={item.done}
                onClick={() => {
                  setFlash(null);
                  setCurrentId(item.id);
                  setEnded(false);
                }}
              >
                <span className="fmw-step-no">{item.done ? "✓" : item.position}</span>
                <span className="fmw-step-text">
                  <span className="fmw-step-name">{item.title}</span>
                  <span className="fmw-step-meta">{item.stepLine}</span>
                </span>
              </button>
            ))}
            {data.emptyText && <div className="fmp-sec-empty">{data.emptyText}</div>}
            {data.notInRound && (
              <div className="fmw-notinround" data-testid="fmw-not-in-round">
                {data.notInRound}
              </div>
            )}
          </aside>

          <section className="fmw-focus" aria-live="polite">
            {flash && (
              <div className="fmw-flash" role="status">
                {flash}
              </div>
            )}
            {current !== null && !ended ? (
              <WalkthroughItemPanel
                key={current.id}
                item={current}
                deps={deps}
                onSettled={(sentence) => void settled(current.id, sentence)}
                onSkip={skip}
              />
            ) : (
              <div className="fmw-end" data-testid="fmw-end">
                <h2 className="fmw-end-title">{data.endTitle}</h2>
                <p className="fmw-end-text">{data.endText}</p>
                <div className="fmw-actions">
                  {data.againLabel && (
                    <Button variant="primary" onClick={again}>
                      {data.againLabel}
                    </Button>
                  )}
                  {data.newRoundLabel && (
                    <Button variant={data.againLabel ? "secondary" : "primary"} onClick={() => void newRound()}>
                      {data.newRoundLabel}
                    </Button>
                  )}
                  <Link className="ui-btn ui-btn-secondary" to="/fleet-manager">
                    {data.backLabel}
                  </Link>
                </div>
              </div>
            )}
          </section>
        </div>
      )}
    </div>
  );
}

// ---- one item -----------------------------------------------------------------------------------------------------

interface ItemPanelProps {
  item: FleetWalkthroughItem;
  deps?: WalkthroughActionDeps;
  /** The item was acted on; the sentence to keep on screen, if any. */
  onSettled: (sentence: string | null) => void;
  onSkip: () => void;
}

function WalkthroughItemPanel({ item, deps, onSettled, onSkip }: ItemPanelProps) {
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [recordFailed, setRecordFailed] = useState<string | null>(null);
  const [picked, setPicked] = useState<number[]>([]);
  const [confirmClose, setConfirmClose] = useState(false);
  const answer = item.answer ?? null;
  const sessionId = item.sessionId ?? "";

  const finish = (result: WalkthroughActResult, what: string) => {
    if (result.kind === "refused") {
      reportClientError(SURFACE, "fleet-manager", `${what} ${item.id}: ${result.sentence}`);
      setRefusal(result.sentence);
      return;
    }
    if (result.kind === "record-failed") {
      reportClientError(SURFACE, "fleet-manager", `record ${what} ${item.id}: ${result.sentence}`);
      setRecordFailed(result.sentence);
      return;
    }
    onSettled(result.sentence.length > 0 ? result.sentence : null);
  };

  const sendAnswer = async (indexes: readonly number[]) => {
    if (busy || answer === null) return;
    setBusy(true);
    setRefusal(null);
    setRecordFailed(null);
    const result = await answerWalkthroughItem(sessionId, item.id, answer.verdictId, indexes, deps);
    setBusy(false);
    finish(result, "answer");
  };

  const snooze = async () => {
    if (busy) return;
    setBusy(true);
    setRefusal(null);
    setRecordFailed(null);
    const result = await snoozeWalkthroughItem(sessionId, item.id, item.snooze.minutes, deps);
    setBusy(false);
    finish(result, "snooze");
  };

  // Thrown errors stay in the confirmation window (ConfirmDialog shows them); a success closes it and moves on.
  const close = async () => {
    const result = await closeWalkthroughSession(item.id);
    onSettled(result.recordError ? `${result.headline} ${result.recordError}` : result.headline);
  };

  const toggle = (index: number) =>
    setPicked((cur) => (cur.includes(index) ? cur.filter((i) => i !== index) : [...cur, index]));

  const reading = item.reading;
  return (
    <div className="fmw-item" data-testid={`fmw-item-${item.id}`}>
      <div className="fmw-head">
        <span className="fmw-count">{item.positionLabel}</span>
        <div className="fmw-head-text">
          <div className="fmw-title">{item.title}</div>
          {item.sessionName && <div className="fmw-session">{item.sessionName}</div>}
          <div className="fmw-meta">{item.meta}</div>
        </div>
        {item.waitLabel && <span className="fmw-wait">{item.waitLabel}</span>}
      </div>

      <div className="fmw-two">
        <div className="fmw-panel fmw-needs" data-testid="fmw-reading">
          <h4>{reading.heading}</h4>
          {reading.note && <p className="fmw-note">{reading.note}</p>}
          {reading.available && (
            <>
              {reading.label && <div className="fmw-label">{reading.label}</div>}
              {reading.summary && <p className="fmw-summary">{reading.summary}</p>}
              {reading.evidence && (
                <p className="fmw-evidence">
                  {reading.evidenceLead} <q data-testid="fmw-evidence">{reading.evidence}</q>
                </p>
              )}
              {reading.riskLine && <p className="fmw-risk">{reading.riskLine}</p>}
            </>
          )}
        </div>
        <div className="fmw-panel fmw-fmsays" data-testid="fmw-advice">
          <h4>{item.advice.heading}</h4>
          {item.advice.text ? (
            <p className="fmw-advice">{item.advice.text}</p>
          ) : (
            <p className="fmw-note">{item.advice.emptyText}</p>
          )}
        </div>
      </div>

      {item.screen.offered && sessionId ? (
        <SessionScreen sessionId={sessionId} lines={item.screen.lines} loadingText={item.screen.loadingText} label={item.screen.heading} />
      ) : (
        item.screen.note && <div className="fmw-screen fmw-screen-off">{item.screen.note}</div>
      )}

      {item.answerMode === "session" && answer !== null && (
        <div className="fmw-answer" data-testid="fmw-answer">
          {answer.question && <div className="fmw-question">{answer.question}</div>}
          <div className="fmw-options">
            {answer.options.map((option) => (
              <div className="fmw-option" key={option.index}>
                <Button
                  variant={option.fleetManagerPick ? "primary" : "secondary"}
                  className={option.sessionPick ? "fmw-session-pick" : undefined}
                  disabled={busy}
                  aria-pressed={answer.multiple ? picked.includes(option.index) : undefined}
                  data-testid={`fmw-option-${option.index}`}
                  onClick={() => (answer.multiple ? toggle(option.index) : void sendAnswer([option.index]))}
                >
                  {option.label}
                  {option.markText && <span className="fmw-mark"> ({option.markText})</span>}
                </Button>
                {option.note && <span className="fmw-option-note">{option.note}</span>}
              </div>
            ))}
          </div>
          {answer.multiple && answer.options.length > 0 && (
            <Button variant="primary" disabled={busy || picked.length === 0} onClick={() => void sendAnswer(picked)}>
              {answer.sendChosenLabel}
            </Button>
          )}
          {answer.parkedReply && (
            <Button variant="primary" disabled={busy} onClick={() => void sendAnswer([])}>
              {answer.parkedReplyLabel}
            </Button>
          )}
          {answer.pickNote && <p className="fmw-note">{answer.pickNote}</p>}
          {busy && (
            <div className="fmw-busy" role="status">
              {answer.sendingText}
            </div>
          )}
          {recordFailed !== null && (
            <div className="fmw-refusal" role="alert">
              {answer.recordFailedLead} {recordFailed}
            </div>
          )}
        </div>
      )}

      {item.answerMode === "fleet-manager" && item.card && (
        <div className="fmw-card" data-testid="fmw-card">
          <OutcomeCard card={item.card} onAnswered={() => onSettled(null)} />
        </div>
      )}

      {refusal !== null && (
        <div className="fmw-refusal" role="alert" data-testid="fmw-refusal">
          {refusal}
        </div>
      )}
      {recordFailed !== null && item.answerMode !== "session" && (
        <div className="fmw-refusal" role="alert">
          {recordFailed}
        </div>
      )}

      <div className="fmw-actions">
        {item.snooze.offered && (
          <Button disabled={busy} onClick={() => void snooze()}>
            {item.snooze.label}
          </Button>
        )}
        <Button variant="ghost" disabled={busy} onClick={onSkip}>
          {item.skipLabel}
        </Button>
        {item.open.offered && sessionId && (
          <Link className="ui-btn ui-btn-ghost" to={`/session/${encodeURIComponent(sessionId)}`}>
            {item.open.label}
          </Link>
        )}
        {item.close.offered && (
          <Button variant="danger" disabled={busy} onClick={() => setConfirmClose(true)}>
            {item.close.label}
          </Button>
        )}
      </div>
      {(item.snooze.note || item.close.refusedText) && (
        <div className="fmw-why-not">
          {item.snooze.note && <div>{item.snooze.note}</div>}
          {item.close.refusedText && <div data-testid="fmw-close-refused">{item.close.refusedText}</div>}
        </div>
      )}

      <ConfirmDialog
        open={confirmClose}
        title={item.close.confirmTitle}
        message={item.close.confirmMessage}
        confirmLabel={item.close.confirmLabel}
        busyLabel={item.close.busyLabel}
        action="close the session"
        onConfirm={close}
        onClose={() => setConfirmClose(false)}
      />
    </div>
  );
}

// ---- the screen ---------------------------------------------------------------------------------------------------

function SessionScreen({ sessionId, lines, loadingText, label }: { sessionId: string; lines: number; loadingText: string; label: string }) {
  const [text, setText] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  useVisiblePolling(
    useCallback(
      async (signal: AbortSignal) => {
        try {
          setText(await readSessionLines(sessionId, lines, signal));
          setError(null);
        } catch (err) {
          if (!signal.aborted) setError(sentenceOf(err));
        }
      },
      [sessionId, lines],
    ),
    SCREEN_REFRESH_MS,
  );
  return (
    <div className="fmw-screen-wrap" aria-label={label}>
      {error !== null && (
        <div className="fmw-refusal" role="alert">
          {error}
        </div>
      )}
      <pre className="fmw-screen" data-testid="fmw-screen-text">
        {text === null ? loadingText : text.replace(/\s+$/, "")}
      </pre>
    </div>
  );
}
