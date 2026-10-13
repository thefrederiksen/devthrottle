// The sign-in callback screen both shells mount, on the answers the Gateway gives at POST /mobile/enroll
// (the Pricing Change mission). The real screen and the real enrolment code run; only fetch is replaced.
//
// Before this, a Free account's 402 landed in the generic error phase: "Something went wrong" over the
// body's bare `error` text, with one button, "Try again", that signed in again and was refused again.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { DeviceCallback } from "./DeviceCallback";
import { COCKPIT_ENROLLMENT_PROFILE, MOBILE_ENROLLMENT_PROFILE, configureEnrollment } from "./enrollRequest";

const SENTENCE = "The phone app and the Cockpit need the Remote plan. Your Director keeps working on the Free plan.";
const PRICING = "https://devthrottle.com/pricing";
const STATE = "state-this-browser-minted";

const realFetch = globalThis.fetch;

// Answer POST /mobile/enroll with the given status and body; anything else the screen sends (the report
// of a shown error) is accepted. Returns the enrolment calls so a test can count them.
function stubEnroll(status: number, body: unknown): { enrollCalls: () => number } {
  let calls = 0;
  globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
    if (String(input) === "/mobile/enroll") {
      calls += 1;
      return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    }
    return new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } });
  }) as unknown as typeof fetch;
  return { enrollCalls: () => calls };
}

// Arrive at the callback the way devthrottle.com returns the browser: the credential and the state this
// browser minted in the fragment, and the same state saved for the match.
function arrive(): void {
  sessionStorage.setItem("cc.enrollState", STATE);
  window.location.hash = `#access_token=account-token&state=${STATE}`;
  render(
    <MemoryRouter initialEntries={["/device-callback"]}>
      <Routes>
        <Route path="/device-callback" element={<DeviceCallback />} />
        <Route path="/signin" element={<div data-testid="signin-screen">sign in</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  sessionStorage.clear();
  localStorage.clear();
});

afterEach(() => {
  cleanup();
  globalThis.fetch = realFetch;
  window.location.hash = "";
  configureEnrollment(MOBILE_ENROLLMENT_PROFILE);
});

describe("DeviceCallback on a 402 at enrolment", () => {
  for (const [shell, profile] of [["the phone", MOBILE_ENROLLMENT_PROFILE], ["the Cockpit", COCKPIT_ENROLLMENT_PROFILE]] as const) {
    it(`${shell}: remote_plan_required shows the notice with the Gateway's sentence and its pricing link`, async () => {
      configureEnrollment(profile);
      stubEnroll(402, { error: "remote plan required", code: "remote_plan_required", message: SENTENCE, subscribeUrl: PRICING });

      arrive();

      await waitFor(() => expect(screen.getByTestId("remote-plan-notice")).toBeTruthy());
      expect(screen.getByTestId("remote-plan-message").textContent).toBe(SENTENCE);
      expect(screen.getByTestId("remote-plan-subscribe").getAttribute("href")).toBe(PRICING);
      // Not the generic failure, and not the body's bare `error` text.
      expect(screen.queryByText("Something went wrong")).toBeNull();
      expect(screen.queryByText("remote plan required")).toBeNull();
    });
  }

  it("try again on the notice starts the sign-in again, and enrolment was asked exactly once", async () => {
    const stub = stubEnroll(402, { error: "remote plan required", code: "remote_plan_required", message: SENTENCE, subscribeUrl: PRICING });

    arrive();
    await waitFor(() => expect(screen.getByTestId("remote-plan-notice")).toBeTruthy());
    fireEvent.click(screen.getByTestId("remote-plan-try-again"));

    await waitFor(() => expect(screen.getByTestId("signin-screen")).toBeTruthy());
    // No loop: the refusal did not make the screen ask again by itself.
    expect(stub.enrollCalls()).toBe(1);
  });

  it("the older 402 with hosted_subscription_required is still the generic failure with the Gateway's reason", async () => {
    stubEnroll(402, { error: "A subscription is required for the hosted Gateway.", code: "hosted_subscription_required" });

    arrive();

    await waitFor(() => expect(screen.getByText("Something went wrong")).toBeTruthy());
    expect(screen.getByText("A subscription is required for the hosted Gateway.")).toBeTruthy();
    expect(screen.queryByTestId("remote-plan-notice")).toBeNull();
  });

  it("the older 402 with no code is still the generic failure", async () => {
    stubEnroll(402, { error: "payment required" });

    arrive();

    await waitFor(() => expect(screen.getByText("Something went wrong")).toBeTruthy());
    expect(screen.getByText("payment required")).toBeTruthy();
    expect(screen.queryByTestId("remote-plan-notice")).toBeNull();
  });

  it("a 503 entitlement_unknown is never shown as a plan problem", async () => {
    stubEnroll(503, { error: "Could not check your plan just now. Try again shortly.", code: "entitlement_unknown", message: SENTENCE, subscribeUrl: PRICING });

    arrive();

    await waitFor(() => expect(screen.getByText("Something went wrong")).toBeTruthy());
    expect(screen.getByText("Could not check your plan just now. Try again shortly.")).toBeTruthy();
    expect(screen.queryByTestId("remote-plan-notice")).toBeNull();
    expect(screen.queryByText(SENTENCE)).toBeNull();
  });
});
