// The enrollment call (issue #908, shared by the desktop Cockpit since issue #1088): hand the Gateway
// the credential this device received from devthrottle.com, and receive back the LOCAL device key the
// Gateway issues and validates offline. This is the only place the cloud-issued credential is used;
// from then on the app authenticates with the local key returned here. POST /mobile/enroll is under
// /mobile/, so it is reachable before the device holds any credential (the Gateway also keeps the old
// POST /m/enroll mapped, so an installed PWA still mid-flight on the previous bundle keeps working).
// Both shells enroll through this ONE generalized endpoint - the platform field tells the Gateway what
// kind of device this is (android/ios -> phone, anything else, for example "browser" -> browser).
//
// There are TWO enrollment credentials, one per gateway kind, and the client forwards whichever the
// website returned in the callback fragment (multi-tenant hosted sign-in, Phase C):
//   - SELF-HOST gateway: an account-scoped device_key carried in the request BODY (enrollDevice), the
//     pre-hosted behavior that must not regress.
//   - HOSTED gateway: the person's short-lived Supabase access token, carried as an
//     `Authorization: Bearer` header (enrollDeviceHosted). The hosted mint reads the account subject
//     from that verified token, checks the paid entitlement, resolves the tenant, and mints a
//     tenant-scoped device key. No device_key is sent in the hosted body.
import { GatewayError } from "./client";
import { readRemotePlanRequired, type RemotePlanRequired } from "../plan/remotePlan";

// The ONE enrollment seam every browser shell POSTs to. Canonicalized from /m/enroll to /mobile/enroll
// with the app's /m -> /mobile re-base; the Gateway still answers the old /m/enroll too (back-compat).
export const ENROLL_PATH = "/mobile/enroll";

/**
 * The Gateway answers POST /mobile/enroll with 409 when THE GATEWAY ITSELF is not signed in to a DevThrottle
 * account, so it has no account to enroll this device onto.
 *
 * This is not really a failure - on a fresh install it is the expected state - and it is the one
 * enrollment outcome the person can fix themselves, by signing the Gateway in. Callers must therefore be
 * able to tell it apart from a genuine error (403 wrong account, 502 cloud unreachable) and offer that
 * action instead of a "try again" that would fail identically.
 */
export const GATEWAY_NOT_SIGNED_IN = 409;

/**
 * Whether an enrollment failure is "the GATEWAY is not signed in" (as opposed to the person not being
 * signed in, which is a different thing entirely - see GATEWAY_NOT_SIGNED_IN).
 */
export function isGatewayNotSignedIn(err: unknown): err is GatewayError {
  return err instanceof GatewayError && err.status === GATEWAY_NOT_SIGNED_IN;
}

/**
 * The Gateway answered POST /mobile/enroll with 402 and the code remote_plan_required: the account is
 * signed in but its plan does not include the phone app and the Cockpit (the Pricing Change mission).
 *
 * Like the Gateway-not-signed-in conflict above, this is not a failure of ours and "try again" alone
 * would fail identically, so it is its own error: the callback screen shows the Gateway's sentence and
 * the pricing page it named instead of "Something went wrong". Its `message` IS the Gateway's sentence,
 * so anything that shows `err.message` shows the right words by construction.
 *
 * Every OTHER 402 from this endpoint (hosted_subscription_required, or no code at all) is still the
 * plain GatewayError it always was.
 */
export class RemotePlanRequiredError extends GatewayError {
  readonly info: RemotePlanRequired;
  constructor(info: RemotePlanRequired) {
    super(402, info.message, { reason: info.message, code: "remote_plan_required", retryable: false });
    this.name = "RemotePlanRequiredError";
    this.info = info;
  }
}

/**
 * Exchange the cloud device key for a local Gateway device key.
 * @returns the local per-device key the app must store and send as its Bearer.
 * @throws GatewayError on any non-2xx, carrying the server's reason (403 = not on this Gateway's
 *   account; 409 = the Gateway is not signed in; 502 = the cloud could not be reached).
 */
export async function enrollDevice(
  cloudDeviceKey: string,
  deviceId: string,
  name: string,
  platform: string,
  signal?: AbortSignal,
): Promise<string> {
  const res = await fetch(ENROLL_PATH, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify({ deviceKey: cloudDeviceKey, deviceId, name, platform }),
    signal,
  });
  return readLocalDeviceKey(res);
}

/**
 * Exchange the account's short-lived Supabase access token for a tenant-scoped local device key on a
 * HOSTED gateway (multi-tenant hosted sign-in, Phase C). The token is forwarded as the standard
 * `Authorization: Bearer` header - exactly the mint boundary the hosted /mobile/enroll endpoint expects -
 * and NO device_key is sent in the body (tenant isolation is bound at the mint from the verified
 * token, never from anything the client puts in the body).
 * @returns the tenant-scoped per-device key the app must store and send as its Bearer.
 * @throws GatewayError on any non-2xx, carrying the server's reason (401 = token missing/invalid;
 *   402 = the account has no paid entitlement - a RemotePlanRequiredError when the Gateway says the
 *   Remote plan is what is missing; 409 = the Gateway is not signed in; 502 = the cloud
 *   could not be reached).
 */
export async function enrollDeviceHosted(
  accessToken: string,
  deviceId: string,
  name: string,
  platform: string,
  signal?: AbortSignal,
): Promise<string> {
  const res = await fetch(ENROLL_PATH, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Accept: "application/json",
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ deviceId, name, platform }),
    signal,
  });
  return readLocalDeviceKey(res);
}

/**
 * Read the local device key from an /mobile/enroll response, or throw a GatewayError carrying the server's
 * reason. Shared verbatim by the self-host and hosted paths so both handle success and every failure
 * identically - only the request differs between them, never the response handling.
 */
async function readLocalDeviceKey(res: Response): Promise<string> {
  if (!res.ok) {
    let detail = `${res.status}`;
    let body: unknown;
    try {
      body = await res.json();
      detail = (body as { error?: string }).error ?? detail;
    } catch {
      /* non-JSON error body - keep the status code */
    }
    const planRequired = readRemotePlanRequired(res.status, body);
    if (planRequired !== null) throw new RemotePlanRequiredError(planRequired);
    throw new GatewayError(res.status, detail);
  }
  const body = (await res.json()) as { deviceKey?: string };
  const localKey = (body.deviceKey ?? "").trim();
  if (!localKey) {
    throw new GatewayError(res.status, "Enrollment succeeded but returned no device key.");
  }
  return localKey;
}
