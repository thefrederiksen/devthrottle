// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";

// THE PHONE'S SEND RED BOX IS REPORTED (the Error Logging mission's first fix, issue #3675). When Send fails the
// phone shows a red box; before this it was the one error the owner complained about most and the one nothing
// recorded. Through the REAL Gateway client and the REAL reporter, only fetch faked: a Send the Gateway refuses
// shows the red box AND posts one report carrying component mobile, action "send prompt", user_visible true, the
// session id, the HTTP status and the Gateway's correlation id from its X-Correlation-Id header.

import { resetReportingForTests, setReportingComponent } from "@devthrottle/client-core/errors/reportClientError";
import { SessionControls } from "./SessionControls";

const SID = "2c3c4215-0a1b-4c2d-9e8f-0123456789ab";

const fetchMock = vi.fn();
const reportBodies = () =>
  fetchMock.mock.calls
    .filter(([url]) => url === "/client-errors")
    .map(([, init]) => JSON.parse((init as { body: string }).body) as Record<string, unknown>);

beforeEach(() => {
  resetReportingForTests();
  localStorage.clear();
  setReportingComponent("mobile");
  fetchMock.mockReset();
  fetchMock.mockImplementation(async (url: string) =>
    url === "/client-errors"
      ? new Response(null, { status: 202 })
      : new Response(JSON.stringify({ error: "owning director is not connected" }), {
          status: 503,
          headers: { "Content-Type": "application/json", "X-Correlation-Id": "corr-7f3a9c" },
        }),
  );
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("phone Send red box", () => {
  it("shows the Gateway's sentence and reports it with the session, the action and the correlation id", async () => {
    const onError = vi.fn();
    render(<SessionControls sessionId={SID} onFlash={() => {}} onError={onError} showKeyRows={false} />);

    fireEvent.change(screen.getByPlaceholderText(/type a message/i), { target: { value: "please run the tests" } });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(onError).toHaveBeenCalledTimes(1));
    const shown = onError.mock.calls[0][0] as string;
    expect(shown).toContain("owning director is not connected");

    await waitFor(() => expect(reportBodies()).toHaveLength(1));
    const body = reportBodies()[0];
    // The exact body the phone sends, printed so the handback can quote it.
    console.log(`[sendRedBoxReported] body: ${JSON.stringify(body)}`);
    expect(body).toEqual({
      component: "mobile",
      surface: "mobile-session-controls",
      action: "send prompt",
      message: shown,
      user_visible: true,
      exception_type: "GatewayError",
      http_status: 503,
      correlation_id: "corr-7f3a9c",
      session_id: SID,
    });
    expect(JSON.stringify(body)).not.toContain("please run the tests");
  });
});
