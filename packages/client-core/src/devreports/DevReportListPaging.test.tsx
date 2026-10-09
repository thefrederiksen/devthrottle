// The Reports list read a page at a time (the account's Reports page timed out reading the whole history on every
// refresh). These tests drive the refresh by hand - the polling hook is replaced by one that remembers the refresh it was
// given - so a "new report arrived" or "the session changed" can be staged exactly, with no timers.

import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useEffect } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DevReportList } from "./DevReportList";
import type { DevReportListPage, DevReportSummary } from "./devReportsClient";

const listDevReports = vi.fn<(sessionId: string | undefined, after: string | null, signal?: AbortSignal) => Promise<DevReportListPage>>();
vi.mock("./devReportsClient", () => ({
  listDevReports: (sessionId: string | undefined, after: string | null, signal?: AbortSignal) => listDevReports(sessionId, after, signal),
}));

let refreshNow: () => Promise<void> = async () => {};
vi.mock("../polling/useVisiblePolling", () => ({
  useVisiblePolling: (refresh: (signal: AbortSignal) => Promise<void>) => {
    useEffect(() => {
      const controller = new AbortController();
      refreshNow = () => refresh(controller.signal);
      void refresh(controller.signal);
      return () => controller.abort();
    }, [refresh]);
  },
}));

afterEach(() => {
  cleanup();
  listDevReports.mockReset();
});

/** A report updated `minute` minutes past ten, in session `sid`. */
function report(id: string, minute: number, sid = "s-1"): DevReportSummary {
  return {
    id,
    sessionId: sid,
    key: `${id}.html`,
    title: `Report ${id}`,
    status: "ready",
    version: 1,
    publishedAtUtc: "2026-10-08T10:00:00Z",
    updatedAtUtc: `2026-10-08T10:${String(minute).padStart(2, "0")}:00Z`,
    sessionEnded: false,
    openItems: 0,
  };
}

const titles = () => screen.getAllByTestId("dev-report-row-title").map((t) => t.textContent);

describe("DevReportList, one page at a time", () => {
  it("keeps a report the newest page pushed out once older pages are on screen, rather than losing it", async () => {
    const A = report("A", 40), B = report("B", 30), C = report("C", 20), D = report("D", 10), X = report("X", 50);
    let newest: DevReportListPage = { reports: [A, B], next: "after-B" };
    listDevReports.mockImplementation(async (_sid, after) =>
      after === null ? newest : { reports: [C, D], next: null });
    render(<DevReportList sessionId={undefined} onOpen={() => {}} />);
    await waitFor(() => expect(titles()).toEqual(["Report A", "Report B"]));
    fireEvent.click(screen.getByTestId("dev-report-list-more"));
    await waitFor(() => expect(titles()).toEqual(["Report A", "Report B", "Report C", "Report D"]));

    // A new report arrives: the newest page is now X and A, and B has moved onto a page that will never be re-read.
    newest = { reports: [X, A], next: "after-A" };
    await act(() => refreshNow());

    expect(titles()).toEqual(["Report X", "Report A", "Report B", "Report C", "Report D"]);
  });

  it("shows only the newest page, as the Gateway sent it, while no older page has been asked for", async () => {
    let newest: DevReportListPage = { reports: [report("A", 40), report("B", 30)], next: "after-B" };
    listDevReports.mockImplementation(async () => newest);
    render(<DevReportList sessionId={undefined} onOpen={() => {}} />);
    await waitFor(() => expect(titles()).toEqual(["Report A", "Report B"]));

    newest = { reports: [report("X", 50), report("A", 40)], next: "after-A" };
    await act(() => refreshNow());

    expect(titles()).toEqual(["Report X", "Report A"]);
  });

  it("shows the later version of a report read on two pages, once", async () => {
    const oldB = report("B", 5);
    const newB = { ...report("B", 45), title: "Report B, version 2" };
    listDevReports.mockImplementation(async (_sid, after) =>
      after === null ? { reports: [newB, report("A", 40)], next: "after-A" } : { reports: [report("C", 20), oldB], next: null });
    render(<DevReportList sessionId={undefined} onOpen={() => {}} />);
    await waitFor(() => expect(titles()).toHaveLength(2));
    fireEvent.click(screen.getByTestId("dev-report-list-more"));

    await waitFor(() => expect(titles()).toEqual(["Report B, version 2", "Report A", "Report C"]));
  });

  it("starts another session's list from nothing - none of the previous session's reports, and its own marker", async () => {
    listDevReports.mockImplementation(async (sid, after) => {
      if (sid === "s-1") return after === null
        ? { reports: [report("A", 40, "s-1")], next: "s-1-after-A" }
        : { reports: [report("B", 30, "s-1")], next: null };
      return { reports: [report("Z", 45, "s-2")], next: "s-2-after-Z" };
    });
    const { rerender } = render(<DevReportList sessionId="s-1" onOpen={() => {}} />);
    await waitFor(() => expect(titles()).toEqual(["Report A"]));
    fireEvent.click(screen.getByTestId("dev-report-list-more"));
    await waitFor(() => expect(titles()).toEqual(["Report A", "Report B"]));

    rerender(<DevReportList sessionId="s-2" onOpen={() => {}} />);

    await waitFor(() => expect(titles()).toEqual(["Report Z"]));
    listDevReports.mockClear();
    fireEvent.click(screen.getByTestId("dev-report-list-more"));
    await waitFor(() => expect(listDevReports).toHaveBeenCalledWith("s-2", "s-2-after-Z", undefined));
    expect(listDevReports).not.toHaveBeenCalledWith("s-2", "s-1-after-A", undefined);
  });

  it("drops an answer for the previous session that arrives after the session changed", async () => {
    let answerS1: (page: DevReportListPage) => void = () => {};
    listDevReports.mockImplementation((sid) => sid === "s-1"
      ? new Promise<DevReportListPage>((resolve) => { answerS1 = resolve; })
      : Promise.resolve({ reports: [report("Z", 45, "s-2")], next: null }));
    const { rerender } = render(<DevReportList sessionId="s-1" onOpen={() => {}} />);
    rerender(<DevReportList sessionId="s-2" onOpen={() => {}} />);
    await waitFor(() => expect(titles()).toEqual(["Report Z"]));

    await act(async () => answerS1({ reports: [report("A", 40, "s-1")], next: null }));

    expect(titles()).toEqual(["Report Z"]);
  });
});
