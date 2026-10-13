import { afterEach, describe, expect, it, vi } from "vitest";
import { GATEWAY_NOT_SIGNED_IN, RemotePlanRequiredError, enrollDevice, enrollDeviceHosted, isGatewayNotSignedIn } from "./enroll";
import { GatewayError } from "./client";

// There are TWO different sign-ins in this product and they are easy to conflate:
//
//   1. the PERSON signs in at devthrottle.com (this is what the Sign in button does), and
//   2. the GATEWAY signs in to a DevThrottle account (without this it has no account to enroll onto).
//
// A device cannot enroll until BOTH have happened, and a fresh install has only ever done (1). The
// Gateway reports that as 409 from POST /mobile/enroll. Before this existed, the callback screen treated it
// as a generic error and offered only "Try again" - which returns to the sign-in screen and fails again
// for exactly the same reason, a loop with no exit. So 409 must stay distinguishable from every other
// enrollment failure, or that dead end comes straight back.
//
// Note this is the pure classification only. client-core has no DOM test tooling (no jsdom, no
// testing-library), so the screen that consumes it is not rendered here.

describe("isGatewayNotSignedIn", () => {
  it("recognises the Gateway-not-signed-in conflict", () => {
    expect(isGatewayNotSignedIn(new GatewayError(GATEWAY_NOT_SIGNED_IN, "not signed in"))).toBe(true);
  });

  it("is 409, the status the Gateway actually answers with", () => {
    expect(GATEWAY_NOT_SIGNED_IN).toBe(409);
  });

  it("does not swallow the other enrollment failures, which the person cannot fix by signing the Gateway in", () => {
    // 403 = this device is not on the Gateway's account; 502 = the cloud was unreachable. Neither is
    // fixed by signing the Gateway in, so neither may reach that screen.
    expect(isGatewayNotSignedIn(new GatewayError(403, "wrong account"))).toBe(false);
    expect(isGatewayNotSignedIn(new GatewayError(502, "cloud unreachable"))).toBe(false);
    expect(isGatewayNotSignedIn(new GatewayError(400, "bad request"))).toBe(false);
  });

  it("does not mistake a plain error or a non-error for the conflict", () => {
    expect(isGatewayNotSignedIn(new Error("boom"))).toBe(false);
    expect(isGatewayNotSignedIn({ status: 409 })).toBe(false);
    expect(isGatewayNotSignedIn(null)).toBe(false);
    expect(isGatewayNotSignedIn(undefined)).toBe(false);
  });
});

// The two enrollment requests (multi-tenant hosted sign-in, Phase C). The client forwards whichever
// credential the website returned in the callback fragment, and the two gateway kinds authenticate the
// mint differently:
//   - SELF-HOST (enrollDevice): the device_key travels in the request BODY, no Authorization header.
//     This is the pre-hosted behavior and must stay byte-for-byte identical, or every self-hosted
//     install's sign-in breaks.
//   - HOSTED (enrollDeviceHosted): the account access token travels as `Authorization: Bearer` and NO
//     device_key is put in the body (tenant isolation is bound at the mint from the verified token).
// Both read the local device key back from the response identically; only the request differs.
describe("enrollment requests", () => {
  const realFetch = globalThis.fetch;
  afterEach(() => {
    globalThis.fetch = realFetch;
    vi.restoreAllMocks();
  });

  // A minimal fetch Response stand-in with a JSON body, enough for the enroll response reader.
  function jsonResponse(status: number, body: unknown): Response {
    return {
      ok: status >= 200 && status < 300,
      status,
      json: async () => body,
    } as unknown as Response;
  }

  function lastInit(fetchMock: ReturnType<typeof vi.fn>): { url: string; init: RequestInit } {
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    return { url, init };
  }

  describe("enrollDevice (self-host, unchanged)", () => {
    it("posts the device_key in the body with NO Authorization header", async () => {
      const fetchMock = vi.fn(async () => jsonResponse(200, { deviceKey: "local-key" }));
      globalThis.fetch = fetchMock as unknown as typeof fetch;

      const local = await enrollDevice("cloud-key", "install-1", "Android phone", "android");

      expect(local).toBe("local-key");
      const { url, init } = lastInit(fetchMock);
      expect(url).toBe("/mobile/enroll");
      expect(init.method).toBe("POST");
      const headers = init.headers as Record<string, string>;
      expect(headers.Authorization).toBeUndefined();
      expect(JSON.parse(init.body as string)).toEqual({
        deviceKey: "cloud-key",
        deviceId: "install-1",
        name: "Android phone",
        platform: "android",
      });
    });

    it("surfaces the Gateway's reason on a non-2xx as a GatewayError", async () => {
      globalThis.fetch = (async () =>
        jsonResponse(409, { error: "gateway not signed in" })) as unknown as typeof fetch;

      await expect(enrollDevice("cloud-key", "install-1", "Android phone", "android")).rejects.toMatchObject({
        status: 409,
      });
    });
  });

  describe("enrollDeviceHosted (hosted)", () => {
    it("sends Authorization: Bearer <token> and OMITS device_key from the body", async () => {
      const fetchMock = vi.fn(async () => jsonResponse(200, { deviceKey: "tenant-scoped-key" }));
      globalThis.fetch = fetchMock as unknown as typeof fetch;

      const local = await enrollDeviceHosted("access-token-abc", "install-9", "Edge on Windows", "browser");

      expect(local).toBe("tenant-scoped-key");
      const { url, init } = lastInit(fetchMock);
      expect(url).toBe("/mobile/enroll");
      expect(init.method).toBe("POST");
      const headers = init.headers as Record<string, string>;
      expect(headers.Authorization).toBe("Bearer access-token-abc");
      const body = JSON.parse(init.body as string) as Record<string, unknown>;
      expect(body).toEqual({ deviceId: "install-9", name: "Edge on Windows", platform: "browser" });
      // The account token must never leak into the request body, only the header.
      expect("deviceKey" in body).toBe(false);
      expect(JSON.stringify(body)).not.toContain("access-token-abc");
    });

    it("surfaces a 402 (no paid entitlement) as a GatewayError carrying the status", async () => {
      globalThis.fetch = (async () =>
        jsonResponse(402, { error: "payment required" })) as unknown as typeof fetch;

      await expect(
        enrollDeviceHosted("access-token-abc", "install-9", "Edge on Windows", "browser"),
      ).rejects.toBeInstanceOf(GatewayError);
      await expect(
        enrollDeviceHosted("access-token-abc", "install-9", "Edge on Windows", "browser"),
      ).rejects.toMatchObject({ status: 402 });
      // With no code it is NOT the Remote plan refusal: it stays the plain error it always was.
      await expect(
        enrollDeviceHosted("access-token-abc", "install-9", "Edge on Windows", "browser"),
      ).rejects.not.toBeInstanceOf(RemotePlanRequiredError);
    });
  });

  // The Pricing Change mission: a Free account's phone or browser is refused at enrolment with 402 and
  // the code remote_plan_required. Both enrolment requests read the answer through the same reader, so
  // both must hand the callback screen the Gateway's sentence and address rather than a bare reason.
  describe("the Remote plan refusal", () => {
    const sentence = "The phone app and the Cockpit need the Remote plan.";
    const refusal = { error: "remote plan required", code: "remote_plan_required", message: sentence, subscribeUrl: "https://devthrottle.com/pricing" };

    it("is a RemotePlanRequiredError carrying the Gateway's sentence and address, on both enrolment paths", async () => {
      globalThis.fetch = (async () => jsonResponse(402, refusal)) as unknown as typeof fetch;

      for (const attempt of [
        () => enrollDeviceHosted("access-token-abc", "install-9", "Android phone", "android"),
        () => enrollDevice("cloud-key", "install-1", "Android phone", "android"),
      ]) {
        const err = await attempt().catch((e: unknown) => e);
        expect(err).toBeInstanceOf(RemotePlanRequiredError);
        expect(err).toMatchObject({
          status: 402,
          code: "remote_plan_required",
          message: sentence,
          info: { message: sentence, subscribeUrl: "https://devthrottle.com/pricing" },
        });
      }
    });

    it("is not taken from the older 402 code or from a 503 entitlement_unknown", async () => {
      globalThis.fetch = (async () =>
        jsonResponse(402, { ...refusal, error: "subscription required", code: "hosted_subscription_required" })) as unknown as typeof fetch;
      const older = await enrollDeviceHosted("t", "i", "Android phone", "android").catch((e: unknown) => e);
      expect(older).not.toBeInstanceOf(RemotePlanRequiredError);
      expect(older).toMatchObject({ status: 402, message: "subscription required" });

      globalThis.fetch = (async () =>
        jsonResponse(503, { ...refusal, error: "could not check your plan", code: "entitlement_unknown" })) as unknown as typeof fetch;
      const unknown = await enrollDeviceHosted("t", "i", "Android phone", "android").catch((e: unknown) => e);
      expect(unknown).not.toBeInstanceOf(RemotePlanRequiredError);
      expect(unknown).toMatchObject({ status: 503, message: "could not check your plan" });
    });
  });
});
