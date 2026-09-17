import { useCallback, useLayoutEffect, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { sendPrompt } from "@devthrottle/client-core/api/client";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { useSessionChat } from "@devthrottle/client-core/history/useSessionChat";
import { usePollingStore } from "@devthrottle/client-core/polling/usePollingStore";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import {
  getFleetManagerPlacement,
  startFleetManager,
  type FleetManagerPlacement,
} from "@devthrottle/client-core/settings/fleetManagerClient";
import { Button } from "../components";
import { SessionComposer } from "../sessions/SessionComposer";
import { mergeConversation } from "./conversation";
import { FleetPanel } from "./FleetPanel";
import { OutcomeCard } from "./OutcomeCard";
import { fleetManagerPageStore } from "./pageStore";

// THE FLEET MANAGER PAGE (the Fleet Manager mission, step 6) - the page the Cockpit opens on.
//
// Three regions, each reading its own Gateway answer and each failing on its own with the Gateway's words:
//
//   the header        GET /gateway/fleet-manager/placement (step 5's answer: agent, computer, state line, and the
//                     not-running sentence with what can be done about it - never a silent start elsewhere)
//   the conversation  the marked Fleet Manager session's own history, through the Chat tab's reader, with each
//                     outcome card from GET /gateway/fleet-manager/page placed at the time it was filed
//   the right panel   GET /gateway/fleet-manager/page, polled like the session list
//
// The composer is the sessions' own (SessionComposer), so the microphone opens the same dictation window and the
// words reach the Fleet Manager exactly as spoken. A message sent while it is thinking queues behind its turn, as
// any prompt does.
//
// THE CLIENT IS DUMB (CLAUDE.md rule 7). Every sentence, label, count, tone and button here is the Gateway's, and so is
// every decision about what a state means: whether the composer and the quick prompts can be used, whether the
// not-running bar shows, and the thinking line all come from status.page (the placement answer). A card button is one
// Gateway call; the Gateway passes the answer to the Fleet Manager.
//
// The "Started ..." lines of the design are not drawn as small lines: the history carries no record that tells a
// start apart from any other reply, and guessing at the prose is exactly what this page must not do. They show as
// the ordinary replies they are. Nor does the history record how a message was entered (typed or dictated), so
// the owner's bubbles do not say.

const SURFACE = "cockpit-fleet-manager";
const PLACEMENT_REFRESH_MS = 8000;
const BOTTOM_THRESHOLD_PX = 40;

function usePlacement() {
  const [placement, setPlacement] = useState<FleetManagerPlacement | null>(null);
  const [error, setError] = useState<string | null>(null);
  const refresh = useCallback(async () => {
    try {
      setPlacement(await getFleetManagerPlacement());
      setError(null);
    } catch (err) {
      setError(describeAndReport(SURFACE, "read where the Fleet Manager runs", err));
    }
  }, []);
  useVisiblePolling(refresh, PLACEMENT_REFRESH_MS);
  return { placement, setPlacement, error, refresh };
}

export function FleetManagerView() {
  const page = usePollingStore(fleetManagerPageStore);
  // The session list's "Hand sessions to the Fleet Manager..." link opens the page with that list open (step 8).
  const [search] = useSearchParams();
  const openHandOver = search.get("handover") === "1";
  const { placement, setPlacement, error: placementError, refresh: refreshPlacement } = usePlacement();
  const status = placement?.status;
  const controls = status?.page;
  // The marked session, from the setting's answer; the page's answer carries the same mark.
  const sessionId = status?.sessionId ?? page.data?.fleetManagerSessionId ?? undefined;

  const chat = useSessionChat(sessionId ?? undefined);
  const items = mergeConversation(chat.bubbles, page.data?.cards ?? []);

  const [text, setText] = useState("");
  const [quickBusy, setQuickBusy] = useState<string | null>(null);
  const [quickError, setQuickError] = useState<string | null>(null);
  const [startBusy, setStartBusy] = useState(false);
  const [startError, setStartError] = useState<string | null>(null);

  const refreshAll = useCallback(() => {
    fleetManagerPageStore.refreshNow();
    void refreshPlacement();
  }, [refreshPlacement]);

  const sendQuick = useCallback(
    async (words: string) => {
      if (!sessionId) return;
      setQuickBusy(words);
      setQuickError(null);
      try {
        await sendPrompt(sessionId, words, true);
        refreshAll();
      } catch (err) {
        setQuickError(describeAndReport(SURFACE, "send that to the Fleet Manager", err));
      } finally {
        setQuickBusy(null);
      }
    },
    [sessionId, refreshAll],
  );

  const start = useCallback(async () => {
    setStartBusy(true);
    setStartError(null);
    try {
      setPlacement(await startFleetManager());
      fleetManagerPageStore.refreshNow();
    } catch (err) {
      setStartError(describeAndReport(SURFACE, "start the Fleet Manager", err));
    } finally {
      setStartBusy(false);
    }
  }, [setPlacement]);

  // Stick to the bottom of the conversation when the reader is already there.
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const atBottomRef = useRef(true);
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (el && atBottomRef.current) el.scrollTop = el.scrollHeight;
  }, [items.length, chat.bubbles, status?.thinking]);
  const onScroll = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < BOTTOM_THRESHOLD_PX;
  }, []);

  return (
    <div className="fmp-screen">
      <header className="fmp-head">
        <div className="fmp-head-text">
          <h1 className="fmp-title">Fleet Manager</h1>
          <div className="fmp-sub" data-testid="fmp-sub">
            {placement === null ? (
              placementError === null ? (
                <span role="status">Reading where the Fleet Manager runs...</span>
              ) : null
            ) : (
              <>
                {placement.status.page.where && <span>{placement.status.page.where} </span>}
                <Link className="fmp-change" to="/settings?tab=fleetmanager">
                  {placement.status.page.changeLabel}
                </Link>
                {" - "}
                <span className={`fmp-line fmp-line-${placement.status.tone}`}>{placement.status.line}</span>
              </>
            )}
            {placementError !== null && (
              <span className="fmp-inline-error" role="alert">
                {" "}
                {placementError}
              </span>
            )}
          </div>
        </div>
        <div className="fmp-head-actions">
          {(page.data?.quickPrompts ?? []).map((q) => (
            <Button key={q.label} disabled={!controls?.quickPromptsUsable || quickBusy !== null} onClick={() => void sendQuick(q.words)}>
              {quickBusy === q.words ? controls?.quickPromptBusyLabel : q.label}
            </Button>
          ))}
        </div>
      </header>
      {quickError !== null && (
        <div className="fmp-bar fmp-bar-bad" role="alert">
          {quickError}
        </div>
      )}

      {status?.replacement && (
        <div className={`fmp-bar fmp-bar-${status.replacementTone ?? "idle"}`} role="status" data-testid="fmp-replacement">
          <span className="fmp-bar-text">{status.replacement}</span>
        </div>
      )}

      {status !== undefined && status.page.notRunningBarShown && (
        <div className={`fmp-bar fmp-bar-${status.tone}`} data-testid="fmp-not-running">
          <span className="fmp-bar-text">{status.sentence}</span>
          <div className="fmp-bar-actions">
            {status.start.offered && (
              <Button variant="primary" disabled={startBusy} onClick={() => void start()}>
                {startBusy ? status.start.busyLabel : status.start.label}
              </Button>
            )}
            <Link className="ui-btn ui-btn-secondary" to="/settings?tab=fleetmanager">
              {status.page.settingsLabel}
            </Link>
          </div>
          {status.start.note && <div className="fmp-bar-note">{status.start.note}</div>}
          {startError !== null && (
            <div className="fmp-inline-error" role="alert">
              {startError}
            </div>
          )}
        </div>
      )}

      <div className="fmp-body">
        <div className="fmp-convo">
          <div className="fmp-scroll" ref={scrollRef} onScroll={onScroll} data-testid="fmp-conversation">
            {chat.staleNotice !== null && (
              <div className="fmp-notice" role="status">
                {chat.staleNotice}
              </div>
            )}
            {chat.loadError !== null && (
              <div className="fmp-region-error" role="alert">
                {chat.loadError}
              </div>
            )}
            {page.error !== null && (
              <div className="fmp-region-error" role="alert">
                {page.error}
              </div>
            )}
            {!sessionId && page.data?.noConversationText && <div className="fmp-empty">{page.data.noConversationText}</div>}
            {sessionId && chat.bubbles.length === 0 && chat.loadError === null && (
              <div className="fmp-empty" role="status">
                {chat.emptyText}
              </div>
            )}
            {page.data === null && page.loading && (
              <div className="fmp-loading" role="status">
                Loading the cards...
              </div>
            )}
            {items.map((item) =>
              item.kind === "card" ? (
                <div className="fmp-fm" key={item.key}>
                  <div className="fmp-av" aria-hidden="true">
                    FM
                  </div>
                  <div className="fmp-fmb">
                    <div className="fmp-who">{item.card.whoLine}</div>
                    <OutcomeCard card={item.card} onAnswered={refreshAll} />
                  </div>
                </div>
              ) : item.bubble.bubble.kind === "user" ? (
                <div className="fmp-me" key={item.key}>
                  <div className="chat-body md" dangerouslySetInnerHTML={{ __html: item.bubble.html }} />
                </div>
              ) : (
                <div className="fmp-fm" key={item.key}>
                  <div className="fmp-av" aria-hidden="true">
                    FM
                  </div>
                  <div className="fmp-fmb">
                    <div className="fmp-who">{item.bubble.bubble.kind === "tool" ? item.bubble.bubble.speaker : "Fleet Manager"}</div>
                    {item.bubble.bubble.isRawText ? (
                      <pre className="chat-body raw">{item.bubble.bubble.body}</pre>
                    ) : (
                      <div className="chat-body md" dangerouslySetInnerHTML={{ __html: item.bubble.html }} />
                    )}
                  </div>
                </div>
              ),
            )}
            {controls?.thinkingShown && status && (
              <div className="fmp-thinking" role="status">
                {status.line}
              </div>
            )}
          </div>

          <div className="fmp-composer">
            {controls?.composerUsable ? (
              <SessionComposer
                sessionId={sessionId}
                value={text}
                onChange={setText}
                onQueued={() => undefined}
                enterSends
                showQueueAndAttach={false}
                placeholder={controls.composerPlaceholder}
                onSent={refreshAll}
              />
            ) : (
              <div className="fmp-composer-off">{controls?.composerOffText ?? ""}</div>
            )}
            {controls && <div className="fmp-hint">{controls.composerHint}</div>}
          </div>
        </div>

        <FleetPanel state={page} openHandOver={openHandOver} onChanged={refreshAll} />
      </div>
    </div>
  );
}
