// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  describeAndReportWhenNew,
  describeReadAndReport,
  reportShownError,
  reportShownErrorWhenNew,
  resetReportingForTests,
  setReportingComponent,
  type ReportMemory,
} from "./reportClientError";
import { GatewayError, gatewayErrorMessage } from "../api/client";

// The siblings of describeAndReport that the phone and client-core sweep added (the Error Logging mission, issue
// #3675): a failure the client found with no error object to describe, a background read whose sentence names no
// action, and a display a poll sets again every round - reported when the failure first appears, not every round.

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

  it("describeReadAndReport_BackgroundRead_SentenceNamesNoActionButTheReportDoes", async () => {
    const err = new GatewayError(503, "x");

    const shown = describeReadAndReport("mobile-repos", "read the repositories", err);

    // The shared background-read line, exactly what the page showed before it reported.
    expect(shown).toBe(gatewayErrorMessage(err));
    await vi.waitFor(() => expect(posted()).toHaveLength(1));
    expect(posted()[0]).toMatchObject({ surface: "mobile-repos", action: "read the repositories", message: shown, http_status: 503 });
  });

  it("describeReadAndReport_PollWithMemory_ReportsOnceUntilTheDisplayClears", async () => {
    const memory: ReportMemory = { current: null };
    const err = new GatewayError(503, "x");

    for (let round = 0; round < 5; round++) describeReadAndReport("mobile-home", "load the sessions", err, undefined, memory);
    await vi.waitFor(() => expect(posted()).toHaveLength(1));

    // The display cleared (a good round), then the same failure came back: that is a new failure, reported again.
    memory.current = null;
    describeReadAndReport("mobile-home", "load the sessions", err, undefined, memory);
    await vi.waitFor(() => expect(posted()).toHaveLength(2));
  });

  it("describeReadAndReport_PollWhoseFailureChanges_ReportsTheNewOne", async () => {
    const memory: ReportMemory = { current: null };

    describeReadAndReport("mobile-home", "load the sessions", new GatewayError(503, "x"), undefined, memory);
    describeReadAndReport("mobile-home", "load the sessions", new GatewayError(500, "y", { reason: "The roster is rebuilding." }), undefined, memory);

    await vi.waitFor(() => expect(posted()).toHaveLength(2));
    expect(posted()[1]).toMatchObject({ http_status: 500 });
  });

  it("describeAndReportWhenNew_RetriedAction_SentenceNamesTheActionAndReportsOnce", async () => {
    const memory: ReportMemory = { current: null };
    const err = new GatewayError(500, "x");

    const first = describeAndReportWhenNew(memory, "current-team", "read your teams", err);
    const second = describeAndReportWhenNew(memory, "current-team", "read your teams", err);

    expect(first).toBe(gatewayErrorMessage(err, "read your teams"));
    expect(second).toBe(first);
    await vi.waitFor(() => expect(posted()).toHaveLength(1));
  });

  it("reportShownErrorWhenNew_SameSentenceEveryRound_ReportsOnce", async () => {
    const memory: ReportMemory = { current: null };

    for (let round = 0; round < 3; round++) {
      expect(reportShownErrorWhenNew(memory, "voice-mode", "read the voice state", "Reconnecting...")).toBe("Reconnecting...");
    }

    await vi.waitFor(() => expect(posted()).toHaveLength(1));
  });
});
