import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router-dom";
import "@xterm/xterm/css/xterm.css";
import { listSessions } from "../api/client";
import { TerminalMirror } from "../terminal/stream";
import { SessionControls } from "../components/SessionControls";
import { ViewTabs } from "../components/ViewTabs";

// Session Terminal mode (issue #817): a faithful 1:1 translation of the Android (MAUI) app's
// Terminal tab into the React PWA. NOT a redesign - it replicates TalkPage.xaml's terminal section
// and RawTerminalPage.cs:
//
//   * A LIVE WebSocket mirror (TerminalMirror) of the session's raw PTY bytes - not 1s polling -
//     with fit-width/1:1, A-/A+ and pinch zoom, sticky-bottom auto-scroll, ~1200ms reconnect.
//   * Controls HIDDEN by default behind a Keys / Hide keys toggle, so the terminal fills the
//     screen (matching the Android tab); toggling reveals the input row and the key rows.
//   * The exact Android control set, now shared with the Chat view (#811) via SessionControls:
//     the input row (input + Speak + Send), Enter/Esc/Stop, and the four arrows, with the same
//     payloads. Speak is DICTATION (the shared DictationDialog), never text-to-speech.
//   * A Terminal/Chat toggle (ViewTabs, #811) switches to the cleaned-history Chat view of the
//     same session.
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
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string>(STATUS_BASE);
  const [showKeys, setShowKeys] = useState(false); // controls hidden by default (Android parity)
  const [fitLabel, setFitLabel] = useState<"Fit" | "1:1">("1:1");

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

  return (
    <div className="terminal-screen">
      <header className="app-bar">
        <Link className="back-link" to="/">&larr; Roster</Link>
        <h1 className="term-title">{name ?? "Session"}</h1>
        <ViewTabs sessionId={sessionId} active="terminal" />
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
          toggles the whole shared control panel (input row + Speak + Send + key rows). */}
      {showKeys && (
        <SessionControls sessionId={sessionId} onFlash={flash} onError={setError} showKeyRows />
      )}
    </div>
  );
}
