import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import {
  answerMessageLinkRequest,
  listMessageLinkRequests,
  requestedAmountWords,
  type MessageLinkRequest,
} from "@devthrottle/client-core/fleet/messageLinksClient";
import { backgroundRecovered, describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { sessionLabel } from "./MessageLinkDialog";

const SURFACE = "cockpit-link-requests";

/** How often the waiting requests are read again. A request is not urgent: the asking session carries on meanwhile. */
export const LINK_REQUESTS_POLL_MS = 20_000;

// A session asking to talk to another (issues #3548, #3631). It used to be a full-width card above the session detail,
// which pushed every session down and sideways. Now a waiting request lives ON THE SESSION THAT ASKED: an amber phone
// badge and a "wants to talk to ..." line on its roster card, and a chip in its own header that opens a small popover.
// Amber, never red: the red dot already means something waits in the queue.
//
// The session names how much talking it needs when it asks; the owner only approves or denies that. Approving sets up an
// ordinary message link and the Gateway tells the session; denying tells it no. Nothing here sends a message.

type Answer = { approve: true } | { decline: true };

interface LinkRequestsValue {
  /** Waiting requests, keyed by the session that asked. */
  byRequester: ReadonlyMap<string, MessageLinkRequest[]>;
  nameOf: (sessionId: string) => string;
  answer: (request: MessageLinkRequest, choice: Answer) => Promise<void>;
  busyId: string | null;
  errors: Readonly<Record<string, string>>;
  listError: string | null;
}

const EMPTY: LinkRequestsValue = {
  byRequester: new Map(),
  nameOf: (id) => id.slice(0, 8),
  answer: async () => {},
  busyId: null,
  errors: {},
  listError: null,
};

const LinkRequestsContext = createContext<LinkRequestsValue>(EMPTY);

/** The waiting requests for the roster and the open session to read. One poll for the whole Sessions screen. */
export function LinkRequestsProvider({ sessions, children }: { sessions: SessionDto[] | null; children: ReactNode }) {
  const [requests, setRequests] = useState<MessageLinkRequest[]>([]);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [listError, setListError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const all = await listMessageLinkRequests();
      setRequests(all.filter((r) => r.status === "pending"));
      setListError(null);
      backgroundRecovered(SURFACE, "load the requests for a message link");
    } catch (err) {
      // The last known requests stay on their sessions; the popover says the list could not be read.
      setListError(describeAndReport(SURFACE, "load the requests for a message link", err, { background: true }));
    }
  }, []);

  useEffect(() => {
    void load();
    const timer = window.setInterval(() => void load(), LINK_REQUESTS_POLL_MS);
    return () => window.clearInterval(timer);
  }, [load]);

  const nameOf = useCallback(
    (id: string) => {
      const known = (sessions ?? []).find((s) => s.sessionId === id);
      return known ? sessionLabel(known) : id.slice(0, 8);
    },
    [sessions],
  );

  const answer = useCallback(
    async (request: MessageLinkRequest, choice: Answer) => {
      setBusyId(request.requestId);
      setErrors((e) => ({ ...e, [request.requestId]: "" }));
      try {
        await answerMessageLinkRequest(request.requestId, choice);
        setRequests((rs) => rs.filter((r) => r.requestId !== request.requestId));
      } catch (err) {
        setErrors((e) => ({ ...e, [request.requestId]: describeAndReport(SURFACE, "answer the request for a message link", err) }));
      } finally {
        setBusyId(null);
      }
      await load();
    },
    [load],
  );

  const byRequester = useMemo(() => {
    const map = new Map<string, MessageLinkRequest[]>();
    for (const r of requests) {
      const key = r.requesterSessionId.toLowerCase();
      map.set(key, [...(map.get(key) ?? []), r]);
    }
    return map;
  }, [requests]);

  const value = useMemo<LinkRequestsValue>(
    () => ({ byRequester, nameOf, answer, busyId, errors, listError }),
    [byRequester, nameOf, answer, busyId, errors, listError],
  );
  return <LinkRequestsContext.Provider value={value}>{children}</LinkRequestsContext.Provider>;
}

function useRequestsFrom(sessionId: string): { ctx: LinkRequestsValue; mine: MessageLinkRequest[] } {
  const ctx = useContext(LinkRequestsContext);
  return { ctx, mine: ctx.byRequester.get(sessionId.toLowerCase()) ?? [] };
}

function PhoneIcon() {
  return (
    <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M22 16.9v3a2 2 0 0 1-2.2 2 19.8 19.8 0 0 1-8.6-3.1 19.5 19.5 0 0 1-6-6A19.8 19.8 0 0 1 2.1 4.2 2 2 0 0 1 4.1 2h3a2 2 0 0 1 2 1.7c.1 1 .4 1.9.7 2.8a2 2 0 0 1-.5 2.1L8 9.9a16 16 0 0 0 6 6l1.3-1.3a2 2 0 0 1 2.1-.4c.9.3 1.8.6 2.8.7a2 2 0 0 1 1.7 2z" />
    </svg>
  );
}

/** The amber phone on the asking session's roster card, with how many requests wait. Nothing when none do. */
export function RosterLinkBadge({ sessionId }: { sessionId: string }) {
  const { mine } = useRequestsFrom(sessionId);
  if (mine.length === 0) return null;
  const title = mine.length === 1 ? "Wants to talk to another session" : `Wants to talk to ${mine.length} other sessions`;
  return (
    <span className="link-ask-badge" title={title} aria-label={title} data-testid="roster-link-badge">
      <PhoneIcon />
      {mine.length}
    </span>
  );
}

/** The roster card's line: "wants to talk to Cube - Release Manager (eb502e6b)". Nothing when no request waits. */
export function RosterLinkLine({ sessionId }: { sessionId: string }) {
  const { ctx, mine } = useRequestsFrom(sessionId);
  if (mine.length === 0) return null;
  const targets = mine.map((r) => ctx.nameOf(r.targetSessionId)).join(", ");
  return <span className="roster-link-ask">wants to talk to {targets}</span>;
}

/** The chip in the open session's header and its popover: what the session asks for, Approve or Deny. */
export function LinkRequestChip({ sessionId }: { sessionId: string }) {
  const { ctx, mine } = useRequestsFrom(sessionId);
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  if (mine.length === 0) return null;
  const label = mine.length === 1 ? `Wants to talk to ${ctx.nameOf(mine[0].targetSessionId)}` : `Wants to talk to ${mine.length} sessions`;
  return (
    <span className="link-ask-wrap" ref={wrapRef}>
      <button
        type="button"
        className="link-ask-chip"
        aria-expanded={open}
        aria-haspopup="dialog"
        onClick={() => setOpen((o) => !o)}
        data-testid="link-ask-chip"
      >
        <PhoneIcon />
        {label}
      </button>
      {open && (
        <div className="link-ask-pop" role="dialog" aria-label="Requests to talk to another session">
          {ctx.listError !== null && <div className="session-dialog-error">{ctx.listError}</div>}
          {mine.map((r) => {
            const busy = ctx.busyId === r.requestId;
            const error = ctx.errors[r.requestId];
            return (
              <div key={r.requestId} className="link-ask-item" role="group" aria-label={`Request to talk to ${ctx.nameOf(r.targetSessionId)}`}>
                <div className="link-ask-title">
                  Wants to talk to <strong>{ctx.nameOf(r.targetSessionId)}</strong>
                </div>
                <div className="link-ask-reason">"{r.reason}"</div>
                <div className="link-ask-amount">
                  Asks for: <span className="link-ask-amount-words">{requestedAmountWords(r)}</span>
                </div>
                <div className="link-ask-actions">
                  <button
                    type="button"
                    className="session-dialog-btn link-ask-approve"
                    disabled={busy}
                    onClick={() => void ctx.answer(r, { approve: true })}
                  >
                    Approve
                  </button>
                  <button type="button" className="session-dialog-btn" disabled={busy} onClick={() => void ctx.answer(r, { decline: true })}>
                    Deny
                  </button>
                </div>
                {error ? <div className="session-dialog-error">{error}</div> : null}
              </div>
            );
          })}
        </div>
      )}
    </span>
  );
}
