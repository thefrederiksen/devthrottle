import type {
  LicenseAccount,
  LicenseAccountForecast,
  LicenseHolding,
  LicenseMachine,
} from "@devthrottle/client-core/licenses/licensesClient";
import { Button } from "../components";
import { TonePill, WindowBar, toneClass } from "./licenseParts";

// One Claude account as a card: its status, its two usage windows as bars, what the Gateway says about it, the
// sign-in each machine holds for it and when that sign-in expires, and Switch now.

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
}

export function LicenseAccountCard({ account, machines, forecast, onSwitchNow }: LicenseAccountCardProps) {
  const held = heldOn(account.accountEmail, machines);
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
        {account.planText} - {account.renewsText}
      </div>

      <WindowBar label="5-hour window" window={account.fiveHour} testId="lic-window-five-hour" />
      <WindowBar label="Week" window={account.sevenDay} testId="lic-window-seven-day" />

      <p className="lic-card-detail">{account.statusDetail}</p>
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
