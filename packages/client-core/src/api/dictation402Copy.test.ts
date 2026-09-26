import { afterEach, describe, expect, it, vi } from "vitest";
import {
  DICTATION_HELD_CREDITS_MESSAGE,
  dictationHeld402Message,
  uploadDictationToSession,
  type DictationUploadArgs,
  type HostedAiUnavailable,
} from "./client";

// Durable dictation 402 copy (issue #1360, inspection round). The Gateway maps every 402 code into the
// shared hosted-AI state and composes the copy; this client must RENDER that mapped copy, never replace
// it with hardcoded add-credits wording. Before this fix, every 402 - subscription required, fair-use
// limit, unknown code - was shown as "Out of transcription credits ...", which is exactly the credit
// wording the owner ruled a normal member never sees. Put the hardcoded DICTATION_HELD_CREDITS_MESSAGE
// return back in the 402 branch and these go red.

const UPLOAD_ID = "33333333-3333-3333-3333-333333333333";

const SUBSCRIPTION_TEXT =
  "Included AI features come with DevThrottle Pro. This account's trial or plan isn't active - see the plans at devthrottle.com/pricing.";
const FAIR_USE_TEXT =
  "This month's fair-use limit for included AI features has been reached. It resets at the start of next month.";
const CREDITS_TEXT =
  "Voice needs credit. Add $5 to turn on transcription, voice mode, and Wingman - enough to last most of a month.";

function fakeResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
    text: async () => JSON.stringify(body),
  } as unknown as Response;
}

function baseArgs(): DictationUploadArgs {
  return {
    sessionId: "11111111-1111-1111-1111-111111111111",
    uploadId: UPLOAD_ID,
    audio: new Blob(["hello"], { type: "audio/webm" }),
    before: "",
    after: "",
    prefix: "",
    sentAtUtc: "2026-09-25T09:05:12.345Z",
    resumed: true,
  };
}

// Register succeeds, then complete answers 402 with the given (already Gateway-mapped) body.
function mock402(body: unknown): void {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: unknown) => {
      const url = String(input);
      if (url.includes("/dictation/upload")) return fakeResponse(200, { upload_id: UPLOAD_ID });
      if (url.includes("/complete")) return fakeResponse(402, body);
      throw new Error("unexpected fetch: " + url);
    }),
  );
}

describe("durable dictation renders the MAPPED 402 state's copy", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("subscription_required shows the subscription copy, not credits wording", async () => {
    mock402({ state: "SubscriptionRequired", text: SUBSCRIPTION_TEXT, ctaLabel: "View plans", ctaAction: "OpenPricing", ctaUrl: "https://devthrottle.com/pricing" });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(false);
    expect(result.outOfCredits).toBe(true); // the driver still throttles the retry loop
    expect(result.error).toContain("DevThrottle Pro");
    expect(result.error).toContain("Your recording is saved on this device.");
    expect(result.error!.toLowerCase()).not.toContain("credit");
  });

  it("fair_use_limit_reached shows the fair-use copy, not credits wording", async () => {
    mock402({ state: "FairUseLimitReached", text: FAIR_USE_TEXT, ctaLabel: "", ctaAction: "None", ctaUrl: null });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(false);
    expect(result.error).toContain("fair-use limit");
    expect(result.error).toContain("Your recording is saved on this device.");
    expect(result.error!.toLowerCase()).not.toContain("credit");
  });

  it("an unreadable 402 body shows the neutral copy, never a credits claim", async () => {
    mock402({});

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(false);
    expect(result.error).toContain("This AI feature is not available for your account right now.");
    expect(result.error).toContain("Your recording is saved on this device.");
    expect(result.error!.toLowerCase()).not.toContain("credit");
  });

  it("the direct-API insufficient_credits state keeps the existing held-credits line", async () => {
    mock402({ state: "NeedsCredits", text: CREDITS_TEXT, ctaLabel: "Add credits", ctaAction: "OpenBilling", ctaUrl: "https://devthrottle.com/account" });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(false);
    expect(result.outOfCredits).toBe(true);
    expect(result.error).toBe(DICTATION_HELD_CREDITS_MESSAGE);
  });
});

describe("dictationHeld402Message", () => {
  it("appends the saved-on-device clause without doubling the full stop", () => {
    const info: HostedAiUnavailable = { state: "FairUseLimitReached", text: FAIR_USE_TEXT, ctaLabel: "", ctaAction: "None", ctaUrl: null };
    const message = dictationHeld402Message(info);
    expect(message).toBe(FAIR_USE_TEXT + " Your recording is saved on this device.");
    expect(message).not.toContain("..");
  });
});
