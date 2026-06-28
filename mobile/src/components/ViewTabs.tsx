import { Link } from "react-router-dom";

// The Terminal/Chat view toggle for one session id (issue #811). Both views drive the SAME session;
// only the display differs - Terminal shows the raw PTY mirror (#817), Chat shows the cleaned
// conversation history (the desktop History tab translated). A segmented two-tab control that links
// between the session's /session/:id (Terminal) and /session/:id/chat (Chat) routes.

export type SessionView = "terminal" | "chat";

export interface ViewTabsProps {
  sessionId: string | undefined;
  active: SessionView;
}

export function ViewTabs({ sessionId, active }: ViewTabsProps) {
  const sid = encodeURIComponent(sessionId ?? "");
  return (
    <div className="view-tabs" role="tablist" aria-label="Session view">
      <Link
        className={`view-tab${active === "terminal" ? " active" : ""}`}
        role="tab"
        aria-selected={active === "terminal"}
        to={`/session/${sid}`}
      >
        Terminal
      </Link>
      <Link
        className={`view-tab${active === "chat" ? " active" : ""}`}
        role="tab"
        aria-selected={active === "chat"}
        to={`/session/${sid}/chat`}
      >
        Chat
      </Link>
    </div>
  );
}
