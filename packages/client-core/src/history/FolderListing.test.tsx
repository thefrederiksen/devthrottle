// @vitest-environment jsdom
// Issue #4084: the ONE folder listing both shells mount (the Cockpit's file viewer and the phone's file page).
// It shows exactly what the Director listed, in the Director's order, and the Director's sentence on a refusal.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { SessionFolderListing } from "../api/client";

let listing: SessionFolderListing | null = null;
let failure: Error | null = null;

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    fetchSessionFolder: async () => {
      if (failure) throw failure;
      if (!listing) throw new Error("test setup: no listing");
      return listing;
    },
  };
});

vi.mock("../errors/reportClientError", async () => {
  const { gatewayErrorMessage } = await vi.importActual<typeof import("../api/client")>("../api/client");
  return { describeAndReport: (_s: string, action: string, err: unknown) => gatewayErrorMessage(err, action) };
});

import { GatewayError } from "../api/client";
import { FolderListing } from "./FolderListing";

const SID = "5b8e1a40-0000-4000-8000-0000000000dd";

beforeEach(() => {
  listing = null;
  failure = null;
});
afterEach(cleanup);

describe("FolderListing", () => {
  it("shows the Director's entries in its order, folders marked, and opens the one chosen", async () => {
    listing = {
      currentPath: "/home/soren/briefs",
      parentPath: "/home/soren",
      entries: [
        { name: "drafts", path: "/home/soren/briefs/drafts", isFile: false },
        { name: "q3.pdf", path: "/home/soren/briefs/q3.pdf", isFile: true },
      ],
    };
    const onOpen = vi.fn();

    render(<FolderListing sessionId={SID} path="/home/soren/briefs" onOpen={onOpen} surface="test" />);

    const rows = (await screen.findAllByRole("button")).map((b) => b.textContent);
    expect(rows).toEqual([".. (up one folder)", "drafts/", "q3.pdf"]);
    fireEvent.click(screen.getByText("q3.pdf"));
    expect(onOpen).toHaveBeenCalledWith("/home/soren/briefs/q3.pdf");
    fireEvent.click(screen.getByText(".. (up one folder)"));
    expect(onOpen).toHaveBeenLastCalledWith("/home/soren");
  });

  it("says so when the folder has nothing to show", async () => {
    listing = { currentPath: "/home/soren/empty", parentPath: null, entries: [] };

    render(<FolderListing sessionId={SID} path="/home/soren/empty" onOpen={() => undefined} surface="test" />);

    expect(await screen.findByText("This folder has no files to show.")).toBeTruthy();
    expect(screen.queryByText(".. (up one folder)")).toBeNull();
  });

  it("shows the Director's sentence when it refuses the folder", async () => {
    const refusal = "This folder is not allowed for this session: it is outside the session's working folder and the agent did not name it in this conversation.";
    failure = new GatewayError(400, refusal, { reason: refusal });

    render(<FolderListing sessionId={SID} path="/elsewhere" onOpen={() => undefined} surface="test" />);

    expect(await screen.findByText(refusal)).toBeTruthy();
  });
});
