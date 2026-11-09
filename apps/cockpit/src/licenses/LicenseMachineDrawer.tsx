import { useEffect } from "react";
import type { LicenseHolding, LicenseMachine } from "@devthrottle/client-core/licenses/licensesClient";
import { Button, useDismissOnBackdrop } from "../components";
import { TonePill, toneClass } from "./licenseParts";

// ONE MACHINE'S DRAWER (the owner's redesign, 8 November 2026): everything about one machine that the Overview keeps off
// the main screen - what it is on, its spares, every sign-in with its Renew button, the sign-ins under way, Switch this machine to..., Add a spare,
// and Remove this machine. Remove is a real red button (the owner could not find the borderless "Forget this machine"
// link it replaces) and it calls the existing forget route through the page's confirmation. Every sentence is the
// Gateway's; the drawer only lays them out.

export interface LicenseMachineDrawerProps {
  machine: LicenseMachine;
  onClose: () => void;
  onRenew: (machine: LicenseMachine, holding: LicenseHolding) => void;
  onAddSpare: (machine: LicenseMachine) => void;
  onRemove: (machine: LicenseMachine) => void;
  onSwitch: (machine: LicenseMachine) => void;
}

export function LicenseMachineDrawer({ machine, onClose, onRenew, onAddSpare, onRemove, onSwitch }: LicenseMachineDrawerProps) {
  const backdrop = useDismissOnBackdrop(onClose);
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const holdings = machine.active === null ? machine.spares : [machine.active, ...machine.spares];
  return (
    <div className="lic-drawer-backdrop" {...backdrop}>
      <aside className="lic-drawer" role="dialog" aria-modal="true" aria-label={machine.label} data-testid="lic-drawer">
        <header className="lic-drawer-head">
          <div>
            <h2 className="lic-drawer-title">{machine.label}</h2>
            <div className="lic-dim">
              <TonePill text={machine.statusText} tone={machine.statusTone} /> {machine.lastReportText}
            </div>
          </div>
          <button type="button" className="lic-drawer-close" aria-label="Close" onClick={onClose}>
            X
          </button>
        </header>

        {machine.statusDetail !== null && <p className="lic-drawer-note">{machine.statusDetail}</p>}
        {machine.heldText !== null && <p className="lic-drawer-note">{machine.heldText}</p>}

        <dl className="lic-drawer-facts">
          <dt>On now</dt>
          <dd>{machine.activeAccountEmail ?? "none"}</dd>
          <dt>Spares held</dt>
          <dd>{machine.spares.length === 0 ? "none" : machine.spares.map((s) => s.accountEmail).join(", ")}</dd>
          <dt>Next license</dt>
          <dd>{machine.readinessTargetAccountEmail ?? "none"}</dd>
        </dl>
        {machine.readinessText !== null && (
          <p className={`lic-drawer-note ${toneClass(machine.readinessTone ?? "neutral")}`} data-testid="lic-drawer-readiness">
            {machine.readinessText}
          </p>
        )}
        {machine.switchInProgress !== null && <p className="lic-drawer-note">{machine.switchInProgress.text}</p>}

        <h3 className="lic-drawer-section">Sign-ins on this machine</h3>
        {holdings.length === 0 ? (
          <p className="lic-dim">None.</p>
        ) : (
          <ul className="lic-drawer-list">
            {holdings.map((holding) => (
              <li key={holding.accountEmail} className="lic-drawer-signin" data-testid="lic-drawer-signin">
                <div className="lic-drawer-signin-line">
                  <span className="lic-strong">{holding.accountEmail}</span>
                  <span className="lic-dim">{holding.roleText}</span>
                </div>
                <div className={toneClass(holding.expiryTone)}>{holding.expiryText}</div>
                {holding.renewalFailedText !== null && (
                  <div className={toneClass(holding.renewalFailedTone ?? "neutral")} data-testid="lic-drawer-renewal-failed">
                    {holding.renewalFailedText}
                  </div>
                )}
                {holding.readErrorText !== null && <div className="lic-dim">{holding.readErrorText}</div>}
                {holding.canRenewNow && (
                  <Button variant="secondary" onClick={() => onRenew(machine, holding)} data-testid="lic-drawer-renew">
                    Renew now
                  </Button>
                )}
                <div className="lic-dim" data-testid="lic-holding-renew-text">
                  {holding.renewNowText}
                </div>
              </li>
            ))}
          </ul>
        )}

        {machine.signInsInProgress.length > 0 && (
          <>
            <h3 className="lic-drawer-section">Sign-ins under way</h3>
            <ul className="lic-drawer-list">
              {machine.signInsInProgress.map((signIn) => (
                <li key={signIn.signInId} data-testid="lic-sign-in-progress">
                  {signIn.text}
                </li>
              ))}
            </ul>
          </>
        )}

        <h3 className="lic-drawer-section">Actions</h3>
        <div className="lic-drawer-actions">
          <Button variant="secondary" disabled={!machine.canSwitchNow} onClick={() => onSwitch(machine)} data-testid="lic-drawer-switch">
            Switch this machine to...
          </Button>
          {machine.switchNowText !== null && (
            <p className="lic-dim" data-testid="lic-drawer-switch-text">
              {machine.switchNowText}
            </p>
          )}
          <Button variant="secondary" disabled={!machine.canAddSpare} onClick={() => onAddSpare(machine)} data-testid="lic-drawer-add-spare">
            Add a spare
          </Button>
          <p className="lic-dim" data-testid="lic-add-spare-text">
            {machine.addSpareText}
          </p>
          <Button variant="danger" disabled={!machine.canForget} onClick={() => onRemove(machine)} data-testid="lic-machine-forget">
            Remove this machine
          </Button>
          <p className="lic-dim" data-testid="lic-drawer-remove-text">
            {machine.forgetText}
          </p>
        </div>
      </aside>
    </div>
  );
}
