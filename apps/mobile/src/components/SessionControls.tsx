import { useCallback, useLayoutEffect, useRef, useState } from "react";
import { sendEscape, sendInterrupt, sendPrompt, uploadImage } from "@devthrottle/client-core/api/client";
import { backgroundTranscribeAndSend, type CapturedUtterance } from "@devthrottle/client-core/dictation/backgroundSend";
import { DictationDialog } from "@devthrottle/client-core/dictation/DictationDialog";
import { insertAt, joinText } from "@devthrottle/client-core/dictation/transcript";
import { ComposerProvenance } from "@devthrottle/client-core/dictation/composerProvenance";
import {
  KEY_ARROW_DOWN,
  KEY_ARROW_LEFT,
  KEY_ARROW_RIGHT,
  KEY_ARROW_UP,
  KEY_ENTER,
} from "@devthrottle/client-core/terminal/keys";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

// The surface label on every client-error report from this view, so the Gateway log and
// GET /client-errors/recent name where the user was standing (issue #2189).
const SURFACE = "mobile-session-controls";

// The ONE shared session control surface (issue #811): the full-width input row, the Send/Speak row
// (Send first, Speak second, equal halves), the Enter/Esc/Stop row, and the arrow row, plus the
// shared Speak dictation dialog. Factored out of the Terminal view (#817) so the Terminal AND the
// Chat view drive a session with byte-identical payloads from a single source - Chat does NOT
// re-implement input/keys/Speak.
//
// Payloads (identical to the Android tab and the desktop): Send -> POST /prompt AppendEnter=true
// (clears input, flashes "Sent"); Enter -> "\r" AppendEnter=false; Esc -> POST /escape; Stop ->
// POST /interrupt; arrows -> ESC[A/B/C/D via /prompt AppendEnter=false; Speak = dictation, Insert
// drops the transcript into the box (no submit), Send submits it via the same /prompt path.
//
// Attach (issue #1316): pick an image from the phone (the native picker offers the camera AND the
// photo library), upload it through the shared uploadImage -> POST /sessions/{sid}/upload-image, and
// insert the Director-side saved path into the box - the same one upload path the Cockpit composer
// uses (issue #1210). The user then adds a sentence and Sends; the agent reads the image from disk.
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
  /** Render the Enter/Esc/Stop and arrow rows. The input row and Send/Speak row always render. */
  showKeyRows: boolean;
}

export function SessionControls({ sessionId, onFlash, onError, showKeyRows }: SessionControlsProps) {
  const [input, setInput] = useState("");
  const [dictating, setDictating] = useState(false);
  const [uploading, setUploading] = useState(false);
  const inputRef = useRef<HTMLTextAreaElement | null>(null);
  const fileRef = useRef<HTMLInputElement | null>(null);
  // The caret position in the composer, snapshotted when Speak is pressed (the dialog is modal, so the
  // box cannot change while it is open). Dictation is inserted here, exactly like the desktop Insert
  // button, instead of being appended at the end.
  const caretRef = useRef(0);
  // WHICH CHARACTERS CAME FROM A MICROPHONE (source logging, owner's ruling 2026-09-05) - the same record the
  // Cockpit composer and the desktop compose box keep, so all three surfaces say which characters were spoken
  // rather than only whether the whole turn was. The ranges ride with the send as claims the Gateway verifies.
  const provenanceRef = useRef(new ComposerProvenance());
  // Where to move the caret after an Insert drops text mid-box; applied post-render below. Null except
  // in the render right after an Insert.
  const pendingCaretRef = useRef<number | null>(null);

  // Auto-grow the textarea to fit its content (up to a cap, after which it scrolls). Re-run on every
  // input change so it grows as you type AND shrinks back when the box is cleared (Send) or replaced
  // by a dictation insert. useLayoutEffect measures before paint so there is no visible jump.
  const MAX_INPUT_HEIGHT_PX = 160;
  useLayoutEffect(() => {
    const el = inputRef.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(el.scrollHeight, MAX_INPUT_HEIGHT_PX)}px`;
  }, [input]);

  // After an Insert drops dictation mid-box, put the caret right after the inserted words so the user
  // can keep editing there. Does not force focus (that would pop the mobile keyboard unbidden).
  useLayoutEffect(() => {
    const pos = pendingCaretRef.current;
    if (pos === null) return;
    pendingCaretRef.current = null;
    const el = inputRef.current;
    if (el) el.selectionStart = el.selectionEnd = pos;
  }, [input]);

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
      await sendPrompt(sessionId, text, true, undefined, undefined, provenanceRef.current.forSend().spans);
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

  // Attach (issue #1316): upload each picked image through the shared uploadImage (the ONE upload
  // path, same as the Cockpit composer, issue #1210) and insert the Director-side saved path at the
  // caret inside any typed text - the user then adds a sentence and Sends. The <input type="file"
  // accept="image/*"> native picker on a phone offers both the camera and the photo library, which
  // covers a screenshot and a fresh photo out and about. Fails loud (onError), never silently.
  const onAttach = useCallback(
    async (e: React.ChangeEvent<HTMLInputElement>) => {
      const files = e.target.files ? Array.from(e.target.files) : [];
      // Reset the input immediately so picking the same image twice fires onChange again.
      if (fileRef.current) fileRef.current.value = "";
      if (!sessionId || files.length === 0 || uploading) return;
      setUploading(true);
      onFlash(files.length === 1 ? "Uploading image..." : `Uploading ${files.length} images...`);
      try {
        const paths: string[] = [];
        for (const file of files) {
          const path = await uploadImage(sessionId, file);
          if (path) paths.push(path);
        }
        if (paths.length === 0) throw new Error("Upload returned no image path");
        // Insert the path(s) at the snapshotted caret, exactly like a dictation Insert, so the box
        // reads "<before> <paths> <after>". The paths are space-joined with a trailing space.
        const caret = caretRef.current;
        const chunk = `${paths.join(" ")} `;
        setInput((cur) => {
          const composed = insertAt(cur, caret, chunk);
          const clamped = caret < 0 || caret > cur.length ? cur.length : caret;
          pendingCaretRef.current = composed.length - (cur.length - clamped);
          return composed;
        });
        onFlash(paths.length === 1 ? "Image attached" : `${paths.length} images attached`);
      } catch (err) {
        onError(describeAndReport(SURFACE, "attach the image", err));
      } finally {
        setUploading(false);
      }
    },
    [sessionId, uploading, onFlash, onError],
  );

  // Speak opens the dictation dialog. Insert drops the transcript into the input at the caret (no
  // submit); Send inserts it at the caret and submits via the same POST /prompt path the Send button
  // uses. Both use the caret snapshotted when Speak was pressed, exactly like the desktop Insert.
  const onDictateInsert = useCallback(
    (text: string, spokenDeliveryId?: string) => {
      setDictating(false);
      if (text.trim().length === 0) return;
      const caret = caretRef.current;
      setInput((cur) => {
        const composed = insertAt(cur, caret, text);
        const clamped = caret < 0 || caret > cur.length ? cur.length : caret;
        pendingCaretRef.current = composed.length - (cur.length - clamped);
        // The inserted characters ARE the transcript; where they landed is where the record marks them.
        const at = composed.indexOf(text, Math.max(0, Math.min(caret, composed.length)));
        provenanceRef.current.textChanged(cur, caret);
        // Only a range that names a verifiable transcript is worth recording; the dialog withholds the id
        // the moment the words were edited, and those characters are then no different from typing.
        if (at >= 0 && spokenDeliveryId !== undefined) provenanceRef.current.inserted(composed, text, at, spokenDeliveryId);
        else provenanceRef.current.textChanged(composed);
        return composed;
      });
      onFlash("Inserted");
    },
    [onFlash],
  );

  const onDictateSend = useCallback(
    async (text: string, spokenDeliveryId?: string) => {
      setDictating(false);
      if (!sessionId) return;
      // Send behaves like Insert-then-Enter: the dictation is inserted at the caret inside any typed
      // text (like the Insert button), then submitted. Clear the box, exactly like the normal Send
      // path, so the user does not re-send what just went out.
      const composed = insertAt(input, caretRef.current, text);
      const at = composed.indexOf(text, Math.max(0, Math.min(caretRef.current, composed.length)));
      provenanceRef.current.textChanged(input, caretRef.current);
      // Only a range that names a verifiable transcript is worth recording (see the Insert path above).
      if (at >= 0 && spokenDeliveryId !== undefined) provenanceRef.current.inserted(composed, text, at, spokenDeliveryId);
      else provenanceRef.current.textChanged(composed);
      const sent = provenanceRef.current.forSend();
      const combined = sent.text;
      if (combined.length === 0) return;
      // SPOKEN only when the dictation is the whole turn - the same test the Cockpit composer makes,
      // because this is the same act on the other surface (ruling R10, "Clean up Your Throttle").
      // Typed text already in the box makes it a mixture, and a mixture counts as typed.
      const spoken = input.trim().length === 0 ? spokenDeliveryId : undefined;
      setInput("");
      provenanceRef.current.reset();
      try {
        await sendPrompt(sessionId, combined, true, undefined, spoken, sent.spans);
        onFlash("Sent");
      } catch (err) {
        setInput(combined); // restore so a failed send never loses the typed + dictated text
        onError(err instanceof Error ? err.message : "Send failed");
      }
    },
    [sessionId, input, onFlash, onError],
  );

  // Immediate (fire-and-forget) Send from the Speak dialog: the dialog already captured the audio
  // buffer and closed itself, releasing the screen. We transcode + upload + transcribe + submit in the
  // background while the roster shows the session orange ("Transcribing..."), so the user can move on
  // immediately. The flash is a brief acknowledgement; the persistent signal is the orange roster row.
  const onDictateSendAudio = useCallback(
    (captured: CapturedUtterance) => {
      setDictating(false);
      if (!sessionId) return;
      // Send behaves like Insert-then-Enter: the dictation is inserted at the caret inside the typed
      // text via the compose hook (like the Insert button). Clear the box now (at dialog-close time) -
      // the background transcribe-and-submit sends the combined text. On a transcription failure the
      // typed text is put back so Send never loses it.
      const composerText = input;
      const caret = caretRef.current;
      setInput("");
      onFlash("Transcribing...");
      void backgroundTranscribeAndSend(sessionId, captured, {
        onError,
        // Insert the dictated words at the snapshotted caret inside the typed text: the Gateway submits
        // before + dictation + after. The caret splits the typed text into the two halves.
        composeParts: { before: composerText.slice(0, caret), after: composerText.slice(caret) },
        // On a send that does not complete the audio is kept durably for resume, but the typed text is
        // client-only: put it back (ahead of anything typed since) so Send never silently loses it. If
        // the user navigated away this component is unmounted and setInput is a harmless no-op.
        onFailed: () => {
          if (composerText.trim().length > 0) setInput((cur) => joinText(composerText, cur));
        },
      });
    },
    [sessionId, input, onFlash, onError],
  );

  return (
    <div className="term-controls">
      <div className="term-row term-row-input">
        <textarea
          ref={inputRef}
          className="term-input"
          rows={1}
          inputMode="text"
          autoComplete="off"
          autoCapitalize="off"
          autoCorrect="off"
          spellCheck={false}
          placeholder="type a message..."
          value={input}
          onChange={(e) => {
            // The caret AFTER the change is what tells a deletion of the first of two identical copies from
            // a deletion of the second; the record cannot know it from the text alone.
            provenanceRef.current.textChanged(e.target.value, e.target.selectionStart ?? undefined);
            setInput(e.target.value);
          }}
        />
      </div>

      <div className="term-row term-row-send">
        <button type="button" className="term-btn term-send" onClick={onSend}>
          Send
        </button>
        <button
          type="button"
          className="term-btn term-speak"
          onClick={() => {
            // Snapshot the caret so the dictation lands where the cursor was, not at the end.
            caretRef.current = inputRef.current?.selectionStart ?? input.length;
            setDictating(true);
          }}
          disabled={dictating}
        >
          Speak
        </button>
        <button
          type="button"
          className="term-btn term-attach"
          onClick={() => {
            // Snapshot the caret so the uploaded path lands where the cursor was, not at the end.
            caretRef.current = inputRef.current?.selectionStart ?? input.length;
            fileRef.current?.click();
          }}
          disabled={uploading || !sessionId}
          title="Attach an image (camera or photo library)"
        >
          {uploading ? "Uploading..." : "Attach"}
        </button>
        <input
          ref={fileRef}
          type="file"
          accept="image/*"
          className="term-file"
          onChange={(e) => void onAttach(e)}
        />
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
          surface="mobile"
          onInsert={onDictateInsert}
          onSend={onDictateSend}
          onSendAudio={onDictateSendAudio}
          onClose={() => setDictating(false)}
        />
      )}
    </div>
  );
}
