import { afterEach, describe, expect, it, vi } from "vitest";
import {
  SWITCH_NOW_TIMEOUT_MS,
  getLicenseForecast,
  getLicenseLog,
  getLicenseOverview,
  setLicenseRenewalDate,
  switchLicenseNow,
} from "./licensesClient";
import { gatewayErrorMessage } from "../api/client";

// The Licenses reads and Switch now, at the wire. What these pin: each call goes to its own root-relative Gateway
// route; the log's cursor goes back exactly as the Gateway gave it; a body without its list is an ERROR, never an
// empty account; and a refusal carries the Gateway's own sentence to whoever shows it.

interface Call {
  url: string;
  init: RequestInit | undefined;
}

function stubFetch(status: number, body: unknown): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      calls.push({ url: String(input), init });
      return {
        ok: status >= 200 && status < 300,
        status,
        headers: new Headers(),
        json: async () => body,
        text: async () => JSON.stringify(body),
      } as unknown as Response;
    }),
  );
  return calls;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the Licenses reads", () => {
  it("reads the overview from /gateway/licenses", async () => {
    const calls = stubFetch(200, { asOfUtc: "2026-10-11T03:12:00Z", isEmpty: true, emptyText: "None yet.", accounts: [], machines: [] });

    const overview = await getLicenseOverview();

    expect(calls.map((c) => c.url)).toEqual(["/gateway/licenses"]);
    expect(calls[0].init?.method).toBe("GET");
    expect(overview.isEmpty).toBe(true);
    expect(overview.emptyText).toBe("None yet.");
  });

  it("reads the forecast from /gateway/licenses/forecast", async () => {
    const calls = stubFetch(200, { asOfUtc: "", isEmpty: false, emptyText: null, nextSwitch: null, nextSwitchText: "No switch is expected.", accounts: [], machines: [] });

    const forecast = await getLicenseForecast();

    expect(calls.map((c) => c.url)).toEqual(["/gateway/licenses/forecast"]);
    expect(forecast.nextSwitchText).toBe("No switch is expected.");
  });

  it("reads the newest log page with no cursor, and an older page with the cursor exactly as given", async () => {
    const calls = stubFetch(200, { asOfUtc: "", isEmpty: false, emptyText: null, entries: [], hasMore: false, nextCursor: null });

    await getLicenseLog();
    await getLicenseLog(null);
    await getLicenseLog("639272851200000000.3");

    expect(calls.map((c) => c.url)).toEqual([
      "/gateway/licenses/log",
      "/gateway/licenses/log",
      "/gateway/licenses/log?cursor=639272851200000000.3",
    ]);
  });

  it("refuses a successful answer that has lost its list, rather than showing an account with nothing in it", async () => {
    stubFetch(200, { asOfUtc: "", isEmpty: false });
    await expect(getLicenseOverview()).rejects.toThrow(/without a 'accounts' list/);

    stubFetch(200, null);
    await expect(getLicenseLog()).rejects.toThrow(/without a 'entries' list/);
  });

  it("throws the Gateway's own sentence when a read is refused", async () => {
    stubFetch(403, { code: "no_account", error: "no account is bound to this request" });

    await expect(getLicenseOverview()).rejects.toMatchObject({ status: 403, code: "no_account", message: "no account is bound to this request" });
  });
});

describe("Switch now", () => {
  it("posts the account and the target to /gateway/licenses/switch-now", async () => {
    const calls = stubFetch(200, { targetAccountEmail: "account-b@example.com", switches: [], skipped: [] });

    const result = await switchLicenseNow("account-a@example.com", "account-b@example.com");

    expect(calls.length).toBe(1);
    expect(calls[0].url).toBe("/gateway/licenses/switch-now");
    expect(calls[0].init?.method).toBe("POST");
    // The Gateway refuses a body not sent as application/json before it reads it.
    expect(new Headers(calls[0].init?.headers).get("Content-Type")).toBe("application/json");
    expect(JSON.parse(String(calls[0].init?.body))).toEqual({ accountEmail: "account-a@example.com", targetAccountEmail: "account-b@example.com" });
    expect(result.targetAccountEmail).toBe("account-b@example.com");
  });

  it("waits longer than the Gateway's own 60-second wait for the machines", () => {
    expect(SWITCH_NOW_TIMEOUT_MS).toBeGreaterThan(60000);
  });

  it("carries a 403 owner_only to the screen as the Gateway's sentence, word for word", async () => {
    const sentence =
      "only the owner on their own signed-in phone or browser may switch the machines of a subscription to another one; this request was made with a device (workstation) credential";
    stubFetch(403, { code: "owner_only", error: sentence });

    const refusal = await switchLicenseNow("account-a@example.com", "account-b@example.com").catch((err: unknown) => err);

    expect(refusal).toMatchObject({ status: 403, code: "owner_only", message: sentence });
    // What a page shows for it: nothing added, nothing taken away - no "Try again", no status number.
    expect(gatewayErrorMessage(refusal, "switch the account")).toBe(sentence);
  });

  it("sends a null target when the Gateway is to choose", async () => {
    const calls = stubFetch(200, { targetAccountEmail: "account-b@example.com", switches: [], skipped: [] });

    await switchLicenseNow("account-a@example.com", null);

    expect(JSON.parse(String(calls[0].init?.body))).toEqual({ accountEmail: "account-a@example.com", targetAccountEmail: null });
  });

  it("throws the Gateway's refusal sentence on a 409", async () => {
    stubFetch(409, { code: "target-full", error: "account-b@example.com is at its limit until its 5-hour window resets." });

    await expect(switchLicenseNow("account-a@example.com", "account-b@example.com")).rejects.toMatchObject({
      status: 409,
      code: "target-full",
      message: "account-b@example.com is at its limit until its 5-hour window resets.",
    });
  });

  it("refuses an accepted answer that has lost its lists", async () => {
    stubFetch(200, { targetAccountEmail: "account-b@example.com" });

    await expect(switchLicenseNow("account-a@example.com", null)).rejects.toThrow(/'switches' and 'skipped'/);
  });
});

describe("the renewal date of a subscription", () => {
  it("puts the account and the date to /gateway/licenses/accounts/renewal as application/json", async () => {
    const calls = stubFetch(200, { accountEmail: "account-a@example.com", renewsOn: "2026-10-17", renewsText: "Renews 17 Oct" });

    const stored = await setLicenseRenewalDate("account-a@example.com", "2026-10-17");

    expect(calls.length).toBe(1);
    expect(calls[0].url).toBe("/gateway/licenses/accounts/renewal");
    expect(calls[0].init?.method).toBe("PUT");
    expect(new Headers(calls[0].init?.headers).get("Content-Type")).toBe("application/json");
    expect(JSON.parse(String(calls[0].init?.body))).toEqual({ accountEmail: "account-a@example.com", renewsOn: "2026-10-17" });
    expect(stored).toEqual({ accountEmail: "account-a@example.com", renewsOn: "2026-10-17", renewsText: "Renews 17 Oct" });
  });

  it("clears the date with an explicit null: a body that leaves renewsOn out is refused by the Gateway", async () => {
    const calls = stubFetch(200, { accountEmail: "account-a@example.com", renewsOn: null, renewsText: "Renewal date not set" });

    const stored = await setLicenseRenewalDate("account-a@example.com", null);

    expect(String(calls[0].init?.body)).toBe('{"accountEmail":"account-a@example.com","renewsOn":null}');
    expect(stored.renewsText).toBe("Renewal date not set");
  });

  it("throws the Gateway's own sentence when the date is refused", async () => {
    stubFetch(403, { code: "owner_only", error: "only the owner on their own signed-in phone or browser may set the renewal date of a subscription; this request was made with a device (workstation) credential" });

    const refusal = await setLicenseRenewalDate("account-a@example.com", "2026-10-17").catch((err: unknown) => err);

    expect(refusal).toMatchObject({ status: 403, code: "owner_only" });
    expect(gatewayErrorMessage(refusal, "set the renewal date")).toBe(
      "only the owner on their own signed-in phone or browser may set the renewal date of a subscription; this request was made with a device (workstation) credential",
    );
  });

  it("refuses a stored answer that has lost its sentence, rather than showing a date with no words", async () => {
    stubFetch(200, { accountEmail: "account-a@example.com", renewsOn: "2026-10-17" });

    await expect(setLicenseRenewalDate("account-a@example.com", "2026-10-17")).rejects.toThrow(/'renewsText'/);
  });
});
