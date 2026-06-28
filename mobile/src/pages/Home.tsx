import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { listSessions, type SessionDto } from "../api/client";
import { classify, contextLine, dotColor, effectiveColor, inBucket, inDesktopOrder, repoLeaf } from "../sessions/ordering";

// Home / roster. A "needs you" group first (when any session wants attention), then the full
// session list, both using the live Gateway /sessions data and the shared triage ordering.
// Tapping a row opens the session-detail placeholder bound to that session id.
const POLL_INTERVAL_MS = 5000;

export function Home() {
  const [sessions, setSessions] = useState<SessionDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const loadedOnce = useRef(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const data = await listSessions(signal);
      setSessions(data);
      setError(null);
      loadedOnce.current = true;
    } catch (err) {
      if (signal?.aborted) return;
      // Keep the last-known roster on screen (offline shell); only show the error banner.
      setError(err instanceof Error ? err.message : "Failed to load sessions");
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    const timer = window.setInterval(() => {
      void load(controller.signal);
    }, POLL_INTERVAL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [load]);

  const needsYou = sessions ? inBucket(sessions, "needsYou") : [];
  const all = sessions ? inDesktopOrder(sessions) : [];

  return (
    <div className="screen">
      <header className="app-bar">
        <h1>DevThrottle</h1>
        <span className="app-bar-sub">Mission Control</span>
      </header>

      {error !== null && (
        <div className="banner banner-error" role="alert">
          {loadedOnce.current ? "Offline - showing last-known roster" : error}
        </div>
      )}

      {sessions === null && error === null && <p className="status-line">Loading sessions...</p>}

      {sessions !== null && all.length === 0 && (
        <p className="status-line">No sessions running.</p>
      )}

      {needsYou.length > 0 && (
        <section className="group">
          <h2 className="group-title group-title-attention">Needs you</h2>
          <ul className="roster">
            {needsYou.map((s) => (
              <SessionRow key={`needs-${s.sessionId}`} session={s} />
            ))}
          </ul>
        </section>
      )}

      {all.length > 0 && (
        <section className="group">
          <h2 className="group-title">All sessions</h2>
          <ul className="roster">
            {all.map((s) => (
              <SessionRow key={s.sessionId} session={s} />
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

function SessionRow({ session }: { session: SessionDto }) {
  const color = effectiveColor(session);
  const name = session.name && session.name.trim().length > 0 ? session.name : "(unnamed session)";
  const repo = repoLeaf(session);
  const attention = classify(session) === "needsYou";
  return (
    <li className={`row${attention ? " row-attention" : ""}`}>
      <Link className="row-link" to={`/session/${encodeURIComponent(session.sessionId ?? "")}`}>
        <span className="dot" style={{ backgroundColor: dotColor(color) }} aria-hidden="true" />
        <span className="row-body">
          <span className="row-name">{name}</span>
          <span className="row-context">{contextLine(session)}</span>
        </span>
        {repo && <span className="row-repo">{repo}</span>}
      </Link>
    </li>
  );
}
