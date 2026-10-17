// @vitest-environment jsdom
// Issue #4078 (review of #4081, defect 4): the phone says WHY a path will not open, in the Director's own
// sentence - never "Could not load file: 400" or "409", and never the Gateway's error JSON drawn in a frame.
// Issue #4084: a folder opens as a listing (the shared FolderListing), and each entry opens as its own page.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import type { PathStatus, SessionFolderListing } from "@devthrottle/client-core/api/client";

const statuses = new Map<string, PathStatus>();
const folders = new Map<string, SessionFolderListing>();
let folderFailure: Error | null = null;
let textFailure: Error | null = null;

vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@devthrottle/client-core/api/client")>();
  return {
    ...actual,
    ensureGatewayCookie: () => undefined,
    fetchPathStatuses: async (_sid: string, paths: string[]) =>
      paths.map((p) => statuses.get(p) ?? { path: p, code: "file", sentence: "" }),
    fetchSessionFileText: async () => {
      if (textFailure) throw textFailure;
      return "the notes";
    },
    fetchSessionFileSize: async () => 12,
    fetchSessionFolder: async (_sid: string, path: string) => {
      if (folderFailure) throw folderFailure;
      const listing = folders.get(path);
      if (!listing) throw new Error(`test setup: no listing for ${path}`);
      return listing;
    },
  };
});

// The shown sentence is the real one (gatewayErrorMessage: the Director's reason wins); only the report is faked.
vi.mock("@devthrottle/client-core/errors/reportClientError", async () => {
  const { gatewayErrorMessage } = await vi.importActual<typeof import("@devthrottle/client-core/api/client")>(
    "@devthrottle/client-core/api/client",
  );
  return {
    describeAndReport: (_s: string, action: string, err: unknown) => gatewayErrorMessage(err, action),
    reportShownError: (_s: string, _a: string, message: string) => message,
  };
});

import { GatewayError } from "@devthrottle/client-core/api/client";
import { FileView } from "./FileView";

const SID = "5b8e1a40-0000-4000-8000-0000000000cc";
const NOT_ON_DISK = "This file is not on disk on the session's computer. It may not have been created or pulled yet.";
const NOT_ALLOWED =
  "This file is not allowed for this session: it is outside the session's working folder and the agent did not name it in this conversation.";
const FOLDER = "This is a folder, not a file.";

function renderFile(path: string, from?: string) {
  const fromParam = from ? `&from=${encodeURIComponent(from)}` : "";
  return render(
    <MemoryRouter initialEntries={[`/session/${SID}/file?path=${encodeURIComponent(path)}${fromParam}`]}>
      <Routes>
        <Route path="/session/:sessionId/file" element={<FileView />} />
        <Route path="/session/:sessionId" element={<div>the chat page</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  statuses.clear();
  folders.clear();
  textFailure = null;
  folderFailure = null;
});
afterEach(cleanup);

describe("mobile FileView says why a path will not open", () => {
  it.each([
    ["not-on-disk", NOT_ON_DISK, "D:\\briefs\\missing.pdf"],
    ["not-allowed", NOT_ALLOWED, "D:\\elsewhere\\private.pdf"],
  ])("a %s path shows the Director's sentence, no frame and no Download", async (code, sentence, path) => {
    statuses.set(path, { path, code, sentence });

    renderFile(path);

    expect(await screen.findByText(sentence)).toBeTruthy();
    expect(document.querySelector("iframe")).toBeNull();
    expect(screen.queryByRole("button", { name: "Download" })).toBeNull();
    expect(document.body.textContent).not.toContain("Could not load file");
  });

  it("a refused read shows the server's sentence, not a status number", async () => {
    const path = "D:\\briefs\\notes.md";
    textFailure = new GatewayError(400, NOT_ALLOWED, { reason: NOT_ALLOWED });

    renderFile(path);

    expect(await screen.findByText(NOT_ALLOWED)).toBeTruthy();
    expect(document.body.textContent).not.toContain("Could not load file: 400");
  });

  it("an allowed file renders", async () => {
    renderFile("D:\\briefs\\notes.md");

    expect(await screen.findByText("the notes")).toBeTruthy();
  });
});

describe("mobile FileView opens a folder as a listing (issue #4084)", () => {
  const folder = "D:\\ReposMindzie\\mindzieWeb\\hosting";
  const readme = `${folder}\\README.md`;

  function aFolderWithAReadme(parentPath: string | null = null) {
    statuses.set(folder, { path: folder, code: "folder", sentence: FOLDER });
    folders.set(folder, {
      currentPath: folder,
      parentPath,
      entries: [
        { name: "config", path: `${folder}\\config`, isFile: false },
        { name: "README.md", path: readme, isFile: true },
      ],
    });
  }

  it("lists the folder's entries, with no Download and not the bare folder sentence", async () => {
    aFolderWithAReadme();

    renderFile(folder);

    expect(await screen.findByText("README.md")).toBeTruthy();
    expect(screen.getByText("config/")).toBeTruthy();
    expect(screen.getByText("This is a folder. Choose a file to open it.")).toBeTruthy();
    expect(screen.queryByText(FOLDER)).toBeNull();
    expect(screen.queryByRole("button", { name: "Download" })).toBeNull();
    expect(screen.queryByText(".. (up one folder)")).toBeNull();
  });

  it("opens a file from the listing as its own page, and Back returns to the folder, then to the chat", async () => {
    aFolderWithAReadme();
    renderFile(folder, `/session/${SID}`);

    fireEvent.click(await screen.findByText("README.md"));

    expect(await screen.findByText("the notes")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "README.md" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Back" }));
    expect(await screen.findByText("README.md")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "hosting" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Back" }));
    expect(await screen.findByText("the chat page")).toBeTruthy();
  });

  it("offers the folder above only when the Director allows going up", async () => {
    aFolderWithAReadme("D:\\ReposMindzie\\mindzieWeb");

    renderFile(folder);

    expect(await screen.findByText(".. (up one folder)")).toBeTruthy();
  });

  it("a listing the Director refuses shows the Director's sentence", async () => {
    statuses.set(folder, { path: folder, code: "folder", sentence: FOLDER });
    const refusal = "This folder is not allowed for this session: it is outside the session's working folder and the agent did not name it in this conversation.";
    folderFailure = new GatewayError(400, refusal, { reason: refusal });

    renderFile(folder);

    expect(await screen.findByText(refusal)).toBeTruthy();
    expect(document.body.textContent).not.toContain("400");
  });
});
