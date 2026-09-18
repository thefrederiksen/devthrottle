import { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate, useOutletContext, useParams, useSearchParams } from "react-router-dom";
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
import { ReportsTab } from "./ReportsTab";
import { QueuePanel } from "./QueuePanel";
import { ScreenshotsPanel } from "./ScreenshotsPanel";
import { appendToCompose } from "./composerInsert";
import { promptDeliveryHistory, promptDeliveryNotice } from "@devthrottle/client-core/sessions/delivery";
import { WingmanTab } from "@devthrottle/client-core/sessions/WingmanTab";
import { sendingButtonFor, wingmanNowActions } from "./wingmanNowActions";
import { useStopSession } from "./StopSessionProvider";
import { Chevron } from "../components";

// The selected session's detail region (issue #972): the live terminal (issue #971's TerminalPane,
// reused verbatim) stacked over the driver action bar and the composer, with a tabbed dock for the
// prompt queue and the screenshots gallery. This is the "answer it" half of the driving loop - it is
// mounted only on /session/{sid}, so selecting a different session remounts it (the terminal engine,
// the composer text, and the queue are all per-session).

type DockTab = "queue" | "shots";

// THE DOCK COLLAPSES (issue #3074). Its 300 pixels are worth having on the Terminal, where the queue is part
// of driving; they are 300 pixels of "No queued prompts." next to a dev report that has been squeezed into
// half the screen. Collapsed it is a thin strip with the same two tabs' worth of room behind one control -
// the dock is never GONE, because a queue you cannot see and cannot reach is a queue you forget.
//
// Remembered per browser, like the rail and the roster's ordering.
const DOCK_STORAGE_KEY = "cockpit.dockCollapsed";

function initialDockCollapsed(): boolean {
  try {
    return window.localStorage.getItem(DOCK_STORAGE_KEY) === "true";
  } catch {
    return false;
  }
}
// The session-main view (issues #1213, #1266): Terminal, Chat, Voice, Source Control. Terminal is the
// live PTY mirror (issue #971); Chat is the cleaned conversation history and Voice is the hands-free
// narration - both ported from the mobile pages through the shared client-core code, not rewritten.
// Source Control is the read-only repository view (issue #1266) - click a file to insert its path into
// the composer. Wingman is every stop the Wingman judged for this session (the Wingman inspector) - the shared
// client-core tab, mounted by the Cockpit only.
// Reports is the session's dev reports (dev reports mission, phase 3): the list, and an open report with its
// conversation beside it, from the shared client-core view the phone also mounts.
type MainTab = "terminal" | "chat" | "voice" | "sourceControl" | "wingman" | "reports";

// THE ADDRESS HOLDS THE TAB AND THE OPEN REPORT (dev reports mission, phase 3b).
// These used to be component state, so `/session/{sid}?tab=reports&report={rid}` could not reach them and a
// link to a report landed on the terminal. They are read from the address instead: a deep link arrives with
// the tab already chosen and the report already open, and every switch writes the address back so it never
// describes a screen that is not the one you are looking at. Terminal is the default and is left OUT of the
// address, so an ordinary session link stays `/session/{sid}`.
const MAIN_TABS: readonly MainTab[] = ["terminal", "chat", "voice", "sourceControl", "wingman", "reports"];
const DEFAULT_MAIN_TAB: MainTab = "terminal";

function tabFromAddress(raw: string | null): MainTab {
  return MAIN_TABS.includes(raw as MainTab) ? (raw as MainTab) : DEFAULT_MAIN_TAB;
}

export function SessionDetail() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const navigate = useNavigate();
  const { sessions } = useOutletContext<SessionsOutletContext>();
  const selected = sessions?.find((s) => s.sessionId === sessionId);
  const { openStop } = useStopSession();

  const [compose, setCompose] = useState("");
  const [queue, setQueue] = useState<QueueItem[]>([]);
  const [tab, setTab] = useState<DockTab>("queue");
  const [dockCollapsed, setDockCollapsed] = useState(initialDockCollapsed);
  const [searchParams, setSearchParams] = useSearchParams();
  const mainTab = tabFromAddress(searchParams.get("tab"));
  // Only the Reports tab has an open report, so `report=` outside it means nothing and is not read.
  const openReportId = mainTab === "reports" ? searchParams.get("report") || null : null;

  // Switching tab REPLACES the address (a tab is not a place you go back to) and drops any open report,
  // because a report is only open on the Reports tab.
  const setMainTab = useCallback(
    (next: MainTab) => {
      setSearchParams(
        (current) => {
          const params = new URLSearchParams(current);
          if (next === DEFAULT_MAIN_TAB) params.delete("tab");
          else params.set("tab", next);
          if (next !== "reports") params.delete("report");
          return params;
        },
        { replace: true },
      );
    },
    [setSearchParams],
  );

  // Opening and closing a report PUSHES, so the browser's own Back closes the report the way the reader
  // expects - and either way the address says exactly what is on screen.
  const openReport = useCallback(
    (reportId: string) => {
      setSearchParams((current) => {
        const params = new URLSearchParams(current);
        params.set("tab", "reports");
        params.set("report", reportId);
        return params;
      });
    },
    [setSearchParams],
  );

  const closeReport = useCallback(() => {
    setSearchParams((current) => {
      const params = new URLSearchParams(current);
      params.set("tab", "reports");
      params.delete("report");
      return params;
    });
  }, [setSearchParams]);

  // The way back to this session from an open report: the session itself, on its default tab.
  const backToSession = useCallback(
    (reportSessionId: string) => navigate(`/session/${encodeURIComponent(reportSessionId)}`),
    [navigate],
  );

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

  // THE EMPTY QUEUE PANEL GIVES ITS WIDTH BACK, on this tab only (the review's item C3). Three hundred pixels of a
  // thousand said "No queued prompts." beside a screen that wanted the room; the Wingman tab is a page to read, not
  // a console with a dock. The moment anything is queued the panel returns, and every other tab keeps it always.
  const showDock = mainTab !== "wingman" || queue.length > 0;

  const toggleDock = useCallback(() => {
    setDockCollapsed((current) => {
      const next = !current;
      try {
        window.localStorage.setItem(DOCK_STORAGE_KEY, next ? "true" : "false");
      } catch {
        // Storage turned off: it still collapses, it just forgets on the next load.
      }
      return next;
    });
  }, []);

  return (
    <div
      className={`session-detail${showDock ? "" : " session-detail-wide"}${
        showDock && dockCollapsed ? " session-detail-dock-collapsed" : ""
      }`}
    >
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
          <button
            type="button"
            role="tab"
            aria-selected={mainTab === "reports"}
            className={`session-tab ${mainTab === "reports" ? "on" : ""}`}
            data-testid="session-tab-reports"
            onClick={() => setMainTab("reports")}
          >
            Reports
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
              {/* ONE STOP, DRAWN ONCE. The shared VerdictPanel used to mount above this tab, so once Now could read
                  the live stop the same stop appeared twice on one screen, each copy with its own answer buttons.
                  The approved mockup draws no panel above Now, and Now renders that stop with more room. The
                  component itself is untouched and still mounts wherever else it is used.
                  What Now can DO is wired in wingmanNowActions, because the shell owns the tabs and the router;
                  the tab owns what is worth showing. An action that is not passed is not drawn, so Now never
                  offers a control that would do nothing - which is why "Why this colour?" and the rating thumbs
                  are still absent: Debug and ratings are not built yet. */}
              <WingmanTab
                sessionId={sessionId}
                actions={(now) =>
                  wingmanNowActions(sessionId, selected, now.state, {
                    openTerminal: () => setMainTab("terminal"),
                    goToSession: (id) => navigate(`/session/${id}`),
                    openSettings: () => navigate("/settings"),
                    openStop: () => selected && openStop(selected, () => navigate("/sessions")),
                  })
                }
                /* THE ONE MESSAGE BOX ON THIS TAB (the review's item B1). It is the page's own composer, moved
                   inside the card beside the question rather than copied - Send, Speak, Queue and Attach are the
                   same controls doing the same things, and the page's copy at the bottom is hidden below while
                   this tab is showing. The words in the empty box are the Gateway's, handed in by the view. */
                replyBox={(placeholder, nowState) => (
                  <SessionComposer
                    sessionId={sessionId}
                    value={compose}
                    onChange={setCompose}
                    onQueued={setQueue}
                    placeholder={placeholder}
                    /* ONE SENDING BUTTON PER STATE (the review's item N3). A stopped session gets Send; a working
                       one gets the single button the words in the box already describe. Send beside Queue, with a
                       box saying a message "is queued", was two buttons and no way to tell what either would do. */
                    sending={sendingButtonFor(nowState)}
                  />
                )}
              />
            </div>
          )}
          {mainTab === "reports" && (
            <div className="session-pane">
              <ReportsTab
                sessionId={sessionId}
                openReportId={openReportId}
                onOpenReport={openReport}
                onCloseReport={closeReport}
                onBackToSession={backToSession}
              />
            </div>
          )}
        </div>

        {/* A prompt to this session was not delivered (issue internal#811). It stays until something actually
            lands, on EVERY tab - a prompt that never reached the session is worth saying wherever he is standing,
            and the next attempt is the box below on most tabs and the one inside the card on the Wingman tab.
            The sentence is the Gateway's, rendered verbatim. */}
        {selected && promptDeliveryNotice(selected) !== null && (
          <div className="delivery-failure-banner" role="alert">
            <span className="delivery-failure-title">{promptDeliveryNotice(selected)}</span>
            {promptDeliveryHistory(selected) !== null && (
              <span className="delivery-failure-history">{promptDeliveryHistory(selected)}</span>
            )}
          </div>
        )}
        {/* NEITHER OF THESE BELONGS ON THE WINGMAN TAB (the review's items B7 and B1).
            The driver bar puts Stop, Interrupt, Compact, Clear context and History directly under the place the
            owner clicks his answers - the design kept destructive controls off Now on purpose, and they arrived
            here by the back door, on every state including the ones where none of them means anything.
            The composer is the second message box: the tab already has one, inside the card, beside the question.
            Every other tab keeps both, unchanged. */}
        {mainTab !== "wingman" && (
          <>
            <SessionActionBar sessionId={sessionId} capabilities={selected?.driverCapabilities} />
            <SessionComposer
              sessionId={sessionId}
              value={compose}
              onChange={setCompose}
              onQueued={setQueue}
              focusHandleRef={composerFocusRef}
            />
          </>
        )}
      </div>

      {showDock && (
      <aside className={dockCollapsed ? "session-dock session-dock-collapsed" : "session-dock"}>
        <div className="dock-tabs">
          {/* Collapsed, the strip carries ONE control and it is this one - and the queue count rides on it,
              so nothing arrives in a queue the reader has hidden without the strip saying so. */}
          <button
            type="button"
            className="dock-collapse"
            data-testid="dock-collapse"
            aria-expanded={!dockCollapsed}
            aria-label={dockCollapsed ? "Expand the queue panel" : "Collapse the queue panel"}
            title={dockCollapsed ? "Expand the queue panel" : "Collapse the queue panel"}
            onClick={toggleDock}
          >
            <Chevron pointing={dockCollapsed ? "left" : "right"} />
            {dockCollapsed && queue.length > 0 && <span className="dock-collapsed-count">{queue.length}</span>}
          </button>
          {!dockCollapsed && (
            <>
              <button type="button" className={`dock-tab ${tab === "queue" ? "on" : ""}`} onClick={() => setTab("queue")}>
                Queue{queue.length > 0 ? ` (${queue.length})` : ""}
              </button>
              <button type="button" className={`dock-tab ${tab === "shots" ? "on" : ""}`} onClick={() => setTab("shots")}>
                Screenshots
              </button>
            </>
          )}
        </div>
        {!dockCollapsed && (
          <div className="dock-body">
            {tab === "queue" ? (
              <QueuePanel sessionId={sessionId} queue={queue} onQueue={setQueue} onPop={appendCompose} />
            ) : (
              <ScreenshotsPanel sessionId={sessionId} onInsert={appendCompose} />
            )}
          </div>
        )}
      </aside>
      )}
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
