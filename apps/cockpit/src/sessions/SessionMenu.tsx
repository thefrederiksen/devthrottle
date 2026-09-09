import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import {
  getHandover,
  holdSession,
  stopSession,
  type SessionDto,
  type SessionHandover,
  type SessionStopOutcome,
} from "@devthrottle/client-core/api/client";
import { renameSession } from "@devthrottle/client-core/fleet/fleetClient";
import { useSnoozeOptions } from "@devthrottle/client-core/settings/snoozeOptions";
import { buildSnoozeMenu } from "@devthrottle/client-core/settings/snoozeMenu";
import { useDismissOnBackdrop } from "../components";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

// The surface label on every client-error report from this view, so the Gateway log and
// GET /client-errors/recent name where the user was standing (issue #2189).
const SURFACE = "cockpit-session-menu";

// The session menu (issue #1214): a three-dot control with Rename, Snooze / Unsnooze, Handover info,
// and Stop session. It is the SAME component on the session page and on every rail card. Every action
// goes Cockpit -> Gateway with relative URLs through the shared client (renameSession, holdSession,
// stopSession, getHandover) - the browser never learns a Director address. A failed action shows a
// visible error, never a silent failure.
//
// STOP ASKS BEFORE IT ACTS AND SPEAKS AFTER IT HAS ACTED (mission "Stop a session", Rulings 4 and 5).
// Before: the Gateway requires a reason and records it, so the dialog collects one and will not offer a
// confirm click that could only be refused. After: the stop's answer is SHOWN - the Gateway's headline
// and then each of its detail lines, verbatim - and the dialog stays open until the user dismisses it,
// only then telling the page it may navigate away. The old Close did neither: it sent no reason and, on
// success, closed the dialog and said nothing at all, which is the defect this replaces.
//
// This view composes no sentence about a stop and never branches on the verdict word. There are four
// verdict words today and there may be five tomorrow; adding one is an edit on the Gateway, and this
// file must not need to know it happened.

export interface SessionMenuProps {
  session: SessionDto;
  /** Called after the session is closed, so the page can navigate away. */
  onClosed?: () => void;
  /** "page" sits in the session header; "rail" is the compact button on a roster card. */
  variant?: "page" | "rail";
}

type Dialog = "rename" | "stop" | "handover";

export function SessionMenu({ session, onClosed, variant = "page" }: SessionMenuProps) {
  const sid = session.sessionId ?? "";
  const [open, setOpen] = useState(false);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [renameText, setRenameText] = useState("");
  const [handover, setHandover] = useState<SessionHandover | null>(null);
  // The reason the Gateway requires with a stop, and the answer it gave back. A non-null outcome is what
  // turns the stop dialog from a question into an answer; it is never cleared while that answer is on
  // screen, because clearing it is what the old Close did the instant the call returned.
  const [stopReason, setStopReason] = useState("");
  const [stopOutcome, setStopOutcome] = useState<SessionStopOutcome | null>(null);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const btnRef = useRef<HTMLButtonElement | null>(null);
  const popRef = useRef<HTMLDivElement | null>(null);
  // The dropdown is rendered in a portal on document.body so it can never be clipped by the scrolling
  // rail or covered by a later card; its position tracks the button (issue #1214). It anchors from
  // the top (opening downward) when there is room below, and flips to anchor from the bottom (opening
  // upward) for a button near the bottom of the window, so it never runs off the bottom of the screen.
  const [popPos, setPopPos] = useState<{ top?: number; bottom?: number; right: number } | null>(null);

  // "Snooze for" and its flyout. The lengths come from one shared cache for the whole page, so opening
  // any menu never waits on the network - see useSnoozeOptions.
  const snoozeOptions = useSnoozeOptions();
  const snoozeMenu = buildSnoozeMenu(session.onHold === true, snoozeOptions);
  const [snoozeForOpen, setSnoozeForOpen] = useState(false);
  const [subPos, setSubPos] = useState<{ top: number; left?: number; right?: number } | null>(null);
  const snoozeForRef = useRef<HTMLButtonElement | null>(null);
  const subPopRef = useRef<HTMLDivElement | null>(null);
  const subCloseTimer = useRef<number | null>(null);

  // Hover intent. The flyout is portaled, so it is NOT a DOM child of "Snooze for" - moving the pointer
  // from the item to the flyout leaves the item and would close the flyout before the pointer arrives,
  // making the lengths unclickable. (The proof harness caught exactly that.) So a leave schedules the
  // close, and an enter on EITHER side cancels it.
  const keepSubOpen = useCallback(() => {
    if (subCloseTimer.current !== null) {
      window.clearTimeout(subCloseTimer.current);
      subCloseTimer.current = null;
    }
  }, []);

  const closeSubSoon = useCallback(() => {
    keepSubOpen();
    subCloseTimer.current = window.setTimeout(() => setSnoozeForOpen(false), 160);
  }, [keepSubOpen]);

  useEffect(() => () => keepSubOpen(), [keepSubOpen]);

  // Place the flyout beside "Snooze for" and open it.
  //
  // It PREFERS to open left, because the parent popup is anchored to the right edge of its button and on
  // a rail card that is usually near the right edge of the window. But "usually" is not "always" - a card
  // near the LEFT edge has no room on the left, and a left-opening flyout lands off-screen at a negative
  // x where it can never be clicked. (The proof harness caught exactly that.) So it flips to the right
  // when the left will not fit, the same way the parent flips up when the bottom will not fit.
  const placeSubmenu = useCallback(() => {
    const r = snoozeForRef.current?.getBoundingClientRect();
    if (!r) return;
    const SUB_WIDTH = 160;
    const SUB_HEIGHT = 16 + snoozeMenu.choices.length * 34;
    const top = Math.max(8, Math.min(r.top - 4, window.innerHeight - SUB_HEIGHT - 8));
    const fitsLeft = r.left - 4 - SUB_WIDTH >= 8;
    setSubPos(
      fitsLeft
        ? { top, right: window.innerWidth - r.left + 4 }
        : { top, left: Math.min(r.right + 4, window.innerWidth - SUB_WIDTH - 8) },
    );
    setSnoozeForOpen(true);
  }, [snoozeMenu.choices.length]);

  useLayoutEffect(() => {
    if (!open) return;
    const place = () => {
      const r = btnRef.current?.getBoundingClientRect();
      if (!r) return;
      const right = Math.max(8, window.innerWidth - r.right);
      // Height of the menu plus padding. If the space below the button cannot fit it, open upward from
      // the button's top instead. "Snooze for" is only present when this client knows the user's snooze
      // lengths, so the height is not fixed - measure it off the item count rather than hard-coding a
      // number that silently goes stale the next time a row is added.
      const MENU_HEIGHT = 184 + (snoozeMenu.choices.length > 0 ? 31 : 0);
      const spaceBelow = window.innerHeight - r.bottom;
      if (spaceBelow < MENU_HEIGHT + 8) {
        setPopPos({ bottom: Math.max(8, window.innerHeight - r.top + 4), right });
      } else {
        setPopPos({ top: r.bottom + 4, right });
      }
    };
    place();
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
    // The choice count is a dependency because it changes the menu's height: the lengths can arrive from
    // the Gateway while the menu is already open, and the flip decision has to be redone when they do.
  }, [open, snoozeMenu.choices.length]);

  // Close the dropdown on an outside click or Escape. The portal'd popover is outside rootRef, so it
  // is excluded explicitly.
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      const t = e.target as Node;
      if (rootRef.current?.contains(t)) return;
      if (popRef.current?.contains(t)) return;
      // The "Snooze for" flyout is its OWN portal on document.body, so it is inside neither ref above.
      // Without this it counts as an outside click: mousedown tears the menu down before the button's
      // click can fire, and the lengths are unclickable. (The proof harness caught exactly that.)
      if (subPopRef.current?.contains(t)) return;
      setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  // Dismissing any dialog. When a stop ANSWER is on screen, dismissing it is also the moment the page is
  // told it may navigate away - so the answer is never destroyed before it has been read, whichever way
  // the user dismisses it (the button, the backdrop, Escape).
  //
  // This is not a view deciding what a verdict means: it does not look at the verdict word at all. Every
  // verdict the Gateway can send is a session that is no longer running, so every one of them leaves the
  // page in the same place.
  const closeDialog = useCallback(() => {
    const stopAnswered = stopOutcome !== null;
    setDialog(null);
    setError(null);
    setHandover(null);
    setStopReason("");
    setStopOutcome(null);
    if (stopAnswered) onClosed?.();
  }, [stopOutcome, onClosed]);

  // Dismissing by clicking the backdrop. It closes only on a press that STARTED on the backdrop, so
  // highlighting the name in the rename box with the mouse and releasing past the edge of the dialog
  // no longer throws the dialog away mid-edit (owner report) - see useDismissOnBackdrop.
  const dismissDialog = useDismissOnBackdrop(closeDialog);

  const openRename = useCallback(() => {
    setOpen(false);
    setError(null);
    setRenameText(session.name ?? "");
    setDialog("rename");
  }, [session.name]);

  const openStop = useCallback(() => {
    setOpen(false);
    setError(null);
    setStopReason("");
    setStopOutcome(null);
    setDialog("stop");
  }, []);

  const openHandover = useCallback(() => {
    setOpen(false);
    setError(null);
    setHandover(null);
    setDialog("handover");
    setBusy(true);
    getHandover(sid)
      .then((h) => setHandover(h))
      .catch((err) => setError(describeAndReport(SURFACE, "load the handover information", err)))
      .finally(() => setBusy(false));
  }, [sid]);

  const doRename = useCallback(async () => {
    const name = renameText.trim();
    if (sid.length === 0 || name.length === 0) return;
    setBusy(true);
    setError(null);
    try {
      await renameSession(sid, name);
      closeDialog();
    } catch (err) {
      setError(describeAndReport(SURFACE, "rename the session", err));
    } finally {
      setBusy(false);
    }
  }, [sid, renameText, closeDialog]);

  // The plain Snooze/Unsnooze click: toggle using the user's DEFAULT length (no length sent = the
  // Gateway applies it). Mirrors the desktop's ToggleSessionHold.
  const doHold = useCallback(async () => {
    if (sid.length === 0) return;
    setOpen(false);
    setBusy(true);
    setError(null);
    try {
      await holdSession(sid, !session.onHold);
    } catch (err) {
      setError(describeAndReport(SURFACE, "snooze the session", err));
    } finally {
      setBusy(false);
    }
  }, [sid, session.onHold]);

  // A "Snooze for" choice: hold for a specific length instead of the default. Always a hold, never an
  // unsnooze - picking a length while already snoozed re-arms the timer, which is why the submenu is
  // offered while snoozed too.
  const doSnoozeFor = useCallback(async (minutes: number) => {
    if (sid.length === 0) return;
    setOpen(false);
    setSnoozeForOpen(false);
    setBusy(true);
    setError(null);
    try {
      await holdSession(sid, true, minutes);
    } catch (err) {
      setError(describeAndReport(SURFACE, "snooze the session", err));
    } finally {
      setBusy(false);
    }
  }, [sid]);

  // Send the stop. On success the dialog KEEPS the answer and stays open; the page is told nothing until
  // the user dismisses it. On failure the dialog also stays open, with the typed reason still in the box,
  // so a retry does not begin by making the user write their sentence again.
  const doStop = useCallback(async () => {
    const reason = stopReason.trim();
    if (sid.length === 0 || reason.length === 0) return;
    setBusy(true);
    setError(null);
    try {
      setStopOutcome(await stopSession(sid, reason));
    } catch (err) {
      setError(describeAndReport(SURFACE, "stop the session", err));
    } finally {
      setBusy(false);
    }
  }, [sid, stopReason]);

  return (
    <div className={`session-menu ${variant}${open ? " open" : ""}`} ref={rootRef}>
      <button
        ref={btnRef}
        type="button"
        className="session-menu-btn"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label="Session menu"
        title="Session menu"
        onClick={(e) => {
          e.preventDefault();
          e.stopPropagation();
          setError(null);
          setOpen((o) => !o);
        }}
      >
        <span aria-hidden="true">...</span>
      </button>

      {open && popPos !== null &&
        createPortal(
          <div
            ref={popRef}
            className="session-menu-pop"
            role="menu"
            style={{
              position: "fixed",
              right: popPos.right,
              // Always set BOTH top and bottom (one to a value, the other to "auto") so that when the
              // menu flips upward (bottom-anchored) no stray top from CSS can leave both set at once,
              // which would break the menu's position.
              top: popPos.top ?? "auto",
              bottom: popPos.bottom ?? "auto",
            }}
          >
            <button type="button" role="menuitem" className="session-menu-item" onClick={openRename}>
              Rename
            </button>
            <button type="button" role="menuitem" className="session-menu-item" onClick={() => void doHold()}>
              {snoozeMenu.toggleHeader}
            </button>
            {snoozeMenu.choices.length > 0 && (
              <div
                className="session-menu-sub"
                onMouseEnter={() => { keepSubOpen(); placeSubmenu(); }}
                onMouseLeave={closeSubSoon}
              >
                <button
                  ref={snoozeForRef}
                  type="button"
                  role="menuitem"
                  className="session-menu-item has-sub"
                  aria-haspopup="menu"
                  aria-expanded={snoozeForOpen}
                  onClick={() => (snoozeForOpen ? setSnoozeForOpen(false) : placeSubmenu())}
                >
                  <span>Snooze for</span>
                  <span aria-hidden="true" className="session-menu-caret">&rsaquo;</span>
                </button>
              </div>
            )}
            <button type="button" role="menuitem" className="session-menu-item" onClick={openHandover}>
              Handover info
            </button>
            <button type="button" role="menuitem" className="session-menu-item danger" onClick={openStop}>
              Stop session
            </button>
          </div>,
          document.body,
        )}

      {/* The flyout is its own portal, like the parent popup, so the scrolling rail cannot clip it. It
          stays open while the pointer is over EITHER the parent item or the flyout itself. */}
      {open && snoozeForOpen && subPos !== null &&
        createPortal(
          <div
            ref={subPopRef}
            className="session-menu-pop session-menu-subpop"
            role="menu"
            // Set BOTH sides (one to a value, the other to "auto") so a flip can never leave a stale
            // left and right set at once, which would stretch or mis-place the flyout.
            style={{
              position: "fixed",
              top: subPos.top,
              left: subPos.left ?? "auto",
              right: subPos.right ?? "auto",
              width: 160,
            }}
            onMouseEnter={keepSubOpen}
            onMouseLeave={closeSubSoon}
          >
            {snoozeMenu.choices.map((c) => (
              <button
                key={c.minutes}
                type="button"
                role="menuitem"
                className="session-menu-item"
                onClick={() => void doSnoozeFor(c.minutes)}
              >
                {c.header}
              </button>
            ))}
          </div>,
          document.body,
        )}

      {/* The action error is shown on the button row when no dialog is open (e.g. a failed Hold). */}
      {error !== null && dialog === null && <span className="session-menu-error">{error}</span>}

      {dialog !== null && (
        <div className="session-dialog-overlay" {...dismissDialog}>
          <div className="session-dialog" role="dialog" aria-modal="true">
            {dialog === "rename" && (
              <>
                <h3 className="session-dialog-title">Rename session</h3>
                <input
                  className="session-dialog-input"
                  value={renameText}
                  onChange={(e) => setRenameText(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") void doRename();
                  }}
                  placeholder="Session name"
                  autoFocus
                />
                {error !== null && <div className="session-dialog-error">{error}</div>}
                <div className="session-dialog-actions">
                  <button type="button" className="session-dialog-btn" onClick={closeDialog} disabled={busy}>
                    Cancel
                  </button>
                  <button
                    type="button"
                    className="session-dialog-btn primary"
                    onClick={() => void doRename()}
                    disabled={busy || renameText.trim().length === 0}
                  >
                    {busy ? "Saving..." : "Save"}
                  </button>
                </div>
              </>
            )}

            {/* The stop dialog is one dialog in two states: the QUESTION until the Gateway has answered,
                and then the ANSWER. It never shows both, and it never shows neither. */}
            {dialog === "stop" && stopOutcome === null && (
              <>
                <h3 className="session-dialog-title">Stop session</h3>
                <p className="session-dialog-text">
                  Stop <strong>{session.name || sid}</strong>? This ends the session on its machine and
                  removes it from the roster. Files in its worktree are left exactly as they are.
                </p>
                <label className="session-dialog-label" htmlFor="session-stop-reason">
                  Why are you stopping it? A reason is required, and it is recorded with the stop so
                  anyone reading the trail later knows what happened.
                </label>
                <input
                  id="session-stop-reason"
                  className="session-dialog-input"
                  value={stopReason}
                  onChange={(e) => setStopReason(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") void doStop();
                  }}
                  placeholder="Spawned into the wrong mode"
                  autoFocus
                />
                {error !== null && <div className="session-dialog-error">{error}</div>}
                <div className="session-dialog-actions">
                  <button type="button" className="session-dialog-btn" onClick={closeDialog} disabled={busy}>
                    Cancel
                  </button>
                  {/* Disabled on an empty or whitespace-only reason: the Gateway would refuse that stop,
                      and a control must not offer a click that can only come back refused. */}
                  <button
                    type="button"
                    className="session-dialog-btn danger"
                    onClick={() => void doStop()}
                    disabled={busy || stopReason.trim().length === 0}
                  >
                    {busy ? "Stopping..." : "Stop session"}
                  </button>
                </div>
              </>
            )}

            {/* The answer, rendered VERBATIM: the Gateway's headline, then each of its detail lines in the
                order it sent them. Nothing here is composed, and nothing here reads the verdict word. */}
            {dialog === "stop" && stopOutcome !== null && (
              <>
                <h3 className="session-dialog-title">Stop session</h3>
                <p className="session-dialog-headline">{stopOutcome.headline}</p>
                {stopOutcome.details.length > 0 && (
                  <ul className="session-dialog-details">
                    {stopOutcome.details.map((line, i) => (
                      <li key={i}>{line}</li>
                    ))}
                  </ul>
                )}
                <div className="session-dialog-actions">
                  <button type="button" className="session-dialog-btn primary" onClick={closeDialog}>
                    Done
                  </button>
                </div>
              </>
            )}

            {dialog === "handover" && (
              <>
                <h3 className="session-dialog-title">Handover info</h3>
                {busy && handover === null && <div className="session-dialog-text">Loading...</div>}
                {error !== null && <div className="session-dialog-error">{error}</div>}
                {handover !== null && (
                  <dl className="session-handover">
                    <dt>Name</dt><dd>{handover.displayName || "(unnamed)"}</dd>
                    <dt>Session ID</dt><dd className="mono">{handover.sessionId}</dd>
                    <dt>Repo</dt><dd className="mono">{handover.repoPath}</dd>
                    <dt>Director ID</dt><dd className="mono">{handover.directorId}</dd>
                    <dt>Machine</dt><dd>{handover.machineName}</dd>
                    <dt>Version</dt><dd>{handover.version}</dd>
                  </dl>
                )}
                <div className="session-dialog-actions">
                  <button type="button" className="session-dialog-btn" onClick={closeDialog}>
                    Close
                  </button>
                </div>
              </>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
