// SAVING A REPORT AS ONE HTML FILE (issue #3074).
//
// The claim is narrow and worth holding to: what lands on the reader's disk is the bytes the Gateway served
// for the version on screen, unchanged. A "helpful" export that wrapped the report in the app's frame, or
// re-serialised the sandboxed iframe's document, would be a different document from the one the agent
// published - and the reason to export at all is to send the report to somebody who has no DevThrottle.

import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DevReportSnapshot } from "./controller";
import { DevReportViewer } from "./DevReportViewer";
import type { DevReportDetail, DevReportHtml } from "./devReportsClient";
import { reportFileName } from "./exportReport";
import { emptyPageState } from "./protocol";

const getDevReportHtml = vi.fn<(reportId: string, version: number, signal?: AbortSignal) => Promise<DevReportHtml | null>>();
vi.mock("./devReportsClient", () => ({
  listDevReports: vi.fn(),
  getDevReport: vi.fn(),
  getDevReportHtml: (reportId: string, version: number, signal?: AbortSignal) => getDevReportHtml(reportId, version, signal),
  sendDevReportItems: vi.fn(),
}));

// The frame, the port handshake and the polling are the controller's and are tested there; here it is a
// scripted snapshot, exactly as the other view tests do it.
let snapshot: DevReportSnapshot = emptySnapshot();

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

const REPORT_ID = "b41e77a2-0000-4000-8000-0000000000aa";
const PAGE = "<!doctype html><html><body><h1>Cube project save</h1></body></html>";

function detail(title: string, version: number): DevReportDetail {
  return {
    report: {
      id: REPORT_ID,
      sessionId: "7d2f9c10-0000-4000-8000-000000000031",
      key: "C:/work/report.html",
      title,
      status: "waiting-on-you",
      version,
      publishedAtUtc: "2026-09-17T09:00:00Z",
      updatedAtUtc: "2026-09-17T10:30:00Z",
      sessionEnded: false,
      openItems: 0,
    },
    items: [],
    replies: [],
  };
}

// jsdom has no object URLs and no downloads, so the two browser calls the save makes are recorded here. The
// blob is kept so the test can read back exactly what would have been written to disk.
const saved: { blobs: Blob[]; revoked: string[] } = { blobs: [], revoked: [] };

function renderViewerWith(title: string, version: number, loadedVersion: number = version) {
  snapshot = { ...emptySnapshot(), detail: detail(title, version), loadedVersion, connected: true };
  return render(<DevReportViewer reportId={REPORT_ID} renderConversation={() => null} />);
}

/** jsdom's Blob has no text(), so the bytes come back through a FileReader. */
function blobText(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error);
    reader.readAsText(blob);
  });
}

/** The anchor the save clicks, after the click has happened. */
function lastDownload(): HTMLAnchorElement | undefined {
  return clicked[clicked.length - 1];
}

const clicked: HTMLAnchorElement[] = [];

beforeEach(() => {
  saved.blobs = [];
  saved.revoked = [];
  clicked.length = 0;
  URL.createObjectURL = vi.fn((blob: Blob) => {
    saved.blobs.push(blob);
    return `blob:report-${saved.blobs.length}`;
  }) as unknown as typeof URL.createObjectURL;
  URL.revokeObjectURL = vi.fn((url: string) => {
    saved.revoked.push(url);
  }) as unknown as typeof URL.revokeObjectURL;
  // jsdom refuses to navigate on a click, and a download link's click is a navigation to it. Recording the
  // anchor instead is what lets the test read the file name the browser would have been given.
  HTMLAnchorElement.prototype.click = function click(this: HTMLAnchorElement) {
    clicked.push(this);
  };
});

afterEach(() => {
  cleanup();
  getDevReportHtml.mockReset();
  snapshot = emptySnapshot();
});

describe("the exported file's name", () => {
  it("is the report's own title, the version, and .html", () => {
    expect(reportFileName("Cube project save, load and backup", 3)).toBe("cube-project-save-load-and-backup-v3.html");
  });

  it("carries nothing a file system would refuse", () => {
    // Every character Windows refuses in a file name, plus the ones that make a name awkward to pass around.
    expect(reportFileName('Queue: audit / "phase 2" <draft> | 50% *done*?', 11)).toBe("queue-audit-phase-2-draft-50-done-v11.html");
  });

  it("names the file for what it is when the title leaves nothing behind", () => {
    expect(reportFileName("---", 1)).toBe("dev-report-v1.html");
  });

  it("never ends the name in a separator, however the title is cut short", () => {
    const name = reportFileName(`${"a".repeat(78)} the rest of a very long title indeed`, 2);
    expect(name.startsWith("a".repeat(78))).toBe(true);
    expect(name).not.toContain("--v");
    expect(name.endsWith("-v2.html")).toBe(true);
  });
});

describe("Export HTML", () => {
  it("saves the bytes the Gateway serves for the version on screen, unchanged", async () => {
    getDevReportHtml.mockResolvedValue({ html: PAGE, version: 3 });
    renderViewerWith("Cube project save", 3);

    fireEvent.click(screen.getByTestId("dev-report-export"));

    await waitFor(() => expect(lastDownload()).toBeTruthy());
    expect(getDevReportHtml.mock.calls[0]?.slice(0, 2)).toEqual([REPORT_ID, 3]);
    expect(saved.blobs).toHaveLength(1);
    expect(await blobText(saved.blobs[0])).toBe(PAGE);
    expect(saved.blobs[0].type).toBe("text/html;charset=utf-8");
    expect(lastDownload()?.download).toBe("cube-project-save-v3.html");
  });

  it("saves the version the reader is looking at, not whatever the record last said", async () => {
    // The frame holds version 4 while the Gateway record still says 3 - the republish the controller loaded
    // in place. Exporting the record's version would hand the reader a different document from the one in
    // front of him.
    getDevReportHtml.mockResolvedValue({ html: PAGE, version: 4 });
    renderViewerWith("Cube project save", 3, 4);

    fireEvent.click(screen.getByTestId("dev-report-export"));

    await waitFor(() => expect(lastDownload()).toBeTruthy());
    expect(getDevReportHtml.mock.calls[0]?.slice(0, 2)).toEqual([REPORT_ID, 4]);
    expect(lastDownload()?.download).toBe("cube-project-save-v4.html");
  });

  it("lets go of the object URL once the save has it", async () => {
    getDevReportHtml.mockResolvedValue({ html: PAGE, version: 3 });
    renderViewerWith("Cube project save", 3);

    fireEvent.click(screen.getByTestId("dev-report-export"));

    await waitFor(() => expect(lastDownload()).toBeTruthy());
    // Not in the same turn as the click - the browser is still reading the URL then - but it must happen.
    await waitFor(() => expect(saved.revoked).toEqual(["blob:report-1"]));
  });

  it("says so when the report cannot be saved, rather than doing nothing", async () => {
    getDevReportHtml.mockRejectedValue(new Error("the network went away"));
    renderViewerWith("Cube project save", 3);

    fireEvent.click(screen.getByTestId("dev-report-export"));

    // A button that reports nothing reads as broken. The failure is on the screen, and nothing was saved.
    const shown = await screen.findByTestId("dev-report-export-error");
    expect(shown.textContent).toContain("the network went away");
    expect(saved.blobs).toHaveLength(0);
    // And the button is usable again for the next try.
    expect((screen.getByTestId("dev-report-export") as HTMLButtonElement).disabled).toBe(false);
  });

  it("says so when the report is gone, rather than saving an empty file", async () => {
    getDevReportHtml.mockResolvedValue(null);
    renderViewerWith("Cube project save", 3);

    fireEvent.click(screen.getByTestId("dev-report-export"));

    const shown = await screen.findByTestId("dev-report-export-error");
    expect(shown.textContent).toContain("does not appear");
    expect(saved.blobs).toHaveLength(0);
  });

  it("is not offered before the report has loaded", () => {
    snapshot = emptySnapshot();
    render(<DevReportViewer reportId={REPORT_ID} renderConversation={() => null} />);

    expect(screen.queryByTestId("dev-report-export")).toBeNull();
  });
});
