import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type CSSProperties } from "react";
import { createPortal } from "react-dom";
import { dismissDictationStatus, retryDroppedDictation, sendDroppedDictationAnyway } from "../dictation/backgroundSend";
import { useDictationStatusFor } from "../dictation/status";
import { dismissTypedPrompt, sendTypedPromptAnyway } from "../dictation/typedPromptDelivery";
import "./notDeliveredIndicator.css";

// THE ONE SMALL RED CHIP FOR "YOUR PROMPT DID NOT GET THROUGH" - shared by the Cockpit and the phone.
//
// WHY IT IS SMALL. A failed delivery used to put TWO big red blocks on the session screen at once: the
// Gateway's notice as a full-width banner above the composer, and the shown-back words with "Send anyway"
// and "Dismiss" as a box under it. Together they pushed the transcript and the composer around and took the
// phone screen over. The owner's ruling (2026-10-07): one small red indicator that does not move the layout,
// and the details behind a click.
//
// WHAT IT SHOWS. The chip reads "Not delivered". Clicking it opens the details - a popover on a wide screen,
// a sheet from the bottom on a phone - with everything the two blocks used to carry: the Gateway's sentence
// verbatim, how often delivery has gone wrong on this session, the words that were not delivered, and the
// Send anyway / Retry / Dismiss actions the Gateway offered. Nothing here decides anything: the sentence and
// the offer are the Gateway's, read verbatim (CLAUDE.md rule 7). It is sticky exactly as before: it goes away
// when a prompt lands or the owner dismisses the words, never on a timer.
//
// The open details are rendered into document.body, placed against the chip: inside the app bar or the session
// pane they were clipped by a narrow frame and painted under that screen's own buttons (review of #3651).
//
// ONE CHIP PER SESSION. The session screens mount it once with the Gateway's notice (`notice`), and the
// shared DictationStatusStrip mounts it for the shown-back words on every surface that has a composer. When
// both are on one screen, the session-level one CLAIMS the session and the strip's copy renders nothing, so
// the owner sees one chip that carries both - not two.

const claims = new Map<string, number>();
const listeners = new Set<() => void>();

function subscribeClaims(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function changeClaim(sessionId: string, delta: number): void {
  const next = (claims.get(sessionId) ?? 0) + delta;
  if (next > 0) claims.set(sessionId, next);
  else claims.delete(sessionId);
  for (const l of listeners) l();
}

/** True while a session-level indicator is mounted for this session, so a second copy must stay quiet. */
export function useNotDeliveredClaimed(sessionId: string | undefined): boolean {
  return useSyncExternalStore(
    subscribeClaims,
    () => (sessionId ? claims.has(sessionId) : false),
    () => false,
  );
}

export interface NotDeliveredIndicatorProps {
  sessionId: string | undefined;
  /** The Gateway's sentence for the lost prompt (promptDeliveryNotice), verbatim, or null. */
  notice?: string | null;
  /** How often delivery has gone wrong on this session (promptDeliveryHistory), or null. */
  history?: string | null;
  /** True for the session-level mount: it owns the session's chip, and the strip's copy stays quiet. */
  claim?: boolean;
  /** Which way the popover opens on a wide screen. A phone always gets the bottom sheet. */
  placement?: "up" | "down";
}

export function NotDeliveredIndicator({ sessionId, notice = null, history = null, claim = false, placement = "up" }: NotDeliveredIndicatorProps) {
  const status = useDictationStatusFor(sessionId);
  const claimed = useNotDeliveredClaimed(sessionId);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [pos, setPos] = useState<CSSProperties | null>(null);
  const rootRef = useRef<HTMLSpanElement | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);

  // A layout effect, not a plain effect: the claim must land before the first paint, or the strip's copy shows
  // for one frame beside this one when a session screen opens on a prompt that is already shown back.
  useLayoutEffect(() => {
    if (!claim || !sessionId) return;
    changeClaim(sessionId, 1);
    return () => changeClaim(sessionId, -1);
  }, [claim, sessionId]);

  const dropped = status?.phase === "dropped" ? status : null;
  const visible = notice !== null || dropped !== null;

  // Close on Escape and on a click outside, the way any popover does. The sheet's backdrop is inside the root,
  // so a tap on it is handled by its own onClick.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    const onDown = (e: MouseEvent) => {
      const t = e.target as Node;
      if (rootRef.current?.contains(t) || panelRef.current?.contains(t)) return;
      setOpen(false);
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("mousedown", onDown);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("mousedown", onDown);
    };
  }, [open]);

  // The open panel is rendered into document.body (see the render below), so it is placed here, against the
  // chip's own position on the screen. On a phone the stylesheet makes it a sheet instead and this is not used.
  useLayoutEffect(() => {
    if (!open) return;
    const place = () => {
      const chip = rootRef.current?.getBoundingClientRect();
      // The same line the stylesheet draws: at 640 pixels or less the details are a sheet, placed by CSS.
      if (!chip || window.innerWidth <= 640) {
        setPos(null);
        return;
      }
      const width = Math.min(440, window.innerWidth - 32);
      const left = Math.min(Math.max(16, chip.right - width), window.innerWidth - 16 - width);
      setPos(
        placement === "up"
          ? { position: "fixed", left, width, bottom: window.innerHeight - chip.top + 6 }
          : { position: "fixed", left, width, top: chip.bottom + 6 },
      );
    };
    place();
    window.addEventListener("resize", place);
    return () => window.removeEventListener("resize", place);
  }, [open, placement]);

  // Focus goes into the details when they open, so a keyboard or screen reader lands on them.
  useEffect(() => {
    if (open) panelRef.current?.focus();
  }, [open]);

  // Nothing left to say: close, so the next failure starts collapsed.
  useEffect(() => {
    if (!visible) setOpen(false);
  }, [visible]);

  if (!visible) return null;
  // The strip's copy defers to the session-level chip, which already carries these words.
  if (!claim && claimed) return null;

  // The FULL message that would have been delivered (typed text included), which is exactly what "Send anyway"
  // sends. Quoting anything else would show the owner one thing and send another.
  const words = (dropped?.recoverableText ?? "").trim();

  const act = async (fn: () => Promise<void>) => {
    setBusy(true);
    try {
      await fn();
    } finally {
      setBusy(false);
    }
  };
  const onSendAnyway = () =>
    act(async () => {
      if (!dropped) return;
      if (dropped.typed) await sendTypedPromptAnyway(dropped.uploadId);
      else await sendDroppedDictationAnyway(dropped.uploadId);
    });
  const onRetryFresh = () =>
    act(async () => {
      if (dropped) await retryDroppedDictation(dropped.uploadId);
    });
  const onDismiss = () =>
    act(async () => {
      if (!dropped) return;
      if (dropped.typed) await dismissTypedPrompt(dropped.uploadId);
      else await dismissDictationStatus(dropped.uploadId);
    });

  return (
    <span ref={rootRef} className={`nd-root nd-${placement}`}>
      <span role="alert" className="nd-alert">
        <button
          type="button"
          className="nd-chip"
          aria-haspopup="dialog"
          aria-expanded={open}
          title="Your last prompt was not delivered - click for details"
          onClick={() => setOpen((v) => !v)}
        >
          <span className="nd-chip-dot" aria-hidden="true">!</span>
          Not delivered
        </button>
      </span>
      {open &&
        createPortal(
          <>
            <span className="nd-backdrop" aria-hidden="true" onClick={() => setOpen(false)} />
            <div
              ref={panelRef}
              className={`nd-panel nd-panel-${placement}`}
              role="dialog"
              aria-modal="true"
              aria-label="Not delivered"
              tabIndex={-1}
              style={pos ?? undefined}
            >
              <div className="nd-panel-head">
                <span className="nd-panel-title">Not delivered</span>
                <button type="button" className="nd-close" aria-label="Close" onClick={() => setOpen(false)}>
                  x
                </button>
              </div>
              {notice !== null && <p className="nd-notice">{notice}</p>}
              {history !== null && <p className="nd-history">{history}</p>}
              {dropped !== null && (
                <>
                  <p className="nd-label">{dropped.error ?? "That recording wasn't sent."}</p>
                  {words.length > 0 && <blockquote className="nd-quote">{words}</blockquote>}
                  <div className="nd-actions">
                    {/* Whether to offer a second send at all is the Gateway's decision (phase 2, change 1): words it
                        could not confirm arrived are shown back with Dismiss only, because they may be in already. */}
                    {dropped.offerSendAnyway === true &&
                      (words.length > 0 ? (
                        <button type="button" className="nd-btn" onClick={() => void onSendAnyway()} disabled={busy}>
                          {busy ? "Sending..." : "Send anyway"}
                        </button>
                      ) : (
                        <button type="button" className="nd-btn" onClick={() => void onRetryFresh()} disabled={busy}>
                          {busy ? "Retrying..." : "Retry"}
                        </button>
                      ))}
                    <button type="button" className="nd-btn nd-btn-quiet" onClick={() => void onDismiss()} disabled={busy}>
                      Dismiss
                    </button>
                  </div>
                </>
              )}
            </div>
          </>,
          document.body,
        )}
    </span>
  );
}
