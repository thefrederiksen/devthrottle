import { useCallback, useEffect, useState } from "react";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import { RestartRequestActions } from "../alerts/actions/RestartRequestActions";
import {
  listRestartRequests,
  visibleRestartRequests,
  type DirectorRestartRequest,
} from "./restartRequests";

const SURFACE = "restart-requests";

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
        backgroundRecovered(SURFACE, "read the restart requests");
      } catch (err) {
        if (stopped || controller.signal.aborted) return;
        // Keep the last-known list on screen; say plainly that the read failed.
        setError(describeAndReport(SURFACE, "read the restart requests", err, { background: true }));
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

/** Every request waiting for the owner's answer, as cards. Renders nothing at all when there are none. */
export function RestartRequestsPanel() {
  const { requests, error, refresh } = useRestartRequests();
  const visible = visibleRestartRequests(requests);
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

/** One request. The sentences are the Gateway's; the answer is the shared block the alert detail also renders. */
export function RestartRequestCard({ request, onChanged }: RestartRequestCardProps) {
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
        // The capability check's own sentences are diagnostics, not the question: collapsed by default so
        // the card reads as one plain ask, and still there, verbatim, for whoever opens them.
        <details className="restart-request-details">
          <summary className="restart-request-dim">Details</summary>
          <p className="restart-request-line restart-request-capability">{request.capability.reason}</p>
          <p className="restart-request-line restart-request-capability">{request.capability.guardedRestartReason}</p>
          {request.capability.launcherVersion && (
            <p className="restart-request-line restart-request-capability">Launcher version: {request.capability.launcherVersion}</p>
          )}
        </details>
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
      <RestartRequestActions request={request} onAnswered={onChanged} />
    </article>
  );
}

/** A wall-clock time in the viewer's locale, or the raw value when it will not parse - never blank. */
function formatClock(iso: string): string {
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return iso;
  return new Date(ms).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}
