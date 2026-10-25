import { useCallback, useEffect, useRef, useState, type Ref } from "react";
import type { SecretTransferPlace } from "../secrets/secretTransfersClient";
import { useAlerts } from "./AlertsProvider";
import { AlertsTray } from "./AlertsTray";
import { badgeText, bellLabel } from "./alertsClient";
import "./alerts.css";

// THE ALERTS BELL (the Alerts mission, section 5.3) - one component, in the phone's app bar and the Cockpit's rail
// head. A bell with a red count of the alerts that NEED AN ANSWER, and only those: the Gateway's needsAnswerCount,
// shown as "9+" above nine, with no badge at all at zero.
//
// In the Cockpit, pressing it opens the popover under it, and closing the popover puts focus back on the bell. On the
// phone, Alerts is a screen of its own (phase 5): the shell passes `onOpenPage` and the bell goes there instead.

export interface AlertsBellProps {
  /** Where a secret transfer answered in the tray is recorded: "phone" on the phone, "cockpit" on the Cockpit. */
  where: SecretTransferPlace;
  /** Open the tray straight onto this alert - a push notification's deep link. Changing it opens the tray again. */
  openAlertId?: string | null;
  /** Called when the tray opens or closes, for a shell that tracks it (the deep link is cleared on close). */
  onOpenChange?: (open: boolean) => void;
  /** Go to the Alerts screen instead of opening the popover - the phone. The bell then draws no popover at all. */
  onOpenPage?: () => void;
  /** Forwarded to the button, for a shell that returns focus to it. */
  buttonRef?: Ref<HTMLButtonElement>;
}

export function AlertsBell({ where, openAlertId, onOpenChange, onOpenPage, buttonRef }: AlertsBellProps) {
  const { needsAnswerCount } = useAlerts();
  const [open, setOpen] = useState(false);
  const bell = useRef<HTMLButtonElement>(null);

  const setOpenAndTell = useCallback(
    (next: boolean) => {
      setOpen(next);
      onOpenChange?.(next);
    },
    [onOpenChange],
  );

  useEffect(() => {
    if (openAlertId) setOpenAndTell(true);
  }, [openAlertId, setOpenAndTell]);

  const close = useCallback(() => {
    setOpenAndTell(false);
    bell.current?.focus();
  }, [setOpenAndTell]);

  const badge = badgeText(needsAnswerCount);
  const badgeNode = badge ? (
    <span className="alerts-badge" data-testid="alerts-badge" aria-hidden="true">
      {badge}
    </span>
  ) : null;
  if (onOpenPage) {
    return (
      <div className="alerts">
        <button
          ref={buttonRef}
          type="button"
          className={`alerts-bell ${badge ? "alerts-bell-needs" : ""}`}
          aria-label={bellLabel(needsAnswerCount)}
          data-testid="alerts-bell"
          onClick={onOpenPage}
        >
          <BellIcon />
          {badgeNode}
        </button>
      </div>
    );
  }
  return (
    <div className="alerts">
      <button
        ref={bell}
        type="button"
        className={`alerts-bell ${badge ? "alerts-bell-needs" : ""}`}
        aria-label={bellLabel(needsAnswerCount)}
        aria-haspopup="dialog"
        aria-expanded={open}
        data-testid="alerts-bell"
        onClick={() => (open ? close() : setOpenAndTell(true))}
      >
        <BellIcon />
        {badgeNode}
      </button>
      {open ? <AlertsTray onClose={close} where={where} initialAlertId={openAlertId ?? null} /> : null}
    </div>
  );
}

function BellIcon() {
  return (
    <svg viewBox="0 0 20 20" width="18" height="18" aria-hidden="true" focusable="false">
      <path
        d="M10 2.5a5 5 0 0 0-5 5v3.2L3.6 13.4a.7.7 0 0 0 .6 1.1h11.6a.7.7 0 0 0 .6-1.1L15 10.7V7.5a5 5 0 0 0-5-5z"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinejoin="round"
      />
      <path d="M8 16.5a2 2 0 0 0 4 0" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
    </svg>
  );
}
