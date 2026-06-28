import { useCallback, useState } from "react";
import { sendEscape, sendInterrupt, sendPrompt } from "../api/client";
import { DictationDialog } from "../dictation/DictationDialog";
import {
  KEY_ARROW_DOWN,
  KEY_ARROW_LEFT,
  KEY_ARROW_RIGHT,
  KEY_ARROW_UP,
  KEY_ENTER,
} from "../terminal/keys";

// The ONE shared session control surface (issue #811): the input row (input + Speak + Send), the
// Enter/Esc/Stop row, and the arrow row, plus the shared Speak dictation dialog. Factored out of the
// Terminal view (#817) so the Terminal AND the Chat view drive a session with byte-identical
// payloads from a single source - Chat does NOT re-implement input/keys/Speak.
//
// Payloads (identical to the Android tab and the desktop): Send -> POST /prompt AppendEnter=true
// (clears input, flashes "Sent"); Enter -> "\r" AppendEnter=false; Esc -> POST /escape; Stop ->
// POST /interrupt; arrows -> ESC[A/B/C/D via /prompt AppendEnter=false; Speak = dictation, Insert
// drops the transcript into the box (no submit), Send submits it via the same /prompt path.
//
// The host owns status/error display: onFlash shows a transient "Sent" style note, onError raises a
// banner. showKeyRows lets a host hide the Enter/Esc/Stop + arrow rows while keeping the input row
// (the Terminal hides the whole panel behind its Keys toggle; the Chat keeps the input row visible).

export interface SessionControlsProps {
  sessionId: string | undefined;
  /** Show a transient status note (e.g. "Sent", "Inserted"). */
  onFlash: (message: string) => void;
  /** Raise an error banner. */
  onError: (message: string) => void;
  /** Render the Enter/Esc/Stop and arrow rows. The input + Speak + Send row always renders. */
  showKeyRows: boolean;
}

export function SessionControls({ sessionId, onFlash, onError, showKeyRows }: SessionControlsProps) {
  const [input, setInput] = useState("");
  const [dictating, setDictating] = useState(false);

  const sendKey = useCallback(
    async (seq: string, label: string) => {
      if (!sessionId) return;
      try {
        await sendPrompt(sessionId, seq, false);
        onFlash("Sent");
      } catch (err) {
        onError(err instanceof Error ? err.message : `${label} failed`);
      }
    },
    [sessionId, onFlash, onError],
  );

  const onSend = useCallback(async () => {
    if (!sessionId) return;
    const text = input;
    if (text.trim().length === 0) return;
    setInput(""); // clear immediately (the Android tab clears the box before the call returns)
    try {
      await sendPrompt(sessionId, text, true);
      onFlash("Sent");
    } catch (err) {
      onError(err instanceof Error ? err.message : "Send failed");
    }
  }, [sessionId, input, onFlash, onError]);

  const onEscape = useCallback(async () => {
    if (!sessionId) return;
    try {
      await sendEscape(sessionId);
      onFlash("Sent Esc");
    } catch (err) {
      onError(err instanceof Error ? err.message : "Esc failed");
    }
  }, [sessionId, onFlash, onError]);

  const onStop = useCallback(async () => {
    if (!sessionId) return;
    try {
      await sendInterrupt(sessionId);
      onFlash("Sent Stop (Ctrl+C)");
    } catch (err) {
      onError(err instanceof Error ? err.message : "Stop failed");
    }
  }, [sessionId, onFlash, onError]);

  // Speak opens the dictation dialog. Insert drops the transcript into the input (no submit, joined
  // onto any existing text); Send submits it via the same POST /prompt path the Send button uses.
  const onDictateInsert = useCallback(
    (text: string) => {
      setDictating(false);
      if (text.trim().length === 0) return;
      setInput((cur) => (cur.trim().length === 0 ? text : `${cur.trimEnd()} ${text}`));
      onFlash("Inserted");
    },
    [onFlash],
  );

  const onDictateSend = useCallback(
    async (text: string) => {
      setDictating(false);
      if (!sessionId || text.trim().length === 0) return;
      try {
        await sendPrompt(sessionId, text, true);
        onFlash("Sent");
      } catch (err) {
        onError(err instanceof Error ? err.message : "Send failed");
      }
    },
    [sessionId, onFlash, onError],
  );

  return (
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
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              void onSend();
            }
          }}
        />
        <button
          type="button"
          className="term-btn term-speak"
          onClick={() => setDictating(true)}
          disabled={dictating}
        >
          Speak
        </button>
        <button type="button" className="term-btn term-send" onClick={onSend}>
          Send
        </button>
      </div>

      {showKeyRows && (
        <>
          <div className="term-row term-row-3">
            <button type="button" className="term-btn term-enter" onClick={() => sendKey(KEY_ENTER, "Enter")}>
              Enter
            </button>
            <button type="button" className="term-btn term-esc" onClick={onEscape}>
              Esc
            </button>
            <button type="button" className="term-btn term-stop" onClick={onStop}>
              Stop
            </button>
          </div>

          <div className="term-row term-row-4">
            <button type="button" className="term-btn term-arrow" onClick={() => sendKey(KEY_ARROW_UP, "Up")}>
              Up
            </button>
            <button type="button" className="term-btn term-arrow" onClick={() => sendKey(KEY_ARROW_DOWN, "Down")}>
              Down
            </button>
            <button type="button" className="term-btn term-arrow" onClick={() => sendKey(KEY_ARROW_LEFT, "Left")}>
              Left
            </button>
            <button type="button" className="term-btn term-arrow" onClick={() => sendKey(KEY_ARROW_RIGHT, "Right")}>
              Right
            </button>
          </div>
        </>
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
