import { useEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { Terminal as Xterm } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import "@xterm/xterm/css/xterm.css";
import { getBuffer, getCleanTail, listSessions } from "../api/client";
import { SessionControls } from "../session/SessionControls";
import { ViewTabs } from "../session/ViewTabs";

// Session Terminal mode (Issue #810, the V1 hero). A live read-only mirror of the session's
// terminal (polling GET /sessions/{sid}/buffer?raw=true&since=<cursor> and writing the new bytes
// into an xterm.js emulator so the screen stays coherent). The write controls (text box + Send,
// Enter / Esc / Stop / arrows, Speak) are the shared SessionControls, reused unchanged by the Chat
// view (#811); here Speak reads the cleaned tail of the raw buffer. A ViewTabs toggle switches
// between Terminal and Chat for the same session.
const POLL_INTERVAL_MS = 1000;
const SPEAK_TAIL_LINES = 30;

function delay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const id = window.setTimeout(resolve, ms);
    signal.addEventListener("abort", () => { window.clearTimeout(id); resolve(); }, { once: true });
  });
}

export function Terminal() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const hostRef = useRef<HTMLDivElement | null>(null);
  const termRef = useRef<Xterm | null>(null);
  const fitRef = useRef<FitAddon | null>(null);

  const [name, setName] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

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

  // The live mirror: create the xterm emulator and poll the raw buffer, advancing the cursor so
  // each poll fetches only the new output. xterm applies the byte stream in order, so a chunked
  // poll renders the same coherent screen a continuous stream would.
  useEffect(() => {
    if (!sessionId || hostRef.current === null) return;

    const term = new Xterm({
      fontFamily: '"Cascadia Mono", Consolas, "Courier New", monospace',
      fontSize: 12,
      lineHeight: 1.0,
      scrollback: 5000,
      cursorBlink: false,
      disableStdin: true, // read-only: writes go through the control buttons, never the canvas
      convertEol: false,
      theme: { background: "#0b1020", foreground: "#e6e9f2" },
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(hostRef.current);
    try { fit.fit(); } catch { /* sized on the next ResizeObserver pass */ }
    termRef.current = term;
    fitRef.current = fit;

    const resize = window.ResizeObserver
      ? new ResizeObserver(() => { try { fit.fit(); } catch { /* ignore transient zero-size */ } })
      : null;
    if (resize && hostRef.current) resize.observe(hostRef.current);

    const controller = new AbortController();
    let cursor = -1; // first poll dumps the whole retained buffer, then advances

    (async () => {
      while (!controller.signal.aborted) {
        try {
          const slice = await getBuffer(sessionId, cursor < 0 ? null : cursor, controller.signal);
          if (controller.signal.aborted) break;
          if (slice.text) term.write(slice.text);
          cursor = slice.newCursor;
          setError(null);
        } catch (err) {
          if (controller.signal.aborted) break;
          setError(err instanceof Error ? err.message : "Terminal poll failed");
        }
        await delay(POLL_INTERVAL_MS, controller.signal);
      }
    })();

    return () => {
      controller.abort();
      resize?.disconnect();
      term.dispose();
      termRef.current = null;
      fitRef.current = null;
    };
  }, [sessionId]);

  return (
    <div className="terminal-screen">
      <header className="app-bar">
        <Link className="back-link" to="/">&larr; Roster</Link>
        <h1 className="term-title">{name ?? "Session"}</h1>
        <span className="app-bar-sub">Terminal</span>
      </header>

      {sessionId && <ViewTabs sessionId={sessionId} active="terminal" />}

      <code className="term-sid" title="session id (carried in the route)">{sessionId}</code>

      {error !== null && <div className="banner banner-error" role="alert">{error}</div>}

      <div className="term-host" ref={hostRef} />

      {sessionId && (
        <SessionControls
          sessionId={sessionId}
          onError={setError}
          getSpeakText={() => getCleanTail(sessionId, SPEAK_TAIL_LINES)}
        />
      )}
    </div>
  );
}
