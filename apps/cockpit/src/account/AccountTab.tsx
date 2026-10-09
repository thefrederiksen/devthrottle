import { useCallback, useEffect, useRef, useState } from "react";
import {
  getAccountStatus,
  logoutAccount,
  getAccountDevices,
  removeAccountDevice,
  beginSignIn,
  type AccountStatus,
  type AccountDevice,
  type AccountDevicesResponse,
} from "@devthrottle/client-core/account/accountClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { useAccounts } from "@devthrottle/client-core/auth/useAccounts";
import { Button, ErrorBanner, LoadingState, PageHeader } from "../components";
import { SignOutDialog, accountName } from "../you/SignOutDialog";
import { TeamsSection } from "./TeamsSection";

// THE ACCOUNT TAB OF SETTINGS (owner, 8 Oct 2026) - what was the Account page (issue #978), reorganised the way ChatGPT
// and Claude do it: Account is one tab of Settings, and it holds only YOU - your email, how you sign in, your devices,
// your teams and Create a team, and one sign-out that names the account.
//
// What LEFT this page, and why. The accounts signed in on this browser - switch, add another - moved to the menu behind
// your name at the bottom of the rail, where switching belongs. And the page's two sign-out buttons became one: "Sign
// out" (this browser forgets the account) and "Log out" (the GATEWAY forgets its own DevThrottle sign-in) did different
// things to different machines and read as the same button. The Gateway's own sign-in is shown only where the Gateway
// says it holds one - a self-hosted Gateway (`gatewaySignIn`) - in its own section, worded as disconnecting the
// Gateway, so it cannot be mistaken for signing out of this browser. On the hosted Gateway there is nothing to log out
// of (it refuses POST /account/logout, issue #984), so that section is not drawn at all.
//
// A pure client of the Gateway account endpoints: the credential lives on the Gateway and the raw token is NEVER
// fetched, stored, or displayed (security rule DT-05). Responsive (CodingStyle.md): the tab renders immediately with a
// loading state and loads the status + device list asynchronously. On any failure it shows an explicit error state,
// never a fabricated signed-out or empty-devices view (the no-fallback rule).

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="acct-row">
      <div className="acct-row-label">{label}</div>
      <div className="acct-row-value">{value}</div>
    </div>
  );
}

function deviceMeta(device: AccountDevice): string {
  const parts: string[] = [];
  if (device.platform && device.platform.length > 0) parts.push(device.platform);
  if (device.deviceType && device.deviceType.length > 0) parts.push(device.deviceType);
  if (device.appVersion && device.appVersion.length > 0) parts.push(`v${device.appVersion}`);
  return parts.length === 0 ? "Unknown device" : parts.join(" - ");
}

// Format a cloud timestamp to the compact "yyyy-MM-dd HH:mm" local-time string; "unknown" when absent,
// and the raw value verbatim when unparseable (the cloud value is the source of truth - never fabricate
// a time).
function formatTimestamp(value: string | null | undefined): string {
  if (value === null || value === undefined || value.trim().length === 0) return "unknown";
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  const pad = (n: number) => String(n).padStart(2, "0");
  return (
    `${parsed.getFullYear()}-${pad(parsed.getMonth() + 1)}-${pad(parsed.getDate())} ` +
    `${pad(parsed.getHours())}:${pad(parsed.getMinutes())}`
  );
}

export function AccountTab() {
  const [status, setStatus] = useState<AccountStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [loggedOut, setLoggedOut] = useState(false);

  // Sign-in (signed-out state).
  const [signInError, setSignInError] = useState<string | null>(null);

  // Devices (signed-in state).
  const [devices, setDevices] = useState<AccountDevicesResponse | null>(null);
  const [devicesLoading, setDevicesLoading] = useState(false);
  const [devicesError, setDevicesError] = useState<string | null>(null);
  const [confirmRemoveId, setConfirmRemoveId] = useState<string | null>(null);
  const [removeBusy, setRemoveBusy] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);
  const [removedNote, setRemovedNote] = useState<string | null>(null);

  // The in-flight sign-in poll is cancelled on unmount and whenever a new sign-in starts, so a poll
  // never survives the component or races a second attempt.
  const pollAbortRef = useRef<AbortController | null>(null);

  const loadDevices = useCallback(async (signal?: AbortSignal) => {
    setDevicesLoading(true);
    setDevicesError(null);
    setConfirmRemoveId(null);
    try {
      setDevices(await getAccountDevices(signal));
    } catch (err) {
      if (signal?.aborted) return;
      // No-fallback: a Gateway/cloud error surfaces as an explicit error state, never an empty list.
      setDevices(null);
      setDevicesError(gatewayErrorMessage(err));
    } finally {
      setDevicesLoading(false);
    }
  }, []);

  const loadStatus = useCallback(
    async (signal?: AbortSignal) => {
      try {
        setError(null);
        const next = await getAccountStatus(signal);
        setStatus(next);
        if (next.signedIn) await loadDevices(signal);
      } catch (err) {
        if (signal?.aborted) return;
        setError(gatewayErrorMessage(err));
      }
    },
    [loadDevices],
  );

  useEffect(() => {
    const controller = new AbortController();
    void loadStatus(controller.signal);
    return () => {
      controller.abort();
      pollAbortRef.current?.abort();
    };
  }, [loadStatus]);

  const logOut = async () => {
    if (busy) return;
    setBusy(true);
    setLoggedOut(false);
    try {
      setStatus(await logoutAccount());
      setLoggedOut(true);
      // The device section belongs to the signed-in view; drop it so a later sign-in reloads fresh.
      setDevices(null);
      setDevicesError(null);
      setConfirmRemoveId(null);
    } catch (err) {
      setError(gatewayErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  const requestRemove = (deviceId: string) => {
    setConfirmRemoveId(deviceId);
    setRemoveError(null);
    setRemovedNote(null);
  };

  const cancelRemove = () => setConfirmRemoveId(null);

  const confirmRemove = async (deviceId: string) => {
    if (removeBusy) return;
    setRemoveBusy(true);
    setRemoveError(null);
    setRemovedNote(null);
    try {
      await removeAccountDevice(deviceId);
      setConfirmRemoveId(null);
      setRemovedNote("Device removed.");
      // Refresh from the source so the row disappears and the list reflects the account exactly.
      await loadDevices();
    } catch (err) {
      setRemoveError(gatewayErrorMessage(err));
    } finally {
      setRemoveBusy(false);
    }
  };

  // Sign the GATEWAY in to its DevThrottle account. This NAVIGATES away to the Gateway's public
  // sign-in front door, which redirects a remote browser on to devthrottle.com and hands it back to
  // the Gateway's own callback - so there is nothing to await and nothing to poll here. The page is
  // simply re-entered signed in.
  const startSignInFlow = () => {
    setSignInError(null);
    setLoggedOut(false);
    beginSignIn();
  };

  const { active } = useAccounts();
  const [signingOut, setSigningOut] = useState(false);

  // The email the tab names you by: the Gateway's answer when it has one, otherwise the account this browser holds.
  const email = status?.email && status.email.length > 0 ? status.email : active?.email ?? null;
  const who = active === null ? email ?? "this account" : accountName(active);

  return (
    <div className="page acct">
      <PageHeader title="Account" />

      {error !== null ? (
        <ErrorBanner
          message={`Could not load your account from the Gateway: ${error}`}
          onRetry={() => void loadStatus()}
        />
      ) : status === null ? (
        <LoadingState />
      ) : (
        <>
          <section className="acct-card" aria-label="Your account">
            <Row label="Email" value={email ?? "(not available)"} />
            {/* How you sign in, only when the Gateway knows it: the hosted Gateway does not record the provider,
                and a guessed one would be worse than none. */}
            {status.provider && status.provider.length > 0 && <Row label="Signed in with" value={status.provider} />}
            <div className="acct-signout-row">
              <Button onClick={() => setSigningOut(true)}>{`Sign out of ${who}`}</Button>
              <span className="acct-note">
                This browser forgets this account. To switch to another account instead, use the menu behind your name.
              </span>
            </div>
          </section>

          {status.signedIn && (
            <div className="acct-devices">
              <div className="acct-devices-head">
                <h2>Your devices</h2>
                <button className="acct-btn" onClick={() => void loadDevices()} disabled={devicesLoading}>
                  Refresh
                </button>
              </div>
              <p className="acct-devices-sub">
                Every device registered to this DevThrottle account. Remove a device to revoke its access.
              </p>

              {devicesLoading ? (
                <LoadingState message="Loading devices..." />
              ) : devicesError !== null ? (
                <ErrorBanner
                  message={`Could not load your devices from the Gateway: ${devicesError}`}
                  onRetry={() => void loadDevices()}
                />
              ) : devices !== null && !devices.signedIn ? (
                <div className="acct-devices-signedout">
                  The Gateway reports it is no longer signed in, so the device list is unavailable. Sign in again to
                  see your devices.
                </div>
              ) : devices !== null && (devices.devices?.length ?? 0) === 0 ? (
                <div className="acct-devices-empty">No devices are registered to this account yet.</div>
              ) : devices !== null && devices.devices ? (
                <div className="acct-devices-list">
                  {devices.devices.map((device) => (
                    <div key={device.id} className="acct-device-row">
                      <div className="acct-device-main">
                        <div className="acct-device-name-row">
                          <span className="acct-device-name">
                            {device.name.length > 0 ? device.name : "(unnamed device)"}
                          </span>
                          {device.thisDevice && <span className="acct-this-device">This device</span>}
                        </div>
                        <div className="acct-device-meta">
                          <span>{deviceMeta(device)}</span>
                          <span className="acct-device-sep">|</span>
                          <span>Last seen: {formatTimestamp(device.lastSeenAt)}</span>
                        </div>
                      </div>

                      {confirmRemoveId === device.id ? (
                        <div className="acct-remove-confirm">
                          <span className="acct-remove-ask">Remove this device?</span>
                          <button
                            className="acct-btn danger"
                            onClick={() => void confirmRemove(device.id)}
                            disabled={removeBusy}
                          >
                            {removeBusy ? "Removing..." : "Remove"}
                          </button>
                          <button className="acct-btn" onClick={cancelRemove} disabled={removeBusy}>
                            Cancel
                          </button>
                        </div>
                      ) : (
                        <button
                          className="acct-btn"
                          onClick={() => requestRemove(device.id)}
                          disabled={confirmRemoveId !== null}
                        >
                          Remove
                        </button>
                      )}
                    </div>
                  ))}
                </div>
              ) : null}

              {removeError !== null && (
                <div className="acct-remove-error">Could not remove the device: {removeError}</div>
              )}
              {removedNote !== null && <div className="acct-remove-ok">{removedNote}</div>}
            </div>
          )}

          {/* Your teams and Create a team (Teams v1). Nothing at all when this Gateway offers no teams. */}
          <TeamsSection />

          {status.gatewaySignIn === true && (
            <GatewaySignInSection
              status={status}
              busy={busy}
              loggedOut={loggedOut}
              signInError={signInError}
              onDisconnect={() => void logOut()}
              onSignIn={startSignInFlow}
            />
          )}
        </>
      )}

      <SignOutDialog open={signingOut} onClose={() => setSigningOut(false)} />
    </div>
  );
}

// A SELF-HOSTED Gateway's own DevThrottle sign-in - the only Gateway that has one (`gatewaySignIn`). It is a different
// thing from this browser's sign-in: it is the computer the Gateway runs on, linked to an account. Worded as connecting
// and disconnecting the GATEWAY, never as "signing out", so it cannot be taken for the sign-out above.
function GatewaySignInSection({
  status,
  busy,
  loggedOut,
  signInError,
  onDisconnect,
  onSignIn,
}: {
  status: AccountStatus;
  busy: boolean;
  loggedOut: boolean;
  signInError: string | null;
  onDisconnect: () => void;
  onSignIn: () => void;
}) {
  return (
    <section className="acct-gateway" aria-label="This Gateway's own sign-in" data-testid="account-gateway-sign-in">
      <h2 className="acct-gateway-head">This Gateway's own sign-in</h2>
      {status.signedIn ? (
        <div className="acct-card">
          <div className="acct-state">
            <span className="acct-dot on" />
            <span className="acct-state-label on">
              {status.email ? `Connected to DevThrottle as ${status.email}` : "Connected to DevThrottle"}
            </span>
          </div>
          <div className="acct-logout-row">
            <button className="acct-btn" onClick={onDisconnect} disabled={busy}>
              {busy ? "Disconnecting..." : "Disconnect this Gateway"}
            </button>
            <span className="acct-note">
              Clears the sign-in stored on the computer this Gateway runs on. It does not sign this browser out.
            </span>
          </div>
        </div>
      ) : (
        <div className="acct-card">
          <div className="acct-state">
            <span className="acct-dot" />
            <span className="acct-state-label">This Gateway is not connected to DevThrottle</span>
          </div>
          <p className="acct-signedout-explain">Connect it to register this computer and manage your account.</p>
          <button className="acct-btn signin" onClick={onSignIn}>
            Connect this Gateway to DevThrottle
          </button>
          <p className="acct-note acct-signin-note">
            Takes you to DevThrottle to sign in with Google, GitHub, or email, then brings you back here.
          </p>
          {signInError !== null && <div className="acct-signin-error">{signInError}</div>}
        </div>
      )}
      {loggedOut && <div className="acct-loggedout">Disconnected. The sign-in stored on this Gateway was cleared.</div>}
    </section>
  );
}
