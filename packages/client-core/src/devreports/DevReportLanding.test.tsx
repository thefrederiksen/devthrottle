// THE LANDING THAT NEEDS ONLY A REPORT ID (dev reports mission, phase 3b, issue #3025).
//
// The printed address `<gateway>/r/<report id>` carries a report id and nothing else, because the Gateway
// route that serves it has to answer with nobody signed in and therefore cannot look the report up. So the
// session has to be learned HERE, from the record, behind the app's own sign-in - and the shell is told
// where to go. A report that is not in this account is SAID, never guessed at.

import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DevReportLanding } from "./DevReportLanding";
import type { DevReportDetail } from "./devReportsClient";

const getDevReport = vi.fn<(reportId: string, signal?: AbortSignal) => Promise<DevReportDetail | null>>();
vi.mock("./devReportsClient", () => ({
  getDevReport: (reportId: string, signal?: AbortSignal) => getDevReport(reportId, signal),
  listDevReports: vi.fn(),
  getDevReportHtml: vi.fn(),
  sendDevReportItems: vi.fn(),
}));

const REPORT = "b41e77a2-0000-4000-8000-0000000000aa";
const SESSION = "7d2f9c10-0000-4000-8000-000000000031";

function detail(): DevReportDetail {
  return {
    report: {
      id: REPORT,
      sessionId: SESSION,
      key: "C:/work/report.html",
      title: "Queue audit",
      status: "waiting-on-you",
      version: 3,
      publishedAtUtc: "2026-09-17T10:00:00Z",
      updatedAtUtc: "2026-09-17T10:05:00Z",
      sessionEnded: false,
      openItems: 1,
    },
    items: [],
    replies: [],
  };
}

afterEach(() => {
  cleanup();
  getDevReport.mockReset();
});

describe("the report landing", () => {
  it("reads the report and hands the shell the session off the record", async () => {
    getDevReport.mockResolvedValue(detail());
    const onFound = vi.fn();

    render(<DevReportLanding reportId={REPORT} onFound={onFound} />);

    // It says something the whole time it is working - this is on screen on the way into every report.
    expect(screen.getByTestId("dev-report-landing").getAttribute("data-state")).toBe("opening");

    await waitFor(() => expect(onFound).toHaveBeenCalledTimes(1));
    // THE SESSION COMES FROM THE RECORD, never from the address: the address carries no session at all.
    expect(onFound).toHaveBeenCalledWith(SESSION, REPORT);
  });

  it("says a report that is not in this account does not appear, and goes nowhere", async () => {
    // The Gateway answers 404 for a report that is not in this account - the same answer whether it belongs
    // to somebody else or to nobody - and the client turns that into null.
    getDevReport.mockResolvedValue(null);
    const onFound = vi.fn();

    render(<DevReportLanding reportId={REPORT} onFound={onFound} />);

    await waitFor(() => expect(screen.getByTestId("dev-report-landing-not-found")).toBeTruthy());
    expect(screen.getByTestId("dev-report-landing-not-found").textContent).toBe("This report does not appear.");
    expect(onFound).not.toHaveBeenCalled();
  });

  it("shows the real failure when the read fails, and goes nowhere", async () => {
    getDevReport.mockRejectedValue(new Error("Could not load the report (503)"));
    const onFound = vi.fn();

    render(<DevReportLanding reportId={REPORT} onFound={onFound} />);

    await waitFor(() => expect(screen.getByTestId("dev-report-landing-error")).toBeTruthy());
    // The failure is reported as it happened - never swallowed into "does not appear", which would say
    // something untrue about the report.
    expect(screen.getByTestId("dev-report-landing-error").textContent).toContain("Could not load the report (503)");
    expect(onFound).not.toHaveBeenCalled();
  });
});
