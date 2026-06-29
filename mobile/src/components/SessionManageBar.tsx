import { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { holdSession, killSession, listSessions } from "../api/client";

// Session management buttons on the SESSION screen (issue #812, owner decision A). Hold and Remove
// have NO Android UI (they are backend-only verbs), so the owner placed them as LARGE BUTTONS on the
// session screen (the Terminal/Chat detail), shared by both views:
//
//   * Hold / Resume: a toggle that calls POST /sessions/{sid}/hold. The button label and the held
//     pill reflect the live on-hold state, which stays consistent with the Home roster (both read
//     SessionDto.onHold).
//   * Remove: shows a confirmation ("Remove this session? This will terminate it."); on confirm it
//     calls DELETE /sessions/{sid} (kills + removes) and returns to the Home roster, where the
//     session is gone (the #545 pattern). Cancel does nothing.
//
// The bar polls the roster so the held state reflects changes made elsewhere (e.g. the desktop),
// exactly the way the Home roster does. Every call carries the Bearer token.

const POLL_INTERVAL_MS = 4000;

export interface SessionManageBarProps {
  sessionId: string | undefined;
}

export function SessionManageBar({ sessionId }: SessionManageBarProps) {
  const navigate = useNavigate();
  const [onHold, setOnHold] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // While a toggle is in flight the optimistic state must not be clobbered by a slower poll.
  const pendingRef = useRef(false);

  // Track this session's live on-hold state from the same roster the Home page reads, so the held
  // state is consistent between the session screen and the roster (AC6).
  useEffect(() => {
    if (!sessionId) return;
    const controller = new AbortController();
    let cancelled = false;
    const refresh = async () => {
      try {
        const all = await listSessions(controller.signal);
        if (cancelled || pendingRef.current) return;
        const match = all.find((s) => s.sessionId === sessionId);
        if (match) setOnHold(Boolean(match.onHold));
      } catch {
        /* keep the last-known held state; the action buttons surface their own errors */
      }
    };
    void refresh();
    const timer = window.setInterval(() => void refresh(), POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      controller.abort();
      window.clearInterval(timer);
    };
  }, [sessionId]);

  const toggleHold = useCallback(async () => {
    if (!sessionId || busy) return;
    const desired = !(onHold ?? false);
    setBusy(true);
    pendingRef.current = true;
    setError(null);
    try {
      const applied = await holdSession(sessionId, desired);
      setOnHold(applied);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Hold failed");
    } finally {
      pendingRef.current = false;
      setBusy(false);
    }
  }, [sessionId, busy, onHold]);

  const confirmRemove = useCallback(async () => {
    if (!sessionId || busy) return;
    setBusy(true);
    setError(null);
    try {
      await killSession(sessionId);
      // Return to the Home roster, where the session is now gone (the #545 pattern).
      navigate("/");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Remove failed");
      setBusy(false);
      setConfirming(false);
    }
  }, [sessionId, busy, navigate]);

  const held = onHold === true;

  return (
    <div className="session-manage">
      {error !== null && <div className="banner banner-error" role="alert">{error}</div>}
      <div className="session-manage-row">
        <button
          type="button"
          className={`manage-btn ${held ? "manage-resume" : "manage-hold"}`}
          onClick={toggleHold}
          disabled={busy || onHold === null}
        >
          {held ? "Resume" : "Hold"}
        </button>
        <button
          type="button"
          className="manage-btn manage-remove"
          onClick={() => setConfirming(true)}
          disabled={busy}
        >
          Remove
        </button>
      </div>
      {held && <span className="manage-held-pill">On hold</span>}

      {confirming && (
        <div className="confirm-overlay" role="dialog" aria-modal="true" aria-label="Remove session">
          <div className="confirm-card">
            <h2 className="confirm-title">Remove this session?</h2>
            <p className="confirm-text">This will terminate it. This cannot be undone.</p>
            <div className="confirm-actions">
              <button
                type="button"
                className="confirm-btn confirm-cancel"
                onClick={() => setConfirming(false)}
                disabled={busy}
              >
                Cancel
              </button>
              <button
                type="button"
                className="confirm-btn confirm-remove"
                onClick={confirmRemove}
                disabled={busy}
              >
                {busy ? "Removing..." : "Remove"}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
