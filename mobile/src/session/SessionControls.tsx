import { useCallback, useRef, useState } from "react";
import { sendEscape, sendInterrupt, sendPrompt, synthesizeSpeech } from "../api/client";
import {
  KEY_ARROW_DOWN,
  KEY_ARROW_LEFT,
  KEY_ARROW_RIGHT,
  KEY_ARROW_UP,
  KEY_ENTER,
} from "../terminal/keys";

// Shared session write-controls, factored out of the Terminal view (#810) so the Terminal and
// Chat views (#811) drive the same session through one set of controls instead of duplicating
// them: a text box + Send (POST /prompt, AppendEnter=true), the control keys Enter / Esc / Stop /
// arrows (raw key sequences via /prompt AppendEnter=false, /escape, /interrupt), and Speak (TTS
// over plain HTTP via the Gateway /wingman/tts nova-voice proxy, played in an <audio> element).
// Every call goes through the typed client with the injected Bearer token, so it works with
// Gateway auth on or off.
//
// What to speak differs per view, so the caller supplies getSpeakText: the Terminal view speaks
// the cleaned tail of the raw buffer; the Chat view speaks the latest assistant reply. Hard
// failures are surfaced to the host view through onError so each view shows them in its own
// banner; transient "X sent" confirmations are shown inline here.
export interface SessionControlsProps {
  sessionId: string;
  getSpeakText: () => Promise<string>;
  onError: (message: string) => void;
}

export function SessionControls({ sessionId, getSpeakText, onError }: SessionControlsProps) {
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const [input, setInput] = useState("");
  const [note, setNote] = useState<string | null>(null);
  const [speaking, setSpeaking] = useState(false);

  const flash = useCallback((message: string) => {
    setNote(message);
    window.setTimeout(() => setNote((cur) => (cur === message ? null : cur)), 1500);
  }, []);

  const sendKey = useCallback(async (seq: string, label: string) => {
    try {
      await sendPrompt(sessionId, seq, false);
      flash(`${label} sent`);
    } catch (err) {
      onError(err instanceof Error ? err.message : `${label} failed`);
    }
  }, [sessionId, flash, onError]);

  const onSend = useCallback(async () => {
    const text = input;
    if (text.trim().length === 0) return;
    try {
      await sendPrompt(sessionId, text, true);
      setInput("");
      flash("Sent");
    } catch (err) {
      onError(err instanceof Error ? err.message : "Send failed");
    }
  }, [sessionId, input, flash, onError]);

  const onEscape = useCallback(async () => {
    try { await sendEscape(sessionId); flash("Esc sent"); }
    catch (err) { onError(err instanceof Error ? err.message : "Esc failed"); }
  }, [sessionId, flash, onError]);

  const onStop = useCallback(async () => {
    try { await sendInterrupt(sessionId); flash("Stop sent"); }
    catch (err) { onError(err instanceof Error ? err.message : "Stop failed"); }
  }, [sessionId, flash, onError]);

  const onSpeak = useCallback(async () => {
    if (speaking) return;
    setSpeaking(true);
    try {
      const text = (await getSpeakText()).trim();
      if (text.length === 0) { flash("Nothing to speak"); return; }
      const blob = await synthesizeSpeech(text);
      const url = URL.createObjectURL(blob);
      const audio = audioRef.current;
      if (audio) {
        audio.src = url;
        audio.onended = () => URL.revokeObjectURL(url);
        await audio.play();
        flash("Speaking");
      }
    } catch (err) {
      onError(err instanceof Error ? err.message : "Speak failed");
    } finally {
      setSpeaking(false);
    }
  }, [speaking, getSpeakText, flash, onError]);

  return (
    <>
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
    </>
  );
}
