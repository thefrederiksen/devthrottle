import { useEffect, useState } from "react";
import { GatewayError, gatewayErrorMessage, type SessionDto } from "../api/client";
import {
  answerTurnVerdict,
  readLatestJudgedStop,
  reportTurnVerdictWrong,
  type TurnVerdict,
  type TurnVerdictRow,
} from "./verdictAnswer";
import { VERDICT_WORDS } from "./verdictVocabulary";
import "./verdictPanel.css";

// ---- The verdict panel: what the Wingman read at this stop, and the owner's answer to it ----------------
//
// The Wingman-on-every-turn mission, slice E. ONE copy, mounted by the Cockpit session view and the phone
// session screen; the desktop does not mount it (the Director wire carries colour and label only).
//
// In order: the risk, when there is one; the receipt - the agent's own words, open; the label; the summary;
// the options as buttons wired to the answer route; and "this is wrong", wired by slice G to the feedback route.
//
// A ROW THAT CARRIES NO VERDICT STILL SHOWS THE LAST ONE, from the history read, COLLAPSED and with only "This
// is wrong" live. This is the ordinary journey rather than an edge: answering a red row is what puts the session
// back to work, working is what supersedes the verdict, and the row then carries none - so a panel that rendered
// nothing at that moment took the reporting control away at exactly the moment the owner thinks "that was never
// a question, it was telling me it was done". The answer buttons are NOT offered on that record: the screen it
// was formed on has moved on, so its options no longer mean what they meant, and the answer route would refuse
// them anyway. What is still true about a superseded verdict is what it SAID, which is what a correction is
// about.
//
// A CURRENT REFUSAL IS SHOWN AS A REFUSAL. When the row's stamp is "failed" and it carries the failed record, that
// record IS what the Wingman made of this stop right now: the panel shows it as refused, with the Gateway's own
// failure reason verbatim, and never as superseded - the session has not gone back to work. The history is not read
// for it, because the row already carries the newest record (the slice I inspection found the history read answering
// with that same failed record, rendered as superseded).
//
// "THIS IS WRONG" OPENS A PICKER OF THE CLOSED WORDS, and the words are the vocabulary's own spellings - see
// verdictVocabulary.ts for why that list is here at all and what stops it drifting from the Gateway's. The panel
// does not decide which correction is plausible, does not pre-select one, and does not hide a word because the
// Wingman would not have said it. The verdict being corrected is usually SUPERSEDED by the time the report is
// sent - answering a red row is what puts the session back to work, and that is what supersedes it - which is
// exactly why slice G stopped deleting those records.
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
  /** Told after a correction is stored, when a shell wants to react to one. The panel does the reporting itself -
   *  it lives in client-core so both surfaces get the same action, and a shell that passes nothing still has it. */
  onReported?: (verdict: TurnVerdict, correctVerdict: string) => void;
}

export function VerdictPanel({ sessionId, session, onReported }: VerdictPanelProps) {
  const row = session as TurnVerdictRow;
  const live = row.verdictState === "judged" ? row.turnVerdict ?? null : null;
  const refusedNow =
    row.verdictState === "failed" && row.turnVerdict?.failed === true ? row.turnVerdict : null;
  const carried = live ?? refusedNow;
  const [past, setPast] = useState<TurnVerdict | null>(null);
  const [pastRefusal, setPastRefusal] = useState<string | null>(null);
  const verdict = carried ?? past;
  const verdictId = verdict?.verdictId ?? "";
  const agentName = (session.agentToolDisplay ?? "").trim() || "Agent tool not reported";

  const [picked, setPicked] = useState<number[]>([]);
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [sent, setSent] = useState<string | null>(null);

  const [reporting, setReporting] = useState(false);
  const [correctWord, setCorrectWord] = useState("");
  const [note, setNote] = useState("");
  const [reportBusy, setReportBusy] = useState(false);
  const [reportRefusal, setReportRefusal] = useState<string | null>(null);
  const [reported, setReported] = useState<string | null>(null);

  // A new stop is a new question: nothing picked, answered, refused or reported carries over from the last one.
  useEffect(() => {
    setPicked([]);
    setRefusal(null);
    setSent(null);
    setReporting(false);
    setCorrectWord("");
    setNote("");
    setReportRefusal(null);
    setReported(null);
  }, [sessionId, verdictId]);

  // THE HISTORY READ, and only when the row carries nothing. A row with a live verdict is already the newest
  // record, so asking again would be a request per render for an answer the roster has. A failed read is SHOWN
  // rather than swallowed: "the read was refused" and "this session was never judged" are different facts, and a
  // panel that rendered nothing for both would hide the first behind the second.
  useEffect(() => {
    if (carried !== null || row.sessionId !== sessionId) {
      setPast(null);
      setPastRefusal(null);
      return;
    }
    const abort = new AbortController();
    let current = true;
    setPastRefusal(null);
    readLatestJudgedStop(sessionId, abort.signal)
      .then((found) => {
        if (current) setPast(found);
      })
      .catch((err) => {
        if (abort.signal.aborted || !current) return;
        setPast(null);
        setPastRefusal(
          err instanceof GatewayError && err.serverReason
            ? err.serverReason
            : gatewayErrorMessage(err, "read what the Wingman said about this session"),
        );
      });
    return () => {
      current = false;
      abort.abort();
    };
  }, [sessionId, carried, row.sessionId]);

  if (row.sessionId !== sessionId) return null;

  if (verdict === null) {
    if (pastRefusal === null) return null;
    return (
      <section className="verdict-panel" aria-label="Wingman verdict">
        <div className="verdict-refusal" role="alert">
          {pastRefusal}
        </div>
      </section>
    );
  }

  // Everything the answer route needs is true only of a verdict the ROW carries. A record read out of the
  // history describes a screen that has moved on, so the panel offers no answer on it - one decision, made
  // here, rather than a condition on each control below.
  const answerable = live !== null;
  // What the row carries now, answerable or refused, as against a record read out of the history.
  const current = carried !== null;
  const refused = verdict.failed === true;

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

  const report = async () => {
    if (reportBusy || !sessionId || correctWord === "") return;
    setReportBusy(true);
    setReportRefusal(null);
    setReported(null);
    try {
      const result = await reportTurnVerdictWrong(sessionId, verdict.verdictId, correctWord, note);
      setReported(result.reason);
      setReporting(false);
      onReported?.(verdict, correctWord);
    } catch (err) {
      setReportRefusal(
        err instanceof GatewayError && err.serverReason
          ? err.serverReason
          : gatewayErrorMessage(err, "report that verdict wrong"),
      );
    } finally {
      setReportBusy(false);
    }
  };

  return (
    <section
      className={`verdict-panel${current ? "" : " verdict-panel-past"}`}
      aria-label={
        !current ? "Wingman verdict, superseded" : refused ? "Wingman verdict, refused" : "Wingman verdict"
      }
    >
      {refused && (
        <div className="verdict-refused" role="note">
          Refused: {verdict.failureReason ?? ""}
        </div>
      )}

      {verdict.risk && verdict.risk !== "none" && (
        <div className="verdict-risk" role="note">
          Risk: {verdict.risk}
        </div>
      )}

      {verdict.evidence && (
        /* STARTS CLOSED for a record read out of the history: the session has moved on, and what is being
            shown is what the Wingman SAID rather than what it is waiting on. One tap to open; nothing removed. */
        <details className="verdict-receipt" open={current}>
          <summary>{agentName} said</summary>
          <blockquote className="verdict-evidence">{verdict.evidence}</blockquote>
        </details>
      )}

      {verdict.label && <div className="verdict-label">{verdict.label}</div>}
      {verdict.summary && <p className="verdict-summary">{verdict.summary}</p>}

      {answerable && menu?.question && <div className="verdict-question">{menu.question}</div>}

      {answerable && options.length > 0 && (
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

      {answerable && multiple && options.length > 0 && (
        <button
          type="button"
          className="verdict-send"
          disabled={busy || picked.length === 0}
          onClick={() => void send(picked)}
        >
          Send the chosen options
        </button>
      )}

      {answerable && parkedReply && (
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

      {!current && (
        <p className="verdict-superseded" role="note">
          This session has gone back to work, so this is the last thing the Wingman said about it rather than
          what it is waiting on now. It can still be reported wrong.
        </p>
      )}

      <div className="verdict-actions">
        <button
          type="button"
          className="verdict-wrong"
          aria-expanded={reporting}
          disabled={reportBusy}
          onClick={() => setReporting((open) => !open)}
        >
          This is wrong
        </button>
      </div>

      {reporting && (
        <div className="verdict-report">
          <label className="verdict-report-label" htmlFor={`verdict-correct-${verdict.verdictId}`}>
            What should it have said?
          </label>
          <select
            id={`verdict-correct-${verdict.verdictId}`}
            className="verdict-report-word"
            value={correctWord}
            disabled={reportBusy}
            onChange={(event) => setCorrectWord(event.target.value)}
          >
            <option value="">Choose a verdict</option>
            {VERDICT_WORDS.map((word) => (
              <option key={word} value={word}>
                {word}
              </option>
            ))}
          </select>

          <label className="verdict-report-label" htmlFor={`verdict-note-${verdict.verdictId}`}>
            Anything to add (optional)
          </label>
          <textarea
            id={`verdict-note-${verdict.verdictId}`}
            className="verdict-report-note"
            value={note}
            rows={2}
            disabled={reportBusy}
            onChange={(event) => setNote(event.target.value)}
          />

          <button
            type="button"
            className="verdict-send"
            disabled={reportBusy || correctWord === ""}
            onClick={() => void report()}
          >
            Send the correction
          </button>
        </div>
      )}

      {reportBusy && <div className="verdict-busy" role="status">Recording...</div>}
      {reportRefusal !== null && (
        <div className="verdict-refusal" role="alert">
          {reportRefusal}
        </div>
      )}
      {reported !== null && (
        <div className="verdict-sent" role="status">
          {reported}
        </div>
      )}
    </section>
  );
}
