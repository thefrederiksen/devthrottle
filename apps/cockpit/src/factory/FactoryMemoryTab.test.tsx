// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

// The factory page's Memory tab (Factory Memory mission, phase 3b). What is held down:
//   * the tab is the Gateway's - it appears because the fold offers it, and /factories/<id>/memory opens it;
//   * every call names THIS factory;
//   * a correction sends the version it was made against, and a stale one shows what is there now beside the
//     owner's own text, keeps his text, and saves over the newer version only when he says so;
//   * delete sends the version read and leaves the note open as a delete;
//   * history shows who wrote each version and when, and restore is offered on an older version - never on the
//     newest, never on a delete - and asks for exactly the version picked;
//   * a deleted note, which the listing hides, can still be reached by name.
// Only the Gateway calls are replaced; the refusal class is the real one, so the tab's stale check meets the same
// object the client throws.

const client = vi.hoisted(() => ({
  listFactoryMemory: vi.fn(),
  getFactoryMemoryNote: vi.fn(),
  getFactoryMemoryHistory: vi.fn(),
  setFactoryMemoryNote: vi.fn(),
  deleteFactoryMemoryNote: vi.fn(),
  restoreFactoryMemoryNote: vi.fn(),
}));

vi.mock("@devthrottle/client-core/factory/factoryMemoryClient", async (importOriginal) => {
  const real = await importOriginal<typeof import("@devthrottle/client-core/factory/factoryMemoryClient")>();
  return { ...real, ...client };
});

const pageClient = vi.hoisted(() => ({ getFactoryPage: vi.fn(), getFactorySeats: vi.fn(), startFactoryTalk: vi.fn() }));
vi.mock("@devthrottle/client-core/factory/factoriesScreenClient", () => pageClient);

import { FactoryMemoryRefusal } from "@devthrottle/client-core/factory/factoryMemoryClient";
import { FactoryView } from "./FactoryView";
import { FACTORY_PAGE } from "./fixtures";

const PAGE = { ...FACTORY_PAGE, id: "website-business", title: "Website Business" };
const PAGE_WITHOUT_MEMORY = { ...PAGE, tabs: PAGE.tabs.filter((t) => t.key !== "memory") };

const SESSION = "4baef30d-281d-44c9-9a86-654cef33ecfa";

function note(over: Partial<Record<string, unknown>> = {}) {
  return {
    factory: "website-business",
    name: "domains",
    version: 3,
    text: "Check the registrar before buying.",
    deleted: false,
    authorKind: "session",
    authorId: SESSION,
    writtenAtUtc: "2026-09-28T12:00:00Z",
    ...over,
  };
}

const LIST = {
  factory: "website-business",
  notes: [note(), note({ name: "deliverability", version: 1, text: "Every email failed DMARC." })],
  bytes: 2048,
  maxBytes: 524288,
  maxNotes: 100,
};

const HISTORY = {
  factory: "website-business",
  name: "domains",
  versions: [
    note(),
    note({ version: 2, text: null, deleted: true, authorId: "7e0c1d2a-0000-0000-0000-000000000000" }),
    note({ version: 1, text: "The first thing learned.", authorKind: "person", authorId: "OWNER-LAPTOP" }),
  ],
};

function renderMemory(path = "/factories/website-business/memory") {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/factories/:factory/:tab" element={<FactoryView />} />
      </Routes>
    </MemoryRouter>,
  );
}

async function openDomains() {
  renderMemory();
  fireEvent.click(await screen.findByRole("button", { name: "domains" }));
  await screen.findByText("Check the registrar before buying.");
}

describe("The factory page's Memory tab (Factory Memory mission, phase 3b)", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    pageClient.getFactoryPage.mockResolvedValue(PAGE);
    client.listFactoryMemory.mockResolvedValue(LIST);
    client.getFactoryMemoryNote.mockResolvedValue(note());
    client.getFactoryMemoryHistory.mockResolvedValue(HISTORY);
  });

  it("is a tab because the Gateway offers it, and its address opens it on this factory's notes", async () => {
    renderMemory();

    const list = await screen.findByTestId("fa-memory-list");
    expect(screen.getByRole("link", { name: "Memory" }).getAttribute("aria-current")).toBe("page");
    expect(client.listFactoryMemory).toHaveBeenCalledWith("website-business", expect.anything());
    expect(within(list).getByRole("button", { name: "domains" })).toBeTruthy();
    expect(within(list).getByRole("button", { name: "deliverability" })).toBeTruthy();
    expect(within(list).getAllByText(`session ${SESSION}`)).toHaveLength(2);
    expect(screen.getByTestId("fa-memory-summary").textContent).toBe("2 of 100 notes . 2.0 KB of 512.0 KB");
  });

  it("opens on the Overview when the Gateway did not offer the tab asked for", async () => {
    pageClient.getFactoryPage.mockResolvedValue(PAGE_WITHOUT_MEMORY);
    renderMemory();

    await screen.findByTestId("fa-overview");
    expect(screen.queryByRole("link", { name: "Memory" })).toBeNull();
    expect(client.listFactoryMemory).not.toHaveBeenCalled();
  });

  it("reads a note and says which version it is and who wrote it", async () => {
    await openDomains();

    expect(client.getFactoryMemoryNote).toHaveBeenCalledWith("website-business", "domains");
    expect(screen.getByTestId("fa-memory-byline").textContent).toContain(`Version 3, written by session ${SESSION}`);
  });

  it("saves a correction against the version that was read", async () => {
    client.setFactoryMemoryNote.mockResolvedValue(note({ version: 4, text: "Corrected.", authorKind: "person", authorId: "OWNER" }));
    await openDomains();

    fireEvent.click(screen.getByRole("button", { name: "Correct" }));
    fireEvent.change(screen.getByLabelText("Text of domains"), { target: { value: "Corrected." } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await screen.findByText("Corrected.");
    expect(client.setFactoryMemoryNote).toHaveBeenCalledWith("website-business", "domains", "Corrected.", 3);
    expect(screen.getByTestId("fa-memory-byline").textContent).toContain("Version 4, written by person OWNER");
    // The list is read again, so it shows the new version too.
    expect(client.listFactoryMemory).toHaveBeenCalledTimes(2);
  });

  it("does NOT overwrite a newer version: it shows what is there now, keeps his text, and saves over it only when told", async () => {
    const newer = note({ version: 5, text: "A factory session learned this meanwhile." });
    client.setFactoryMemoryNote
      .mockRejectedValueOnce(new FactoryMemoryRefusal(409, "Save failed: the note changed since you read it.", "Stale", newer))
      .mockResolvedValueOnce(note({ version: 6, text: "Merged.", authorKind: "person", authorId: "OWNER" }));
    await openDomains();

    fireEvent.click(screen.getByRole("button", { name: "Correct" }));
    fireEvent.change(screen.getByLabelText("Text of domains"), { target: { value: "My correction." } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    const conflict = await screen.findByTestId("fa-memory-conflict");
    expect(conflict.textContent).toContain("changed since you opened it: version 5");
    expect(within(conflict).getByText("A factory session learned this meanwhile.")).toBeTruthy();
    expect((screen.getByLabelText("Text of domains") as HTMLTextAreaElement).value).toBe("My correction.");
    expect(client.setFactoryMemoryNote).toHaveBeenCalledTimes(1);

    fireEvent.change(screen.getByLabelText("Text of domains"), { target: { value: "Merged." } });
    fireEvent.click(within(conflict).getByRole("button", { name: "Save over version 5" }));

    await screen.findByText("Merged.");
    expect(client.setFactoryMemoryNote).toHaveBeenLastCalledWith("website-business", "domains", "Merged.", 5);
  });

  it("can take the newer version and drop the edit instead", async () => {
    const newer = note({ version: 5, text: "Theirs." });
    client.setFactoryMemoryNote.mockRejectedValueOnce(new FactoryMemoryRefusal(409, "stale", "Stale", newer));
    await openDomains();

    fireEvent.click(screen.getByRole("button", { name: "Correct" }));
    fireEvent.change(screen.getByLabelText("Text of domains"), { target: { value: "Mine." } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    fireEvent.click(await screen.findByRole("button", { name: "Take version 5 and drop my edit" }));

    expect((screen.getByLabelText("Text of domains") as HTMLTextAreaElement).value).toBe("Theirs.");
    expect(screen.queryByTestId("fa-memory-conflict")).toBeNull();
  });

  it("shows any other refusal as the Gateway's sentence, and keeps the edit open", async () => {
    client.setFactoryMemoryNote.mockRejectedValueOnce(
      new FactoryMemoryRefusal(409, "Save failed: a note holds at most 16 KB.", "TooLarge", null),
    );
    await openDomains();

    fireEvent.click(screen.getByRole("button", { name: "Correct" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect((await screen.findByRole("alert")).textContent).toContain("a note holds at most 16 KB");
    expect(screen.queryByTestId("fa-memory-conflict")).toBeNull();
    expect(screen.getByLabelText("Text of domains")).toBeTruthy();
  });

  it("deletes against the version read, after asking, and leaves the note open as a delete", async () => {
    client.deleteFactoryMemoryNote.mockResolvedValue(
      note({ version: 4, text: null, deleted: true, authorKind: "person", authorId: "OWNER" }),
    );
    await openDomains();

    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    expect(client.deleteFactoryMemoryNote).not.toHaveBeenCalled();
    const dialog = screen.getByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(screen.getByTestId("fa-memory-byline").textContent).toContain("Deleted in version 4 by person OWNER"));
    expect(client.deleteFactoryMemoryNote).toHaveBeenCalledWith("website-business", "domains", 3);
    expect(screen.queryByRole("button", { name: "Correct" })).toBeNull();
  });

  it("shows each version with who wrote it, and offers Restore only on an older version with text", async () => {
    await openDomains();

    fireEvent.click(screen.getByRole("button", { name: "History" }));

    await screen.findByTestId("fa-memory-history");
    expect(client.getFactoryMemoryHistory).toHaveBeenCalledWith("website-business", "domains");
    const v3 = screen.getByTestId("fa-memory-version-3");
    const v2 = screen.getByTestId("fa-memory-version-2");
    const v1 = screen.getByTestId("fa-memory-version-1");
    expect(v3.textContent).toContain(`Written by session ${SESSION}`);
    expect(v2.textContent).toContain("Deleted by session 7e0c1d2a");
    expect(v1.textContent).toContain("Written by person OWNER-LAPTOP");
    expect(within(v3).queryByRole("button", { name: "Restore" })).toBeNull();
    expect(within(v2).queryByRole("button", { name: "Restore" })).toBeNull();
    expect(within(v1).getByRole("button", { name: "Restore" })).toBeTruthy();
  });

  it("restores exactly the version picked, after asking, and the note becomes that text as a new version by the person", async () => {
    client.restoreFactoryMemoryNote.mockResolvedValue(
      note({ version: 4, text: "The first thing learned.", authorKind: "person", authorId: "OWNER" }),
    );
    await openDomains();
    fireEvent.click(screen.getByRole("button", { name: "History" }));
    const v1 = await screen.findByTestId("fa-memory-version-1");

    fireEvent.click(within(v1).getByRole("button", { name: "Restore" }));
    expect(client.restoreFactoryMemoryNote).not.toHaveBeenCalled();
    fireEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Restore" }));

    // The fourth argument is the version the person was looking at: without it the Gateway cannot refuse a
    // restore that would bury a version written while the history was open (review finding 4).
    await waitFor(() =>
      expect(client.restoreFactoryMemoryNote).toHaveBeenCalledWith("website-business", "domains", 1, 3));
    await waitFor(() => expect(screen.getByTestId("fa-memory-byline").textContent).toContain("Version 4, written by person OWNER"));
    expect(client.getFactoryMemoryHistory).toHaveBeenCalledTimes(2);
  });

  it("reaches a deleted note by name, since the listing hides it, and offers its history", async () => {
    client.getFactoryMemoryNote.mockResolvedValue(note({ name: "old-trick", version: 2, text: null, deleted: true }));
    renderMemory();
    await screen.findByTestId("fa-memory-list");

    fireEvent.change(screen.getByLabelText("Open a note by name, including a deleted one"), { target: { value: "old-trick" } });
    fireEvent.click(screen.getByRole("button", { name: "Open" }));

    await waitFor(() => expect(screen.getByTestId("fa-memory-byline").textContent).toContain("Deleted in version 2"));
    expect(client.getFactoryMemoryNote).toHaveBeenCalledWith("website-business", "old-trick");
    expect(screen.getByRole("button", { name: "History" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Delete" })).toBeNull();
  });

  it("says a factory with no notes has none, rather than showing an empty table", async () => {
    client.listFactoryMemory.mockResolvedValue({ ...LIST, notes: [], bytes: 0 });
    renderMemory();

    expect(await screen.findByText("This factory has no notes yet. Its sessions write them as they learn.")).toBeTruthy();
    expect(screen.queryByTestId("fa-memory-list")).toBeNull();
  });
});
