// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

// The factory page's Records tab (Factory Definition mission, phase 6). What is held down:
//   * the tab is the Gateway's - it appears because the fold offers it, and /factories/<id>/records opens it;
//   * every word drawn is the Gateway's, including what an empty section says;
//   * Read opens the journal of exactly the seat on the card, and the factory's own journal with no seat;
//   * a document opens at its newest version, History lists every version, and Open on a row reads that version;
//   * Download fetches the address the Gateway gave.
// Only the Gateway calls are replaced.

const client = vi.hoisted(() => ({
  getFactoryRecordsTab: vi.fn(),
  getFactoryRecordsJournal: vi.fn(),
  getFactoryRecordsDocument: vi.fn(),
  getFactoryRecordsDocumentHistory: vi.fn(),
  downloadFactoryAttachment: vi.fn(),
}));

vi.mock("@devthrottle/client-core/factory/factoryRecordsClient", async (importOriginal) => {
  const real = await importOriginal<typeof import("@devthrottle/client-core/factory/factoryRecordsClient")>();
  return { ...real, ...client };
});

const pageClient = vi.hoisted(() => ({ getFactoryPage: vi.fn(), getFactorySeats: vi.fn(), getFactoryFloor: vi.fn(() => new Promise(() => {})), startFactoryTalk: vi.fn() }));
vi.mock("@devthrottle/client-core/factory/factoriesScreenClient", () => pageClient);

import { FactoryView } from "./FactoryView";
import { FACTORY_PAGE } from "./fixtures";

const PAGE = { ...FACTORY_PAGE, id: "qa-outreach", title: "QA Outreach" };
const PAGE_WITHOUT_RECORDS = { ...PAGE, tabs: PAGE.tabs.filter((t) => t.key !== "records") };

const ATTACHMENT = {
  id: "0b8f6c2e-5a1d-4f3b-9c7e-2d4a6b8c0e1f",
  name: "shots/home.png",
  seatText: "Writer (writer)",
  contentType: "image/png",
  sizeText: "3 bytes",
  whoText: "session 75000000-0000-4000-8000-000000000001",
  whenText: "today 12:00",
  downloadLabel: "Download",
  downloadHref: "/gateway/factories/qa-outreach/records/attachments/0b8f6c2e-5a1d-4f3b-9c7e-2d4a6b8c0e1f",
};

const TAB = {
  factory: "qa-outreach",
  heading: "The records of QA Outreach",
  introText: "What QA Outreach has produced. (fixture)",
  journals: { heading: "Journals", summaryText: "2 journals, 13 entries in all.", emptyText: null },
  journalGroups: [
    { seat: null, title: "The factory as a whole", summaryText: "1 entry, the newest of 2026-10-13.", openLabel: "Read" },
    { seat: "writer", title: "Writer (writer)", summaryText: "12 entries, the newest of 2026-10-14.", openLabel: "Read" },
  ],
  documents: { heading: "Working documents", summaryText: "1 document.", emptyText: null },
  documentColumns: ["Document", "Version", "Written by", "When", "Size"],
  documentRows: [
    {
      name: "outreach/ledger.json",
      versionText: "Version 2",
      whoText: "a person (device browser abc)",
      whenText: "today 12:00",
      sizeText: "11 bytes",
      openLabel: "Open",
      historyLabel: "History",
    },
  ],
  attachments: { heading: "Attachments", summaryText: "1 attachment.", emptyText: null },
  attachmentColumns: ["File", "Seat", "Type", "Size", "Kept by", "When"],
  attachmentRows: [ATTACHMENT],
};

const EMPTY_TAB = {
  ...TAB,
  journals: { heading: "Journals", summaryText: "", emptyText: "No journal entry yet. (fixture)" },
  journalGroups: [],
  documents: { heading: "Working documents", summaryText: "", emptyText: "No working document yet. (fixture)" },
  documentRows: [],
  attachments: { heading: "Attachments", summaryText: "", emptyText: "No attachment yet. (fixture)" },
  attachmentRows: [],
};

function doc(version: number, text: string) {
  return {
    name: "outreach/ledger.json",
    version,
    title: `outreach/ledger.json, version ${version}`,
    lineText: `Version ${version} line. (fixture)`,
    text,
    historyLabel: "History",
    closeLabel: "Back to the records",
  };
}

function renderRecords(path = "/factories/qa-outreach/records") {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/factories/:factory/:tab" element={<FactoryView />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("The factory page's Records tab (Factory Definition mission, phase 6)", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    pageClient.getFactoryPage.mockResolvedValue(PAGE);
    client.getFactoryRecordsTab.mockResolvedValue(TAB);
  });

  it("is a tab because the Gateway offers it, and draws the Gateway's journals, documents and attachments", async () => {
    renderRecords();

    await screen.findByText("The records of QA Outreach");
    expect(screen.getByRole("link", { name: "Records" }).getAttribute("aria-current")).toBe("page");
    expect(client.getFactoryRecordsTab).toHaveBeenCalledWith("qa-outreach", expect.anything());
    expect(screen.getByText("12 entries, the newest of 2026-10-14.")).toBeTruthy();
    expect(within(screen.getByTestId("fa-rec-document-outreach/ledger.json")).getByText("Version 2")).toBeTruthy();
    expect(within(screen.getByTestId("fa-rec-attachment-shots/home.png")).getByText("Writer (writer)")).toBeTruthy();
  });

  it("opens on the Overview when the Gateway did not offer the tab", async () => {
    pageClient.getFactoryPage.mockResolvedValue(PAGE_WITHOUT_RECORDS);
    renderRecords();

    await screen.findByTestId("fa-overview");
    expect(screen.queryByRole("link", { name: "Records" })).toBeNull();
    expect(client.getFactoryRecordsTab).not.toHaveBeenCalled();
  });

  it("says what each empty section says, in the Gateway's words", async () => {
    client.getFactoryRecordsTab.mockResolvedValue(EMPTY_TAB);
    renderRecords();

    expect(await screen.findByText("No journal entry yet. (fixture)")).toBeTruthy();
    expect(screen.getByText("No working document yet. (fixture)")).toBeTruthy();
    expect(screen.getByText("No attachment yet. (fixture)")).toBeTruthy();
  });

  it("reads the journal of exactly the seat on the card, and the factory's own with no seat", async () => {
    client.getFactoryRecordsJournal.mockResolvedValue({
      heading: "The journal of Writer (writer)",
      summaryText: "12 entries. (fixture)",
      entries: [{ id: "e1", dateText: "2026-10-14", lineText: "Written by a session.", text: "Wrote the post.", attachments: [ATTACHMENT] }],
      emptyText: null,
      closeLabel: "Back to the records",
    });
    renderRecords();

    const writer = await screen.findByTestId("fa-rec-journal-writer");
    fireEvent.click(within(writer).getByRole("button", { name: "Read" }));
    expect(await screen.findByText("Wrote the post.")).toBeTruthy();
    expect(client.getFactoryRecordsJournal).toHaveBeenCalledWith("qa-outreach", "writer", expect.anything());
    expect(within(screen.getByTestId("fa-rec-entry-e1")).getByText("shots/home.png")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Back to the records" }));
    fireEvent.click(within(await screen.findByTestId("fa-rec-journal-factory")).getByRole("button", { name: "Read" }));
    await screen.findByText("Wrote the post.");
    expect(client.getFactoryRecordsJournal).toHaveBeenLastCalledWith("qa-outreach", null, expect.anything());
  });

  it("opens a document at its newest version, lists its versions, and reads the one picked", async () => {
    client.getFactoryRecordsDocument.mockImplementation(async (_f: string, _n: string, v: number | null) =>
      v === 1 ? doc(1, "row-a") : doc(2, "row-a\nrow-b"),
    );
    client.getFactoryRecordsDocumentHistory.mockResolvedValue({
      name: "outreach/ledger.json",
      heading: "Every version of outreach/ledger.json",
      summaryText: "2 versions. (fixture)",
      columns: ["Version", "State", "Written by", "When", "Size"],
      rows: [
        { version: 2, title: "Version 2", stateWord: "Newest", stateTone: "green", whoText: "a person", whenText: "today", sizeText: "11 bytes", openLabel: "Open" },
        { version: 1, title: "Version 1", stateWord: "Earlier", stateTone: "grey", whoText: "a session", whenText: "today", sizeText: "5 bytes", openLabel: "Open" },
      ],
      nextBefore: null,
      olderLabel: null,
      newestLabel: null,
      closeLabel: "Back to the records",
    });
    renderRecords();

    const row = await screen.findByTestId("fa-rec-document-outreach/ledger.json");
    fireEvent.click(within(row).getByRole("button", { name: "Open" }));
    await screen.findByText("outreach/ledger.json, version 2");
    expect(client.getFactoryRecordsDocument).toHaveBeenCalledWith("qa-outreach", "outreach/ledger.json", null, expect.anything());

    fireEvent.click(screen.getByRole("button", { name: "History" }));
    const older = await screen.findByTestId("fa-rec-version-1");
    expect(within(older).getByText("Earlier")).toBeTruthy();
    fireEvent.click(within(older).getByRole("button", { name: "Open" }));
    await screen.findByText("outreach/ledger.json, version 1");
    expect(client.getFactoryRecordsDocument).toHaveBeenLastCalledWith("qa-outreach", "outreach/ledger.json", 1, expect.anything());
    expect(screen.getByText("row-a")).toBeTruthy();
  });

  it("downloads an attachment from the address the Gateway gave, and shows a failure in words", async () => {
    client.downloadFactoryAttachment.mockRejectedValueOnce(new Error("qa-outreach has no attachment (fixture)"));
    renderRecords();

    const row = await screen.findByTestId("fa-rec-attachment-shots/home.png");
    fireEvent.click(within(row).getByRole("button", { name: "Download" }));
    expect(await within(row).findByRole("alert")).toBeTruthy();
    expect(client.downloadFactoryAttachment).toHaveBeenCalledWith(ATTACHMENT.downloadHref);
  });
});
