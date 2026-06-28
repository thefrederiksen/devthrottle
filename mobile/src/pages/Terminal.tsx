import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { Terminal as Xterm } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import "@xterm/xterm/css/xterm.css";
import {
  getBuffer,
  getCleanTail,
  listSessions,
  sendEscape,
  sendInterrupt,
  sendPrompt,
  synthesizeSpeech,
} from "../api/client";
import {
  KEY_ARROW_DOWN,
  KEY_ARROW_LEFT,
  KEY_ARROW_RIGHT,
  KEY_ARROW_UP,
  KEY_ENTER,
} from "../terminal/keys";

// Session Terminal mode (Issue #810, the V1 hero). A live read-only mirror of the session's
// terminal (polling GET /sessions/{sid}/buffer?raw=true&since=<cursor> and writing the new bytes
// into an xterm.js emulator so the screen stays coherent), plus the write controls: a text box +
// Send (POST /prompt, AppendEnter=true), control keys Enter / Esc / Stop / arrows (raw key
// sequences via /prompt AppendEnter=false, /escape, /interrupt), and Speak (TTS of the latest
// output via the Gateway /wingman/tts nova-voice proxy, played in an <audio> element). Every call
// goes through the typed client with the injected Bearer token, so it works with Gateway auth on
// or off. This replaces the #806 session-detail placeholder.
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
  const audioRef = useRef<HTMLAudioElement | null>(null);

  const [name, setName] = useState<string | null>(null);
  const [input, setInput] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [speaking, setSpeaking] = useState(false);

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

  const flash = useCallback((message: string) => {
    setNote(message);
    window.setTimeout(() => setNote((cur) => (cur === message ? null : cur)), 1500);
  }, []);

  const sendKey = useCallback(async (seq: string, label: string) => {
    if (!sessionId) return;
    try {
      await sendPrompt(sessionId, seq, false);
      flash(`${label} sent`);
    } catch (err) {
      setError(err instanceof Error ? err.message : `${label} failed`);
    }
  }, [sessionId, flash]);

  const onSend = useCallback(async () => {
    if (!sessionId) return;
    const text = input;
    if (text.trim().length === 0) return;
    try {
      await sendPrompt(sessionId, text, true);
      setInput("");
      flash("Sent");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Send failed");
    }
  }, [sessionId, input, flash]);

  const onEscape = useCallback(async () => {
    if (!sessionId) return;
    try { await sendEscape(sessionId); flash("Esc sent"); }
    catch (err) { setError(err instanceof Error ? err.message : "Esc failed"); }
  }, [sessionId, flash]);

  const onStop = useCallback(async () => {
    if (!sessionId) return;
    try { await sendInterrupt(sessionId); flash("Stop sent"); }
    catch (err) { setError(err instanceof Error ? err.message : "Stop failed"); }
  }, [sessionId, flash]);

  const onSpeak = useCallback(async () => {
    if (!sessionId || speaking) return;
    setSpeaking(true);
    try {
      const tail = (await getCleanTail(sessionId, SPEAK_TAIL_LINES)).trim();
      if (tail.length === 0) { flash("Nothing to speak"); return; }
      const blob = await synthesizeSpeech(tail);
      const url = URL.createObjectURL(blob);
      const audio = audioRef.current;
      if (audio) {
        audio.src = url;
        audio.onended = () => URL.revokeObjectURL(url);
        await audio.play();
        flash("Speaking");
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Speak failed");
    } finally {
      setSpeaking(false);
    }
  }, [sessionId, speaking, flash]);

  return (
    <div className="terminal-screen">
      <header className="app-bar">
        <Link className="back-link" to="/">&larr; Roster</Link>
        <h1 className="term-title">{name ?? "Session"}</h1>
        <span className="app-bar-sub">Terminal</span>
      </header>

      <code className="term-sid" title="session id (carried in the route)">{sessionId}</code>

      {error !== null && <div className="banner banner-error" role="alert">{error}</div>}

      <div className="term-host" ref={hostRef} />

      {note !== null && <div className="term-note" role="status">{note}</div>}

      <div className="term-controls">
        <button type="button" className="key-btn key-stop" onClick={onStop}>Stop</button>
        <button type="button" className="key-btn" onClick={onEscape}>Esc</button>
        <button type="button" className="key-btn" onClick={() => sendKey(KEY_ARROW_UP, "Up")} aria-label="Arrow up">&uarr;</button>
        <button type="button" className="key-btn" onClick={() => sendKey(KEY_ARROW_DOWN, "Down")} aria-label="Arrow down">&darr;</button>
        <button type="button" className="key-btn" onClick={() => sendKey(KEY_ARROW_LEFT, "Left")} aria-label="Arrow left">&larr;</button>
        <button type="button" className="key-btn" onClick={() => sendKey(KEY_ARROW_RIGHT, "Right")} aria-label="Arrow right">&rarr;</button>
        <button type="button" className="key-btn" onClick={() => sendKey(KEY_ENTER, "Enter")}>Enter</button>
        <button type="button" className="key-btn key-speak" onClick={onSpeak} disabled={speaking}>
          {speaking ? "..." : "Speak"}
        </button>
      </div>

      <div className="term-input-row">
        <input
          className="term-input"
          type="text"
          inputMode="text"
          autoComplete="off"
          autoCapitalize="off"
          autoCorrect="off"
          spellCheck={false}
          placeholder="Type, then Send"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); void onSend(); } }}
        />
        <button type="button" className="term-send" onClick={onSend} disabled={input.trim().length === 0}>
          Send
        </button>
      </div>

      <audio ref={audioRef} hidden />
    </div>
  );
}
