// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  admitReport,
  describeAndReport,
  installGlobalErrorReporting,
  lostReportCount,
  MAX_QUEUED_REPORTS,
  queuedReports,
  reportClientError,
  resetReportingForTests,
  setReportingComponent,
} from "./reportClientError";
import { GatewayError } from "../api/client";

describe("client error report rate cap", () => {
  beforeEach(() => resetReportingForTests());

  it("admits up to the cap within one minute, then holds the rest back", () => {
    const t0 = 1_000_000;
    let admitted = 0;
    for (let i = 0; i < 60; i++) {
      if (admitReport(t0 + i * 10)) admitted++;
    }
    expect(admitted).toBe(20);
  });

  it("a new minute opens a fresh window", () => {
    const t0 = 1_000_000;
    for (let i = 0; i < 30; i++) admitReport(t0);
    expect(admitReport(t0 + 60_000)).toBe(true);
  });

  it("a render-loop burst cannot exceed the cap even across repeated calls", () => {
    const t0 = 5_000_000;
    let admitted = 0;
    for (let i = 0; i < 1000; i++) {
      if (admitReport(t0 + i)) admitted++;
    }
    expect(admitted).toBe(20);
  });
});

// The Error Logging mission (issue #3675): a report carries the structured facts and nothing else, and a report
// that cannot be sent is queued - or counted - never dropped in silence.
describe("reporting: what a report carries, and the queue", () => {
  const fetchMock = vi.fn();
  const posted = () =>
    fetchMock.mock.calls
      .filter(([url]) => url === "/client-errors")
      .map(([, init]) => JSON.parse((init as { body: string }).body) as Record<string, unknown>);
  let online = true;

  beforeEach(() => {
    resetReportingForTests();
    localStorage.clear();
    fetchMock.mockReset();
    fetchMock.mockResolvedValue(new Response(null, { status: 202 }));
    vi.stubGlobal("fetch", fetchMock);
    online = true;
    vi.spyOn(navigator, "onLine", "get").mockImplementation(() => online);
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.spyOn(console, "warn").mockImplementation(() => {});
    setReportingComponent("mobile");
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("describeAndReport carries user_visible, surface, action, status, code, correlation id and session id - and no detail, stack or page", async () => {
    const err = new GatewayError(503, "x", {
      reason: "That machine is catching up.",
      code: "director_stale",
      retryable: true,
      correlationId: "corr-1234",
    });

    const shown = describeAndReport("mobile-session-controls", "send prompt", err, { sessionId: "sess-42" });

    expect(shown).toContain("That machine is catching up.");
    await vi.waitFor(() => expect(posted()).toHaveLength(1));
    expect(posted()[0]).toEqual({
      component: "mobile",
      surface: "mobile-session-controls",
      action: "send prompt",
      message: shown,
      user_visible: true,
      exception_type: "GatewayError",
      http_status: 503,
      error_code: "director_stale",
      correlation_id: "corr-1234",
      session_id: "sess-42",
    });
  });

  it("a report made before the shell names itself is said on the console and never sent", () => {
    resetReportingForTests();

    reportClientError({ surface: "s", action: "a", message: "m", user_visible: true });

    expect(fetchMock).not.toHaveBeenCalled();
    expect(console.error).toHaveBeenCalledWith(expect.stringContaining("setReportingComponent"));
  });

  it("offline: the report is queued, kept on the device, and delivered when the connection returns", async () => {
    online = false;
    installGlobalErrorReporting("mobile");
    fetchMock.mockClear();

    reportClientError({ surface: "s", action: "send prompt", message: "could not send", user_visible: true });

    expect(fetchMock).not.toHaveBeenCalled();
    expect(queuedReports()).toHaveLength(1);
    expect(JSON.parse(localStorage.getItem("devthrottle.clientErrors.queue.v1") ?? "[]")).toHaveLength(1);

    online = true;
    window.dispatchEvent(new Event("online"));

    await vi.waitFor(() => expect(posted()).toHaveLength(1));
    expect(posted()[0].message).toBe("could not send");
    await vi.waitFor(() => expect(queuedReports()).toHaveLength(0));
    expect(localStorage.getItem("devthrottle.clientErrors.queue.v1")).toBeNull();
  });

  it("a network failure queues the report, and the next successful send delivers it", async () => {
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    reportClientError({ surface: "s", action: "a", message: "first", user_visible: true });
    await vi.waitFor(() => expect(queuedReports()).toHaveLength(1));

    reportClientError({ surface: "s", action: "a", message: "second", user_visible: true });

    await vi.waitFor(() => expect(posted().map((b) => b.message)).toEqual(["first", "second", "first"]));
    await vi.waitFor(() => expect(queuedReports()).toHaveLength(0));
  });

  it.each([503, 429])("a Gateway answering %i queues the report", async (status) => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status }));

    reportClientError({ surface: "s", action: "a", message: "m", user_visible: true });

    await vi.waitFor(() => expect(queuedReports()).toHaveLength(1));
  });

  it("a report the Gateway refuses (400) is written to the console with its reason, not queued", async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ error: "component must be one of: cockpit, mobile" }), { status: 400 }));

    reportClientError({ surface: "s", action: "a", message: "m", user_visible: true });

    await vi.waitFor(() =>
      expect(console.error).toHaveBeenCalledWith(expect.stringContaining("component must be one of")),
    );
    expect(queuedReports()).toHaveLength(0);
  });

  it("a full queue counts what it loses, and the count goes out as a report of its own", async () => {
    online = false;
    for (let i = 0; i < MAX_QUEUED_REPORTS + 3; i++) {
      reportClientError({ surface: "s", action: "a", message: `m${i}`, user_visible: false });
    }
    expect(queuedReports()).toHaveLength(MAX_QUEUED_REPORTS);
    expect(lostReportCount()).toBe(3);
    expect(localStorage.getItem("devthrottle.clientErrors.lost.v1")).toBe("3");

    online = true;
    installGlobalErrorReporting("mobile");

    await vi.waitFor(() => expect(posted().length).toBeGreaterThan(0));
    expect(posted()[0]).toEqual({
      component: "mobile",
      surface: "mobile-client-errors",
      action: "report errors",
      message: "3 error reports were lost before they could be sent",
      user_visible: false,
    });
    await vi.waitFor(() => expect(lostReportCount()).toBe(0));
  });

  it("a lost-count row the Gateway refuses does not pin the queue: the count is dropped and the queue drains", async () => {
    online = false;
    for (let i = 0; i < MAX_QUEUED_REPORTS + 2; i++) {
      reportClientError({ surface: "s", action: "a", message: `m${i}`, user_visible: false });
    }
    expect(lostReportCount()).toBe(2);

    online = true;
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ error: "no account is bound to this request" }), { status: 403 }));
    installGlobalErrorReporting("mobile");

    await vi.waitFor(() => expect(lostReportCount()).toBe(0));
    expect(console.error).toHaveBeenCalledWith(expect.stringContaining("the count of 2 lost error reports was refused"));
    // The rows behind it go out in the same drain, up to the client's per-minute cap.
    await vi.waitFor(() => expect(posted().filter((b) => b.action === "a").length).toBeGreaterThan(0));
    expect(posted()[0].action).toBe("report errors");
  });

  it("the queue survives a page reload and goes out when the shell starts again", async () => {
    online = false;
    reportClientError({ surface: "s", action: "a", message: "from before the reload", user_visible: true });
    expect(queuedReports()).toHaveLength(1);

    // The reload: every in-memory piece of the module is gone; only the device's storage remains.
    resetReportingForTests();
    online = true;
    installGlobalErrorReporting("cockpit");

    await vi.waitFor(() => expect(posted()).toHaveLength(1));
    expect(posted()[0]).toMatchObject({ component: "mobile", message: "from before the reload" });
    await vi.waitFor(() => expect(queuedReports()).toHaveLength(0));
  });
});
