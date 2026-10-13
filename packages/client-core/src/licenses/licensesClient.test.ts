import { afterEach, describe, expect, it, vi } from "vitest";
import {
  SWITCH_NOW_TIMEOUT_MS,
  addLicenseSpare,
  getLicenseApprovers,
  getLicenseForecast,
  getLicenseLog,
  getLicenseOverview,
  removeLicenseApprover,
  renewLicenseSignIn,
  setLicenseApprover,
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

// The sign-ins of the Licenses page, at the wire: who approves an account (read, set, remove), Add a spare and Renew
// now. What these pin: each call goes to its own route by its own method; a write with a body is application/json and
// carries exactly the fields the Gateway reads; the removal names its account in the query and has no body; a refusal
// carries the Gateway's own sentence, word for word; and an answer that lost what the page shows is an error.
const APPROVER = {
  accountEmail: "account-a@example.com",
  machineName: "the-mac",
  osUser: "owner",
  browserProfile: "work",
  machineId: "527062dd720c4d28a144183033cf93c1",
  label: "the-mac",
  canApproveNow: true,
  status: "ready",
  statusText: "Ready",
  statusTone: "good",
  text: "Sign-ins for account-a@example.com are approved on the-mac, in the browser profile work.",
};
const STARTED = {
  signInId: "501d61535066417d966ef172151b00bc",
  accountEmail: "account-a@example.com",
  machine: { machineId: "2f9cea24fe1b44f4951e48ed1748f105", machineName: "the-windows-machine", osUser: "owner", label: "the-windows-machine" },
  text: "Asked the Director on the-windows-machine to renew the sign-in for account-a@example.com. It can take a few minutes; the outcome appears in the log.",
  entry: { entryId: "1e080484ba78433982ea385e8ffa04f4", kind: "sign-in", stateText: "Signing in", stateTone: "neutral", summary: "the-windows-machine is renewing its sign-in for account-a@example.com." },
};
const OWNER_ONLY =
  "only the owner on their own signed-in phone or browser may start a sign-in on a machine; this request was made with a device (workstation) credential";

describe("who approves the sign-ins of an account", () => {
  it("reads the approvers from /gateway/licenses/approvers", async () => {
    const calls = stubFetch(200, { asOfUtc: "2026-10-11T03:12:00Z", isEmpty: false, emptyText: null, approvers: [APPROVER] });

    const answer = await getLicenseApprovers();

    expect(calls.map((c) => c.url)).toEqual(["/gateway/licenses/approvers"]);
    expect(calls[0].init?.method).toBe("GET");
    expect(answer.approvers).toEqual([APPROVER]);
  });

  it("refuses a read that has lost its list, rather than showing that nobody approves anything", async () => {
    stubFetch(200, { asOfUtc: "", isEmpty: true, emptyText: "None." });

    await expect(getLicenseApprovers()).rejects.toThrow(/without a 'approvers' list/);
  });

  it("puts the account, the machine, its user and the browser profile as application/json, and nothing else", async () => {
    const calls = stubFetch(200, APPROVER);

    const stored = await setLicenseApprover({ accountEmail: "account-a@example.com", machineName: "the-mac", osUser: "owner", browserProfile: "work" });

    expect(calls.length).toBe(1);
    expect(calls[0].url).toBe("/gateway/licenses/approvers");
    expect(calls[0].init?.method).toBe("PUT");
    expect(new Headers(calls[0].init?.headers).get("Content-Type")).toBe("application/json");
    expect(String(calls[0].init?.body)).toBe('{"accountEmail":"account-a@example.com","machineName":"the-mac","osUser":"owner","browserProfile":"work"}');
    expect(stored).toEqual(APPROVER);
  });

  it("throws the Gateway's own sentence when the approver is refused", async () => {
    const sentence =
      "browserProfile is the name of a browser profile on the approving machine: letters, digits, spaces, dots, underscores and hyphens, at most 64 characters.";
    stubFetch(400, { code: "invalid_browser_profile", error: sentence });

    const refusal = await setLicenseApprover({ accountEmail: "account-a@example.com", machineName: "the-mac", osUser: "owner", browserProfile: "a/b" }).catch((err: unknown) => err);

    expect(refusal).toMatchObject({ status: 400, code: "invalid_browser_profile", message: sentence });
    expect(gatewayErrorMessage(refusal, "set who approves the sign-ins")).toBe(sentence);
  });

  it("refuses a stored approver that has lost its sentence", async () => {
    stubFetch(200, { ...APPROVER, text: undefined });

    await expect(setLicenseApprover({ accountEmail: "a@example.com", machineName: "m", osUser: "u", browserProfile: "p" })).rejects.toThrow(/'text'/);
  });

  it("removes an approver with a DELETE that names the account in the query and sends no body", async () => {
    const answer = { accountEmail: "account+b@example.com", removed: true, text: "No machine is set to approve sign-ins for account+b@example.com any more." };
    const calls = stubFetch(200, answer);

    const removed = await removeLicenseApprover("account+b@example.com");

    expect(calls.length).toBe(1);
    // The account is escaped: a plus sign sent bare would reach the Gateway as a space.
    expect(calls[0].url).toBe("/gateway/licenses/approvers?accountEmail=account%2Bb%40example.com");
    expect(calls[0].init?.method).toBe("DELETE");
    expect(calls[0].init?.body).toBeUndefined();
    expect(removed).toEqual(answer);
  });

  it("throws the Gateway's own sentence when the removal is refused", async () => {
    const sentence =
      "only the owner on their own signed-in phone or browser may remove the machine that approves an account's sign-ins; this request was made with a device (workstation) credential";
    stubFetch(403, { code: "owner_only", error: sentence });

    const refusal = await removeLicenseApprover("account-b@example.com").catch((err: unknown) => err);

    expect(refusal).toMatchObject({ status: 403, code: "owner_only", message: sentence });
    expect(gatewayErrorMessage(refusal, "remove who approves the sign-ins")).toBe(sentence);
  });

  it("refuses a removal answered without its sentence", async () => {
    stubFetch(200, { accountEmail: "account-b@example.com", removed: true });

    await expect(removeLicenseApprover("account-b@example.com")).rejects.toThrow(/'text'/);
  });
});

describe("Add a spare and Renew now", () => {
  it("posts the account and the machine's identifier to /gateway/licenses/sign-ins/add-spare as application/json", async () => {
    const calls = stubFetch(202, STARTED);

    const started = await addLicenseSpare("account-c@example.com", "2f9cea24fe1b44f4951e48ed1748f105");

    expect(calls.length).toBe(1);
    expect(calls[0].url).toBe("/gateway/licenses/sign-ins/add-spare");
    expect(calls[0].init?.method).toBe("POST");
    expect(new Headers(calls[0].init?.headers).get("Content-Type")).toBe("application/json");
    expect(String(calls[0].init?.body)).toBe('{"accountEmail":"account-c@example.com","machineId":"2f9cea24fe1b44f4951e48ed1748f105"}');
    expect(started).toEqual(STARTED);
  });

  it("posts the same body to /gateway/licenses/sign-ins/renew, and to no other route", async () => {
    const calls = stubFetch(202, STARTED);

    const started = await renewLicenseSignIn("account-a@example.com", "2f9cea24fe1b44f4951e48ed1748f105");

    expect(calls.map((c) => c.url)).toEqual(["/gateway/licenses/sign-ins/renew"]);
    expect(calls[0].init?.method).toBe("POST");
    expect(new Headers(calls[0].init?.headers).get("Content-Type")).toBe("application/json");
    expect(String(calls[0].init?.body)).toBe('{"accountEmail":"account-a@example.com","machineId":"2f9cea24fe1b44f4951e48ed1748f105"}');
    expect(started.entry.entryId).toBe("1e080484ba78433982ea385e8ffa04f4");
  });

  it("carries each refusal to the screen as the Gateway's sentence, word for word", async () => {
    stubFetch(403, { code: "owner_only", error: OWNER_ONLY });
    const forbidden = await addLicenseSpare("account-c@example.com", "m").catch((err: unknown) => err);
    expect(forbidden).toMatchObject({ status: 403, code: "owner_only", message: OWNER_ONLY });
    expect(gatewayErrorMessage(forbidden, "add a spare")).toBe(OWNER_ONLY);

    const running = "A sign-in for account-a@example.com on the-windows-machine is already running.";
    stubFetch(409, { code: "sign_in_running", error: running });
    const conflict = await renewLicenseSignIn("account-a@example.com", "m").catch((err: unknown) => err);
    expect(conflict).toMatchObject({ status: 409, code: "sign_in_running", message: running });
    expect(gatewayErrorMessage(conflict, "renew the sign-in")).toBe(running);
  });

  it("refuses a started answer that has lost its sentence or its row of the log", async () => {
    stubFetch(202, { ...STARTED, entry: undefined });
    await expect(addLicenseSpare("account-c@example.com", "m")).rejects.toThrow(/'text' sentence and its 'entry' row/);

    stubFetch(202, { ...STARTED, text: undefined });
    await expect(renewLicenseSignIn("account-a@example.com", "m")).rejects.toThrow(/'text' sentence and its 'entry' row/);
  });
});
