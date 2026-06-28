import { Link } from "react-router-dom";

// The Chat / Terminal segmented toggle shown in both session views (#811, AC7). Both links carry
// the SAME session id, so the two views are interchangeable windows onto one session: a Send from
// either reaches the same Director session. The Terminal view is the route root
// (/session/:id, set up in #810); Chat is /session/:id/chat.
export type SessionView = "chat" | "terminal";

export function ViewTabs({ sessionId, active }: { sessionId: string; active: SessionView }) {
  const sid = encodeURIComponent(sessionId);
  return (
    <nav className="view-tabs" aria-label="Session view">
      <Link
        className={`view-tab${active === "chat" ? " view-tab-active" : ""}`}
        to={`/session/${sid}/chat`}
        aria-current={active === "chat" ? "page" : undefined}
      >
        Chat
      </Link>
      <Link
        className={`view-tab${active === "terminal" ? " view-tab-active" : ""}`}
        to={`/session/${sid}`}
        aria-current={active === "terminal" ? "page" : undefined}
      >
        Terminal
      </Link>
    </nav>
  );
}
