import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { listSessions, type SessionDto } from "../api/client";
import { contextLine, dotColor, effectiveColor } from "../sessions/ordering";

// Minimal session-detail placeholder (Issue 1). The full Terminal and Chat modes are later
// issues (2 and 3); this confirms the route binds to the tapped session id and shows the
// session's at-a-glance status so the navigation is provably wired end to end.
export function SessionDetail() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const [session, setSession] = useState<SessionDto | null>(null);
  const [notFound, setNotFound] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    listSessions(controller.signal)
      .then((all) => {
        const match = all.find((s) => s.sessionId === sessionId) ?? null;
        setSession(match);
        setNotFound(match === null);
      })
      .catch(() => {
        // Detail is a placeholder; if the lookup fails we still show the bound id.
      });
    return () => controller.abort();
  }, [sessionId]);

  const color = session ? effectiveColor(session) : "unknown";

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          &larr; Roster
        </Link>
        <h1>Session</h1>
      </header>

      <section className="detail">
        <div className="detail-id">
          <span className="dot" style={{ backgroundColor: dotColor(color) }} aria-hidden="true" />
          <code>{sessionId}</code>
        </div>

        {session !== null && (
          <>
            <h2 className="detail-name">
              {session.name && session.name.trim() ? session.name : "(unnamed session)"}
            </h2>
            <p className="detail-meta">{contextLine(session)}</p>
            {session.repoPath && <p className="detail-meta detail-repo">{session.repoPath}</p>}
            {session.agent && <p className="detail-meta">Agent: {session.agent}</p>}
          </>
        )}

        {notFound && <p className="status-line">Session not found in the current roster.</p>}

        <p className="placeholder-note">
          Terminal and Chat modes arrive in later issues. This placeholder confirms the roster
          tap is wired to this session.
        </p>
      </section>
    </div>
  );
}
