// The report list and the conversation render the Gateway's words verbatim. Every status, label and reply
// below is deliberately odd, so a label the client wrote for itself would fail the test.

import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DevReportConversation } from "./DevReportConversation";
import { DevReportList } from "./DevReportList";
import type { DevReportRecordedItem, DevReportSummary } from "./devReportsClient";

const listDevReports = vi.fn<(sessionId: string, signal?: AbortSignal) => Promise<DevReportSummary[]>>();
vi.mock("./devReportsClient", () => ({
  listDevReports: (sessionId: string, signal?: AbortSignal) => listDevReports(sessionId, signal),
}));

afterEach(() => {
  cleanup();
  listDevReports.mockReset();
});

const summary: DevReportSummary = {
  id: "r-1",
  sessionId: "s-1",
  key: "C:/work/report.html",
  title: "Queue audit ~ odd title 17",
  status: "waiting-on-you#odd",
  version: 7,
  publishedAtUtc: "2026-09-17T09:00:00Z",
  updatedAtUtc: "2026-09-17T10:30:00Z",
  sessionEnded: true,
  openItems: 3,
};

describe("DevReportList", () => {
  it("renders every report with the Gateway's title, status, version and open items as sent", async () => {
    listDevReports.mockResolvedValue([summary]);
    const onOpen = vi.fn();
    render(<DevReportList sessionId="s-1" onOpen={onOpen} />);
    const row = await screen.findByTestId("dev-report-row");
    expect(listDevReports).toHaveBeenCalledWith("s-1", expect.anything());
    expect(within(row).getByTestId("dev-report-row-title").textContent).toBe("Queue audit ~ odd title 17");
    expect(within(row).getByTestId("dev-report-row-status").textContent).toBe("waiting-on-you#odd");
    expect(within(row).getByTestId("dev-report-row-version").textContent).toBe("Version 7");
    expect(within(row).getByTestId("dev-report-row-open-items").textContent).toBe("3 open");
    expect(within(row).getByTestId("dev-report-row-updated").getAttribute("datetime")).toBe("2026-09-17T10:30:00Z");
    fireEvent.click(row);
    expect(onOpen).toHaveBeenCalledWith(summary);
  });

  it("says so when the session has no reports", async () => {
    listDevReports.mockResolvedValue([]);
    render(<DevReportList sessionId="s-1" onOpen={() => {}} />);
    expect(await screen.findByTestId("dev-report-list-empty")).toBeTruthy();
  });

  it("shows the Gateway's sentence when the list cannot be read", async () => {
    const { GatewayError } = await import("../api/client");
    listDevReports.mockRejectedValue(new GatewayError(403, "x", { reason: "No account is bound - odd 9." }));
    render(<DevReportList sessionId="s-1" onOpen={() => {}} />);
    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("No account is bound - odd 9."));
  });
});

describe("DevReportConversation", () => {
  const sent: DevReportRecordedItem[] = [
    {
      id: "n1",
      kind: "note",
      text: "Wrong number",
      anchor: { type: "table-cell", selector: "#t td", quote: "42", rowLabel: "Gateway", columnLabel: "Failures" },
      status: "zz-delivered",
      statusLabel: "Delivered (Gateway words, odd #5)",
      sentAtUtc: "2026-09-17T10:00:00Z",
      deliveredAtUtc: null,
    },
  ];

  it("renders queued items, sent items with the Gateway's labels, and replies, verbatim", () => {
    render(
      <DevReportConversation
        conversation={{
          queued: [
            {
              id: "a1",
              kind: "answer",
              questionId: "q",
              question: "When?",
              optionValue: "t",
              optionLabel: "Tonight",
              comment: "",
              statusLabel: "This session has ended - refusal words odd 3",
            },
          ],
          sent,
          replies: [{ id: "r1", text: "Fixed it - reply words odd 8", at: "2026-09-17T11:00:00Z" }],
          sending: false,
          sendError: null,
          send: () => {},
        }}
      />,
    );
    const queued = screen.getByTestId("dev-report-queued-item");
    expect(within(queued).getByTestId("dev-report-item-status").textContent).toBe("This session has ended - refusal words odd 3");
    const sentRow = screen.getByTestId("dev-report-sent-item");
    expect(within(sentRow).getByTestId("dev-report-item-status").textContent).toBe("Delivered (Gateway words, odd #5)");
    expect(sentRow.textContent).toContain("Wrong number");
    expect(sentRow.textContent).toContain("Gateway, Failures");
    expect(screen.getByTestId("dev-report-reply").textContent).toContain("Fixed it - reply words odd 8");
  });

  it("offers Send for the queue and shows the sending state and a failed request's words", () => {
    const send = vi.fn();
    const { rerender } = render(
      <DevReportConversation conversation={{ queued: [sent[0]], sent: [], replies: [], sending: false, sendError: null, send }} />,
    );
    const button = screen.getByTestId("dev-report-send") as HTMLButtonElement;
    expect(button.disabled).toBe(false);
    fireEvent.click(button);
    expect(send).toHaveBeenCalledTimes(1);

    rerender(
      <DevReportConversation
        conversation={{ queued: [sent[0]], sent: [], replies: [], sending: true, sendError: "Not sent: odd failure words", send }}
      />,
    );
    expect((screen.getByTestId("dev-report-send") as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByTestId("dev-report-send").getAttribute("data-sending")).toBe("true");
    expect(screen.getByTestId("dev-report-send-error").textContent).toBe("Not sent: odd failure words");
  });

  it("disables Send when nothing is queued", () => {
    render(<DevReportConversation conversation={{ queued: [], sent: [], replies: [], sending: false, sendError: null, send: () => {} }} />);
    expect((screen.getByTestId("dev-report-send") as HTMLButtonElement).disabled).toBe(true);
  });
});
