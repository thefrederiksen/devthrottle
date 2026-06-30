import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { listSessions } from "../api/client";
import { SessionManageBar } from "../components/SessionManageBar";
import { ViewTabs } from "../components/ViewTabs";

// Session Voice mode (placeholder). The third session view alongside Terminal (#817) and Chat
// (#811). It shares the same screen shell - app-bar + the three-tab ViewTabs row + the Hold/Remove
// manage bar - so switching to it feels like the other two views, but the body is a "coming soon"
// placeholder until the hands-free voice view is designed and built.

export function VoiceMode() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const [name, setName] = useState<string | null>(null);

  // One-shot fetch of the session's display name for the header, matching Terminal/Chat.
  useEffect(() => {
    const controller = new AbortController();
    listSessions(controller.signal)
      .then((all) => {
        const match = all.find((s) => s.sessionId === sessionId) ?? null;
        if (match?.name && match.name.trim()) setName(match.name.trim());
      })
      .catch(() => { /* header label is best-effort */ });
    return () => controller.abort();
  }, [sessionId]);

  return (
    <div className="terminal-screen">
      <header className="app-bar">
        <Link className="back-link" to="/">&larr; Roster</Link>
        <h1 className="term-title">{name ?? "Session"}</h1>
      </header>

      <ViewTabs sessionId={sessionId} active="voice" />

      {/* Hold/Resume + Remove management buttons on the session screen (issue #812). */}
      <SessionManageBar sessionId={sessionId} />

      <div className="voice-stage">
        <div className="voice-placeholder">
          <h2 className="voice-placeholder-title">Voice mode</h2>
          <p className="voice-placeholder-text">Coming soon.</p>
        </div>
      </div>
    </div>
  );
}
