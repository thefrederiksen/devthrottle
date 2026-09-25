import { useEffect, useState } from "react";
import {
  abandonPendingDictation,
  dismissDictationStatus,
  retryDroppedDictation,
  retryPendingDictation,
  sendDroppedDictationAnyway,
} from "./backgroundSend";
import { clearDictationStatus, useDictationStatusFor } from "./status";
// The strip carries its own styles, exactly like DictationDialog does (issue #1288's rule): every
// shell that mounts it gets the dictate-strip-* rules from this one copy. The rules formerly lived
// only in the mobile stylesheet (apps/mobile/src/styles.css); they moved here when the strip itself
// was hoisted out of the mobile app so the Cockpit could mount it too.
import "./dictationStrip.css";

// The on-screen live-status strip for a dictation Send - SHARED between the mobile shell (Terminal,
// Chat, and Voice screens) and the Cockpit (session composer, Voice tab). Owner rule after #1139: a
// dictation must never fail silently, and while the user stays on the screen it must show what is
// happening. It is non-blocking - a thin bar, not a modal - so the user can keep working or walk
// away; on the phone the roster badge (Home) carries the same status once they leave, because both
// read the one shared store.
//
// It lived in apps/mobile/src/components until the Cockpit was rewired onto the same fire-and-forget
// dictation pipeline the phone uses; a background send without this strip is a send that can hold or
// drop with no feedback at all, which is exactly why the Cockpit was kept OFF that pipeline before.
//
// While a send is in flight it shows the live phase (saving -> uploading N of M -> transcribing). On
// success it shows a brief "Sent" that clears itself. A held send (saved and still being delivered on a
// bad connection) shows a calm amber strip with the honest reason and an "Upload now" control that kicks
// a waiting or throttled retry to full speed - it is not a failure, so it offers no Dismiss. A PARKED send
// (a permanent failure that stopped the auto-loop, issue #1184) shows the saved-and-retryable message with
// an explicit "Retry" control - the audio is safe, delivery just no longer loops on its own. A genuine
// failure (durable storage unavailable, so nothing could be queued) shows a red alert with Dismiss; it
// does NOT disappear on its own, so it can never be missed.
//
// A DROPPED send (issue #1590) is the loud one: the session moved on before the recording arrived, so the
// server threw the user's words away. It shows a red alert that never clears itself, quotes the words back,
// and offers "Send anyway" - which sends them as a fresh, normal turn (re-driving the dictation itself is
// useless by design; its moved-on tombstone is permanent). On the rare drop before transcription there are no
// words to show, so it offers "Retry" instead, which re-sends the recording under a fresh upload id. Both
// carry a Dismiss, and Dismiss is the ONLY thing that throws the words away - never a timer.
// An UNHEARD send is the quiet cousin: the clip arrived and had no speech in it, so there was no turn to
// make. Nothing was lost and there is nothing to retry, but it is still an answer rather than silence.

const DONE_AUTOCLEAR_MS = 2500;

export function DictationStatusStrip({ sessionId }: { sessionId: string | undefined }) {
  const status = useDictationStatusFor(sessionId);
  const [uploadingNow, setUploadingNow] = useState(false);

  // A clean successful send is acknowledged briefly, then clears itself so the strip does not linger. A
  // delivered send that dropped audio carries a warning and must NOT auto-clear - the user has to see and
  // dismiss it, so a Send that lost words is never silent.
  useEffect(() => {
    if (status?.phase !== "done" || status.warning) return;
    const uploadId = status.uploadId;
    const t = window.setTimeout(() => clearDictationStatus(uploadId), DONE_AUTOCLEAR_MS);
    return () => window.clearTimeout(t);
  }, [status?.phase, status?.uploadId, status?.warning]);

  if (!status) return null;

  if (status.phase === "held") {
    const onUploadNow = async () => {
      setUploadingNow(true);
      try {
        await retryPendingDictation(status.uploadId);
      } finally {
        setUploadingNow(false);
      }
    };
    // Still delivering (voice delivery, #3398): the words may already be in the session. Shown calm and in
    // progress, like a send under way - not amber, not red - and with nothing that could send a second copy:
    // no "Send anyway" and no fresh-id "Retry". "Upload now" re-drives this same upload id, which the
    // Director refuses to type twice, so it stays.
    if (status.delivering) {
      return (
        <div className="dictate-strip dictate-strip-busy dictate-strip-delivering" role="status">
          <span className="dictate-strip-spin" aria-hidden="true" />
          <span className="dictate-strip-text">{status.error ?? "Still delivering"}</span>
          <button type="button" className="dictate-strip-btn" onClick={() => void onUploadNow()} disabled={uploadingNow}>
            {uploadingNow ? "Uploading..." : "Upload now"}
          </button>
        </div>
      );
    }
    return (
      <div className="dictate-strip dictate-strip-held" role="status">
        <span className="dictate-strip-icon" aria-hidden="true">!</span>
        <span className="dictate-strip-text">{status.error ?? "Saved - still trying to send your recording..."}</span>
        <button type="button" className="dictate-strip-btn" onClick={() => void onUploadNow()} disabled={uploadingNow}>
          {uploadingNow ? "Uploading..." : "Upload now"}
        </button>
        <button type="button" className="dictate-strip-btn dictate-strip-cancel" onClick={() => void abandonPendingDictation(status.uploadId)} disabled={uploadingNow}>
          Cancel
        </button>
      </div>
    );
  }

  if (status.phase === "parked") {
    const onRetry = async () => {
      setUploadingNow(true);
      try {
        await retryPendingDictation(status.uploadId);
      } finally {
        setUploadingNow(false);
      }
    };
    return (
      <div className="dictate-strip dictate-strip-parked" role="status">
        <span className="dictate-strip-icon" aria-hidden="true">!</span>
        <span className="dictate-strip-text">{status.error ?? "Saved on your device - you can retry it."}</span>
        <button type="button" className="dictate-strip-btn" onClick={() => void onRetry()} disabled={uploadingNow}>
          {uploadingNow ? "Retrying..." : "Retry"}
        </button>
        <button type="button" className="dictate-strip-btn dictate-strip-cancel" onClick={() => void abandonPendingDictation(status.uploadId)} disabled={uploadingNow}>
          Cancel
        </button>
      </div>
    );
  }

  // Dropped as stale (issue #1590). Sticky by construction: there is no timer on this arm, and nothing but
  // an explicit user action removes it. role="alert" because the user's words were NOT delivered.
  if (status.phase === "dropped") {
    // The FULL message that would have been delivered (typed text included), which is exactly what "Send
    // anyway" sends. Quoting anything else would show the user one thing and send another.
    const words = (status.recoverableText ?? "").trim();
    const onSendAnyway = async () => {
      setUploadingNow(true);
      try {
        await sendDroppedDictationAnyway(status.uploadId);
      } finally {
        setUploadingNow(false);
      }
    };
    const onRetryFresh = async () => {
      setUploadingNow(true);
      try {
        await retryDroppedDictation(status.uploadId);
      } finally {
        setUploadingNow(false);
      }
    };
    return (
      <div className="dictate-strip dictate-strip-dropped" role="alert">
        <span className="dictate-strip-icon" aria-hidden="true">!</span>
        <div className="dictate-strip-body">
          <span className="dictate-strip-text">{status.error ?? "That recording wasn't sent."}</span>
          {words.length > 0 && <blockquote className="dictate-strip-quote">{words}</blockquote>}
        </div>
        {/* Whether to offer a second send at all is the Gateway's decision (phase 2, change 1): a recording it
            could not confirm arrived is shown back with Dismiss only, because the words may be in already. */}
        {status.offerSendAnyway === true &&
          (words.length > 0 ? (
            <button type="button" className="dictate-strip-btn" onClick={() => void onSendAnyway()} disabled={uploadingNow}>
              {uploadingNow ? "Sending..." : "Send anyway"}
            </button>
          ) : (
            <button type="button" className="dictate-strip-btn" onClick={() => void onRetryFresh()} disabled={uploadingNow}>
              {uploadingNow ? "Retrying..." : "Retry"}
            </button>
          ))}
        <button
          type="button"
          className="dictate-strip-btn dictate-strip-dismiss"
          onClick={() => void dismissDictationStatus(status.uploadId)}
          disabled={uploadingNow}
        >
          Dismiss
        </button>
      </div>
    );
  }

  // Nothing was heard (issue #1590): not a failure, but never silence either.
  if (status.phase === "unheard") {
    return (
      <div className="dictate-strip dictate-strip-unheard" role="status">
        <span className="dictate-strip-icon" aria-hidden="true">!</span>
        <span className="dictate-strip-text">{status.error ?? "Nothing was heard in that recording."}</span>
        <button
          type="button"
          className="dictate-strip-btn dictate-strip-dismiss"
          onClick={() => void dismissDictationStatus(status.uploadId)}
        >
          Dismiss
        </button>
      </div>
    );
  }

  if (status.phase === "failed") {
    return (
      <div className="dictate-strip dictate-strip-failed" role="alert">
        <span className="dictate-strip-icon" aria-hidden="true">!</span>
        <span className="dictate-strip-text">{status.error ?? "Dictation failed."}</span>
        <button type="button" className="dictate-strip-btn dictate-strip-dismiss" onClick={() => clearDictationStatus(status.uploadId)}>
          Dismiss
        </button>
      </div>
    );
  }

  if (status.phase === "done") {
    // Delivered, but the capture dropped audio: the words went in, yet the transcript may be missing some.
    // Show a non-blocking caution that stays until dismissed (never a silent "Sent"), so the user knows to
    // check the result. role="status" not "alert" - nothing failed, the send succeeded.
    if (status.warning) {
      return (
        <div className="dictate-strip dictate-strip-warning" role="status">
          <span className="dictate-strip-icon" aria-hidden="true">!</span>
          <span className="dictate-strip-text">{status.warning}</span>
          <button type="button" className="dictate-strip-btn dictate-strip-dismiss" onClick={() => clearDictationStatus(status.uploadId)}>
            Dismiss
          </button>
        </div>
      );
    }
    return (
      <div className="dictate-strip dictate-strip-done" role="status">
        <span className="dictate-strip-icon" aria-hidden="true">+</span>
        <span className="dictate-strip-text">Sent</span>
      </div>
    );
  }

  // In flight: saving / uploading / transcribing.
  return (
    <div className="dictate-strip dictate-strip-busy" role="status">
      <span className="dictate-strip-spin" aria-hidden="true" />
      <span className="dictate-strip-text">{busyLabel(status.phase, status.uploaded, status.total)}</span>
    </div>
  );
}

function busyLabel(phase: string, uploaded?: number, total?: number): string {
  if (phase === "saving") return "Saving your recording...";
  if (phase === "uploading") {
    if (total && total > 1) return `Uploading recording... ${uploaded ?? 0} of ${total}`;
    return "Uploading recording...";
  }
  if (phase === "transcribing") return "Transcribing...";
  return "Working...";
}
