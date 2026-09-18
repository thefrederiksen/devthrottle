// The DevThrottle account surface of the Gateway (issue #978, epic #967): the typed, same-origin
// client the React Cockpit's Account page reads and drives. It is the shared-library port of the
// Blazor Cockpit's GatewayClient account methods (GetAccountStatusAsync / LogoutAccountAsync /
// GetAccountDevicesAsync / RemoveAccountDeviceAsync / StartSignInAsync), so the desktop React shell
// keeps exactly one copy of each account contract.
//
// The credential lives on the Gateway; this client NEVER fetches, stores, or displays the raw token
// (security rule DT-05). Every contract here is token-free. Every request is root-relative to the
// Gateway front door (never a Director address) and carries the same Bearer via authHeaders(). A user
// action throws GatewayError carrying the Gateway's own message on a non-2xx.
import { authHeaders, GatewayError } from "../api/client";

/** The GET /account/status body (also the body POST /account/logout echoes back). Token-free: only the
 *  signed-in boolean and, when signed in with a resolvable identity, the email + provider. */
export interface AccountStatus {
  signedIn: boolean;
  email?: string | null;
  provider?: string | null;
  /** True when this account is on the deployment's staff list, which today unlocks exactly one thing: the Wingman's
   *  debug view. The GATEWAY decides it - a client never works out what it may see - and the route it unlocks asks
   *  the same question for itself, so hiding a tab is never what protects anything. */
  staff?: boolean;
}

/** One device in the account device list (GET /account/devices). Every field is a display value the
 *  cloud already masked - never a raw key (DT-05). */
export interface AccountDevice {
  id: string;
  name: string;
  platform?: string | null;
  deviceType?: string | null;
  appVersion?: string | null;
  keyPrefix?: string | null;
  keyLast4?: string | null;
  createdAt?: string | null;
  lastSeenAt?: string | null;
  /** True when this record is the Gateway's own machine, so the page can mark it "This device". */
  thisDevice: boolean;
}

/** The GET /account/devices envelope. When signed in, signedIn is true and devices carries the list
 *  (possibly empty); when signed out, signedIn is false and devices is omitted (an explicit signed-out
 *  envelope, never a fabricated empty list). */
export interface AccountDevicesResponse {
  signedIn: boolean;
  devices?: AccountDevice[] | null;
}

/** The Gateway's public sign-in START front door (epic #1069, issues #1076/#1080). Public on the
 *  AuthMiddleware allow-list, so a browser with no Gateway credential can reach it. */
export const SIGN_IN_START_PATH = "/account/sign-in-start";

async function gatewayErrorFrom(res: Response, label: string): Promise<GatewayError> {
  let detail = `${res.status}`;
  try {
    const text = await res.text();
    if (text.length > 0) {
      try {
        const body = JSON.parse(text) as { error?: string; detail?: string };
        detail = body.error ?? body.detail ?? text;
      } catch {
        detail = text;
      }
    }
  } catch {
    /* body unreadable - keep the status code */
  }
  return new GatewayError(res.status, `${label} failed: ${detail}`);
}

// GET /account/status - the Gateway's signed-in DevThrottle identity, computed locally with no cloud
// call. Throws on transport failure so the Account page shows an error banner rather than a dead
// Gateway masquerading as a signed-out one.
export async function getAccountStatus(signal?: AbortSignal): Promise<AccountStatus> {
  const res = await fetch("/account/status", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /account/status");
  const body = (await res.json()) as Partial<AccountStatus> | null;
  return {
    signedIn: Boolean(body?.signedIn),
    staff: Boolean(body?.staff),
    email: body?.email ?? null,
    provider: body?.provider ?? null,
  };
}

/**
 * GET /account/status authenticated with a SPECIFIC device key instead of the active one
 * (devthrottle_internal #1509). Used at the end of enrollment to learn which account the key that was
 * just minted belongs to, so the account switcher labels it with the person's email rather than
 * "Account 2" - at that moment the new key is not the active one yet, so authHeaders() would ask about
 * the wrong account.
 *
 * On a HOSTED Gateway this answers about the CALLER, folded from the tenant that device key is bound to
 * (AccountStatusEndpoint.HostedStatus), which is precisely the identity wanted here. On a self-host
 * Gateway it answers about the Gateway's own single account - the same account by definition there.
 *
 * Returns null rather than throwing. A label is a nicety and the enrollment has ALREADY SUCCEEDED by
 * the time this runs, so a Gateway that cannot resolve an identity must not turn a completed sign-in
 * into a failure; the account is stored under its positional name and can be renamed.
 */
export async function getAccountStatusForKey(deviceKey: string, signal?: AbortSignal): Promise<AccountStatus | null> {
  try {
    const res = await fetch("/account/status", {
      method: "GET",
      headers: { Accept: "application/json", Authorization: `Bearer ${deviceKey}` },
      signal,
    });
    if (!res.ok) return null;
    const body = (await res.json()) as Partial<AccountStatus> | null;
    return {
      signedIn: Boolean(body?.signedIn),
      email: body?.email ?? null,
      provider: body?.provider ?? null,
    };
  } catch {
    return null;
  }
}

// POST /account/logout - clear the Gateway credential. Returns the post-logout status the Gateway
// echoes back (signed-out) so the page confirms without a second round-trip. Throws with the server
// error on failure.
export async function logoutAccount(signal?: AbortSignal): Promise<AccountStatus> {
  const res = await fetch("/account/logout", {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "POST /account/logout");
  const body = (await res.json()) as Partial<AccountStatus> | null;
  return {
    signedIn: Boolean(body?.signedIn),
    email: body?.email ?? null,
    provider: body?.provider ?? null,
  };
}

// GET /account/devices - the account-wide device list behind the Gateway's own stored credential.
// Throws on a Gateway/cloud error (502 etc.) so the page shows an explicit error state rather than
// presenting an empty list as "no devices".
export async function getAccountDevices(signal?: AbortSignal): Promise<AccountDevicesResponse> {
  const res = await fetch("/account/devices", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /account/devices");
  const body = (await res.json()) as Partial<AccountDevicesResponse> | null;
  return {
    signedIn: Boolean(body?.signedIn),
    // Preserve the signed-out envelope's omitted list as null; a signed-in account with no devices is
    // an empty array. Never fabricate one from the other.
    devices: body?.devices ?? null,
  };
}

// DELETE /account/devices/{id} - revoke one device from the account. Throws with the server error on
// failure (incl. 404 when the id is not the account's, 502 when the cloud is unreachable) so the page
// can show it; on success the caller refreshes the list.
export async function removeAccountDevice(deviceId: string, signal?: AbortSignal): Promise<void> {
  const id = encodeURIComponent(deviceId);
  const res = await fetch(`/account/devices/${id}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `DELETE /account/devices/${deviceId}`);
}

// Begin signing the GATEWAY in to its DevThrottle account by NAVIGATING this browser to the Gateway's
// public sign-in START front door.
//
// This must be a real form navigation, not a fetch. POST /account/sign-in-start answers a remote caller
// with a 302 to devthrottle.com carrying a redirect_uri back to the Gateway's own /account/sign-in-callback
// (AccountSignInStartEndpoint, epic #1069). Only a navigation lets the browser FOLLOW that redirect, sign
// in on the cloud page, and be handed back - a fetch would follow it in the background, where the person
// can never see or use the sign-in page.
//
// It deliberately does NOT use the old POST /account/sign-in: that endpoint always runs the Gateway's
// browser LOOPBACK sign-in, which opens a browser on the GATEWAY HOST's desktop and waits on 127.0.0.1.
// A Cockpit on any other machine can never reach that loopback, so the button appeared to hang forever.
//
// The browser leaves this page and returns via the callback, so there is nothing to poll and nothing to
// await - this function does not return.
//
// `doc` exists only so a unit test can assert the method and target without a DOM; callers pass nothing.
export function beginSignIn(doc: Document = document): void {
  const form = doc.createElement("form");
  form.method = "POST";
  form.action = SIGN_IN_START_PATH;
  doc.body.appendChild(form);
  form.submit();
}
