// @vitest-environment jsdom
// Issue #4078: the file viewer says WHY a path will not open, in the Director's own sentence, for each of the
// three cases - not on disk, not allowed for this session, and a folder - and a folder opens as a listing whose
// files open in the viewer in turn. The Download button fetches first and shows the reason when refused.
//
// The Gateway calls are mocked at the client boundary; what is asserted is what a person sees.
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import type { PathStatus, SessionFolderListing } from "@devthrottle/client-core/api/client";

const statuses = new Map<string, PathStatus>();
const folders = new Map<string, SessionFolderListing>();
const fileTexts = new Map<string, string>();
let downloadFailure: Error | null = null;
const downloaded: string[] = [];

class FakeGatewayError extends Error {
  readonly status: number;
  readonly serverReason: string;
  readonly retryable = false;
  constructor(status: number, reason: string) {
    super(reason);
    this.status = status;
    this.serverReason = reason;
  }
}

vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@devthrottle/client-core/api/client")>();
  return {
    ...actual,
    ensureGatewayCookie: () => undefined,
    fetchPathStatuses: async (_sid: string, paths: string[]) =>
      paths.map((p) => statuses.get(p) ?? { path: p, code: "file", sentence: "" }),
    fetchSessionFolder: async (_sid: string, path: string) => {
      const listing = folders.get(path);
      if (!listing) throw new FakeGatewayError(400, "This folder is not allowed for this session.");
      return listing;
    },
    fetchSessionFileText: async (_sid: string, path: string) => fileTexts.get(path) ?? "",
    fetchSessionFileSize: async () => 12,
    downloadSessionFile: async (_sid: string, path: string) => {
      if (downloadFailure) throw downloadFailure;
      downloaded.push(path);
    },
  };
});

vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  describeAndReport: (_surface: string, _action: string, err: unknown) =>
    err instanceof FakeGatewayError ? err.serverReason : String(err),
}));

import { FileViewerModal } from "./FileViewerModal";

const SID = "5b8e1a40-0000-4000-8000-0000000000bb";
const NOT_ON_DISK = "This file is not on disk on the session's computer. It may not have been created or pulled yet.";
const NOT_ALLOWED =
  "This file is not allowed for this session: it is outside the session's working folder and the agent did not name it in this conversation.";

beforeEach(() => {
  statuses.clear();
  folders.clear();
  fileTexts.clear();
  downloadFailure = null;
  downloaded.length = 0;
});
afterEach(cleanup);

describe("FileViewerModal says why a path will not open", () => {
  it("shows the not-on-disk sentence and offers no download", async () => {
    const path = "D:\\ReposMindzie\\mindzieWeb\\hosting\\README.md";
    statuses.set(path, { path, code: "not-on-disk", sentence: NOT_ON_DISK });

    render(<FileViewerModal sessionId={SID} path={path} onClose={() => {}} />);

    expect(await screen.findByText(NOT_ON_DISK)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Download" })).toBeNull();
  });

  it("shows the not-allowed sentence and offers no download", async () => {
    const path = "D:\\elsewhere\\private.txt";
    statuses.set(path, { path, code: "not-allowed", sentence: NOT_ALLOWED });

    render(<FileViewerModal sessionId={SID} path={path} onClose={() => {}} />);

    expect(await screen.findByText(NOT_ALLOWED)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Download" })).toBeNull();
  });

  it("opens a folder as a listing, says it is a folder, and opens a file from it with Back to return", async () => {
    const folder = "D:\\ReposMindzie\\mindzieWeb\\hosting";
    const readme = `${folder}\\README.md`;
    statuses.set(folder, { path: folder, code: "folder", sentence: "This is a folder, not a file." });
    folders.set(folder, {
      currentPath: folder,
      parentPath: null,
      entries: [
        { name: "README.md", path: readme, isFile: true },
        { name: "deploy.ps1", path: `${folder}\\deploy.ps1`, isFile: true },
      ],
    });
    fileTexts.set(readme, "the hosting notes");

    render(<FileViewerModal sessionId={SID} path={folder} onClose={() => {}} />);

    expect(await screen.findByText("This is a folder. Choose a file to open it.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Download" })).toBeNull();
    const list = screen.getByRole("list", { name: "Folder contents" });
    expect(list.textContent).toContain("README.md");
    expect(list.textContent).toContain("deploy.ps1");

    fireEvent.click(screen.getByRole("button", { name: "README.md" }));
    // A .md file renders as Markdown in place.
    await waitFor(() => expect(document.querySelector(".file-viewer-md")?.textContent).toContain("the hosting notes"));
    expect(screen.getByRole("button", { name: "Download" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Back" }));
    expect(await screen.findByRole("list", { name: "Folder contents" })).toBeTruthy();
  });

  it("a folder the session may not list shows the Director's sentence", async () => {
    const folder = "D:\\not-named";
    statuses.set(folder, { path: folder, code: "folder", sentence: "This is a folder, not a file." });

    render(<FileViewerModal sessionId={SID} path={folder} onClose={() => {}} />);

    expect(await screen.findByText("This folder is not allowed for this session.")).toBeTruthy();
  });

  it("Download fetches first and shows the refusal's reason instead of a bare failed download", async () => {
    const path = "D:\\briefs\\brief.bin";
    downloadFailure = new FakeGatewayError(400, NOT_ALLOWED);

    render(<FileViewerModal sessionId={SID} path={path} onClose={() => {}} />);

    const buttons = await screen.findAllByRole("button", { name: "Download" });
    fireEvent.click(buttons[0]);
    expect(await screen.findByText(NOT_ALLOWED)).toBeTruthy();
    expect(downloaded).toEqual([]);
  });

  it("Download of an allowed file saves it", async () => {
    const path = "D:\\briefs\\brief.bin";

    render(<FileViewerModal sessionId={SID} path={path} onClose={() => {}} />);

    const buttons = await screen.findAllByRole("button", { name: "Download" });
    fireEvent.click(buttons[0]);
    await waitFor(() => expect(downloaded).toEqual([path]));
  });
});
