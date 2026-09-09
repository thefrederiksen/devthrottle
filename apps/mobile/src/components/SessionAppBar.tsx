import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { holdPillLabel } from "@devthrottle/client-core/sessions/snoozeAction";
import type { SessionStopOutcome } from "@devthrottle/client-core/api/client";
import type { SessionManage } from "./useSessionManage";

// The ONE app bar shared by every per-session screen: Chat, Terminal and Voice mode (owner design
// review, "Option A"). It replaces the old SessionManageBar row, which mixed navigation (back to the
// roster) with actions (Snooze) and a destructive verb (Remove) in three equal-weight coloured slabs
// directly under the tab you press most - which is how Remove got mis-tapped.
//
// The rules this encodes, in the owner's order of use:
//
//   * Back to Sessions is NAVIGATION, so it lives at the LEFT of the app bar and is identical on
//     every screen. styles.css has always intended this ("Shared by every per-session screen via
//     .back-link", issue #1004); the session screens just never used it.
//   * Stop is rare and destructive, so it is in the overflow menu - still two taps away, never
//     under your thumb. It keeps its confirmation.
//   * Frequent actions do NOT live up here. They belong at the bottom, in the thumb zone; Voice mode
//     puts Snooze and Respond there. Screens with no bottom room for Snooze (Chat/Terminal, whose
//     bottom is the message composer) pass showSnooze so it appears in this menu instead.
//
// A CONTROL outranks an INDICATOR for the top-right corner. This bar used to put the overflow button
// on the LEFT, because the globally-mounted network pill was fixed to the top-right and nothing wanted
// to guess its width. The cost was a broken menu: .session-menu is anchored right:0 - correct for a
// button on the right - so hanging it off a LEFT button opened it off the left edge of the screen,
// cut in half and unreadable. The pill was then moved inline onto row 1, and finally deleted outright
// (see ConnectionBanner - the app says nothing while the connection is fine). The corner is the
// button's for good. Nothing guesses any widths: both rows are plain flex.
//
//   row 1:  [<- Sessions] ................... [...]     navigation left, menu right
//   row 2:  102 devthrottle / f9e7 .....................  the name gets the whole row
//
// THE STOP SHEET IS THE COCKPIT'S STOP DIALOG, LAID OUT FOR A PHONE (mission "Stop a session"). Same
// words, same behaviour, different layout: it asks for the reason the Gateway requires before it acts
// and will not submit an empty one, and when the answer comes back it SHOWS it - the Gateway's headline
// and then each of its detail lines, verbatim - and waits to be dismissed. Leaving for the roster is
// what dismissing it does, so the answer is never destroyed by a navigation the user did not ask for.
// The phone may show less of a card than the desktop; it may not say something different, offer
// something different, or leave anything out (the reasoning behind CLAUDE.md rule 8).
//
// A FAILED STOP IS SHOWN INSIDE THE SHEET (inspection finding I8). It used to render on this bar's
// sibling error banner, which sits UNDER the full-screen overlay of a dialog that declares
// aria-modal="true" - so the sheet stayed open with a reason box, a retry button and no explanation
// anywhere the operator could see. Worse, backing out with Cancel cleared that banner as well, so the
// one copy of the explanation was deleted by the gesture used to go and read it. Now the Gateway's
// sentence renders in the card the operator is looking at, and backing out leaves it on the banner
// behind rather than throwing it away. The bar's banner is suppressed while the sheet is open, so the
// failure is described once, in one place, exactly as the Cockpit describes it.
//
// Nothing here composes a sentence about a stop and nothing branches on the verdict word.

export interface SessionAppBarProps {
  title: string;
  manage: SessionManage;
  /** Put Snooze/Unsnooze in the overflow menu. Screens that surface Snooze in their own bottom bar
   *  (Voice mode) leave this off, so the verb is never in two places at once. */
  showSnooze?: boolean;
  /** Offer "Switch to voice mode" in the menu. The screens that are NOT voice (Chat/Terminal) pass
   *  this so voice is reachable in one tap from where you already are, instead of making you open the
   *  Voice mode tab first purely to find the button on it. */
  showSwitchToVoice?: boolean;
  /** Screen-specific menu entries, rendered above Stop. Use <button className="menu-item">. */
  extraMenuItems?: ReactNode;
  /** Router state for the Back to Sessions navigation. When supplied, the session route is replaced
   *  by the roster route so browser Back cannot reopen the session that was just left. */
  backState?: Record<string, unknown>;
}

export function SessionAppBar({ title, manage, showSnooze = false, showSwitchToVoice = false, extraMenuItems, backState }: SessionAppBarProps) {
  const navigate = useNavigate();
  const { sessionId } = useParams<{ sessionId: string }>();
  const [open, setOpen] = useState(false);
  const [confirming, setConfirming] = useState(false);
  // The reason the Gateway requires with a stop, and the answer it gave back. A non-null outcome turns
  // the sheet from a question into an answer, and it is what the dismiss control then leaves on.
  const [stopReason, setStopReason] = useState("");
  const [stopOutcome, setStopOutcome] = useState<SessionStopOutcome | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);

  // Close the menu on an outside tap or Escape, the way a menu is expected to behave.
  useEffect(() => {
    if (!open) return;
    const onPointer = (e: PointerEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("pointerdown", onPointer);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("pointerdown", onPointer);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  // Send the stop and KEEP the answer. The sheet stays open either way: on success so the answer can be
  // read, and on failure so the typed reason is not lost and the banner behind it is readable.
  const onConfirmStop = useCallback(async () => {
    const reason = stopReason.trim();
    if (reason.length === 0) return;
    try {
      setStopOutcome(await manage.stopSession(reason));
    } catch {
      /* the hook has already surfaced the message on manage.error */
    }
  }, [manage, stopReason]);

  // Dismissing the answer is what leaves for the roster - never the stop returning. The session is gone
  // under every verdict the Gateway can send, so this looks at no verdict word.
  const onDismissStopOutcome = useCallback(() => {
    setConfirming(false);
    setStopReason("");
    setStopOutcome(null);
    navigate("/");
  }, [navigate]);

  // Backing out of the question. It does NOT clear manage.error: the operator may be backing out of a
  // stop that FAILED, and the failure is the one explanation of why the session is still here. Clearing
  // it here is what deleted it. Opening the sheet again clears it, which is the right moment - that is
  // a fresh question, so there is nothing yet to explain.
  const onCancelStop = useCallback(() => {
    setConfirming(false);
    setStopReason("");
  }, []);

  return (
    <>
      <header className="app-bar session-app-bar">
        <button
          type="button"
          className="back-link session-back"
          onClick={() =>
            navigate("/", backState !== undefined ? { state: backState, replace: true } : undefined)
          }
          aria-label="Back to sessions"
        >
          &larr; Sessions
        </button>

        {/* The spacer pushes the menu button to the corner and guesses no widths. It used to be two
            spacers with the network status pill centred between them; the pill is gone (see
            ConnectionBanner) and row 1 is now just Back and the menu button. */}
        <div className="session-bar-spacer" />

        <div className="session-menu-wrap" ref={menuRef}>
          <button
            type="button"
            className="session-menu-btn"
            onClick={() => setOpen((v) => !v)}
            aria-haspopup="menu"
            aria-expanded={open}
            aria-label="Session menu"
          >
            <span className="session-menu-dots" aria-hidden="true" />
          </button>

          {open && (
            <div className="session-menu" role="menu">
              {showSwitchToVoice && (
                <button
                  type="button"
                  className="menu-item"
                  role="menuitem"
                  onClick={() => {
                    setOpen(false);
                    // Hand the switch-on to the Voice screen rather than doing it here: useVoiceMode
                    // already owns that verb (mark the session Voice on its Director, then explain on
                    // the Gateway). Duplicating it here would be a second copy free to disagree with
                    // the first. The screen reads this and runs its own onSwitchOn once, on arrival.
                    navigate(`/session/${encodeURIComponent(sessionId ?? "")}/voice`, {
                      state: { switchOn: true },
                    });
                  }}
                >
                  Switch to voice mode
                </button>
              )}

              {showSnooze && (
                <button
                  type="button"
                  className="menu-item"
                  role="menuitem"
                  onClick={() => {
                    setOpen(false);
                    void manage.toggleHold();
                  }}
                  disabled={manage.busy || manage.onHold === null}
                >
                  {manage.held || manage.deferred ? "Unsnooze" : "Snooze"}
                </button>
              )}

              {extraMenuItems !== undefined && (
                <div className="menu-group" onClick={() => setOpen(false)}>
                  {extraMenuItems}
                </div>
              )}

              <button
                type="button"
                className="menu-item menu-item-danger"
                role="menuitem"
                onClick={() => {
                  setOpen(false);
                  setStopReason("");
                  setStopOutcome(null);
                  manage.setError(null);
                  setConfirming(true);
                }}
                disabled={manage.busy}
              >
                Stop session
              </button>
            </div>
          )}
        </div>

      </header>

      {/* The session name gets its OWN row, and now the WHOLE row: it cannot share row one, where the
          back button and the menu button leave no room for a real name ("102 Ghost Directors from tests"
          collapsed to "102 M..."). The network pill used to ride the end of this row and has moved up to
          the middle of row 1 - see the note there; in short, it was sitting directly above the Voice mode
          tab and getting hit instead of it. */}
      <div className="session-title-row">
        <h1 className="term-title session-title">{title}</h1>
      </div>

      {/* The action error banner. It is suppressed while the stop sheet is open, because the sheet
          shows the same sentence inside itself and a modal overlay covers this row anyway - two copies
          of one event, one of them unreadable, is exactly what finding I8 was. */}
      {manage.error !== null && !confirming && (
        <div className="banner banner-error" role="alert">{manage.error}</div>
      )}
      {/* A prompt to this session was NOT delivered - the user's words never reached the agent (issue
          internal#811). It lives on the shared app bar so it is on EVERY per-session screen: the loss
          usually happens while the phone is somewhere else entirely (a dictation sent from the roster,
          the screen locked), so a banner that only existed on one tab would be a banner nobody sees.
          Sticky by design - it clears when a prompt actually lands, never on a tap and never on a timer -
          because a failure the user can wave away is how two spoken prompts went missing for two days. */}
      {manage.deliveryNotice !== null && (
        <div className="banner banner-not-delivered" role="alert">{manage.deliveryNotice}</div>
      )}
      {/* The snooze pill. A DEFERRED snooze reads "Snoozing when it finishes" (asked for while the agent is
          working, so it arms when the work ends) - this is what makes snoozing a busy session give instant
          feedback instead of looking like nothing happened. An armed snooze reads the Gateway FOLD
          (manage.snoozed), not the raw onHold flag - a Held session that has started working is blue
          "Working" and must not still read "Snoozed" - with the running hold time beside it, matching the
          roster and the desktop rail. */}
      {(() => {
        const pill = holdPillLabel(
          { held: manage.held, deferred: manage.deferred },
          manage.snoozed,
          manage.holdCountdown,
        );
        return pill !== null ? <span className="manage-held-pill">{pill}</span> : null;
      })()}

      {/* One sheet in two states: the QUESTION until the Gateway has answered, then the ANSWER. */}
      {confirming && stopOutcome === null && (
        <div className="confirm-overlay" role="dialog" aria-modal="true" aria-label="Stop session">
          <div className="confirm-card">
            <h2 className="confirm-title">Stop this session?</h2>
            <p className="confirm-text">
              This ends the session on its machine and removes it from the roster. Files in its worktree
              are left exactly as they are.
            </p>
            <label className="confirm-label" htmlFor="session-stop-reason">
              Why are you stopping it? A reason is required, and it is recorded with the stop so anyone
              reading the trail later knows what happened.
            </label>
            <input
              id="session-stop-reason"
              className="confirm-input"
              value={stopReason}
              onChange={(e) => setStopReason(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") void onConfirmStop();
              }}
              placeholder="Spawned into the wrong mode"
              autoFocus
            />
            {/* The failure, in the Gateway's own words, INSIDE the dialog the operator is looking at -
                the same place the Cockpit puts it, and above the retry button it explains. */}
            {manage.error !== null && (
              <div className="confirm-error" role="alert">{manage.error}</div>
            )}
            <div className="confirm-actions">
              <button
                type="button"
                className="confirm-btn confirm-cancel"
                onClick={onCancelStop}
                disabled={manage.busy}
              >
                Cancel
              </button>
              {/* Disabled on an empty or whitespace-only reason: the Gateway would refuse that stop, and
                  a control must not offer a tap that can only come back refused. */}
              <button
                type="button"
                className="confirm-btn confirm-remove"
                onClick={onConfirmStop}
                disabled={manage.busy || stopReason.trim().length === 0}
              >
                {manage.busy ? "Stopping..." : "Stop"}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* The answer, rendered VERBATIM: the Gateway's headline, then each of its detail lines in the
          order it sent them. The same strings the Cockpit shows, on a phone-sized card. */}
      {confirming && stopOutcome !== null && (
        <div className="confirm-overlay" role="dialog" aria-modal="true" aria-label="Stop session">
          <div className="confirm-card">
            <h2 className="confirm-title">Stop session</h2>
            <p className="confirm-headline">{stopOutcome.headline}</p>
            {stopOutcome.details.length > 0 && (
              <ul className="confirm-details">
                {stopOutcome.details.map((line, i) => (
                  <li key={i}>{line}</li>
                ))}
              </ul>
            )}
            <div className="confirm-actions">
              <button type="button" className="confirm-btn confirm-cancel" onClick={onDismissStopOutcome}>
                Done
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
