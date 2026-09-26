import { useCallback, useEffect, useRef, useState } from "react";
import type { MutableRefObject } from "react";
import {
  enqueuePrompt,
  uploadImage,
  GatewayError,
  type QueueItem,
} from "@devthrottle/client-core/api/client";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { DictationDialog } from "@devthrottle/client-core/dictation/DictationDialog";
import { DictationStatusStrip } from "@devthrottle/client-core/dictation/DictationStatusStrip";
import { backgroundTranscribeAndSend, type CapturedUtterance } from "@devthrottle/client-core/dictation/backgroundSend";
import { insertAt, joinText } from "@devthrottle/client-core/dictation/transcript";
import { ComposerProvenance } from "@devthrottle/client-core/dictation/composerProvenance";
import { sendTypedPrompt } from "@devthrottle/client-core/dictation/typedPromptDelivery";

// The composer (issue #972, completed in issue #1210) - the React port of the Blazor Cockpit composer.
// It drives the selected session's reply through the shared Gateway client:
//
//   Send  -> POST /sessions/{sid}/prompt { appendEnter: true }  (submit the typed line; Ctrl+Enter)
//            through sendTypedPrompt: a 202 "still delivering" holds the words in the DictationStatusStrip
//            below and reads what the Gateway rules - never a second send (voice delivery phase 5, T6).
//   Speak -> mounts the shared DictationDialog (packages/client-core), exactly as the mobile
//            SessionControls does; the transcript inserts at the caret (Insert) or inserts + submits
//            (Send). Insert transcribes synchronously (transcribeUtterance -> the Gateway's
//            /wingman/utterance/* transcription); Send while still recording is the same
//            fire-and-forget path the phone uses (onSendAudio -> backgroundTranscribeAndSend -> the
//            /dictation/* durable pipeline): the dialog closes the instant Send is pressed and the
//            transcription + submit run in the background, with the shared DictationStatusStrip
//            (mounted below) showing where the send is.
//
// HISTORY, so nobody re-unplugs this: the Cockpit deliberately did NOT wire onSendAudio between PR
// #1975 and this change, because back then the /dictation/* routes resolved sessions in the Local
// partition on the hosted Gateway (blocker #1884) - a hosted Send through them held forever with no
// status ("Send basically sends nothing"), and the Cockpit mounted no status strip to even show the
// hold. Both halves are gone: the dictation upload family is tenant-partitioned end to end behind
// DictationTenantGate (PR #1945, per-tenant transcript storage #2093), the phone has been sending
// through it on hosted daily, and the Cockpit now mounts the same DictationStatusStrip and resumes
// pending dictations on load (AppShell.tsx) exactly like the mobile shell. The PAUSED-stage Send still
// submits the already-transcribed text synchronously - no audio round trip.
//   Queue -> POST /sessions/{sid}/queue { text }                (append to the queue; Ctrl+Shift+Enter)
//   Attach -> POST /sessions/{sid}/upload-image                 (upload a device-local image, then
//             insert the Director-side saved path into the composer for the agent to read)
//
// Attach, clipboard paste, and drag-and-drop of an image all funnel through the single `attachFiles`
// helper (issue #1210) so there is exactly one upload code path (the shared `uploadImage`), never a
// second one.
//
// The composer text is owned by the parent (SessionDetail) so the queue's Pop and the screenshot
// gallery's Insert can drop text into it. Send/Queue are disabled while empty or a call is in flight.

// The surface label on every client-error report from this composer, so a report in the Gateway log
// and in GET /client-errors/recent names where the user was standing (issue #2189).
const SURFACE = "cockpit-composer";

// How long to wait before the single retry below. Long enough to outlast the observed hole (the Gateway
// refuses actions on a session whose Director missed a push, and the next push closes it), short enough
// that a person watching the button does not think it hung.
const RETRY_DELAY_MS = 2500;

/**
 * Upload one image, retrying ONCE if the Gateway said the failure was retryable (issue #2188/#2189).
 *
 * Why: the reported failure was a ten-second window in which the owning Director had missed a snapshot
 * push, so the Gateway refused every action on a session that was alive the whole time. A single retry a
 * couple of seconds later would have succeeded. One retry, announced in the status line - not a silent
 * loop, and not a fallback that hides a real outage: a Director that is genuinely gone fails on the
 * retry too, and the user is told what happened.
 */
async function uploadWithOneRetry(
  sessionId: string,
  file: File,
  onRetrying: () => void,
): Promise<string> {
  try {
    return await uploadImage(sessionId, file);
  } catch (err) {
    if (!(err instanceof GatewayError) || !err.retryable) throw err;
    onRetrying();
    await new Promise((resolve) => setTimeout(resolve, RETRY_DELAY_MS));
    return await uploadImage(sessionId, file);
  }
}

export interface SessionComposerProps {
  sessionId: string | undefined;
  value: string;
  onChange: (value: string) => void;
  /** Replace the queue with the server's authoritative list after a Queue action. */
  onQueued: (items: QueueItem[]) => void;
  /**
   * When set, the composer publishes a function into this ref that focuses its textarea. The Source
   * Control tab (issue #1266) calls it after inserting a clicked file's path so the reader can type the
   * matching instruction straight away. Cleared on unmount so a stale focuser is never called.
   */
  focusHandleRef?: MutableRefObject<(() => void) | null>;
  /**
   * The Fleet Manager page (step 6) talks to one session like a chat: plain Enter sends and Shift+Enter starts a
   * new line. Sessions keep Ctrl+Enter, because a prompt there is often several lines.
   */
  enterSends?: boolean;
  /** The hint in the empty box. */
  placeholder?: string;
  /** False hides Queue and Attach, for a surface that only sends and dictates (the Fleet Manager page). */
  showQueueAndAttach?: boolean;
  /**
   * WHICH SENDING BUTTON THIS BOX OFFERS - "send", "queue", or "both".
   *
   * "both" is the page's own composer at the bottom of a session, where the reader chooses between interrupting
   * the session now and waiting for it to stop. The Wingman tab is not that surface: it draws ONE box against one
   * state, and a Send beside a Queue there was a choice nobody could predict the effect of - on a working session
   * the words in the box said a message "is queued", and then offered both (the review's item N3). So the tab
   * asks for one, named for what it does.
   */
  sending?: "send" | "queue" | "both";
  /** Called with the words the moment a send succeeds, so the surface can show it went. */
  onSent?: (text: string) => void;
}

const DEFAULT_PLACEHOLDER = "Type a message... (Ctrl+Enter to send, Ctrl+Shift+Enter to queue)";

/** The one filled control in the window. An arrow, because Send and Queue both mean "away from here". */
function SendArrow() {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false">
      <path
        d="M8 12.5v-9M4.5 7L8 3.5 11.5 7"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

export function SessionComposer({
  sessionId,
  value,
  onChange,
  onQueued,
  focusHandleRef,
  enterSends = false,
  placeholder = DEFAULT_PLACEHOLDER,
  showQueueAndAttach = true,
  sending = "both",
  onSent,
}: SessionComposerProps) {
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dictating, setDictating] = useState(false);
  const [dragOver, setDragOver] = useState(false);
  const fileRef = useRef<HTMLInputElement | null>(null);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  // The caret position, snapshotted when Speak is pressed (the dialog is modal, so the box cannot
  // change while it is open). Dictation is inserted here, exactly like the desktop Insert button and
  // the mobile SessionControls, instead of always at the end.
  const caretRef = useRef(0);
  // Mirror of the parent-owned composer text, for callbacks that outlive their render (the
  // background dictation send's onFailed fires long after this component re-rendered, and the
  // `value` its closure captured is stale by then). The parent owns the text, so there is no
  // functional setState to lean on the way the mobile SessionControls does.
  const valueRef = useRef(value);
  useEffect(() => {
    valueRef.current = value;
  }, [value]);
  // WHICH CHARACTERS CAME FROM A MICROPHONE (source logging, owner's ruling 2026-09-05). The textarea is a
  // plain control, so the record sits beside it and follows every change: an inserted transcript is a
  // character RANGE, typing around it moves it, editing inside it forgets it. The ranges ride to the Gateway
  // with the send as CLAIMS, which it verifies against the transcript it registered - so a turn that mixes
  // typing and speech still says which of its characters were spoken, instead of saying nothing at all.
  const provenanceRef = useRef(new ComposerProvenance());
  // The parent owns the text, so a change can reach this component without passing through its own handler
  // (the Source Control tab inserts a path; a failed send restores the box). The record is told about the
  // text on every render it has not seen, so it never describes a string the box no longer holds.
  useEffect(() => {
    provenanceRef.current.textChanged(value, caretRef.current);
  }, [value]);
  // Publish a focuser for the composer textarea into the parent-owned ref (issue #1266), and clear it on
  // unmount so the Source Control tab never calls into a torn-down composer.
  useEffect(() => {
    if (!focusHandleRef) return;
    focusHandleRef.current = () => textareaRef.current?.focus();
    return () => {
      focusHandleRef.current = null;
    };
  }, [focusHandleRef]);

  const send = useCallback(async () => {
    if (!sessionId || busy) return;
    const text = value;
    if (text.trim().length === 0) return;
    // The text as sent and the spoken ranges over THAT text, from one projection: the claim can never
    // describe a different string from the one submitted.
    const sent = provenanceRef.current.forSend();
    setBusy(true);
    setError(null);
    onChange(""); // clear immediately, like the desktop composer
    provenanceRef.current.reset();
    try {
      // Held by the Gateway: the strip below shows it "Still delivering", so this line says nothing - and the
      // words are NOT restored to the box, because they may already be in.
      const outcome = await sendTypedPrompt(sessionId, text, { spokenSpans: sent.spans });
      if (outcome === "delivered") {
        setStatus("Sent");
        onSent?.(text);
      } else {
        setStatus(null);
      }
    } catch (err) {
      onChange(text); // restore so a failed send never loses the typed text
      setError(describeAndReport(SURFACE, "send that to the session", err));
    } finally {
      setBusy(false);
    }
  }, [sessionId, busy, value, onChange, onSent]);

  const queue = useCallback(async () => {
    if (!sessionId || busy) return;
    const text = value;
    if (text.trim().length === 0) return;
    setBusy(true);
    setError(null);
    try {
      const items = await enqueuePrompt(sessionId, text);
      onQueued(items);
      onChange("");
      setStatus("Queued");
    } catch (err) {
      setError(describeAndReport(SURFACE, "queue that prompt", err));
    } finally {
      setBusy(false);
    }
  }, [sessionId, busy, value, onChange, onQueued]);

  // WHICH SENDING BUTTONS ARE ON THE BAR, worked out once and used by the bar AND by the keyboard, so a shortcut
  // can never do something the screen does not offer.
  const offersSend = sending === "send" || sending === "both";
  const offersQueue = sending === "queue" || (sending === "both" && showQueueAndAttach);
  // The one sending button on a box that only queues. It carries the primary weight and is named for what it does.
  const queueIsTheOnlySend = offersQueue && !offersSend;

  const onKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
      // Ctrl+Shift+Enter = Queue; Ctrl+Enter = Send; plain Enter = newline (default). With enterSends, plain
      // Enter sends and Shift+Enter is the newline. A key press that is composing text (an input method) is left alone.
      // A box that only queues does BOTH shortcuts as a queue: the alternative is a key that silently does nothing.
      const submit = offersSend ? send : queue;
      if (enterSends && e.key === "Enter" && !e.shiftKey && !e.ctrlKey && !e.nativeEvent.isComposing) {
        e.preventDefault();
        void submit();
        return;
      }
      if (e.key === "Enter" && e.ctrlKey && e.shiftKey && offersQueue) {
        e.preventDefault();
        void queue();
        return;
      }
      if (e.key === "Enter" && e.ctrlKey) {
        e.preventDefault();
        void submit();
      }
    },
    [queue, send, enterSends, offersSend, offersQueue],
  );

  // The ONE image-upload path (issue #1210): Attach, clipboard paste, and drag-and-drop all call this.
  // It uploads each image with the shared `uploadImage` (browser-device image up to the Director) and
  // inserts the Director-side saved path(s) into the composer - the same end state as the desktop
  // "drag the image onto the prompt". There is no second upload code path anywhere.
  const attachFiles = useCallback(
    async (files: File[]) => {
      if (!sessionId || files.length === 0) return;
      setBusy(true);
      setError(null);
      try {
        const paths: string[] = [];
        for (const file of files) {
          const path = await uploadWithOneRetry(sessionId, file, () => setStatus("Retrying..."));
          if (path) paths.push(path);
        }
        if (paths.length > 0) {
          const prefix = value.length > 0 && !value.endsWith(" ") ? `${value} ` : value;
          onChange(`${prefix}${paths.join(" ")} `);
          setStatus(paths.length === 1 ? "Image attached" : `${paths.length} images attached`);
        }
      } catch (err) {
        setStatus(null);
        setError(describeAndReport(SURFACE, "attach the image", err));
      } finally {
        setBusy(false);
      }
    },
    [sessionId, value, onChange],
  );

  const onAttach = useCallback(
    async (e: React.ChangeEvent<HTMLInputElement>) => {
      const files = e.target.files;
      if (files) await attachFiles(Array.from(files));
      if (fileRef.current) fileRef.current.value = ""; // allow re-selecting the same file
    },
    [attachFiles],
  );

  // Clipboard paste of an image into the textarea (issue #1210): routes through the same attachFiles
  // upload. A paste that carries no image is left alone so normal text paste is unaffected.
  const onPaste = useCallback(
    (e: React.ClipboardEvent<HTMLTextAreaElement>) => {
      const images = Array.from(e.clipboardData.files).filter((f) => f.type.startsWith("image/"));
      if (images.length === 0) return;
      e.preventDefault();
      void attachFiles(images);
    },
    [attachFiles],
  );

  // Drag-and-drop of an image file onto the composer (issue #1210): same attachFiles upload.
  const onDrop = useCallback(
    (e: React.DragEvent<HTMLDivElement>) => {
      const images = Array.from(e.dataTransfer.files).filter((f) => f.type.startsWith("image/"));
      setDragOver(false);
      if (images.length === 0) return;
      e.preventDefault();
      void attachFiles(images);
    },
    [attachFiles],
  );

  const onDragOver = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    if (Array.from(e.dataTransfer.items).some((i) => i.kind === "file")) {
      e.preventDefault();
      setDragOver(true);
    }
  }, []);

  const onDragLeave = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    // Only clear when the pointer actually leaves the composer (not when moving between its children).
    if (e.currentTarget.contains(e.relatedTarget as Node | null)) return;
    setDragOver(false);
  }, []);

  // Speak: mount the shared dictation dialog, exactly like the mobile SessionControls. Insert drops the
  // transcript at the caret (no submit); Send inserts at the caret and submits.
  const onSpeak = useCallback(() => {
    caretRef.current = textareaRef.current?.selectionStart ?? value.length;
    setError(null);
    setDictating(true);
  }, [value]);

  const onDictateInsert = useCallback(
    (text: string, spokenDeliveryId?: string) => {
      setDictating(false);
      if (text.trim().length === 0) return;
      const composed = insertAt(value, caretRef.current, text);
      const at = composed.indexOf(text, Math.max(0, Math.min(caretRef.current, composed.length)));
      provenanceRef.current.textChanged(value, caretRef.current);
      // A range is only worth recording when it names a transcript the Gateway can verify. The dialog
      // withholds the id the moment the words stop being one unedited transcription, and those characters
      // are then exactly as unattributable as typing - so nothing is marked.
      if (at >= 0 && spokenDeliveryId !== undefined) provenanceRef.current.inserted(composed, text, at, spokenDeliveryId);
      else provenanceRef.current.textChanged(composed);
      onChange(composed);
      setStatus("Inserted");
    },
    [value, onChange],
  );

  const onDictateSend = useCallback(
    async (text: string, spokenDeliveryId?: string) => {
      setDictating(false);
      if (!sessionId) return;
      const composed = insertAt(value, caretRef.current, text);
      const at = composed.indexOf(text, Math.max(0, Math.min(caretRef.current, composed.length)));
      provenanceRef.current.textChanged(value, caretRef.current);
      // A range is only worth recording when it names a transcript the Gateway can verify. The dialog
      // withholds the id the moment the words stop being one unedited transcription, and those characters
      // are then exactly as unattributable as typing - so nothing is marked.
      if (at >= 0 && spokenDeliveryId !== undefined) provenanceRef.current.inserted(composed, text, at, spokenDeliveryId);
      else provenanceRef.current.textChanged(composed);
      const sent = provenanceRef.current.forSend();
      const combined = sent.text;
      if (combined.length === 0) return;
      // The turn is SPOKEN only when the dictation is the whole of it. Send behaves like
      // Insert-then-Enter, so anything already in the box is typed text this person composed, and a
      // mixture is not a spoken turn - the page's own disclosure says so (ruling R10, "Clean up Your
      // Throttle", 2026-09-05). The dialog has already withheld the id if the transcript itself was
      // edited or came from more than one segment; this is the other half of the same test.
      const spoken = value.trim().length === 0 ? spokenDeliveryId : undefined;
      setBusy(true);
      setError(null);
      onChange("");
      provenanceRef.current.reset();
      try {
        const outcome = await sendTypedPrompt(sessionId, combined, { spokenDeliveryId: spoken, spokenSpans: sent.spans });
        if (outcome === "delivered") {
          setStatus("Sent");
          onSent?.(combined);
        } else {
          setStatus(null);
        }
      } catch (err) {
        onChange(combined); // restore so a failed send never loses the typed + dictated text
        setError(describeAndReport(SURFACE, "send that to the session", err));
      } finally {
        setBusy(false);
      }
    },
    [sessionId, value, onChange, onSent],
  );

  // Immediate (fire-and-forget) Send from the Speak dialog, the same shape as the mobile
  // SessionControls: the dialog already captured the audio buffer and closed itself, releasing the
  // screen. Transcode + upload + transcribe + submit run in the background; the DictationStatusStrip
  // below shows the live phase and any held/dropped outcome. Send behaves like Insert-then-Enter: the
  // dictation is inserted at the snapshotted caret inside any typed text, then submitted. The box is
  // cleared now (at dialog-close time); on a send that does not complete the typed text is put back -
  // the audio itself is kept durably for resume, but the typed text is client-only.
  const onDictateSendAudio = useCallback(
    (captured: CapturedUtterance) => {
      setDictating(false);
      if (!sessionId) return;
      const composerText = value;
      const caret = caretRef.current;
      setError(null);
      onChange("");
      // No composer status line here: the DictationStatusStrip below is the ONE live voice for this
      // send (it publishes "Saving..." before any network work and follows through to "Sent"). A
      // second "Transcribing..." in the button row would go stale the moment the strip moved on.
      void backgroundTranscribeAndSend(sessionId, captured, {
        onError: (message) => setError(message),
        // Insert the dictated words at the snapshotted caret inside the typed text: the Gateway
        // submits before + dictation + after. The caret splits the typed text into the two halves.
        composeParts: { before: composerText.slice(0, caret), after: composerText.slice(caret) },
        // Restore the typed text (ahead of anything typed since - valueRef reads the box as it is at
        // failure time, this callback's own `value` is a stale snapshot by then) so Send never
        // silently loses it. The audio itself is kept durably for resume; the typed text is
        // client-only, which is why it has to be put back here.
        onFailed: () => {
          if (composerText.trim().length > 0) onChange(joinText(composerText, valueRef.current));
        },
      });
    },
    [sessionId, value, onChange],
  );

  const empty = value.trim().length === 0;

  // The queue button, built once and placed once. It takes the SENDING slot - first on the bar, and drawn as the
  // loud one - when it is the only way to send from this box; otherwise it keeps its ordinary place after Speak.
  // When queueing is the ONLY way to send - a turn is in flight - it takes the filled control and the
  // arrow, because that is the one thing pressing it does. It does not pretend to be a second verb
  // sitting beside Send; it IS the send, honest about where the words will go.
  const queueButton = offersQueue ? (
    queueIsTheOnlySend ? (
      <button
        type="button"
        className="composer-send"
        disabled={busy || empty}
        onClick={() => void queue()}
        title="The session is working - your words go in the queue and land when it finishes"
        aria-label="Queue it"
      >
        <SendArrow />
      </button>
    ) : (
      <button
        type="button"
        className="composer-ghost"
        disabled={busy || empty}
        onClick={() => void queue()}
        title="Put this in the queue instead of sending it now"
      >
        Queue
      </button>
    )
  ) : null;

  return (
    <div
      className={`composer ${dragOver ? "composer-dragover" : ""}`}
      onDrop={onDrop}
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
    >
      {/* ONE COMPOSER (owner ruling, 2026-09-20). This row used to be four buttons under a box, with
          five more driver verbs in a bar above it - nine controls of equal weight under every
          conversation. Attach and Speak are INPUTS, so they belong inside the input; Send is the one
          filled control in the window; the driver verbs went to where they can actually act (Stop to
          the strip under this pill, the rest to the session menu). Nothing was dropped. */}
      <div className="composer-pill">
        {showQueueAndAttach && (
          <>
            <button
              type="button"
              className="composer-round"
              disabled={busy}
              onClick={() => fileRef.current?.click()}
              title="Upload a device-local image and insert its path (or paste / drag one in)"
              aria-label="Attach"
            >
              <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false">
                <path d="M8 3.5v9M3.5 8h9" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
              </svg>
            </button>
            <input
              ref={fileRef}
              type="file"
              accept="image/*"
              multiple
              className="composer-file"
              onChange={(e) => void onAttach(e)}
            />
          </>
        )}
        <textarea
          ref={textareaRef}
          className="composer-input"
          rows={1}
          placeholder={placeholder}
          value={value}
          onChange={(e) => {
            // The caret AFTER the change is what tells a deletion of the first of two identical copies from a
            // deletion of the second; the record cannot know it from the text alone.
            provenanceRef.current.textChanged(e.target.value, e.target.selectionStart ?? undefined);
            onChange(e.target.value);
          }}
          onKeyDown={onKeyDown}
          onPaste={onPaste}
          spellCheck={false}
        />
        <button
          type="button"
          className="composer-round"
          disabled={busy || dictating || !sessionId}
          onClick={onSpeak}
          title="Dictate into the composer"
          aria-label="Speak"
        >
          <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false">
            <rect x="6" y="2.2" width="4" height="7" rx="2" fill="none" stroke="currentColor" strokeWidth="1.4" />
            <path d="M3.8 7.4a4.2 4.2 0 0 0 8.4 0M8 11.6v2.2" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
          </svg>
        </button>
        {!queueIsTheOnlySend && queueButton}
        {offersSend && (
          <button
            type="button"
            className="composer-send"
            disabled={busy || empty}
            onClick={() => void send()}
            aria-label="Send"
            title="Send this to the session"
          >
            <SendArrow />
          </button>
        )}
        {queueIsTheOnlySend && queueButton}
      </div>

      {(status !== null || error !== null) && (
        <div className="composer-strip">
          {status !== null && <span className="composer-status">{status}</span>}
          {error !== null && <span className="composer-error">{error}</span>}
        </div>
      )}

      {/* The live status of a background dictation Send for THIS session (the same shared strip the
          mobile screens mount): in-flight phase, held/parked with retry controls, dropped with the
          words quoted back. Renders nothing when no dictation is in play, which is nearly always. */}
      <DictationStatusStrip sessionId={sessionId} />

      {dictating && (
        <DictationDialog
          surface="cockpit"
          onInsert={onDictateInsert}
          onSend={onDictateSend}
          onSendAudio={onDictateSendAudio}
          onClose={() => setDictating(false)}
        />
      )}
    </div>
  );
}
