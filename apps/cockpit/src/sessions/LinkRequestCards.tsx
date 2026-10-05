import { useCallback, useEffect, useState } from "react";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import {
  answerMessageLinkRequest,
  listMessageLinkRequests,
  MESSAGE_LINK_AMOUNTS,
  type MessageLinkAmount,
  type MessageLinkRequest,
} from "@devthrottle/client-core/fleet/messageLinksClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { sessionLabel } from "./MessageLinkDialog";

const SURFACE = "cockpit-link-requests";

/** How often the waiting requests are read again. A request is not urgent: the asking session carries on meanwhile. */
export const LINK_REQUESTS_POLL_MS = 20_000;

// A session asking to talk to another (issue #3548). One card per waiting request, above the session detail: who asks,
// whom it wants to talk to, why - in its own words - and four answers. Allowing sets up an ordinary message link and the
// Gateway tells the session; "No" tells it no. Nothing here sends a message: the sessions decide what to say.

export interface LinkRequestCardsProps {
  sessions: SessionDto[] | null;
}

export function LinkRequestCards({ sessions }: LinkRequestCardsProps) {
  const [requests, setRequests] = useState<MessageLinkRequest[]>([]);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [listError, setListError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const all = await listMessageLinkRequests();
      setRequests(all.filter((r) => r.status === "pending"));
      setListError(null);
    } catch (err) {
      setListError(describeAndReport(SURFACE, "load the requests for a message link", err));
    }
  }, []);

  useEffect(() => {
    void load();
    const timer = window.setInterval(() => void load(), LINK_REQUESTS_POLL_MS);
    return () => window.clearInterval(timer);
  }, [load]);

  const nameOf = (id: string) => {
    const known = (sessions ?? []).find((s) => s.sessionId === id);
    return known ? sessionLabel(known) : id.slice(0, 8);
  };

  const answer = async (request: MessageLinkRequest, choice: { amount: MessageLinkAmount } | { decline: true }) => {
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
  };

  if (requests.length === 0 && listError === null) return null;

  return (
    <div className="link-requests" aria-label="Requests to talk to another session">
      {listError !== null && <div className="session-dialog-error">{listError}</div>}
      {requests.map((r) => {
        const busy = busyId === r.requestId;
        const error = errors[r.requestId];
        return (
          <div key={r.requestId} className="link-request-card" role="group" aria-label={`Request from ${nameOf(r.requesterSessionId)}`}>
            <div className="link-request-title">
              <strong>{nameOf(r.requesterSessionId)}</strong> wants to talk to <strong>{nameOf(r.targetSessionId)}</strong>.
            </div>
            <div className="link-request-reason">"{r.reason}"</div>
            <div className="link-request-actions">
              {MESSAGE_LINK_AMOUNTS.map((a) => (
                <button
                  key={a.amount}
                  type="button"
                  className="session-dialog-btn"
                  title={a.detail}
                  disabled={busy}
                  onClick={() => void answer(r, { amount: a.amount })}
                >
                  {a.label}
                </button>
              ))}
              <button type="button" className="session-dialog-btn" disabled={busy} onClick={() => void answer(r, { decline: true })}>
                No
              </button>
            </div>
            {error ? <div className="session-dialog-error">{error}</div> : null}
          </div>
        );
      })}
    </div>
  );
}
