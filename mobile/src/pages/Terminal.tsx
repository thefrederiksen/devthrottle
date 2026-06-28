import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router-dom";
import "@xterm/xterm/css/xterm.css";
import {
  listSessions,
  sendEscape,
  sendInterrupt,
  sendPrompt,
} from "../api/client";
import { TerminalMirror } from "../terminal/stream";
import { DictationDialog } from "../dictation/DictationDialog";
import {
  KEY_ARROW_DOWN,
  KEY_ARROW_LEFT,
  KEY_ARROW_RIGHT,
  KEY_ARROW_UP,
  KEY_ENTER,
} from "../terminal/keys";

// Session Terminal mode (issue #817): a faithful 1:1 translation of the Android (MAUI) app's
// Terminal tab into the React PWA. NOT a redesign - it replicates TalkPage.xaml's terminal section
// and RawTerminalPage.cs:
//
//   * A LIVE WebSocket mirror (TerminalMirror) of the session's raw PTY bytes - not 1s polling -
//     with fit-width/1:1, A-/A+ and pinch zoom, sticky-bottom auto-scroll, ~1200ms reconnect.
//   * Controls HIDDEN by default behind a Keys / Hide keys toggle, so the terminal fills the
//     screen (matching the Android tab); toggling reveals the input row and the key rows.
//   * The exact Android button set, grouping, and payloads: row (input + Speak + Send),
//     row (Enter + Esc + Stop), row (Up + Down + Left + Right). Send -> POST /prompt AppendEnter
//     true (clears input, flashes "Sent"); Enter -> "\r" AppendEnter false; Esc -> POST /escape;
//     Stop -> POST /interrupt; arrows -> ESC[A/B/C/D via /prompt AppendEnter false.
//   * Speak is DICTATION (the shared DictationDialog), never text-to-speech.
//   * Keep-screen-on via the Screen Wake Lock API while this view is open (the Android tab sets
//     KeepScreenOn so the screen does not sleep while driving), released on leave.
//
// Every call carries the Bearer token (and the stream the cc-gateway-token cookie), so the whole
// tab works with global Gateway auth on or off.

const STATUS_BASE = "Live terminal (read-only)";

export function Terminal() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const hostRef = useRef<HTMLDivElement | null>(null);
  const mirrorRef = useRef<TerminalMirror | null>(null);

  const [name, setName] = useState<string | null>(null);
  const [input, setInput] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string>(STATUS_BASE);
  const [showKeys, setShowKeys] = useState(false); // controls hidden by default (Android parity)
  const [fitLabel, setFitLabel] = useState<"Fit" | "1:1">("1:1");
  const [dictating, setDictating] = useState(false);

  // One-shot fetch of the session's display name for the header (the mirror itself never needs it).
  useEffect(() => {
    const controller = new AbortController();
    listSessions(controller.signal)
      .then((all) => {
        const match = all.find((s) => s.sessionId === sessionId) ?? null;
        if (match?.name && match.name.trim()) setName(match.name.trim());
      })
      .catch(() => { /* header label is best-effort; the mirror is the substance */ });
    return () => controller.abort();
  }, [sessionId]);

  // The live mirror: a real xterm.js terminal fed by the WebSocket session stream.
  useEffect(() => {
    if (!sessionId || wrapRef.current === null || hostRef.current === null) return;
    const mirror = new TerminalMirror(wrapRef.current, hostRef.current, sessionId, setFitLabel);
    mirrorRef.current = mirror;
    mirror.start();
    return () => { mirror.dispose(); mirrorRef.current = null; };
  }, [sessionId]);

  // Keep the screen awake while the Terminal view is open (Waze-style, matching the Android tab's
  // KeepScreenOn). Wake locks drop when the tab is hidden, so re-acquire on visibility return.
  useEffect(() => {
    type WakeLockSentinelLike = { release: () => Promise<void> };
    type WakeLockNavigator = Navigator & { wakeLock?: { request: (t: "screen") => Promise<WakeLockSentinelLike> } };
    const wl = (navigator as WakeLockNavigator).wakeLock;
    if (!wl) return; // not supported on this browser; nothing to do (no fallback)
    let sentinel: WakeLockSentinelLike | null = null;
    let released = false;
    const acquire = async () => {
      try { sentinel = await wl.request("screen"); } catch { /* denied/again on next visibility */ }
    };
    const onVisibility = () => { if (document.visibilityState === "visible" && !released) void acquire(); };
    void acquire();
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      released = true;
      document.removeEventListener("visibilitychange", onVisibility);
      if (sentinel) { void sentinel.release().catch(() => { /* already gone */ }); }
    };
  }, []);

  // Flash an action result in the status line, then settle back to the base "read-only" label.
  const flash = useCallback((message: string) => {
    setStatus(message);
    window.setTimeout(() => setStatus((cur) => (cur === message ? STATUS_BASE : cur)), 1500);
  }, []);

  const sendKey = useCallback(async (seq: string, label: string) => {
    if (!sessionId) return;
    try { await sendPrompt(sessionId, seq, false); flash("Sent"); }
    catch (err) { setError(err instanceof Error ? err.message : `${label} failed`); }
  }, [sessionId, flash]);

  const onSend = useCallback(async () => {
    if (!sessionId) return;
    const text = input;
    if (text.trim().length === 0) return;
    setInput(""); // clear immediately (Android clears the box before the call returns)
    try { await sendPrompt(sessionId, text, true); flash("Sent"); }
    catch (err) { setError(err instanceof Error ? err.message : "Send failed"); }
  }, [sessionId, input, flash]);

  const onEscape = useCallback(async () => {
    if (!sessionId) return;
    try { await sendEscape(sessionId); flash("Sent Esc"); }
    catch (err) { setError(err instanceof Error ? err.message : "Esc failed"); }
  }, [sessionId, flash]);

  const onStop = useCallback(async () => {
    if (!sessionId) return;
    try { await sendInterrupt(sessionId); flash("Sent Stop (Ctrl+C)"); }
    catch (err) { setError(err instanceof Error ? err.message : "Stop failed"); }
  }, [sessionId, flash]);

  // Speak opens the dictation dialog. Insert drops the transcript into the input (no submit, joined
  // onto any existing text); Send submits it via the same POST /prompt path the Send button uses.
  const onDictateInsert = useCallback((text: string) => {
    setDictating(false);
    if (text.trim().length === 0) return;
    setInput((cur) => (cur.trim().length === 0 ? text : `${cur.trimEnd()} ${text}`));
    flash("Inserted");
  }, [flash]);

  const onDictateSend = useCallback(async (text: string) => {
    setDictating(false);
    if (!sessionId || text.trim().length === 0) return;
    try { await sendPrompt(sessionId, text, true); flash("Sent"); }
    catch (err) { setError(err instanceof Error ? err.message : "Send failed"); }
  }, [sessionId, flash]);

  return (
    <div className="terminal-screen">
      <header className="app-bar">
        <Link className="back-link" to="/">&larr; Roster</Link>
        <h1 className="term-title">{name ?? "Session"}</h1>
        <span className="app-bar-sub">Terminal</span>
      </header>

      <div className="term-statusbar">
        <span className="term-status" role="status">{status}</span>
        <button type="button" className="term-keys-toggle" onClick={() => setShowKeys((v) => !v)}>
          {showKeys ? "Hide keys" : "Keys"}
        </button>
      </div>

      {error !== null && <div className="banner banner-error" role="alert">{error}</div>}

      {/* The terminal fills all remaining space. .term-wrap is the only scroll container (vertical
          scrollback + horizontal pan); the Fit and A-/A+ controls float over its corners. */}
      <div className="term-stage">
        <div className="term-wrap" ref={wrapRef}><div className="term-xterm" ref={hostRef} /></div>
        <div className="term-zoom">
          <button type="button" className="term-zoom-btn" aria-label="Zoom out"
                  onClick={() => mirrorRef.current?.zoomBy(1 / 1.2)}>A&minus;</button>
          <button type="button" className="term-zoom-btn" aria-label="Zoom in"
                  onClick={() => mirrorRef.current?.zoomBy(1.2)}>A+</button>
        </div>
        <button type="button" className="term-fit-btn" onClick={() => mirrorRef.current?.toggleFit()}>
          {fitLabel}
        </button>
      </div>

      {/* Collapsible controls: hidden by default so the terminal is maximized. The Keys button
          toggles this whole panel. Grouped exactly as the Android tab groups them. */}
      {showKeys && (
        <div className="term-controls">
          <div className="term-row term-row-input">
            <input
              className="term-input"
              type="text"
              inputMode="text"
              autoComplete="off"
              autoCapitalize="off"
              autoCorrect="off"
              spellCheck={false}
              placeholder="type / send..."
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); void onSend(); } }}
            />
            <button type="button" className="term-btn term-speak" onClick={() => setDictating(true)} disabled={dictating}>
              Speak
            </button>
            <button type="button" className="term-btn term-send" onClick={onSend}>
              Send
            </button>
          </div>

          <div className="term-row term-row-3">
            <button type="button" className="term-btn term-enter" onClick={() => sendKey(KEY_ENTER, "Enter")}>Enter</button>
            <button type="button" className="term-btn term-esc" onClick={onEscape}>Esc</button>
            <button type="button" className="term-btn term-stop" onClick={onStop}>Stop</button>
          </div>

          <div className="term-row term-row-4">
            <button type="button" className="term-btn term-arrow" onClick={() => sendKey(KEY_ARROW_UP, "Up")}>Up</button>
            <button type="button" className="term-btn term-arrow" onClick={() => sendKey(KEY_ARROW_DOWN, "Down")}>Down</button>
            <button type="button" className="term-btn term-arrow" onClick={() => sendKey(KEY_ARROW_LEFT, "Left")}>Left</button>
            <button type="button" className="term-btn term-arrow" onClick={() => sendKey(KEY_ARROW_RIGHT, "Right")}>Right</button>
          </div>
        </div>
      )}

      {dictating && (
        <DictationDialog
          onInsert={onDictateInsert}
          onSend={onDictateSend}
          onClose={() => setDictating(false)}
        />
      )}
    </div>
  );
}
