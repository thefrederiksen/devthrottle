import { useState } from "react";
import {
  cancelTeamPlan,
  renewTeamPlan,
  setTeamPlanAutoRenew,
  startTeamPlan,
  type TeamBill,
} from "@devthrottle/client-core/teams/teamPageClient";
import { Button, ConfirmDialog } from "../components";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-team-billing";

// The Billing section of the Team page (Teams v1, the team bill without Stripe). The Owner starts the plan, renews it,
// switches auto-renew and cancels; a Manager sees the same bill read-only; a Developer never gets this section (the
// Gateway sends no bill).
//
// CRITICAL RULE 7 - every sentence and flag here is the Gateway's: the status and its line, the money lines, "No charge",
// which buttons show, the checkout's confirmation, the cancel warning, the Manager's note and each history line. The
// section lays them out; it never reads a role or a state to decide what the caller may do.

export interface TeamBillingSectionProps {
  teamId: string;
  bill: TeamBill;
  /** Reload the page after a change, so the section shows the Gateway's new verdict. */
  onChanged: (note: string) => Promise<void>;
}

export function TeamBillingSection({ teamId, bill, onChanged }: TeamBillingSectionProps) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [checkoutOpen, setCheckoutOpen] = useState(false);
  const [cancelOpen, setCancelOpen] = useState(false);

  const flipAutoRenew = async () => {
    setBusy(true);
    setError(null);
    try {
      await setTeamPlanAutoRenew(teamId, !bill.autoRenew);
      await onChanged(bill.autoRenew ? "Auto-renew is off. The team plan ends at the end of this period." : "Auto-renew is on.");
    } catch (err) {
      setError(describeAndReport(SURFACE, bill.autoRenew ? "switch auto-renew off" : "switch auto-renew on", err));
    } finally {
      setBusy(false);
    }
  };

  // The checkout confirms a start or a renewal; a failure throws into the dialog, which stays open and shows it.
  const confirmCheckout = async () => {
    if (bill.canStart) {
      await startTeamPlan(teamId);
      await onChanged("The team plan has started.");
    } else {
      await renewTeamPlan(teamId);
      await onChanged("The team plan is renewed for a new month.");
    }
  };

  const confirmCancel = async () => {
    await cancelTeamPlan(teamId);
    await onChanged("The team plan is cancelled. It stays active until the end of this period.");
  };

  return (
    <section className="team-card team-bill" aria-label="Billing">
      <div className="team-bill-head">
        <h2 className="team-h2">Billing</h2>
        <span className={`team-bill-state team-bill-state-${bill.state}`}>{bill.statusLabel}</span>
      </div>
      <p className="team-bill-status">{bill.statusLine}</p>

      <dl className="team-bill-facts">
        <div><dt>Seats</dt><dd>{bill.seatsLine}</dd></div>
        <div><dt>Price</dt><dd>{bill.priceLine}</dd></div>
        <div><dt>Amount</dt><dd>{bill.amountLine}</dd></div>
        <div><dt>Charged</dt><dd className="team-bill-nocharge">{bill.chargeLine}</dd></div>
        {bill.periodEnd !== null && <div><dt>Period ends</dt><dd>{bill.periodEnd}</dd></div>}
        {bill.state !== "not-started" && bill.state !== "ended" && (
          <div>
            <dt>Auto-renew</dt>
            <dd className="team-bill-auto">
              {bill.canSetAutoRenew && (
                <button
                  type="button"
                  className={bill.autoRenew ? "team-switch team-switch-on" : "team-switch"}
                  role="switch"
                  aria-checked={bill.autoRenew}
                  aria-label={bill.autoRenew ? "Auto-renew is on - switch it off" : "Auto-renew is off - switch it on"}
                  disabled={busy}
                  onClick={() => void flipAutoRenew()}
                ></button>
              )}
              <span>{bill.autoRenew ? "On" : "Off"}</span>
            </dd>
          </div>
        )}
      </dl>

      {(bill.canStart || bill.canRenew || bill.canCancel) && (
        <div className="team-row">
          {bill.canStart && <Button variant="primary" disabled={busy} onClick={() => setCheckoutOpen(true)}>Start the team plan</Button>}
          {bill.canRenew && <Button variant="primary" disabled={busy} onClick={() => setCheckoutOpen(true)}>Renew now</Button>}
          {bill.canCancel && <Button variant="ghost" disabled={busy} onClick={() => setCancelOpen(true)}>Cancel plan</Button>}
        </div>
      )}
      {bill.note !== null && <p className="team-hint team-bill-note">{bill.note}</p>}
      {error !== null && <p className="team-warn" role="alert">{error}</p>}

      <h3 className="team-bill-h3">History</h3>
      {bill.history.length === 0 ? (
        <p className="team-hint">No periods yet.</p>
      ) : (
        <table className="team-table team-bill-history">
          <thead>
            <tr><th>Period</th><th>Amount</th><th>Charged</th><th></th></tr>
          </thead>
          <tbody>
            {bill.history.map((h) => (
              <tr key={h.id}>
                <td>{h.period}</td>
                <td>{h.amount}</td>
                <td className="team-bill-nocharge">{h.charged}</td>
                <td className="team-seat">{h.reason}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <ConfirmDialog
        open={checkoutOpen && bill.checkout !== null}
        title={bill.checkout?.title ?? ""}
        message={bill.checkout === null ? "" : (
          <dl className="team-bill-checkout">
            <div><dt>Seats</dt><dd>{bill.checkout.seatsLine}</dd></div>
            <div><dt>Price</dt><dd>{bill.checkout.priceLine}</dd></div>
            <div><dt>Total</dt><dd>{bill.checkout.totalLine}</dd></div>
            <div><dt>Period</dt><dd>{bill.checkout.periodLine}</dd></div>
            <div className="team-bill-checkout-charge"><dt>Charge</dt><dd>{bill.checkout.chargeLine}</dd></div>
          </dl>
        )}
        confirmLabel={bill.checkout?.confirmLabel ?? ""}
        cancelLabel="Not now"
        danger={false}
        action={bill.canStart ? "start the team plan" : "renew the team plan"}
        onConfirm={confirmCheckout}
        onClose={() => setCheckoutOpen(false)}
      />
      <ConfirmDialog
        open={cancelOpen && bill.cancelWarning !== null}
        title="Cancel the team plan?"
        message={bill.cancelWarning ?? ""}
        confirmLabel="Cancel plan"
        cancelLabel="Keep it"
        danger
        action="cancel the team plan"
        onConfirm={confirmCancel}
        onClose={() => setCancelOpen(false)}
      />
    </section>
  );
}
