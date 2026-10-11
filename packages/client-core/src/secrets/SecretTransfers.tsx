import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import {
  answerSecretTransfer,
  listSecretTransfers,
  timeLeftWords,
  type SecretTransfer,
  type SecretTransferPlace,
} from "./secretTransfersClient";
import "./secrets.css";

// THE SECRET TRANSFER APPROVAL CARD (Secret Handoff, issue #2943, phase 5). ONE card, written once here and mounted by
// both shells: the Cockpit shows it above the roster and in the popover of the badge on the session that asked; the
// phone shows it at the top of the session list and on the page a push notification opens. Each shell supplies only
// its own layout tuning for the `secret-transfer-*` classes.
//
// THE CARD DECIDES NOTHING. The Gateway wrote every sentence on a transfer - what moves where (summary), what it
// replaces (replaceNote), who asked (askedBy), why (reason, in the asker's own words), where it stands (statusText) -
// and whether it can still be answered (canAnswer). This renders those verbatim and offers Approve and Deny only while
// canAnswer is true. The only words of this file's own are the button labels, the time left (a formatted expiry
// time), and the line that says a read failed. A transfer never carries a value, so neither does the card.

const SURFACE = "secret-transfers";
const READ = "load the secret transfers";
const ANSWER = "answer the secret transfer";

/** How often the transfers are read again. A transfer expires in 15 minutes and an agent waits on it, so this is
 *  quicker than the message-link requests. */
export const SECRET_TRANSFERS_POLL_MS = 10_000;

interface SecretTransfersValue {
  /** Every transfer the Gateway listed, waiting or finished lately. */
  transfers: SecretTransfer[];
  /** The ones the owner can answer now, and the ones answered from this screen (so their outcome stays in view). */
  shown: SecretTransfer[];
  /** Waiting transfers asked by one session, for its badge. */
  waitingFrom: (sessionId: string) => SecretTransfer[];
  answer: (transfer: SecretTransfer, approve: boolean, where: SecretTransferPlace) => Promise<void>;
  /** Put an answered card away. */
  dismiss: (transferId: string) => void;
  busyId: string | null;
  errors: Readonly<Record<string, string>>;
  listError: string | null;
  now: () => number;
}

// Outside a provider there are no transfers, as with the message-link requests (LinkRequests.tsx): a roster or a session
// header rendered on its own - in a test, or in a shell that does not offer approvals - shows no badge rather than
// starting a poll of its own.
const NONE: SecretTransfersValue = {
  transfers: [],
  shown: [],
  waitingFrom: () => [],
  answer: async () => {},
  dismiss: () => {},
  busyId: null,
  errors: {},
  listError: null,
  now: Date.now,
};

const SecretTransfersContext = createContext<SecretTransfersValue>(NONE);

/** The transfers for everything inside it to read. One poll per screen, however many cards and badges show. */
export function SecretTransfersProvider({ children, now }: { children: ReactNode; now?: () => number }) {
  const [transfers, setTransfers] = useState<SecretTransfer[]>([]);
  const [answeredHere, setAnsweredHere] = useState<ReadonlySet<string>>(new Set());
  const [busyId, setBusyId] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [listError, setListError] = useState<string | null>(null);
  const clock = now ?? Date.now;

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const list = await listSecretTransfers(signal);
      setTransfers(list.transfers);
      setListError(null);
      backgroundRecovered(SURFACE, READ);
    } catch (err) {
      if (signal?.aborted) return;
      // The last known transfers stay on screen; the card says the list could not be read.
      setListError(describeAndReport(SURFACE, READ, err, { background: true }));
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    const timer = window.setInterval(() => void load(controller.signal), SECRET_TRANSFERS_POLL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [load]);

  const answer = useCallback(
    async (transfer: SecretTransfer, approve: boolean, where: SecretTransferPlace) => {
      setBusyId(transfer.transferId);
      setErrors((e) => ({ ...e, [transfer.transferId]: "" }));
      setAnsweredHere((s) => new Set(s).add(transfer.transferId));
      try {
        const result = await answerSecretTransfer(transfer.transferId, approve, where);
        setTransfers((ts) => ts.map((t) => (t.transferId === transfer.transferId ? result.transfer : t)));
      } catch (err) {
        // The Gateway's own sentence - "This transfer was already approved in the cc-secrets window." - verbatim.
        setErrors((e) => ({ ...e, [transfer.transferId]: describeAndReport(SURFACE, ANSWER, err) }));
      } finally {
        setBusyId(null);
      }
      await load();
    },
    [load],
  );

  const dismiss = useCallback((transferId: string) => {
    setAnsweredHere((s) => {
      const next = new Set(s);
      next.delete(transferId);
      return next;
    });
  }, []);

  const shown = useMemo(
    () => transfers.filter((t) => t.canAnswer || answeredHere.has(t.transferId)),
    [transfers, answeredHere],
  );

  const waitingFrom = useCallback(
    (sessionId: string) => {
      const id = sessionId.toLowerCase();
      return transfers.filter((t) => t.canAnswer && (t.askedBySessionId ?? "").toLowerCase() === id);
    },
    [transfers],
  );

  const value = useMemo<SecretTransfersValue>(
    () => ({ transfers, shown, waitingFrom, answer, dismiss, busyId, errors, listError, now: clock }),
    [transfers, shown, waitingFrom, answer, dismiss, busyId, errors, listError, clock],
  );
  return <SecretTransfersContext.Provider value={value}>{children}</SecretTransfersContext.Provider>;
}

/** The transfers, from the nearest SecretTransfersProvider. */
export function useSecretTransfers(): SecretTransfersValue {
  return useContext(SecretTransfersContext);
}

export interface SecretTransfersPanelProps {
  /** Where an answer given from this panel is recorded: "phone" on the phone, "cockpit" on the Cockpit. */
  where: SecretTransferPlace;
  /** A sentence to show when nothing waits. Omitted, the panel draws nothing at all then. */
  emptyText?: string;
}

/** Every transfer the owner can answer now, as cards. Draws nothing when none waits, unless given emptyText. */
export function SecretTransfersPanel({ where, emptyText }: SecretTransfersPanelProps) {
  const ctx = useSecretTransfers();
  if (ctx.shown.length === 0 && ctx.listError === null) {
    return emptyText ? <p className="secret-transfer-empty">{emptyText}</p> : null;
  }
  return (
    <section className="secret-transfers" aria-label="Secret transfers waiting for your answer" data-testid="secret-transfers">
      {ctx.listError !== null && (
        <p className="secret-transfer-error" role="alert">
          {/* error-reported-by: SecretTransfersProvider */}
          Could not read the secret transfers: {ctx.listError}
        </p>
      )}
      {ctx.shown.map((t) => (
        <SecretTransferCard key={t.transferId} transfer={t} where={where} />
      ))}
      {ctx.shown.length === 0 && emptyText ? <p className="secret-transfer-empty">{emptyText}</p> : null}
    </section>
  );
}

export interface SecretTransferCardProps {
  transfer: SecretTransfer;
  where: SecretTransferPlace;
}

/** One transfer. The sentences are the Gateway's; Approve and Deny appear only while canAnswer is true. */
export function SecretTransferCard({ transfer: t, where }: SecretTransferCardProps) {
  const ctx = useSecretTransfers();
  const busy = ctx.busyId === t.transferId;
  const error = ctx.errors[t.transferId];
  return (
    <article
      className={`secret-transfer ${t.canAnswer ? "secret-transfer-waiting" : "secret-transfer-closed"}`}
      data-state={t.state}
      data-testid="secret-transfer-card"
      aria-label={`Secret transfer: ${t.summary}`}
    >
      <header className="secret-transfer-head">
        <span className="secret-transfer-kind">Secret transfer</span>
        {t.canAnswer && <span className="secret-transfer-time">{timeLeftWords(t.expiresAtUtc, ctx.now())}</span>}
      </header>
      <p className="secret-transfer-summary">{t.summary}</p>
      {t.replaceNote ? <p className="secret-transfer-line secret-transfer-replace">{t.replaceNote}</p> : null}
      {t.reason ? <p className="secret-transfer-line secret-transfer-reason">"{t.reason}"</p> : null}
      <p className="secret-transfer-line secret-transfer-dim">Asked by {t.askedBy}</p>
      <p className="secret-transfer-line secret-transfer-status" role="status">
        {t.statusText}
      </p>
      {error ? (
        <p className="secret-transfer-error" role="alert">
          {/* error-reported-by: SecretTransfersProvider */}
          {error}
        </p>
      ) : null}
      {t.canAnswer ? (
        <div className="secret-transfer-actions">
          <button
            type="button"
            className="secret-transfer-btn secret-transfer-btn-primary"
            disabled={busy}
            onClick={() => void ctx.answer(t, true, where)}
          >
            Approve
          </button>
          <button type="button" className="secret-transfer-btn" disabled={busy} onClick={() => void ctx.answer(t, false, where)}>
            Deny
          </button>
        </div>
      ) : (
        <div className="secret-transfer-actions">
          <button type="button" className="secret-transfer-btn" onClick={() => ctx.dismiss(t.transferId)}>
            Close
          </button>
        </div>
      )}
    </article>
  );
}
