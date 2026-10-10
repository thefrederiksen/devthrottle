// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  backgroundRecovered,
  describeAndReport,
  reportShownError,
  resetReportingForTests,
  setReportingComponent,
} from "./reportClientError";
import { GatewayError } from "../api/client";

// reportShownError, the sibling of describeAndReport the phone and client-core sweep added (the Error Logging mission,
// issue #3675): a failure the client found with no error object to describe. Given `background` it shares
// describeAndReport's memory, so a poll that sets the same sentence every round reports it once.

describe("reportShownError and the background-read reporters", () => {
  const fetchMock = vi.fn();
  const posted = () =>
    fetchMock.mock.calls
      .filter(([url]) => url === "/client-errors")
      .map(([, init]) => JSON.parse((init as { body: string }).body) as Record<string, unknown>);

  beforeEach(() => {
    resetReportingForTests();
    localStorage.clear();
    fetchMock.mockReset();
    fetchMock.mockResolvedValue(new Response(null, { status: 202 }));
    vi.stubGlobal("fetch", fetchMock);
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.spyOn(console, "warn").mockImplementation(() => {});
    setReportingComponent("mobile");
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("reportShownError_FixedSentence_IsShownUnchangedAndReportedAsSeen", async () => {
    const shown = reportShownError("recorder", "start a recording", "This browser cannot store recordings durably (no IndexedDB).", {
      sessionId: "sess-7",
    });

    expect(shown).toBe("This browser cannot store recordings durably (no IndexedDB).");
    await vi.waitFor(() => expect(posted()).toHaveLength(1));
    expect(posted()[0]).toEqual({
      component: "mobile",
      surface: "recorder",
      action: "start a recording",
      message: shown,
      user_visible: true,
      session_id: "sess-7",
    });
  });

  it("reportShownError_WithACause_CarriesTheGatewayFactsButKeepsTheSentence", async () => {
    const cause = new GatewayError(502, "x", { reason: "That computer is offline.", code: "director_offline", correlationId: "corr-9" });

    const shown = reportShownError("voice-mode", "generate the narration", "This session's computer looks offline.", undefined, cause);

    expect(shown).toBe("This session's computer looks offline.");
    await vi.waitFor(() => expect(posted()).toHaveLength(1));
    expect(posted()[0]).toMatchObject({
      message: "This session's computer looks offline.",
      exception_type: "GatewayError",
      http_status: 502,
      error_code: "director_offline",
      correlation_id: "corr-9",
    });
  });

  it("reportShownError_Background_SameSentenceEveryRound_ReportsOnceUntilRecovered", async () => {
    for (let round = 0; round < 3; round++) {
      expect(reportShownError("voice-mode", "read the voice state", "Reconnecting...", { background: true })).toBe("Reconnecting...");
    }
    await vi.waitFor(() => expect(posted()).toHaveLength(1));

    // A good round, then the same sentence again: that is a new failure, reported again.
    backgroundRecovered("voice-mode", "read the voice state");
    reportShownError("voice-mode", "read the voice state", "Reconnecting...", { background: true });
    await vi.waitFor(() => expect(posted()).toHaveLength(2));
  });

  it("reportShownError_Background_SentenceChanges_ReportsTheNewOne", async () => {
    reportShownError("voice-mode", "read the voice state", "Reconnecting...", { background: true });
    reportShownError("voice-mode", "read the voice state", "This session's computer looks offline.", { background: true });

    await vi.waitFor(() => expect(posted()).toHaveLength(2));
    expect(posted()[1]).toMatchObject({ message: "This session's computer looks offline." });
  });

  it("reportShownError_BackgroundSharesTheKeyWithDescribeAndReport_OneDisplayOneReport", async () => {
    const err = new GatewayError(503, "x");
    const shown = describeAndReport("voice-mode", "read the voice state", err, { background: true });
    reportShownError("voice-mode", "read the voice state", shown, { background: true }, err);

    await vi.waitFor(() => expect(posted()).toHaveLength(1));
  });
});
