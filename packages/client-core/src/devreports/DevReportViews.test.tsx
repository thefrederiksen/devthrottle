// The report list and the conversation render the Gateway's words verbatim. Every status, label and reply
// below is deliberately odd, so a label the client wrote for itself would fail the test.

import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DevReportSnapshot } from "./controller";
import { DevReportConversation } from "./DevReportConversation";
import { DevReportList } from "./DevReportList";
import { DevReportViewer } from "./DevReportViewer";
import type { DevReportDetail, DevReportRecordedItem, DevReportSummary } from "./devReportsClient";
import { emptyPageState } from "./protocol";

const listDevReports = vi.fn<(sessionId: string, signal?: AbortSignal) => Promise<DevReportSummary[]>>();
vi.mock("./devReportsClient", () => ({
  listDevReports: (sessionId: string, signal?: AbortSignal) => listDevReports(sessionId, signal),
  getDevReport: vi.fn(),
  getDevReportHtml: vi.fn(),
  sendDevReportItems: vi.fn(),
}));

// The viewer's job here is what it PUTS ON THE SCREEN from the Gateway's record. Loading a report - the
// iframe, the port handshake, the polling - is the controller's and is tested in controller.test.ts, so the
// controller is a scripted snapshot and nothing here touches a frame.
let snapshot: DevReportSnapshot = { ...emptySnapshot() };

function emptySnapshot(): DevReportSnapshot {
  return {
    detail: null,
    notFound: false,
    loadError: null,
    loadedVersion: null,
    pageState: emptyPageState(),
    connected: false,
    sending: false,
    sendError: null,
  };
}

vi.mock("./controller", () => ({
  DevReportController: class {
    subscribe = () => () => {};
    getSnapshot = () => snapshot;
    refresh = async () => {};
    sendQueued = async () => {};
    dispose = () => {};
  },
}));

afterEach(() => {
  cleanup();
  listDevReports.mockReset();
  snapshot = emptySnapshot();
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

// THE WAY BACK, NAMED FOR A HUMAN (dev reports mission, phase 3b, proofs 1, 2 and 3).
//
// The Gateway composes `sessionLabel` and `backLabel`; the viewer renders them WHOLE and writes neither.
// The two strings below are deliberately unusual and are NOT one composed from the other - a viewer that
// built its own "back to ..." out of the session label would fail on the first assertion, not pass by
// accident. The identifiers are long and distinctive so proof 3 can search the whole rendered screen for
// them.
const VIEWER_SESSION_ID = "3f5b91ac-7710-4d2e-9d7c-viewer-session";
const VIEWER_REPORT_ID = "9c2d40fe-1188-4a63-b0e1-viewer-report";

const GATEWAY_SESSION_LABEL = "121 devthrottle ~ tool not working on linux #odd";
const GATEWAY_BACK_LABEL = "<< return to session 121 (Gateway words, odd #4)";

function detailWith(over: Partial<DevReportSummary>): DevReportDetail {
  return {
    report: {
      ...summary,
      id: VIEWER_REPORT_ID,
      sessionId: VIEWER_SESSION_ID,
      key: "C:/work/report.html",
      title: "Queue audit ~ odd title 17",
      ...over,
    },
    items: [],
    replies: [],
  };
}

function renderViewer(detail: DevReportDetail, onBackToSession?: (sessionId: string) => void) {
  snapshot = { ...emptySnapshot(), detail, loadedVersion: detail.report.version, connected: true };
  return render(
    <DevReportViewer reportId={VIEWER_REPORT_ID} onBackToSession={onBackToSession} renderConversation={() => null} />,
  );
}

describe("DevReportViewer - the session it came from, and the way back", () => {
  it("shows the Gateway's backLabel and sessionLabel verbatim, and goes back to that session", () => {
    const onBackToSession = vi.fn();
    renderViewer(detailWith({ sessionLabel: GATEWAY_SESSION_LABEL, backLabel: GATEWAY_BACK_LABEL }), onBackToSession);

    const back = screen.getByTestId("dev-report-back");
    expect(back.textContent).toBe(GATEWAY_BACK_LABEL);
    expect(screen.getByTestId("dev-report-session-label").textContent).toBe(GATEWAY_SESSION_LABEL);

    fireEvent.click(back);
    expect(onBackToSession).toHaveBeenCalledWith(VIEWER_SESSION_ID);
  });

  it("shows NO back link and no session identifier when the Gateway sent no words for one", () => {
    const { container } = renderViewer(detailWith({}), vi.fn());

    expect(screen.queryByTestId("dev-report-back")).toBeNull();
    expect(screen.queryByTestId("dev-report-session-label")).toBeNull();
    expect(container.textContent ?? "").not.toContain(VIEWER_SESSION_ID);
  });

  it("puts no session id and no report id in anything the owner can see", () => {
    const { container } = renderViewer(
      detailWith({ sessionLabel: GATEWAY_SESSION_LABEL, backLabel: GATEWAY_BACK_LABEL }),
      vi.fn(),
    );

    // Every visible word on the screen, plus every title and aria-label a reader is shown.
    const shown = [
      container.textContent ?? "",
      ...Array.from(container.querySelectorAll("[title]")).map((el) => el.getAttribute("title") ?? ""),
      ...Array.from(container.querySelectorAll("[aria-label]")).map((el) => el.getAttribute("aria-label") ?? ""),
    ].join(" ");

    expect(shown).not.toContain(VIEWER_SESSION_ID);
    expect(shown).not.toContain(VIEWER_REPORT_ID);
    // The words that ARE on screen are the Gateway's, so the test is not passing on an empty screen.
    expect(shown).toContain(GATEWAY_BACK_LABEL);
    expect(shown).toContain(GATEWAY_SESSION_LABEL);
  });
});

// ONE SCROLL REGION FOR THE REPORT (dev reports mission, phase 3b, the owner counted three).
//
// What a browser actually paints is not decidable in jsdom - it has no layout - so this proves the RULE is
// declared, not that a scrollbar is absent on screen: every box the shared viewer wraps round the report
// frame clips rather than scrolls, so only the report's own page can scroll. The count itself is checked in
// a real browser at both widths; see WORKER-phase-3b-apps.md.
describe("the report frame's boxes", () => {
  const clipped = ["dev-report-viewer", "dev-report-viewer-main", "dev-report-frame-box"];

  it("clips every box around the report, so the frame is the only thing that scrolls", async () => {
    const { readFileSync } = await import("node:fs");
    const { fileURLToPath } = await import("node:url");
    const { dirname, join } = await import("node:path");
    const here = dirname(fileURLToPath(import.meta.url));
    const css = readFileSync(join(here, "devReports.css"), "utf8");

    for (const name of clipped) {
      const opened = css.indexOf(`.${name} {`);
      expect(opened, `.${name} is not in devReports.css`).toBeGreaterThanOrEqual(0);
      const block = css.slice(opened, css.indexOf("}", opened));
      expect(block, `.${name} must clip, not scroll`).toContain("overflow: hidden");
      expect(block, `.${name} must be allowed to shrink below its content`).toContain("min-height: 0");
    }
  });
});
