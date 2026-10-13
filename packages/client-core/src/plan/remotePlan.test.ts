// The "Remote plan needed" refusal (the Pricing Change mission), below the screen: which answers are
// recognised as it, which are not, and that an ordinary signed-in request refused with it raises the one
// app-level signal the shared notice renders from.
//
// The contract, as the Gateway answers it: HTTP 402 and
//   { error, code: "remote_plan_required", message: "<one finished sentence>", subscribeUrl: "<the pricing page>" }
// The sentence and the address are the Gateway's; nothing here may reword either.
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { GatewayError, listSessions } from "../api/client";
import {
  clearRemotePlanRequired,
  currentRemotePlanRequired,
  readRemotePlanRequired,
  reportRemotePlanRequired,
  subscribeRemotePlanRequired,
} from "./remotePlan";

const SENTENCE = "The phone app and the Cockpit need the Remote plan. Your Director keeps working on the Free plan.";
const PRICING = "https://devthrottle.com/pricing";
const REFUSAL = { error: "remote plan required", code: "remote_plan_required", message: SENTENCE, subscribeUrl: PRICING };

function answer(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("readRemotePlanRequired", () => {
  it("carries the Gateway's sentence and address unchanged", () => {
    expect(readRemotePlanRequired(402, REFUSAL)).toEqual({ message: SENTENCE, subscribeUrl: PRICING });
  });

  it("does not take the older 402 shapes for it", () => {
    // The 402 that already existed, and an enrolment 402 with no code at all. Both must keep doing
    // whatever they did before this notice existed.
    expect(readRemotePlanRequired(402, { error: "subscription required", code: "hosted_subscription_required", message: SENTENCE, subscribeUrl: PRICING })).toBeNull();
    expect(readRemotePlanRequired(402, { error: "payment required" })).toBeNull();
    expect(readRemotePlanRequired(402, null)).toBeNull();
  });

  it("never takes a 503 entitlement_unknown for a plan problem, whatever its body carries", () => {
    // Temporary: the Gateway could not read the plan. Telling that person to subscribe would be false.
    expect(readRemotePlanRequired(503, { error: "try again shortly", code: "entitlement_unknown" })).toBeNull();
    expect(readRemotePlanRequired(503, { ...REFUSAL, code: "entitlement_unknown" })).toBeNull();
    // Not even the right code on the wrong status.
    expect(readRemotePlanRequired(503, REFUSAL)).toBeNull();
  });

  it("is not the refusal without the Gateway's sentence, because this client has none of its own to show", () => {
    expect(readRemotePlanRequired(402, { code: "remote_plan_required", subscribeUrl: PRICING })).toBeNull();
    expect(readRemotePlanRequired(402, { code: "remote_plan_required", message: "   ", subscribeUrl: PRICING })).toBeNull();
  });

  it("carries no address when the Gateway sent none a browser may open", () => {
    expect(readRemotePlanRequired(402, { ...REFUSAL, subscribeUrl: undefined })).toEqual({ message: SENTENCE, subscribeUrl: null });
    expect(readRemotePlanRequired(402, { ...REFUSAL, subscribeUrl: "javascript:alert(1)" })).toEqual({ message: SENTENCE, subscribeUrl: null });
    expect(readRemotePlanRequired(402, { ...REFUSAL, subscribeUrl: "not an address" })).toEqual({ message: SENTENCE, subscribeUrl: null });
  });
});

describe("the app-level signal", () => {
  afterEach(clearRemotePlanRequired);

  it("holds the refusal for a screen mounted later, tells subscribers once, and forgets it on request", () => {
    let told = 0;
    const stop = subscribeRemotePlanRequired(() => { told += 1; });

    reportRemotePlanRequired({ message: SENTENCE, subscribeUrl: PRICING });
    // The app polls, so the same refusal arrives again and again; it must not re-render anything.
    reportRemotePlanRequired({ message: SENTENCE, subscribeUrl: PRICING });
    expect(currentRemotePlanRequired()).toEqual({ message: SENTENCE, subscribeUrl: PRICING });
    expect(told).toBe(1);

    clearRemotePlanRequired();
    expect(currentRemotePlanRequired()).toBeNull();
    expect(told).toBe(2);
    stop();
  });
});

// An ORDINARY request - the session list both shells poll - made by an account that was signed in and
// has lost the plan. This goes through the real client and its transport choke point; only fetch is
// replaced, with the answer the Gateway gives.
describe("an ordinary signed-in request", () => {
  const realFetch = globalThis.fetch;
  beforeEach(clearRemotePlanRequired);
  afterEach(() => {
    globalThis.fetch = realFetch;
    clearRemotePlanRequired();
  });

  it("refused with remote_plan_required raises the notice signal with the Gateway's sentence and address", async () => {
    globalThis.fetch = (async () => answer(402, REFUSAL)) as unknown as typeof fetch;

    // The caller still gets its error, built from the body it always read - the choke point read a copy.
    await expect(listSessions()).rejects.toBeInstanceOf(GatewayError);
    expect(currentRemotePlanRequired()).toEqual({ message: SENTENCE, subscribeUrl: PRICING });
  });

  it("refused with the older 402 raises nothing and fails as it always did", async () => {
    globalThis.fetch = (async () =>
      answer(402, { error: "A subscription is required.", code: "hosted_subscription_required" })) as unknown as typeof fetch;

    await expect(listSessions()).rejects.toMatchObject({ status: 402, message: "A subscription is required." });
    expect(currentRemotePlanRequired()).toBeNull();
  });

  it("answered 503 entitlement_unknown raises nothing: it is temporary, not a plan problem", async () => {
    globalThis.fetch = (async () =>
      answer(503, { ...REFUSAL, error: "Could not check your plan just now.", code: "entitlement_unknown" })) as unknown as typeof fetch;

    await expect(listSessions()).rejects.toMatchObject({ status: 503, code: "entitlement_unknown" });
    expect(currentRemotePlanRequired()).toBeNull();
  });

  it("refused with a 402 that is not JSON raises nothing and does not turn into a parse error", async () => {
    globalThis.fetch = (async () => new Response("payment required", { status: 402 })) as unknown as typeof fetch;

    await expect(listSessions()).rejects.toMatchObject({ status: 402, message: "payment required" });
    expect(currentRemotePlanRequired()).toBeNull();
  });
});
