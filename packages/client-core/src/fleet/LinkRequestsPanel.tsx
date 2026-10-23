import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import {
  answerMessageLinkRequest,
  listMessageLinkRequests,
  requestedAmountWords,
  type MessageLinkRequest,
} from "./messageLinksClient";
import "./linkRequests.css";

// THE MESSAGE LINK REQUEST CARD ON THE PHONE (issue #4373). A session that may not message another asks the owner for a
// link (`cc-devthrottle message request`); this card lets him approve or refuse it with one tap, wherever he is. It
// answers through the same Gateway call as the Director's flyout and the Cockpit's popover, so the asking session hears
// the answer in its inbox exactly as it would from either of them.
//
// THE CARD DECIDES NOTHING. The Gateway names both sessions (requesterLabel, targetLabel - number and name, never an
// id), keeps the reason in the asking session's own words, and says what was asked for. The only words of this file's
// own are the button labels, the line that joins the two names, and the line that says a read failed.

const SURFACE = "link-requests";
const READ = "load the requests for a message link";
const ANSWER = "answer the request for a message link";

/** How often the waiting requests are read again. A request answered at the desk leaves the phone within this. */
export const LINK_REQUESTS_PANEL_POLL_MS = 15_000;

type Answer = { approve: true } | { decline: true };

interface LinkRequestsValue {
  /** The requests still waiting for the owner. */
  pending: MessageLinkRequest[];
  /** The waiting ones, and the ones answered from this screen (so their outcome stays in view until put away). */
  shown: MessageLinkRequest[];
  answer: (request: MessageLinkRequest, choice: Answer) => Promise<void>;
  /** Put an answered card away. */
  dismiss: (requestId: string) => void;
  busyId: string | null;
  errors: Readonly<Record<string, string>>;
  listError: string | null;
  /** False until the first read has come back, so a page does not say "nothing waits" before it knows. */
  loaded: boolean;
}

const NONE: LinkRequestsValue = {
  pending: [],
  shown: [],
  answer: async () => {},
  dismiss: () => {},
  busyId: null,
  errors: {},
  listError: null,
  loaded: false,
};

const LinkRequestsContext = createContext<LinkRequestsValue>(NONE);

/** The requests for everything inside it to read. One poll per screen, however many cards show. */
export function LinkRequestsPanelProvider({ children }: { children: ReactNode }) {
  const [requests, setRequests] = useState<MessageLinkRequest[]>([]);
  const [answeredHere, setAnsweredHere] = useState<ReadonlySet<string>>(new Set());
  const [busyId, setBusyId] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [listError, setListError] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const list = await listMessageLinkRequests(signal);
      setRequests(list);
      setListError(null);
      setLoaded(true);
      backgroundRecovered(SURFACE, READ);
    } catch (err) {
      if (signal?.aborted) return;
      // The last known requests stay on screen; the panel says the list could not be read.
      setListError(describeAndReport(SURFACE, READ, err, { background: true }));
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    const timer = window.setInterval(() => void load(controller.signal), LINK_REQUESTS_PANEL_POLL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [load]);

  const answer = useCallback(
    async (request: MessageLinkRequest, choice: Answer) => {
      setBusyId(request.requestId);
      setErrors((e) => ({ ...e, [request.requestId]: "" }));
      setAnsweredHere((s) => new Set(s).add(request.requestId));
      try {
        const result = await answerMessageLinkRequest(request.requestId, choice);
        // The answer carries no labels (only the list does), so the card keeps the ones it already shows.
        setRequests((rs) =>
          rs.map((r) =>
            r.requestId === request.requestId
              ? { ...result.request, requesterLabel: r.requesterLabel, targetLabel: r.targetLabel }
              : r,
          ),
        );
      } catch (err) {
        // The Gateway's own sentence - "This request was already allowed. Nothing changed." - verbatim.
        setErrors((e) => ({ ...e, [request.requestId]: describeAndReport(SURFACE, ANSWER, err) }));
      } finally {
        setBusyId(null);
      }
      await load();
    },
    [load],
  );

  const dismiss = useCallback((requestId: string) => {
    setAnsweredHere((s) => {
      const next = new Set(s);
      next.delete(requestId);
      return next;
    });
  }, []);

  const pending = useMemo(() => requests.filter((r) => r.status === "pending"), [requests]);
  const shown = useMemo(
    () => requests.filter((r) => r.status === "pending" || answeredHere.has(r.requestId)),
    [requests, answeredHere],
  );

  const value = useMemo<LinkRequestsValue>(
    () => ({ pending, shown, answer, dismiss, busyId, errors, listError, loaded }),
    [pending, shown, answer, dismiss, busyId, errors, listError, loaded],
  );
  return <LinkRequestsContext.Provider value={value}>{children}</LinkRequestsContext.Provider>;
}

/** The requests, from the nearest LinkRequestsPanelProvider. */
export function useLinkRequests(): LinkRequestsValue {
  return useContext(LinkRequestsContext);
}

export interface LinkRequestsPanelProps {
  /** A sentence to show when nothing waits. Omitted, the panel draws nothing at all then. */
  emptyText?: string;
}

/** Every request the owner can answer now, as cards. Draws nothing when none waits, unless given emptyText. */
export function LinkRequestsPanel({ emptyText }: LinkRequestsPanelProps) {
  const ctx = useLinkRequests();
  if (ctx.shown.length === 0 && ctx.listError === null) {
    return emptyText && ctx.loaded ? <p className="link-request-empty">{emptyText}</p> : null;
  }
  return (
    <section className="link-requests" aria-label="Sessions asking to message another session" data-testid="link-requests">
      {ctx.listError !== null && (
        <p className="link-request-error" role="alert">
          {/* error-reported-by: LinkRequestsPanelProvider */}
          Could not read the requests: {ctx.listError}
        </p>
      )}
      {ctx.shown.map((r) => (
        <LinkRequestCard key={r.requestId} request={r} />
      ))}
      {ctx.shown.length === 0 && emptyText && ctx.loaded ? <p className="link-request-empty">{emptyText}</p> : null}
    </section>
  );
}

/** What became of a request answered from this screen, in a word. */
function outcomeWords(status: string): string {
  if (status === "allowed") return "Approved. The session has been told in its inbox.";
  if (status === "declined") return "Refused. The session has been told in its inbox.";
  return "This request ended because one of the sessions ended.";
}

/** One request: who asks, to whom, why, and how much. Approve and Refuse while it waits. */
export function LinkRequestCard({ request: r }: { request: MessageLinkRequest }) {
  const ctx = useLinkRequests();
  const busy = ctx.busyId === r.requestId;
  const error = ctx.errors[r.requestId];
  const waiting = r.status === "pending";
  const from = r.requesterLabel ?? "A session";
  const to = r.targetLabel ?? "another session";
  return (
    <article
      className={`link-request ${waiting ? "link-request-waiting" : "link-request-closed"}`}
      data-status={r.status}
      data-testid="link-request-card"
      aria-label={`${from} asks to message ${to}`}
    >
      <header className="link-request-head">
        <span className="link-request-kind">Message request</span>
      </header>
      <p className="link-request-summary">
        <strong>{from}</strong> asks to message <strong>{to}</strong>
      </p>
      <p className="link-request-line link-request-reason">"{r.reason}"</p>
      <p className="link-request-line link-request-dim">Asks for: {requestedAmountWords(r)}</p>
      {!waiting && (
        <p className="link-request-line link-request-status" role="status">
          {outcomeWords(r.status)}
        </p>
      )}
      {error ? (
        <p className="link-request-error" role="alert">
          {/* error-reported-by: LinkRequestsPanelProvider */}
          {error}
        </p>
      ) : null}
      {waiting ? (
        <div className="link-request-actions">
          <button
            type="button"
            className="link-request-btn link-request-btn-primary"
            disabled={busy}
            onClick={() => void ctx.answer(r, { approve: true })}
          >
            Approve
          </button>
          <button type="button" className="link-request-btn" disabled={busy} onClick={() => void ctx.answer(r, { decline: true })}>
            Refuse
          </button>
        </div>
      ) : (
        <div className="link-request-actions">
          <button type="button" className="link-request-btn" onClick={() => ctx.dismiss(r.requestId)}>
            Close
          </button>
        </div>
      )}
    </article>
  );
}
