import { useCallback, useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import type { SecretTransferPlace } from "../secrets/secretTransfersClient";
import { useDismissOnBackdrop } from "../ui/useDismissOnBackdrop";
import { useAlerts } from "./AlertsProvider";
import { AlertDetail } from "./AlertDetail";
import { alertClock, type AlertDto } from "./alertsClient";
import "./alerts.css";

// THE ALERTS TRAY (the Alerts mission, section 5.3). "Needs you" first, "Earlier" below, one line per alert, newest
// first. A row opens the alert; an Earlier row has an X that clears it, and the header has "Clear all" while Earlier
// holds anything. A failed read keeps the last-known list and says so in one line.
//
// It is a modal dialog: Escape closes it, Tab and Shift+Tab stay inside it while it is open, and the shell's caller
// returns focus to the bell on close. Opening it marks the alerts it shows as seen.
//
// The same component is a bottom sheet on the phone and a popover under the bell in the Cockpit. A shell chooses which
// only by setting the position and size variables documented at the top of alerts.css - never by copying a rule.

export interface AlertsTrayProps {
  onClose: () => void;
  /** Where a secret transfer answered here is recorded: "phone" on the phone, "cockpit" on the Cockpit. */
  where: SecretTransferPlace;
  /** Open straight onto this alert (a push notification's deep link). */
  initialAlertId?: string | null;
}

const FOCUSABLE = 'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), summary, [tabindex]:not([tabindex="-1"])';

export function AlertsTray({ onClose, where, initialAlertId }: AlertsTrayProps) {
  const ctx = useAlerts();
  const [openId, setOpenId] = useState<string | null>(initialAlertId ?? null);
  const panel = useRef<HTMLDivElement>(null);
  const backdrop = useDismissOnBackdrop(onClose);

  useEffect(() => {
    setOpenId(initialAlertId ?? null);
  }, [initialAlertId]);

  // Opening the tray marks what it shows as seen - once per alert version, so a read that has not caught up yet does
  // not send the same ids again on every render.
  const sentSeen = useRef<Set<string>>(new Set());
  const { alerts, markSeen } = ctx;
  useEffect(() => {
    const unseen = alerts.filter((a) => !a.seen && !sentSeen.current.has(`${a.id}@${a.updatedAtUtc}`));
    if (unseen.length === 0) return;
    for (const a of unseen) sentSeen.current.add(`${a.id}@${a.updatedAtUtc}`);
    void markSeen(unseen.map((a) => a.id));
  }, [alerts, markSeen]);

  // Focus moves into the tray when it opens, and onto the back button when an alert opens.
  useEffect(() => {
    const first = panel.current?.querySelector<HTMLElement>(FOCUSABLE);
    first?.focus();
  }, [openId]);

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

  const opened = openId === null ? null : ctx.alerts.find((a) => a.id === openId) ?? null;

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
        <div className="alerts-tray-handle" aria-hidden="true" />
        {openId !== null ? (
          <>
            <header className="alerts-tray-head">
              <button type="button" className="alerts-icon-btn" aria-label="Back to alerts" onClick={() => setOpenId(null)}>
                <BackIcon />
              </button>
              <h2 className="alerts-tray-heading">Alert</h2>
              <CloseButton onClose={onClose} />
            </header>
            <div className="alerts-tray-body">
              {opened ? (
                <AlertDetail alert={opened} where={where} onAnswered={() => void ctx.refresh()} />
              ) : (
                <p className="alert-line alert-dim">{ctx.loaded ? "This alert is no longer listed." : "Loading..."}</p>
              )}
            </div>
          </>
        ) : (
          <>
            <header className="alerts-tray-head">
              <h2 className="alerts-tray-heading">Alerts</h2>
              {ctx.earlier.length > 0 ? (
                <button type="button" className="alert-btn alerts-clear-all" disabled={ctx.clearing !== null} onClick={() => void ctx.clearAll()}>
                  {ctx.clearing === "all" ? "Clearing..." : "Clear all"}
                </button>
              ) : null}
              <CloseButton onClose={onClose} />
            </header>
            <div className="alerts-tray-body">
              {ctx.listError !== null ? (
                <p className="alert-line alert-error" role="alert">
                  {/* error-reported-by: AlertsProvider */}
                  Could not load the alerts: {ctx.listError}
                </p>
              ) : null}
              {ctx.actionError !== null ? (
                <p className="alert-line alert-error" role="alert">
                  {/* error-reported-by: AlertsProvider */}
                  {ctx.actionError}
                </p>
              ) : null}
              {!ctx.loaded && ctx.listError === null ? <p className="alert-line alert-dim">Loading...</p> : null}
              {ctx.loaded || ctx.alerts.length > 0 ? (
                <section className="alerts-section" aria-label="Needs you">
                  <h3 className="alerts-section-heading">Needs you</h3>
                  {ctx.needsYou.length === 0 ? (
                    <p className="alert-line alerts-empty">Nothing needs you.</p>
                  ) : (
                    <ul className="alerts-list">
                      {ctx.needsYou.map((a) => (
                        <AlertRow key={a.id} alert={a} onOpen={() => setOpenId(a.id)} />
                      ))}
                    </ul>
                  )}
                </section>
              ) : null}
              {ctx.earlier.length > 0 ? (
                <section className="alerts-section" aria-label="Earlier">
                  <h3 className="alerts-section-heading">Earlier</h3>
                  <ul className="alerts-list">
                    {ctx.earlier.map((a) => (
                      <AlertRow key={a.id} alert={a} onOpen={() => setOpenId(a.id)} />
                    ))}
                  </ul>
                </section>
              ) : null}
            </div>
          </>
        )}
      </div>
    </>
  );
}

/** One line: unseen dot, title, the one-line summary, the time, and an X on an Earlier row. */
function AlertRow({ alert: a, onOpen }: { alert: AlertDto; onOpen: () => void }) {
  const ctx = useAlerts();
  const clearError = ctx.clearErrors[a.id];
  return (
    <li className={`alerts-row ${a.needsAnswer ? "alerts-row-needs" : ""}`} data-testid="alerts-row" data-alert-id={a.id}>
      <div className="alerts-row-main">
        <button type="button" className="alerts-row-open" onClick={onOpen}>
          <span className={`alerts-row-dot ${a.seen ? "" : "alerts-row-dot-unseen"}`} aria-hidden="true" />
          <span className="alerts-row-text">
            <span className="alerts-row-title">
              {a.seen ? null : <span className="alerts-visually-hidden">New: </span>}
              {a.title}
            </span>
            <span className="alerts-row-summary">{a.summary}</span>
          </span>
          <span className="alerts-row-meta">
            {a.needsAnswer ? null : <span className="alert-status">{a.status}</span>}
            <span className="alerts-row-time">{alertClock(a.updatedAtUtc)}</span>
          </span>
        </button>
        {a.needsAnswer ? null : (
          <button
            type="button"
            className="alerts-icon-btn alerts-row-clear"
            aria-label={`Clear: ${a.title}`}
            disabled={ctx.clearing !== null}
            onClick={() => void ctx.clear(a.id)}
          >
            <CrossIcon />
          </button>
        )}
      </div>
      {clearError ? (
        <p className="alert-line alert-error" role="alert">
          {/* error-reported-by: AlertsProvider */}
          {clearError}
        </p>
      ) : null}
    </li>
  );
}

function CloseButton({ onClose }: { onClose: () => void }) {
  return (
    <button type="button" className="alerts-icon-btn alerts-close" aria-label="Close alerts" onClick={onClose}>
      <CrossIcon />
    </button>
  );
}

function CrossIcon() {
  return (
    <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true" focusable="false">
      <path d="M3.5 3.5l9 9m0-9l-9 9" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" fill="none" />
    </svg>
  );
}

function BackIcon() {
  return (
    <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true" focusable="false">
      <path d="M10 3L5 8l5 5" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" fill="none" />
    </svg>
  );
}
