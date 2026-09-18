import { Link } from "react-router-dom";

// The Chat/Terminal/Voice mode view toggle for one session id (issue #811). All views drive the SAME
// session; only the display differs - Chat shows the cleaned conversation history (the desktop
// History tab translated) and is the DEFAULT view, Terminal shows the raw PTY mirror (#817), Voice
// mode is a placeholder for the upcoming hands-free view. A segmented three-tab control that links
// between the session's /session/:id (Chat, the default), /session/:id/terminal (Terminal), and
// /session/:id/voice (Voice mode) routes. It renders as its own full-width row below the title so the
// three tabs share the width equally.

// Reports (dev reports mission, phase 3) lists the session's dev reports; an open report is its own
// full-screen route under it, like the file viewer.
export type SessionView = "terminal" | "chat" | "voice" | "reports";

export interface ViewTabsProps {
  sessionId: string | undefined;
  active: SessionView;
}

export function ViewTabs({ sessionId, active }: ViewTabsProps) {
  const sid = encodeURIComponent(sessionId ?? "");
  return (
    <div className="view-tabs" role="tablist" aria-label="Session view">
      <Link
        className={`view-tab${active === "chat" ? " active" : ""}`}
        role="tab"
        aria-selected={active === "chat"}
        to={`/session/${sid}`}
      >
        Chat
      </Link>
      <Link
        className={`view-tab${active === "terminal" ? " active" : ""}`}
        role="tab"
        aria-selected={active === "terminal"}
        to={`/session/${sid}/terminal`}
      >
        Terminal
      </Link>
      <Link
        className={`view-tab${active === "voice" ? " active" : ""}`}
        role="tab"
        aria-selected={active === "voice"}
        to={`/session/${sid}/voice`}
      >
        Voice mode
      </Link>
      <Link
        className={`view-tab${active === "reports" ? " active" : ""}`}
        role="tab"
        aria-selected={active === "reports"}
        data-testid="session-tab-reports"
        to={`/session/${sid}/reports`}
      >
        Reports
      </Link>
    </div>
  );
}
