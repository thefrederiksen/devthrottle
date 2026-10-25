import { useEffect, useRef, useState, type ReactNode } from "react";
import { AlertDetail } from "@devthrottle/client-core/alerts/AlertDetail";
import { useAlerts } from "@devthrottle/client-core/alerts/AlertsProvider";
import type { AlertDto } from "@devthrottle/client-core/alerts/alertsClient";

// THE CHIP IN AN OPEN SESSION'S HEADER for what that session asked the owner for (a secret transfer, a talk to another
// session). The alerts are the shared Alerts list (the Alerts mission) - nothing here polls - and the popover shows each
// one exactly as the Alerts tray opens it: the shared AlertDetail, with its own answer block. An answer given here is
// recorded as given "from the badge". Amber, never red: the red dot already means something waits in the queue.

export interface AskChipProps {
  /** The alerts this session asked for that still need an answer. Nothing is drawn when there are none. */
  alerts: AlertDto[];
  label: string;
  icon: ReactNode;
  /** The popover's accessible name. */
  dialogLabel: string;
  testId: string;
}

export function AskChip({ alerts, label, icon, dialogLabel, testId }: AskChipProps) {
  const { refresh, listError } = useAlerts();
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false);
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

  if (alerts.length === 0) return null;
  return (
    <span className="link-ask-wrap" ref={wrapRef}>
      <button
        type="button"
        className="link-ask-chip"
        aria-expanded={open}
        aria-haspopup="dialog"
        onClick={() => setOpen((o) => !o)}
        data-testid={testId}
      >
        {icon}
        {label}
      </button>
      {open && (
        <div className="link-ask-pop" role="dialog" aria-label={dialogLabel}>
          {/* error-reported-by: AlertsProvider */}
          {listError !== null && <div className="session-dialog-error">{listError}</div>}
          {alerts.map((a) => (
            <AlertDetail key={a.id} alert={a} where="badge" onAnswered={() => void refresh()} />
          ))}
        </div>
      )}
    </span>
  );
}
