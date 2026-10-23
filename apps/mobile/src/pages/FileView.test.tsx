// @vitest-environment jsdom
// Issue #4078 (review of #4081, defect 4): the phone says WHY a path will not open, in the Director's own
// sentence - never "Could not load file: 400" or "409", and never the Gateway's error JSON drawn in a frame.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import type { PathStatus } from "@devthrottle/client-core/api/client";

const statuses = new Map<string, PathStatus>();
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
  };
});

vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  describeAndReport: (_s: string, _a: string, err: unknown) => String(err),
  reportShownError: (_s: string, _a: string, message: string) => message,
}));

import { GatewayError } from "@devthrottle/client-core/api/client";
import { FileView } from "./FileView";

const SID = "5b8e1a40-0000-4000-8000-0000000000cc";
const NOT_ON_DISK = "This file is not on disk on the session's computer. It may not have been created or pulled yet.";
const NOT_ALLOWED =
  "This file is not allowed for this session: it is outside the session's working folder and the agent did not name it in this conversation.";
const FOLDER = "This is a folder, not a file.";

function renderFile(path: string) {
  return render(
    <MemoryRouter initialEntries={[`/session/${SID}/file?path=${encodeURIComponent(path)}`]}>
      <Routes>
        <Route path="/session/:sessionId/file" element={<FileView />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  statuses.clear();
  textFailure = null;
});
afterEach(cleanup);

describe("mobile FileView says why a path will not open", () => {
  it.each([
    ["not-on-disk", NOT_ON_DISK, "D:\\briefs\\missing.pdf"],
    ["not-allowed", NOT_ALLOWED, "D:\\elsewhere\\private.pdf"],
    ["folder", FOLDER, "D:\\ReposMindzie\\mindzieWeb\\hosting"],
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
