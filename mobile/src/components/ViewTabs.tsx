import { Link } from "react-router-dom";

// The Terminal/Chat/Voice mode view toggle for one session id (issue #811). All views drive the SAME
// session; only the display differs - Terminal shows the raw PTY mirror (#817), Chat shows the
// cleaned conversation history (the desktop History tab translated), Voice mode is a placeholder for
// the upcoming hands-free view. A segmented three-tab control that links between the session's
// /session/:id (Terminal), /session/:id/chat (Chat), and /session/:id/voice (Voice mode) routes. It
// renders as its own full-width row below the title so the three tabs share the width equally.

export type SessionView = "terminal" | "chat" | "voice";

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
      <Link
        className={`view-tab${active === "voice" ? " active" : ""}`}
        role="tab"
        aria-selected={active === "voice"}
        to={`/session/${sid}/voice`}
      >
        Voice mode
      </Link>
    </div>
  );
}
