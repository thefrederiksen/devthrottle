import { useCallback, useEffect, useRef, useState } from "react";
import type { SecretTransferPlace } from "../secrets/secretTransfersClient";
import { useAlerts } from "./AlertsProvider";
import { AlertDetail } from "./AlertDetail";
import { alertClock, type AlertDto } from "./alertsClient";
import "./alerts.css";

// WHAT THE OWNER SEES WHEN HE OPENS ALERTS (the Alerts mission, phase 5) - the same content on the phone's own Alerts
// screen and in the Cockpit's popover under the bell. Each shell supplies only the frame around it.
//
// "Needs you" comes first, and each alert in it is a CARD with its answer on it: the question, who asked and why, when
// it expires, and the buttons. One tap answers. An answered card leaves at once and a short line says what happened
// ("Approved: Send the secret ...?"), in the Gateway's own status word, so it never just vanishes.
//
// "Earlier" sits below, quieter and collapsed: "Earlier (5)" opens to one compact row per alert - title, status, time
// and an X - with "Clear all" in that section's own header. A row opens in place to show its summary and Details.
//
// Showing the list marks what it shows as seen. A failed read keeps the last-known list and says so in one line.

/** How long the line saying what an answer did stays on screen. */
export const ANSWERED_LINE_MS = 6000;

export interface AlertsListProps {
  /** Where a secret transfer answered here is recorded: "phone" on the phone, "cockpit" on the Cockpit. */
  where: SecretTransferPlace;
  /** Bring this alert into view and put focus on it - a push notification's deep link. An Earlier alert opens its
   *  section and its row. */
  focusAlertId?: string | null;
}

export function AlertsList({ where, focusAlertId }: AlertsListProps) {
  const ctx = useAlerts();
  const { alerts, markSeen, refresh } = ctx;
  const [earlierOpen, setEarlierOpen] = useState(false);
  const [openRow, setOpenRow] = useState<string | null>(null);
  // The alerts answered here in the last few seconds, oldest first: the line saying what each answer did.
  const [answered, setAnswered] = useState<AlertDto[]>([]);
  const root = useRef<HTMLDivElement>(null);

  // Showing the list marks what it shows as seen - once per alert version, so a read that has not caught up yet does
  // not send the same ids again on every render.
  const sentSeen = useRef<Set<string>>(new Set());
  useEffect(() => {
    const unseen = alerts.filter((a) => !a.seen && !sentSeen.current.has(`${a.id}@${a.updatedAtUtc}`));
    if (unseen.length === 0) return;
    for (const a of unseen) sentSeen.current.add(`${a.id}@${a.updatedAtUtc}`);
    void markSeen(unseen.map((a) => a.id));
  }, [alerts, markSeen]);

  const onAnswered = useCallback(
    (alert: AlertDto) => {
      setAnswered((list) => [...list.filter((a) => a.id !== alert.id), alert]);
      window.setTimeout(() => setAnswered((list) => list.filter((a) => a.id !== alert.id)), ANSWERED_LINE_MS);
      void refresh();
    },
    [refresh],
  );

  // The deep link: once the alert is listed, open what holds it and move focus onto it. Done once per link.
  const focusedFor = useRef<string | null>(null);
  const target = focusAlertId ? alerts.find((a) => a.id === focusAlertId) ?? null : null;
  useEffect(() => {
    if (!target || focusedFor.current === target.id) return;
    if (!target.needsAnswer) {
      setEarlierOpen(true);
      setOpenRow(target.id);
    }
  }, [target]);
  useEffect(() => {
    if (!target || focusedFor.current === target.id) return;
    const el = [...(root.current?.querySelectorAll<HTMLElement>("[data-alert-id]") ?? [])].find((e) => e.dataset.alertId === target.id);
    if (!el) return;
    focusedFor.current = target.id;
    el.scrollIntoView?.({ block: "nearest" });
    el.focus();
  }, [target, earlierOpen, openRow]);

  // Only an answer the Gateway now lists as answered gets its line, in the Gateway's status word; one still waiting - a
  // read that has not caught up - gets none yet, so the line never claims what the Gateway has not said.
  const lines = answered.flatMap((was) => {
    const now = alerts.find((a) => a.id === was.id);
    return now && !now.needsAnswer ? [{ id: now.id, text: `${now.status}: ${now.title}` }] : [];
  });

  return (
    <div className="alerts-content" ref={root}>
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
          <h2 className="alerts-section-heading">Needs you</h2>
          <div role="status" className="alerts-answered">
            {lines.map((l) => (
              <p key={l.id} className="alert-line alerts-answered-line" data-testid="alerts-answered">
                {l.text}
              </p>
            ))}
          </div>
          {ctx.needsYou.length === 0 ? (
            <p className="alert-line alerts-empty">Nothing needs you.</p>
          ) : (
            <ul className="alerts-cards">
              {ctx.needsYou.map((a) => (
                <li key={a.id} className="alert-card" data-testid="alert-card" data-alert-id={a.id} tabIndex={-1}>
                  <AlertDetail alert={a} where={where} onAnswered={() => onAnswered(a)} />
                </li>
              ))}
            </ul>
          )}
        </section>
      ) : null}

      {ctx.earlier.length > 0 ? (
        <section className="alerts-section alerts-earlier" aria-label="Earlier">
          <div className="alerts-section-head">
            <h2 className="alerts-section-heading">
              <button
                type="button"
                className="alerts-section-toggle"
                aria-expanded={earlierOpen}
                aria-controls="alerts-earlier-list"
                onClick={() => setEarlierOpen((o) => !o)}
              >
                <Chevron open={earlierOpen} />
                Earlier ({ctx.earlier.length})
              </button>
            </h2>
            <button type="button" className="alert-btn alerts-clear-all" disabled={ctx.clearing !== null} onClick={() => void ctx.clearAll()}>
              {ctx.clearing === "all" ? "Clearing..." : "Clear all"}
            </button>
          </div>
          {earlierOpen ? (
            <ul className="alerts-list" id="alerts-earlier-list">
              {ctx.earlier.map((a) => (
                <EarlierRow
                  key={a.id}
                  alert={a}
                  where={where}
                  open={openRow === a.id}
                  onToggle={() => setOpenRow((id) => (id === a.id ? null : a.id))}
                  onAnswered={() => void refresh()}
                />
              ))}
            </ul>
          ) : null}
        </section>
      ) : null}
    </div>
  );
}

interface EarlierRowProps {
  alert: AlertDto;
  where: SecretTransferPlace;
  open: boolean;
  onToggle: () => void;
  onAnswered: () => void;
}

/** One compact row: title, status, time and an X. Opening it shows the summary and Details in place. */
function EarlierRow({ alert: a, where, open, onToggle, onAnswered }: EarlierRowProps) {
  const ctx = useAlerts();
  const clearError = ctx.clearErrors[a.id];
  return (
    <li className="alerts-row" data-testid="alerts-row" data-alert-id={a.id} tabIndex={-1}>
      <div className="alerts-row-main">
        <button type="button" className="alerts-row-open" aria-expanded={open} onClick={onToggle}>
          <span className={`alerts-row-dot ${a.seen ? "" : "alerts-row-dot-unseen"}`} aria-hidden="true" />
          <span className="alerts-row-title">
            {a.seen ? null : <span className="alerts-visually-hidden">New: </span>}
            {a.title}
          </span>
          <span className="alerts-row-meta">
            <span className="alert-status">{a.status}</span>
            <span className="alerts-row-time">{alertClock(a.updatedAtUtc)}</span>
          </span>
        </button>
        <button
          type="button"
          className="alerts-icon-btn alerts-row-clear"
          aria-label={`Clear: ${a.title}`}
          disabled={ctx.clearing !== null}
          onClick={() => void ctx.clear(a.id)}
        >
          <CrossIcon />
        </button>
      </div>
      {clearError ? (
        <p className="alert-line alert-error" role="alert">
          {/* error-reported-by: AlertsProvider */}
          {clearError}
        </p>
      ) : null}
      {open ? (
        <div className="alerts-row-more">
          <p className="alert-line alert-summary">{a.summary}</p>
          <AlertDetail alert={a} where={where} onAnswered={onAnswered} headless />
        </div>
      ) : null}
    </li>
  );
}

function Chevron({ open }: { open: boolean }) {
  return (
    <svg className="alerts-chevron" viewBox="0 0 16 16" width="12" height="12" aria-hidden="true" focusable="false">
      <path d={open ? "M3.5 6l4.5 4.5L12.5 6" : "M6 3.5l4.5 4.5L6 12.5"} stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" fill="none" />
    </svg>
  );
}

export function CrossIcon() {
  return (
    <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true" focusable="false">
      <path d="M3.5 3.5l9 9m0-9l-9 9" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" fill="none" />
    </svg>
  );
}
