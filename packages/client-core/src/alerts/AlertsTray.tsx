import { useCallback, useEffect, useRef, type KeyboardEvent as ReactKeyboardEvent } from "react";
import type { SecretTransferPlace } from "../secrets/secretTransfersClient";
import { useDismissOnBackdrop } from "../ui/useDismissOnBackdrop";
import { AlertsList, CrossIcon } from "./AlertsList";
import "./alerts.css";

// THE ALERTS POPOVER (the Alerts mission, section 5.5 and phase 5) - the Cockpit's frame around the shared AlertsList,
// anchored to the bell. The phone does not use it: there Alerts is a screen of its own.
//
// It is a modal dialog: Escape closes it, Tab and Shift+Tab stay inside it while it is open, and the bell takes focus
// back on close. A shell places it only through the position and size variables documented at the top of alerts.css.

export interface AlertsTrayProps {
  onClose: () => void;
  /** Where a secret transfer answered here is recorded. */
  where: SecretTransferPlace;
  /** Bring this alert into view (a push notification's deep link). */
  initialAlertId?: string | null;
}

const FOCUSABLE = 'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), summary, [tabindex]:not([tabindex="-1"])';

export function AlertsTray({ onClose, where, initialAlertId }: AlertsTrayProps) {
  const panel = useRef<HTMLDivElement>(null);
  const backdrop = useDismissOnBackdrop(onClose);

  // Focus moves into the popover when it opens - unless a deep link is about to put it on its alert.
  useEffect(() => {
    if (initialAlertId) return;
    panel.current?.querySelector<HTMLElement>(FOCUSABLE)?.focus();
  }, [initialAlertId]);

  const onKeyDown = useCallback(
    (event: ReactKeyboardEvent<HTMLDivElement>) => {
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        onClose();
        return;
      }
      if (event.key !== "Tab" || !panel.current) return;
      const items = [...panel.current.querySelectorAll<HTMLElement>(FOCUSABLE)];
      if (items.length === 0) return;
      const first = items[0];
      const last = items[items.length - 1];
      const active = document.activeElement;
      if (event.shiftKey && (active === first || !panel.current.contains(active))) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && (active === last || !panel.current.contains(active))) {
        event.preventDefault();
        first.focus();
      }
    },
    [onClose],
  );

  return (
    <>
      <div className="alerts-backdrop" data-testid="alerts-backdrop" {...backdrop} />
      <div
        ref={panel}
        className="alerts-tray"
        role="dialog"
        aria-modal="true"
        aria-label="Alerts"
        data-testid="alerts-tray"
        onKeyDown={onKeyDown}
      >
        <header className="alerts-tray-head">
          <h2 className="alerts-tray-heading">Alerts</h2>
          <button type="button" className="alerts-icon-btn alerts-close" aria-label="Close alerts" onClick={onClose}>
            <CrossIcon />
          </button>
        </header>
        <div className="alerts-tray-body">
          <AlertsList where={where} focusAlertId={initialAlertId} />
        </div>
      </div>
    </>
  );
}
