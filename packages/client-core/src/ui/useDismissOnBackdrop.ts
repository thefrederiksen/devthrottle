import { useCallback, useRef, type MouseEvent as ReactMouseEvent } from "react";

// Click-the-backdrop-to-dismiss, done so a MOUSE DRAG can never dismiss the dialog (owner report).
//
// Every Cockpit overlay used to be written the same way: a backdrop with onClick={onClose}, and the
// panel inside it stopping propagation so a click that lands on the panel is not treated as a click
// outside. That guard is wrong, and it fails in the one case that matters most - selecting text.
//
// A browser fires `click` on the nearest COMMON ANCESTOR of where the button went down and where it
// came up. Press inside the rename box, drag left to highlight the first word, release a few pixels
// past the edge of the dialog, and the common ancestor is the BACKDROP - so the browser dispatches
// the click straight at the backdrop. The panel is never on that event's path, its stopPropagation
// never runs, and the dialog closes the instant the mouse button comes up, taking the typed text with
// it. Selecting text with the mouse was simply impossible in any Cockpit dialog.
//
// So the dismissal does not ask "where did the click land" - it asks "where did the PRESS start":
// only a press that began on the backdrop itself can dismiss. A drag that begins anywhere inside the
// panel is a text selection, never a dismissal, no matter where it ends.
//
// The returned handlers go on the backdrop element. The panel inside no longer needs to stop
// propagation at all: a click whose press started inside the panel is rejected by the press test, and
// a click that merely BUBBLES from the panel is rejected because its target is not the backdrop.

export interface BackdropDismissHandlers {
  onMouseDown: (event: ReactMouseEvent) => void;
  onClick: (event: ReactMouseEvent) => void;
}

/**
 * Handlers for a modal backdrop that dismisses on an outside click.
 *
 * @param onDismiss What to run when the backdrop is genuinely clicked. Pass undefined to make the
 *   backdrop non-dismissing for now (a dialog that is mid-save, or showing a result the person must
 *   read); the handlers are still attached, they simply do nothing.
 */
export function useDismissOnBackdrop(onDismiss: (() => void) | undefined): BackdropDismissHandlers {
  // Whether the CURRENT press started on the backdrop itself. A ref, not state: it must be readable
  // by the click handler that follows the very same press, and it must never cause a re-render.
  const pressedOnBackdrop = useRef(false);

  const onMouseDown = useCallback((event: ReactMouseEvent) => {
    pressedOnBackdrop.current = event.target === event.currentTarget;
  }, []);

  const onClick = useCallback(
    (event: ReactMouseEvent) => {
      const startedOnBackdrop = pressedOnBackdrop.current;
      // Consume it either way, so a later keyboard-driven click (which has no preceding mousedown)
      // can never inherit a stale "yes" from an earlier press.
      pressedOnBackdrop.current = false;
      if (!startedOnBackdrop) return;
      // The press started on the backdrop AND the event is aimed at the backdrop, not bubbling up
      // from something inside it.
      if (event.target !== event.currentTarget) return;
      onDismiss?.();
    },
    [onDismiss],
  );

  return { onMouseDown, onClick };
}
