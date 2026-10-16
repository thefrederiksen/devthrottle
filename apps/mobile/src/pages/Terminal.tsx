import { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import "@xterm/xterm/css/xterm.css";
import { listSessions } from "@devthrottle/client-core/api/client";
import { TerminalMirror, type TerminalStreamStatus } from "@devthrottle/client-core/terminal/stream";
import { DictationStatusStrip } from "@devthrottle/client-core/dictation/DictationStatusStrip";
import { TerminalWheelButton } from "@devthrottle/client-core/sessions/TerminalWheelButton";
import { SessionControls } from "../components/SessionControls";
import { SessionAppBar } from "../components/SessionAppBar";
import { useSessionManage } from "../components/useSessionManage";
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
  const navigate = useNavigate();
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const hostRef = useRef<HTMLDivElement | null>(null);
  const mirrorRef = useRef<TerminalMirror | null>(null);

  // Local Files (Phase 3): a clicked FILE path in the mirror opens the full-screen file viewer for that
  // path. A stable callback so the TerminalMirror (constructed once per session) holds it across renders
  // without being torn down. http/https URLs are opened by the mirror itself and never reach here.
  const openFile = useCallback((path: string) => {
    const sid = encodeURIComponent(sessionId ?? "");
    // ?from= records THIS tab so the viewer's Back returns to Terminal (not a dead history.back()).
    const from = encodeURIComponent(`/session/${sessionId ?? ""}/terminal`);
    navigate(`/session/${sid}/file?path=${encodeURIComponent(path)}&from=${from}`);
  }, [navigate, sessionId]);

  const [name, setName] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const manage = useSessionManage(sessionId);
  const [status, setStatus] = useState<string>(STATUS_BASE);
  const [showKeys, setShowKeys] = useState(false); // controls hidden by default (Android parity)
  const [fitLabel, setFitLabel] = useState<"Fit" | "1:1">("1:1");
  // The live-stream connection state (mobile-resilience Phase 3). While it is not "live" a small note
  // shows over the terminal that it is (re)connecting - the last-known screen stays visible underneath,
  // never blanked, and the note is the only sign of trouble.
  const [streamStatus, setStreamStatus] = useState<TerminalStreamStatus>("connecting");

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
    const mirror = new TerminalMirror(wrapRef.current, hostRef.current, sessionId, setFitLabel, openFile, setStreamStatus);
    mirrorRef.current = mirror;
    mirror.start();
    return () => { mirror.dispose(); mirrorRef.current = null; };
  }, [sessionId, openFile]);

  // The screen is kept awake app-wide by the gated layout's useScreenWakeLock (issue #981), which
  // covers the Terminal view too - so this page no longer holds its own separate wake lock.

  // Flash an action result in the status line, then settle back to the base "read-only" label.
  const flash = useCallback((message: string) => {
    setStatus(message);
    window.setTimeout(() => setStatus((cur) => (cur === message ? STATUS_BASE : cur)), 1500);
  }, []);

  return (
    <div className="terminal-screen">
      {/* Snooze is in the overflow here: this screen's bottom belongs to the composer and the key
          rows, so there is no thumb-zone room for it (Voice mode, which has room, keeps it one tap). */}
      <SessionAppBar title={name ?? "Session"} manage={manage} showSnooze showSwitchToVoice />

      <ViewTabs sessionId={sessionId} active="terminal" />

      <div className="term-statusbar">
        <span className="term-status" role="status">{status}</span>
        {/* The Gateway's wheel verdict for an API-mode session; renders nothing when the verdict has no button. */}
        <TerminalWheelButton session={manage.session} onChanged={manage.refreshNow} />
        <button type="button" className="term-keys-toggle" onClick={() => setShowKeys((v) => !v)}>
          {showKeys ? "Hide keys" : "Keys"}
        </button>
      </div>

      {error !== null && <div className="banner banner-error" role="alert">{error}</div>}

      {/* Live dictation status - always visible (even with the Keys panel hidden) so a Speak Send is
          never silent: progress while it runs, a saved "still sending" strip with Upload now if the
          connection is bad, and a sticky failure only if it truly could not be saved (#1182). */}
      <DictationStatusStrip sessionId={sessionId} />

      {/* The terminal fills all remaining space. .term-wrap is the only scroll container (vertical
          scrollback + horizontal pan); the Fit and A-/A+ controls float over its corners. */}
      <div className="term-stage">
        {/* Mobile-resilience Phase 3: while the live stream is down, a small note floats over the top of
            the terminal. The last-known screen stays visible underneath (never blanked); this note is the
            only sign the mirror is (re)connecting, and it clears itself the instant the socket reopens. */}
        {streamStatus !== "live" && (
          <div className="term-reconnecting" role="status">
            {streamStatus === "connecting" ? "Connecting to the live terminal..." : "Reconnecting to the live terminal..."}
          </div>
        )}
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
