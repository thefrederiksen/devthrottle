import { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate, useOutletContext, useParams } from "react-router-dom";
import { getQueue, type QueueItem, type SessionDto } from "@devthrottle/client-core/api/client";
import { modelChipOf } from "@devthrottle/client-core/sessions/model";
import { TerminalPane } from "../panes/TerminalPane";
import type { SessionsOutletContext } from "./SessionsView";
import { SessionActionBar } from "./SessionActionBar";
import { SessionComposer } from "./SessionComposer";
import { SessionMenu } from "./SessionMenu";
import { ChatTab } from "./ChatTab";
import { VoiceTab } from "./VoiceTab";
import { SourceControlTab } from "./SourceControlTab";
import { QueuePanel } from "./QueuePanel";
import { ScreenshotsPanel } from "./ScreenshotsPanel";
import { appendToCompose } from "./composerInsert";
import { promptDeliveryHistory, promptDeliveryNotice } from "@devthrottle/client-core/sessions/delivery";
import { VerdictPanel } from "@devthrottle/client-core/sessions/VerdictPanel";
import { WingmanTab } from "@devthrottle/client-core/sessions/WingmanTab";

// The selected session's detail region (issue #972): the live terminal (issue #971's TerminalPane,
// reused verbatim) stacked over the driver action bar and the composer, with a tabbed dock for the
// prompt queue and the screenshots gallery. This is the "answer it" half of the driving loop - it is
// mounted only on /session/{sid}, so selecting a different session remounts it (the terminal engine,
// the composer text, and the queue are all per-session).

type DockTab = "queue" | "shots";
// The session-main view (issues #1213, #1266): Terminal, Chat, Voice, Source Control. Terminal is the
// live PTY mirror (issue #971); Chat is the cleaned conversation history and Voice is the hands-free
// narration - both ported from the mobile pages through the shared client-core code, not rewritten.
// Source Control is the read-only repository view (issue #1266) - click a file to insert its path into
// the composer. Wingman is every stop the Wingman judged for this session (the Wingman inspector) - the shared
// client-core tab, mounted by the Cockpit only.
type MainTab = "terminal" | "chat" | "voice" | "sourceControl" | "wingman";

export function SessionDetail() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const navigate = useNavigate();
  const { sessions } = useOutletContext<SessionsOutletContext>();
  const selected = sessions?.find((s) => s.sessionId === sessionId);

  const [compose, setCompose] = useState("");
  const [queue, setQueue] = useState<QueueItem[]>([]);
  const [tab, setTab] = useState<DockTab>("queue");
  const [mainTab, setMainTab] = useState<MainTab>("terminal");
  // Set by the composer to a function that focuses its textarea, so the Source Control tab can focus the
  // composer after inserting a clicked file's path (issue #1266).
  const composerFocusRef = useRef<(() => void) | null>(null);

  // Load the queue for the selected session (and reset the composer) whenever the session changes, so
  // the composer and the queue never carry over from a previously-selected session.
  useEffect(() => {
    if (!sessionId) return;
    const controller = new AbortController();
    setCompose("");
    setQueue([]);
    getQueue(sessionId, controller.signal)
      .then(setQueue)
      .catch(() => {
        /* the queue panel and composer surface their own action errors; an empty initial load is fine */
      });
    return () => controller.abort();
  }, [sessionId]);

  // Append text (a popped queue item, or a screenshot path) into the composer, separated by a space.
  const appendCompose = useCallback((text: string) => {
    setCompose((cur) => appendToCompose(cur, text));
  }, []);

  // The Source Control tab's click-a-file: insert the repository-relative path AND focus the composer,
  // so the reader can immediately type the instruction that goes with the file (issue #1266).
  const insertPathAndFocus = useCallback(
    (path: string) => {
      appendCompose(path);
      composerFocusRef.current?.();
    },
    [appendCompose],
  );

  return (
    <div className="session-detail">
      <div className="session-main">
        <div className="session-tabs" role="tablist" aria-label="Session view">
          <button
            type="button"
            role="tab"
            aria-selected={mainTab === "terminal"}
            className={`session-tab ${mainTab === "terminal" ? "on" : ""}`}
            onClick={() => setMainTab("terminal")}
          >
            Terminal
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={mainTab === "chat"}
            className={`session-tab ${mainTab === "chat" ? "on" : ""}`}
            onClick={() => setMainTab("chat")}
          >
            Chat
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={mainTab === "voice"}
            className={`session-tab ${mainTab === "voice" ? "on" : ""}`}
            onClick={() => setMainTab("voice")}
          >
            Voice
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={mainTab === "sourceControl"}
            className={`session-tab ${mainTab === "sourceControl" ? "on" : ""}`}
            onClick={() => setMainTab("sourceControl")}
          >
            Source Control
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={mainTab === "wingman"}
            className={`session-tab ${mainTab === "wingman" ? "on" : ""}`}
            onClick={() => setMainTab("wingman")}
          >
            Wingman
          </button>
          {/* Which agent and which MODEL the open session is running (issue devthrottle_internal#1340).
              The roster says it for every row; this says it for the session actually on screen, so the
              answer is on the surface you are looking at rather than one click away. Both the words and
              the tooltip are the Gateway's fold, read through the same shared reader the roster and the
              Fleet Map use. */}
          {selected && <SessionModelChip session={selected} />}
          {/* The session menu (issue #1214): Rename, Hold/Resume, Handover info, Close - top right of
              the session header, driving the shared Gateway calls. */}
          {selected && <SessionMenu session={selected} variant="page" onClosed={() => navigate("/sessions")} />}
        </div>

        {/* What the Wingman read at this stop, and the owner's answer to it - the shared client-core panel.
            THIS SHELL DECIDES NOTHING ABOUT WHAT IT SHOWS: it hands over the selected row and this route's
            session id, and the panel owns whether there is anything to show and what is live on it. */}
        {selected && sessionId && <VerdictPanel sessionId={sessionId} session={selected} />}

        <div className="session-content">
          {/* The terminal is ALWAYS mounted (hidden, not unmounted, when Chat or Voice is active) so its
              live WebSocket is never torn down on a tab switch (issue #1213). */}
          <div className={`session-pane ${mainTab === "terminal" ? "" : "session-pane-off"}`}>
            <TerminalPane />
          </div>
          {mainTab === "chat" && (
            <div className="session-pane">
              <ChatTab sessionId={sessionId} />
            </div>
          )}
          {mainTab === "voice" && (
            <div className="session-pane">
              <VoiceTab sessionId={sessionId} />
            </div>
          )}
          {mainTab === "sourceControl" && (
            <div className="session-pane">
              <SourceControlTab sessionId={sessionId} onInsertPath={insertPathAndFocus} />
            </div>
          )}
          {mainTab === "wingman" && sessionId && (
            <div className="session-pane">
              <WingmanTab sessionId={sessionId} />
            </div>
          )}
        </div>

        {/* A prompt to this session was not delivered (issue internal#811). It sits directly above the
            composer - the place the words were typed and the place the next attempt will be made - and it
            stays until something actually lands. The sentence is the Gateway's, rendered verbatim. */}
        {selected && promptDeliveryNotice(selected) !== null && (
          <div className="delivery-failure-banner" role="alert">
            <span className="delivery-failure-title">{promptDeliveryNotice(selected)}</span>
            {promptDeliveryHistory(selected) !== null && (
              <span className="delivery-failure-history">{promptDeliveryHistory(selected)}</span>
            )}
          </div>
        )}
        <SessionActionBar sessionId={sessionId} capabilities={selected?.driverCapabilities} />
        <SessionComposer
          sessionId={sessionId}
          value={compose}
          onChange={setCompose}
          onQueued={setQueue}
          focusHandleRef={composerFocusRef}
        />
      </div>

      <aside className="session-dock">
        <div className="dock-tabs">
          <button type="button" className={`dock-tab ${tab === "queue" ? "on" : ""}`} onClick={() => setTab("queue")}>
            Queue{queue.length > 0 ? ` (${queue.length})` : ""}
          </button>
          <button type="button" className={`dock-tab ${tab === "shots" ? "on" : ""}`} onClick={() => setTab("shots")}>
            Screenshots
          </button>
        </div>
        <div className="dock-body">
          {tab === "queue" ? (
            <QueuePanel sessionId={sessionId} queue={queue} onQueue={setQueue} onPop={appendCompose} />
          ) : (
            <ScreenshotsPanel sessionId={sessionId} onInsert={appendCompose} />
          )}
        </div>
      </aside>
    </div>
  );
}

/**
 * The agent + model chip in the session header (issue devthrottle_internal#1340).
 *
 * The agent name comes from the session; every word about the MODEL - including which of the two absences
 * applies when there is none - is the Gateway's fold, read verbatim through the shared reader. Renders
 * nothing at all when the Gateway stamped no verdict (an older Gateway), because a placeholder invented
 * here would be this client ruling in the Gateway's place.
 */
function SessionModelChip({ session }: { session: SessionDto }) {
  const model = modelChipOf(session);
  if (model === null) return null;
  const agent = (session.agent ?? "").trim();
  return (
    <span className={model.absent ? "session-model absent" : "session-model"} title={model.title}>
      {agent.length > 0 && <span className="session-model-agent">{agent}</span>}
      {model.text}
    </span>
  );
}
