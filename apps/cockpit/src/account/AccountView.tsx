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
import { AccountsPanel } from "@devthrottle/client-core/auth/AccountsPanel";
import { useNavigate } from "react-router-dom";
import { ErrorBanner, LoadingState, PageHeader } from "../components";
import { TeamsSection } from "./TeamsSection";

// The Account page (issue #978, epic #967) - the React port of the Blazor Cockpit Account.razor
// (#853/#648/#854). A pure client of the Gateway account endpoints: the credential lives on the
// Gateway and the raw token is NEVER fetched, stored, or displayed (security rule DT-05).
//   - signed-out: a real "Sign in" action that NAVIGATES to the Gateway's public sign-in front door
//     (POST /account/sign-in-start), which redirects a remote browser on to devthrottle.com and hands
//     it back to the Gateway's own callback - so this page just leaves and is re-entered signed in;
//   - signed-in: identity + Log out (POST /account/logout) AND a "Your devices" list from
//     GET /account/devices with a per-device Remove (DELETE /account/devices/{id}).
// Responsive (CodingStyle.md): the page renders immediately with a loading state and loads the status +
// device list asynchronously. On any failure it shows an explicit error state, never a fabricated
// signed-out or empty-devices view (the no-fallback rule).

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

export function AccountView() {
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

  // Adding an account to THIS BROWSER is the shared enrollment flow at the Cockpit's own /signin route,
  // which is a different journey from signing the GATEWAY in above (beginSignIn). client-core must not
  // hardcode either shell's route, so the shell supplies its own.
  const navigate = useNavigate();

  return (
    <div className="page acct">
      <PageHeader
        title="DevThrottle Account"
        subtitle={
          "The DevThrottle identity this Gateway is signed in as, and the devices registered to the " +
          "account. The credential lives on the Gateway; this page never sees the raw token."
        }
      />

      {/* THIS BROWSER's signed-in accounts (devthrottle_internal #1507/#1509) - the same shared panel
          the phone's Account screen mounts, so the two surfaces cannot drift (rule 8).
          It sits ABOVE the Gateway credential because the two are easy to confuse and only one of them
          is about the machine you are sitting at: signing out HERE means this browser forgets its key,
          while "Log out" further down clears the GATEWAY's own link to a DevThrottle account. Two
          different actions on two different things, so they are kept apart and worded apart. */}
      <section className="acct-browser" aria-label="Accounts signed in on this browser">
        <h2>Signed in on this browser</h2>
        <AccountsPanel onAddAccount={() => navigate("/signin")} />
      </section>

      {/* The person's teams and Create a team (Teams v1). Nothing at all when this Gateway offers no teams. */}
      <TeamsSection />

      <h2 className="acct-gateway-head">This Gateway's account</h2>

      {error !== null ? (
        <ErrorBanner
          message={`Could not load account status from the Gateway: ${error}`}
          onRetry={() => void loadStatus()}
        />
      ) : status === null ? (
        <LoadingState />
      ) : status.signedIn ? (
        <>
          <div className="acct-card">
            <div className="acct-state">
              <span className="acct-dot on" />
              <span className="acct-state-label on">Signed in</span>
            </div>
            <Row label="Email" value={status.email && status.email.length > 0 ? status.email : "(not available)"} />
            <Row
              label="Provider"
              value={status.provider && status.provider.length > 0 ? status.provider : "(not available)"}
            />
          </div>

          <div className="acct-logout-row">
            <button className="acct-btn primary" onClick={() => void logOut()} disabled={busy}>
              {busy ? "Logging out..." : "Log out"}
            </button>
            <span className="acct-note">
              Clears the Gateway credential and returns the Gateway to its sign-in prompt.
            </span>
          </div>

          <div className="acct-devices">
            <div className="acct-devices-head">
              <h2>Your devices</h2>
              <button
                className="acct-btn"
                onClick={() => void loadDevices()}
                disabled={devicesLoading}
              >
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
                The Gateway reports it is no longer signed in, so the device list is unavailable. Sign in
                again to see your devices.
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
        </>
      ) : (
        <div className="acct-card">
          <div className="acct-state">
            <span className="acct-dot" />
            <span className="acct-state-label">Not signed in</span>
          </div>
          <p className="acct-signedout-explain">
            This Gateway is not signed in to DevThrottle. Sign in to register this device and manage your
            account.
          </p>
          <button className="acct-btn signin" onClick={startSignInFlow}>
            Sign in to DevThrottle
          </button>
          <p className="acct-note acct-signin-note">
            Takes you to DevThrottle to sign in with Google, GitHub, or email, then brings you back here.
          </p>

          {signInError !== null && <div className="acct-signin-error">{signInError}</div>}
        </div>
      )}

      {loggedOut && <div className="acct-loggedout">Logged out. The Gateway credential was cleared.</div>}
    </div>
  );
}
