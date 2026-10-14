import { useState } from "react";
import type {
  LicenseAccount,
  LicenseAccountForecast,
  LicenseHolding,
  LicenseMachine,
} from "@devthrottle/client-core/licenses/licensesClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button } from "../components";
import { LICENSES_SURFACE, LocalTime, TonePill, WindowBar, toneClass } from "./licenseParts";

// One Claude account as a card: its status, the day its subscription is billed again and the control that sets it,
// its two usage windows as bars, what the Gateway says about it, the sign-in each machine holds for it and when that
// sign-in expires, who approves a sign-in for it, Renew now on each sign-in, and Switch now.

interface HeldOn {
  machine: LicenseMachine;
  holding: LicenseHolding;
}

/** Every sign-in for this account, on every machine that holds one. A lookup by account, not a judgement. */
function heldOn(accountEmail: string, machines: ReadonlyArray<LicenseMachine>): HeldOn[] {
  const held: HeldOn[] = [];
  for (const machine of machines) {
    const holdings = machine.active === null ? machine.spares : [machine.active, ...machine.spares];
    for (const holding of holdings) {
      if (holding.accountEmail === accountEmail) held.push({ machine, holding });
    }
  }
  return held;
}

export interface LicenseAccountCardProps {
  account: LicenseAccount;
  machines: ReadonlyArray<LicenseMachine>;
  /** This account's forecast; undefined while the forecast has not been read. */
  forecast: LicenseAccountForecast | undefined;
  onSwitchNow: (account: LicenseAccount) => void;
  /** Store the day the subscription is billed again (yyyy-MM-dd), or clear it with null. Throws on a refusal. */
  onSetRenewalDate: (accountEmail: string, renewsOn: string | null) => Promise<void>;
  /** Ask to renew the sign-in `holding` that `machine` holds. Nothing is sent until it is confirmed. */
  onRenewNow: (machine: LicenseMachine, holding: LicenseHolding) => void;
}

/**
 * The control that sets the day a subscription is billed again - the one value on this page a person types. It sends
 * the date as the date field gives it (yyyy-MM-dd) or null to clear it, and what the card then shows is the Gateway's
 * answer. A refusal is shown here in the Gateway's own sentence and the editor stays open.
 */
function RenewalDateEditor({
  account,
  onSetRenewalDate,
  onClose,
}: {
  account: LicenseAccount;
  onSetRenewalDate: (accountEmail: string, renewsOn: string | null) => Promise<void>;
  onClose: () => void;
}) {
  const [value, setValue] = useState(account.renewsOn ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const store = async (renewsOn: string | null) => {
    setBusy(true);
    setError(null);
    try {
      await onSetRenewalDate(account.accountEmail, renewsOn);
      onClose();
    } catch (err) {
      setError(describeAndReport(LICENSES_SURFACE, "set the renewal date", err));
      setBusy(false);
    }
  };

  return (
    <div className="lic-renewal-editor" data-testid="lic-renewal-editor">
      <input
        type="date"
        className="lic-renewal-input"
        aria-label={`Renewal date of ${account.accountEmail}`}
        value={value}
        disabled={busy}
        onChange={(event) => setValue(event.target.value)}
      />
      <Button variant="primary" disabled={busy || value === ""} onClick={() => void store(value)}>
        {busy ? "Saving..." : "Save"}
      </Button>
      {account.renewsOn !== null && (
        <Button variant="secondary" disabled={busy} onClick={() => void store(null)}>
          Clear date
        </Button>
      )}
      <Button variant="ghost" disabled={busy} onClick={onClose}>
        Cancel
      </Button>
      {error !== null && (
        <div className="lic-renewal-error" role="alert" data-testid="lic-renewal-error">
          {error}
        </div>
      )}
    </div>
  );
}

export function LicenseAccountCard({ account, machines, forecast, onSwitchNow, onSetRenewalDate, onRenewNow }: LicenseAccountCardProps) {
  const held = heldOn(account.accountEmail, machines);
  const [editingRenewal, setEditingRenewal] = useState(false);
  return (
    <section
      className={`lic-card${account.isInUse ? " lic-card-in-use" : ""}${account.isStale ? " lic-card-stale" : ""}`}
      aria-label={account.accountEmail}
      data-testid="lic-account"
      data-status={account.status}
    >
      <div className="lic-card-head">
        <span className="lic-card-name">{account.accountEmail}</span>
        <TonePill text={account.statusText} tone={account.statusTone} testId="lic-account-status" />
      </div>
      <div className="lic-card-meta">
        <span data-testid="lic-account-renewal">
          {account.planText} - {account.renewsText}
        </span>
        {!editingRenewal && (
          <Button variant="ghost" onClick={() => setEditingRenewal(true)}>
            Set renewal date
          </Button>
        )}
      </div>
      {editingRenewal && (
        <RenewalDateEditor account={account} onSetRenewalDate={onSetRenewalDate} onClose={() => setEditingRenewal(false)} />
      )}

      <WindowBar label="5-hour window" window={account.fiveHour} testId="lic-window-five-hour" />
      <WindowBar label="Week" window={account.sevenDay} testId="lic-window-seven-day" />

      <p className="lic-card-detail">{account.statusDetail}</p>
      {/* Barred from the automatic choice: the Gateway's sentence, and the time it gave in the viewer's time zone.
          The Gateway sends no tone for it, so it is drawn like its neighbours. */}
      {account.barredText !== null && (
        <p className="lic-card-line" data-testid="lic-account-barred">
          {account.barredText}
          {account.barredUntilUtc !== null && (
            <>
              {" "}
              (<LocalTime iso={account.barredUntilUtc} />)
            </>
          )}
        </p>
      )}
      {forecast !== undefined && (
        <p className="lic-card-line" data-testid="lic-account-expected">
          {forecast.expectedText}
        </p>
      )}
      <p className="lic-card-line">{account.extraUsage.text}</p>
      <p className="lic-card-line" data-testid="lic-account-read-age">
        {account.readAgeText}
      </p>
      {/* The Gateway sends no tone for a read error, so the page gives it none: it is drawn like its neighbours. */}
      {account.readErrorText !== null && (
        <p className="lic-card-line" data-testid="lic-account-read-error">
          {account.readErrorText}
        </p>
      )}
      {/* Who approves a sign-in for this account, or that nobody is set to: the Gateway's sentence in its tone. */}
      <p className={`lic-card-line ${toneClass(account.approverTone)}`} data-testid="lic-account-approver">
        {account.approverText}
      </p>

      {held.length > 0 && (
        <ul className="lic-holdings" aria-label={`Sign-ins for ${account.accountEmail}`}>
          {held.map(({ machine, holding }) => (
            <li key={machine.machineId} className="lic-holding" data-testid="lic-holding" data-verdict={holding.expiryVerdict}>
              <span className="lic-holding-machine">{machine.label}</span>
              <span className="lic-holding-role">{holding.isActive ? "Active" : "Spare"}</span>
              <span className={`lic-holding-expiry ${toneClass(holding.expiryTone)}`}>{holding.expiryText}</span>
              <span className="lic-holding-signed-in">{holding.signedInText}</span>
              {holding.readErrorText !== null && (
                <span className="lic-holding-error" data-testid="lic-holding-read-error">
                  {holding.readErrorText}
                </span>
              )}
              {holding.renewalFailedText !== null && (
                <span
                  className={`lic-holding-error ${toneClass(holding.renewalFailedTone ?? "neutral")}`}
                  data-testid="lic-holding-renewal-failed"
                >
                  {holding.renewalFailedText}
                </span>
              )}
              {/* Renew now is offered when the Gateway says so; its sentence says what it does, or why it is not. */}
              <span className="lic-holding-renew">
                {holding.canRenewNow && (
                  <Button variant="ghost" onClick={() => onRenewNow(machine, holding)}>
                    Renew now
                  </Button>
                )}
                <span data-testid="lic-holding-renew-text">{holding.renewNowText}</span>
              </span>
            </li>
          ))}
        </ul>
      )}

      <div className="lic-card-switch">
        {account.canSwitchNow && (
          <Button variant="primary" onClick={() => onSwitchNow(account)}>
            Switch now
          </Button>
        )}
        <span className="lic-card-switch-text">{account.switchNowText}</span>
      </div>
    </section>
  );
}
