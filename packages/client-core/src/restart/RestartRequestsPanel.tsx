import { useCallback, useEffect, useRef, useState } from "react";
import { gatewayErrorMessage } from "../api/client";
import {
  acceptRestartRequest,
  declineRestartRequest,
  listRestartRequests,
  visibleRestartRequests,
  type DirectorRestartRequest,
} from "./restartRequests";

// The "Needs you" item for a Director restart - issue #2725 (restart epic, Phase 6). Shared by the
// Cockpit and the phone: ONE component, mounted by both shells above their "Needs you" group, so the
// two surfaces cannot drift apart. Each shell supplies only its own layout tuning for the
// `restart-request-*` classes.
//
// THE CARD DECIDES NOTHING. The Gateway wrote every sentence on the request - who asked and why, how many
// sessions are live, what the capability check said in Phase 1's own words, what accepting will do
// (`acceptSentence`), and whether an accept would be honoured right now (`canAccept`). This renders those
// verbatim and offers the two actions only when the Gateway says they are available. The accept goes
// through an in-card confirmation step rather than a browser dialog, because accepting closes every
// session on that Director and a single tap must not be enough. The only words of this file's own are
// the button labels and the two lines that say a read or an action failed.

const POLL_INTERVAL_MS = 5000;

/** The requests, polled. Exposed so a shell can badge or count them if it wants to. */
export function useRestartRequests(): { requests: DirectorRestartRequest[]; error: string | null; refresh: () => void } {
  const [requests, setRequests] = useState<DirectorRestartRequest[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);
  const refresh = useCallback(() => setTick((t) => t + 1), []);

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let stopped = false;
    const load = async () => {
      try {
        const list = await listRestartRequests(controller.signal);
        if (stopped) return;
        setRequests(list);
        setError(null);
      } catch (err) {
        if (stopped || controller.signal.aborted) return;
        // Keep the last-known list on screen; say plainly that the read failed.
        setError(gatewayErrorMessage(err));
      }
      if (!stopped) timer = setTimeout(() => void load(), POLL_INTERVAL_MS);
    };
    void load();
    return () => {
      stopped = true;
      controller.abort();
      if (timer !== undefined) clearTimeout(timer);
    };
  }, [tick]);

  return { requests, error, refresh };
}

export interface RestartRequestsPanelProps {
  /** The clock, injectable for tests. Defaults to Date.now. */
  now?: () => number;
}

/** Every visible restart request, as cards. Renders nothing at all when there are none. */
export function RestartRequestsPanel({ now }: RestartRequestsPanelProps) {
  const { requests, error, refresh } = useRestartRequests();
  const clock = now ?? Date.now;
  const visible = visibleRestartRequests(requests, clock());
  if (visible.length === 0 && !error) return null;
  return (
    <section className="restart-requests" aria-label="Director restart requests">
      {error && (
        <p className="restart-request-error" role="alert">
          Could not read the restart requests: {error}
        </p>
      )}
      {visible.map((r) => (
        <RestartRequestCard key={r.id} request={r} onChanged={refresh} />
      ))}
    </section>
  );
}

export interface RestartRequestCardProps {
  request: DirectorRestartRequest;
  /** Called after an accept or a decline landed, so the list refreshes without waiting for the poll. */
  onChanged?: () => void;
}

/** One request. The sentences are the Gateway's; the two actions appear only while `canAccept` is true. */
export function RestartRequestCard({ request, onChanged }: RestartRequestCardProps) {
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState<"accept" | "decline" | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const mounted = useRef(true);
  useEffect(() => () => { mounted.current = false; }, []);

  const act = async (which: "accept" | "decline") => {
    setBusy(which);
    setFailure(null);
    try {
      if (which === "accept") await acceptRestartRequest(request.machine, request.id);
      else await declineRestartRequest(request.machine, request.id);
      if (mounted.current) setConfirming(false);
      onChanged?.();
    } catch (err) {
      // The Gateway's own sentence, verbatim - a refusal on accept names what changed on the machine.
      if (mounted.current) setFailure(gatewayErrorMessage(err, which === "accept" ? "accept the restart" : "decline the restart"));
    } finally {
      if (mounted.current) setBusy(null);
    }
  };

  const tone = request.state === "Pending" ? "pending" : request.state === "Accepted" ? "running" : "closed";
  return (
    <article className={`restart-request restart-request-${tone}`} data-state={request.state}>
      <header className="restart-request-head">
        <span className="restart-request-title">{request.title}</span>
        <span className="restart-request-state">{request.state}</span>
      </header>
      <p className="restart-request-line">{request.askedBySentence}</p>
      <p className="restart-request-line">{request.liveSessionsSentence}</p>
      {request.capability && (
        <p className="restart-request-line restart-request-capability">
          {request.capability.reason} {request.capability.guardedRestartReason}
        </p>
      )}
      {request.state === "Pending" && (
        <p className="restart-request-line restart-request-dim">
          Expires at {formatClock(request.expiresAtUtc)}.
        </p>
      )}
      {request.progress && request.state === "Accepted" && (
        <p className="restart-request-line restart-request-progress" role="status">{request.progress}</p>
      )}
      {request.stateReason && request.state !== "Pending" && request.state !== "Accepted" && (
        <p className="restart-request-line restart-request-outcome">{request.stateReason}</p>
      )}
      {request.workspaceId && (
        <p className="restart-request-line restart-request-dim">Record: workspace {request.workspaceId}</p>
      )}
      {failure && <p className="restart-request-error" role="alert">{failure}</p>}
      {request.canAccept && !confirming && (
        <div className="restart-request-actions">
          <button type="button" className="restart-request-btn restart-request-btn-primary" onClick={() => setConfirming(true)} disabled={busy !== null}>
            Accept
          </button>
          <button type="button" className="restart-request-btn" onClick={() => void act("decline")} disabled={busy !== null}>
            {busy === "decline" ? "Declining..." : "Decline"}
          </button>
        </div>
      )}
      {request.canAccept && confirming && (
        <div className="restart-request-actions restart-request-confirm">
          <span className="restart-request-line">{request.acceptSentence || request.title}</span>
          <button type="button" className="restart-request-btn restart-request-btn-danger" onClick={() => void act("accept")} disabled={busy !== null}>
            {busy === "accept" ? "Accepting..." : "Yes, restart it"}
          </button>
          <button type="button" className="restart-request-btn" onClick={() => setConfirming(false)} disabled={busy !== null}>
            Not now
          </button>
        </div>
      )}
    </article>
  );
}

/** A wall-clock time in the viewer's locale, or the raw value when it will not parse - never blank. */
function formatClock(iso: string): string {
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return iso;
  return new Date(ms).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}
