import { useCallback, useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import {
  acceptRequest,
  declineRequest,
  listMyRequests,
  listTeamRequests,
  markRequestDone,
  sendRequest,
  type TeamRequest,
} from "@devthrottle/client-core/teams/requestsClient";
import { Button, EmptyState, ErrorBanner, LoadingState, PageHeader } from "../components";
import "./team.css";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-team-requests";

// Screen S9, Requests (devthrottle_internal#2308). Two pages over one card:
//
// - RequestsView, the SENDER's page: a write box, "Send request", and the person's own requests newest first, each
//   with its trail - Sent, Accepted by X, Done, or "Not doing this" with the person and the reason.
// - TeamRequestsView, the Owner and Managers' list: every request in the team, with Accept, Not doing this (a reason
//   is required) and Done.
//
// CRITICAL RULE 7 - every label, every trail line and every button's presence is the Gateway's answer on the request
// (stateLabel, trail[].sentence, canAccept, canDecline, canMarkDone). These pages lay them out; neither decides what a
// person may do from their role or from a request's state.
//
// Each page takes the team on screen as a prop, so it plugs into whichever route or slot the app shell gives it.

function day(utc: string): string {
  return new Date(utc).toLocaleDateString(undefined, { day: "numeric", month: "short" });
}

interface RequestCardProps {
  request: TeamRequest;
  /** Whether the card names who sent it - on the team's list, not on the sender's own page. */
  showSender: boolean;
  busy: boolean;
  onAccept?: () => void;
  onDecline?: (reason: string) => void;
  onDone?: () => void;
}

/** One request: its words, when it was sent, its trail, the reason when it is not being done, and the buttons the
 *  Gateway says the reader may use. */
export function RequestCard({ request, showSender, busy, onAccept, onDecline, onDone }: RequestCardProps) {
  const [declining, setDeclining] = useState(false);
  const [reason, setReason] = useState("");
  // Every decision adds a step to the trail. When the Gateway answers with a longer trail, the reason box it was
  // written in is finished with and closes, whatever the request now allows.
  useEffect(() => {
    setDeclining(false);
    setReason("");
  }, [request.trail.length]);
  // The Gateway sends a reason on the "Not doing this" step only; when there is one, it is shown with who gave it.
  const declined = request.trail.find((s) => s.reason !== null);
  const anyAction = request.canAccept || request.canDecline || request.canMarkDone;

  return (
    <article className="team-req" aria-label={request.text}>
      <div className="team-req-text">{request.text}</div>
      <div className="team-req-meta">
        {showSender ? `${request.sentBy} - sent ${day(request.sentAtUtc)}` : `Sent ${day(request.sentAtUtc)}`}
        <span className={`team-req-state team-req-state-${request.state}`}>{request.stateLabel}</span>
      </div>
      <ol className="team-req-trail" aria-label="What happened">
        {request.trail.map((step, i) => (
          <li key={`${step.state}-${step.atUtc}-${i}`} className={i === request.trail.length - 1 ? "team-req-now" : "team-req-step"}>
            {step.sentence} <span className="team-req-when">{day(step.atUtc)}</span>
          </li>
        ))}
      </ol>
      {declined !== undefined && (
        <div className="team-req-reason" role="note">
          <div className="team-req-reason-who">{declined.by}</div>
          {declined.reason}
        </div>
      )}
      {anyAction && !declining && (
        <div className="team-row">
          {request.canAccept && <Button variant="primary" disabled={busy} onClick={onAccept}>Accept</Button>}
          {request.canDecline && <Button variant="secondary" disabled={busy} onClick={() => setDeclining(true)}>Not doing this</Button>}
          {request.canMarkDone && <Button variant="secondary" disabled={busy} onClick={onDone}>Done</Button>}
        </div>
      )}
      {declining && (
        <div className="team-req-decline">
          <label className="team-label" htmlFor={`decline-${request.id}`}>Why is this not being done? The person who sent it reads this.</label>
          <textarea
            id={`decline-${request.id}`}
            className="team-input team-textarea"
            rows={3}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
          />
          <div className="team-row">
            <Button variant="danger" disabled={busy || reason.trim().length === 0} onClick={() => onDecline?.(reason)}>
              Mark Not doing this
            </Button>
            <Button variant="ghost" disabled={busy} onClick={() => { setDeclining(false); setReason(""); }}>Keep it</Button>
          </div>
        </div>
      )}
    </article>
  );
}

/** The sender's page: write a request, and see what happened to each one sent. */
export function RequestsView({ teamId }: { teamId: string }) {
  const [requests, setRequests] = useState<TeamRequest[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [sentNote, setSentNote] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoadError(null);
    try {
      setRequests(await listMyRequests(teamId, signal));
    } catch (err) {
      if (signal?.aborted) return;
      setLoadError(describeAndReport(SURFACE, "load your requests", err));
    }
  }, [teamId]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  const send = async () => {
    setSending(true);
    setSendError(null);
    setSentNote(null);
    try {
      await sendRequest(teamId, text.trim());
      setText("");
      setSentNote("Sent. The team's Owner and Managers see it in their Requests list.");
      setRequests(await listMyRequests(teamId));
    } catch (err) {
      setSendError(describeAndReport(SURFACE, "send the request", err));
    } finally {
      setSending(false);
    }
  };

  return (
    <div className="team-page">
      <PageHeader title="Requests" subtitle="Ask for something. A Manager reads it and decides." />
      <section className="team-card" aria-label="Write a request">
        <label className="team-label" htmlFor="request-text">What would you like? Write it in your own words.</label>
        <textarea
          id="request-text"
          className="team-input team-textarea"
          rows={3}
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        <div className="team-row">
          <Button variant="primary" disabled={sending || text.trim().length === 0} onClick={() => void send()}>
            {sending ? "Sending..." : "Send request"}
          </Button>
          <span className="team-hint">It goes to the team's Owner and Managers - never to an agent.</span>
        </div>
        {sendError !== null && <ErrorBanner message={sendError} />}
        {sentNote !== null && <p className="team-ok" role="status">{sentNote}</p>}
      </section>

      {loadError !== null && <ErrorBanner message={loadError} onRetry={() => void load()} />}
      {loadError === null && requests === null && <LoadingState message="Loading your requests..." />}
      {requests !== null && requests.length === 0 && (
        <EmptyState message="No requests from you yet. What you send appears here, with everything that happens to it." />
      )}
      {requests !== null && requests.map((r) => (
        <RequestCard key={r.id} request={r} showSender={false} busy={false} />
      ))}
    </div>
  );
}

/** The Owner and Managers' list: every request in the team, and the three decisions. */
export function TeamRequestsView({ teamId }: { teamId: string }) {
  const [requests, setRequests] = useState<TeamRequest[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoadError(null);
    try {
      setRequests(await listTeamRequests(teamId, signal));
    } catch (err) {
      if (signal?.aborted) return;
      setLoadError(describeAndReport(SURFACE, "load the team's requests", err));
    }
  }, [teamId]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  // The Gateway answers each decision with the request as it now stands; that one card is replaced in place. When it
  // REFUSES one - most often because another Owner or Manager decided the request a moment earlier - the cards on
  // screen are the Gateway's old verdicts, so the list is read again: the refusal is shown, and beside it the request
  // as it now stands, with only the buttons the Gateway now allows.
  const decide = async (request: TeamRequest, what: string, act: () => Promise<TeamRequest>) => {
    setBusy(request.id);
    setActionError(null);
    try {
      const updated = await act();
      setRequests((list) => (list ?? []).map((r) => (r.id === updated.id ? updated : r)));
    } catch (err) {
      setActionError(describeAndReport(SURFACE, what, err));
      await load();
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="team-page">
      <PageHeader title="Requests" subtitle="What people on the team have asked for. Accept it, mark it Not doing this with a reason, or mark it Done - the person who sent it sees each change." />
      {actionError !== null && <ErrorBanner message={actionError} />}
      {loadError !== null && <ErrorBanner message={loadError} onRetry={() => void load()} />}
      {loadError === null && requests === null && <LoadingState message="Loading the team's requests..." />}
      {requests !== null && requests.length === 0 && (
        <EmptyState message="Nobody on the team has sent a request yet." />
      )}
      {requests !== null && requests.map((r) => (
        <RequestCard
          key={r.id}
          request={r}
          showSender
          busy={busy !== null}
          onAccept={() => void decide(r, "accept the request", () => acceptRequest(teamId, r.id))}
          onDecline={(reason) => void decide(r, "mark the request Not doing this", () => declineRequest(teamId, r.id, reason.trim()))}
          onDone={() => void decide(r, "mark the request Done", () => markRequestDone(teamId, r.id))}
        />
      ))}
    </div>
  );
}

/** The Owner and Managers' list at /team/{teamId}/requests. The team comes from the address, as on the Team page and
 *  the invite form; whether the reader may see the list is the Gateway's answer, shown as it gives it. */
export function TeamRequestsRoute() {
  const { teamId = "" } = useParams<{ teamId: string }>();
  return <TeamRequestsView teamId={teamId} />;
}
