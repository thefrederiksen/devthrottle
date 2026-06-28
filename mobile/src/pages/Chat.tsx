import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router-dom";
import {
  getExplain,
  getSummary,
  getTurns,
  listSessions,
  type SessionExplain,
  type SessionSummary,
  type TurnWidget,
} from "../api/client";
import { SessionControls } from "../session/SessionControls";
import { ViewTabs } from "../session/ViewTabs";

// Session Chat mode (Issue #811). Renders the CLEANED conversation history instead of the raw
// terminal: GET /sessions/{sid}/turns gives the chronological widgets (assistant Text, user
// UserMessage, Thinking, tool actions); GET /summary drives a compact context strip; GET
// /wingman/explain supplies a "latest, explained" card when the Director has one cached. The
// transcript polls so a freshly completed turn appears without a manual reload. The write controls
// are the SAME shared SessionControls the Terminal view uses (Send / type / Enter / Esc / Stop /
// arrows), so the two views are interchangeable; here Speak reads the latest assistant reply
// aloud. A ViewTabs toggle switches between Chat and Terminal for the same session.
const POLL_INTERVAL_MS = 2000;

// Widget kinds that are the user's own input; everything else is assistant-side (Claude).
const USER_KIND = "UserMessage";
const ASSISTANT_TEXT_KIND = "Text";
const THINKING_KIND = "Thinking";

// The latest assistant reply for Speak: the most recent assistant Text widget, falling back to the
// summary's last assistant text, then the explained text. Empty string if nothing is available.
function latestAssistantReply(
  widgets: TurnWidget[],
  summary: SessionSummary | null,
  explain: SessionExplain | null,
): string {
  for (let i = widgets.length - 1; i >= 0; i--) {
    if (widgets[i].kind === ASSISTANT_TEXT_KIND && widgets[i].content.trim().length > 0) {
      return widgets[i].content;
    }
  }
  if (summary?.lastAssistantText && summary.lastAssistantText.trim().length > 0) {
    return summary.lastAssistantText;
  }
  return explain?.text ?? "";
}

export function Chat() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const transcriptRef = useRef<HTMLDivElement | null>(null);

  const [name, setName] = useState<string | null>(null);
  const [widgets, setWidgets] = useState<TurnWidget[]>([]);
  const [summary, setSummary] = useState<SessionSummary | null>(null);
  const [explain, setExplain] = useState<SessionExplain | null>(null);
  const [turnsStatus, setTurnsStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const loadedOnce = useRef(false);

  // One-shot header label (best-effort, like the Terminal view).
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

  // Poll the cleaned history. /turns is the substance (its errors surface in the banner); /summary
  // and /wingman/explain are additive context, so a transient failure of either leaves its section
  // empty rather than blanking the transcript.
  useEffect(() => {
    if (!sessionId) return;
    const controller = new AbortController();

    const load = async () => {
      try {
        const turns = await getTurns(sessionId, controller.signal);
        if (controller.signal.aborted) return;
        setWidgets(turns.widgets);
        setTurnsStatus(turns.status);
        setError(turns.status === "ok" || turns.widgets.length > 0 ? null : turns.error);
        loadedOnce.current = true;
      } catch (err) {
        if (controller.signal.aborted) return;
        setError(err instanceof Error ? err.message : "Failed to load history");
      }
      try {
        const s = await getSummary(sessionId, controller.signal);
        if (!controller.signal.aborted) setSummary(s);
      } catch { /* additive context; leave the strip as-is */ }
      try {
        const e = await getExplain(sessionId, controller.signal);
        if (!controller.signal.aborted) setExplain(e);
      } catch { /* additive context; leave the card hidden */ }
    };

    void load();
    const timer = window.setInterval(() => { void load(); }, POLL_INTERVAL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [sessionId]);

  // Keep the newest turn in view as the transcript grows, unless the user has scrolled up to read
  // back (then leave their position alone).
  const widgetCount = widgets.length;
  useEffect(() => {
    const el = transcriptRef.current;
    if (el === null) return;
    const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 120;
    if (nearBottom) el.scrollTop = el.scrollHeight;
  }, [widgetCount]);

  const getSpeakText = useCallback(
    async () => latestAssistantReply(widgets, summary, explain),
    [widgets, summary, explain],
  );

  const explainHeadline = explain?.headline?.trim() || null;
  const explainBody = (explain?.whatHappened?.trim() || explain?.text?.trim()) ?? null;
  const openTodos = summary?.openTodos ?? [];

  return (
    <div className="terminal-screen">
      <header className="app-bar">
        <Link className="back-link" to="/">&larr; Roster</Link>
        <h1 className="term-title">{name ?? "Session"}</h1>
        <span className="app-bar-sub">Chat</span>
      </header>

      {sessionId && <ViewTabs sessionId={sessionId} active="chat" />}

      <code className="term-sid" title="session id (carried in the route)">{sessionId}</code>

      {error !== null && <div className="banner banner-error" role="alert">{error}</div>}

      <div className="chat-transcript" ref={transcriptRef}>
        {explainHeadline !== null && (
          <div className="chat-explain">
            <div className="chat-explain-label">Latest, explained</div>
            <div className="chat-explain-headline">{explainHeadline}</div>
            {explainBody !== null && <div className="chat-explain-body">{explainBody}</div>}
          </div>
        )}

        {(summary?.activityState || openTodos.length > 0) && (
          <div className="chat-summary">
            {summary?.activityState && (
              <span className="chat-summary-state">{summary.activityState}</span>
            )}
            {openTodos.length > 0 && (
              <span className="chat-summary-todos">
                {openTodos.length} open todo{openTodos.length === 1 ? "" : "s"}
              </span>
            )}
          </div>
        )}

        {!loadedOnce.current && widgets.length === 0 && error === null && (
          <p className="status-line">Loading history...</p>
        )}

        {loadedOnce.current && widgets.length === 0 && error === null && (
          <p className="status-line">
            {turnsStatus === "no_session_id" || turnsStatus === "no_jsonl"
              ? "No conversation yet for this session."
              : "No turns to show."}
          </p>
        )}

        {widgets.map((w, i) => (
          <TranscriptItem key={`${i}-${w.toolUseId || w.kind}`} widget={w} />
        ))}
      </div>

      {sessionId && (
        <SessionControls sessionId={sessionId} onError={setError} getSpeakText={getSpeakText} />
      )}
    </div>
  );
}

// One transcript row. User input and Claude's text replies are full chat bubbles (left / right);
// Thinking is a muted aside; tool uses render as a compact action chip so the transcript reads as
// conversation, not a raw tool log.
function TranscriptItem({ widget }: { widget: TurnWidget }) {
  if (widget.kind === USER_KIND) {
    return (
      <div className="chat-turn chat-user">
        <div className="chat-role">You</div>
        <div className="chat-bubble">{widget.content}</div>
      </div>
    );
  }
  if (widget.kind === ASSISTANT_TEXT_KIND) {
    return (
      <div className="chat-turn chat-assistant">
        <div className="chat-role">{widget.header || "Claude"}</div>
        <div className="chat-bubble">{widget.content}</div>
      </div>
    );
  }
  if (widget.kind === THINKING_KIND) {
    return (
      <div className="chat-turn chat-assistant">
        <div className="chat-thinking">{widget.content}</div>
      </div>
    );
  }
  // A tool action (Bash, Read, Edit, Grep, ...): compact chip, optionally flagged as an error.
  return (
    <div className="chat-turn chat-assistant">
      <div className={`chat-action${widget.isError ? " chat-action-error" : ""}`}>
        <span className="chat-action-kind">{widget.header || widget.kind}</span>
        {widget.subheader && <span className="chat-action-sub">{widget.subheader}</span>}
      </div>
    </div>
  );
}
