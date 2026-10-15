// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

// The factory page's Definition tab (Factory Definition mission, phase 6). What is held down:
//   * the tab is the Gateway's - it appears because the fold offers it, /factories/<id>/definition opens it, and the
//     old /documents address opens it too;
//   * a factory with no definition shows the Gateway's sentence, what an export leaves out, and no Publish;
//   * a draft shows what changes exactly as the Gateway compared it, and Publish sends back the versions it named;
//   * a refused publish leaves the Gateway's sentence and every finding, file and line, on the tab;
//   * a publish that went through shows what the Gateway applied;
//   * the history carries the Gateway's buttons, and Restore sends the version, the newest seen and the published one;
//   * an import sends the chosen file to the Gateway's address and shows its sentence.
// Only the Gateway calls are replaced; the refusal class is the real one.

const client = vi.hoisted(() => ({
  getFactoryDefinitionTab: vi.fn(),
  getFactoryDefinitionHistory: vi.fn(),
  getFactoryDefinitionVersion: vi.fn(),
  publishFactoryDefinition: vi.fn(),
  restoreFactoryDefinition: vi.fn(),
  importFactoryDefinitionZip: vi.fn(),
  downloadFactoryDefinitionZip: vi.fn(),
}));

vi.mock("@devthrottle/client-core/factory/factoryDefinitionClient", async (importOriginal) => {
  const real = await importOriginal<typeof import("@devthrottle/client-core/factory/factoryDefinitionClient")>();
  return { ...real, ...client };
});

const pageClient = vi.hoisted(() => ({ getFactoryPage: vi.fn(), getFactorySeats: vi.fn(), getFactoryFloor: vi.fn(() => new Promise(() => {})), startFactoryTalk: vi.fn() }));
vi.mock("@devthrottle/client-core/factory/factoriesScreenClient", () => pageClient);

vi.mock("@devthrottle/client-core/errors/reportClientError", async (importOriginal) => {
  const real = await importOriginal<typeof import("@devthrottle/client-core/errors/reportClientError")>();
  return { ...real, describeAndReport: (s: string, a: string, e: unknown) => real.describeAndReport(s, a, e) };
});

import { FactoryDefinitionRefusal } from "@devthrottle/client-core/factory/factoryDefinitionClient";
import { FactoryView } from "./FactoryView";
import { FACTORY_PAGE } from "./fixtures";

const PAGE = { ...FACTORY_PAGE, id: "qa-outreach", title: "QA Outreach" };

const IMPORT = {
  label: "Import a folder",
  noteText: "Choose a zip of a definition folder whose factory.yaml names qa-outreach.",
  href: "/gateway/factories/qa-outreach/definition/import",
  busyLabel: "Importing...",
};

const UNDEFINED = {
  factory: "qa-outreach",
  heading: "The definition of QA Outreach",
  stateText: "QA Outreach has no definition on the Gateway.",
  emptyText: "Today QA Outreach is defined by its registry entry, its briefs, its schedules and its triggers.",
  published: null,
  draft: null,
  changes: null,
  publish: null,
  historyLabel: null,
  export: {
    label: "Export",
    href: "/gateway/factories/qa-outreach/definition/export",
    noteText: "A zip of a folder written from what QA Outreach has today.",
    leavesOut: ["factory.yaml: The goal is held in a file."],
  },
  import: IMPORT,
  footnoteText: "Only a person publishes or restores.",
};

const PUBLISH = {
  version: 5,
  expectedVersion: 5,
  replaces: 3,
  label: "Publish version 5",
  busyLabel: "Publishing...",
  confirmTitle: "Publish version 5 of the definition of QA Outreach?",
  confirmLines: ["Versions 4 to 5 were all saved since version 3 was published. They all go live with it, not only the newest edit."],
  confirmLabel: "Publish",
  doneText: "Version 5 is published. It is the definition of QA Outreach now.",
};

const DRAFTED = {
  ...UNDEFINED,
  stateText: "Version 3 is published. 2 drafts saved after it; the newest is version 5.",
  emptyText: null,
  published: { version: 3, title: "Published: version 3", lineText: "Published by a person.", noteText: null, stateWord: "published", stateTone: "ok", openLabel: "Open" },
  draft: { version: 5, title: "Draft: version 5", lineText: "Written by a session.", noteText: "rename the writer", stateWord: "draft", stateTone: "amber", openLabel: "Open" },
  changes: {
    heading: "What changes when version 5 is published",
    cannotCompareText: null,
    changes: {
      summary: "1 seat changed.",
      nothingChanges: false,
      firstPublish: false,
      factory: [],
      seats: [
        {
          seat: "writer",
          change: "changed",
          sentence: "Seat Author (writer) changed.",
          details: ["name Writer -> Author"],
          briefDiff: {
            added: 1,
            removed: 1,
            lines: [
              { kind: "removed", marker: "-", text: "Write one post.", oldLine: 1, newLine: null },
              { kind: "added", marker: "+", text: "Write one post, and link the last one.", oldLine: null, newLine: 1 },
            ],
            linesNotShown: 0,
            note: null,
          },
        },
      ],
      starts: [],
      rules: [],
      grants: [],
      files: [],
    },
    plan: { version: 5, act: "publish", heading: "What publishing version 5 would do to the live factory now:", steps: ["Registry: seat writer renamed Writer -> Author."], refused: null, findings: [] },
  },
  publish: PUBLISH,
  historyLabel: "History",
  export: { label: "Export", href: "/gateway/factories/qa-outreach/definition/export?version=3", noteText: "A zip of the folder of version 3.", leavesOut: [] },
};

const RESTORE = { ...PUBLISH, version: 2, label: "Restore", confirmTitle: "Restore version 2 of the definition of QA Outreach?", confirmLabel: "Restore", doneText: "Version 2 was restored as version 6." };

const HISTORY = {
  heading: "History of the definition of QA Outreach",
  summaryText: "5 versions. Version 3 is published.",
  pageText: "Versions 5 to 1 of 5.",
  columns: ["Version", "State", "Written by", "When", "Note", "Size"],
  rows: [
    { version: 5, title: "Version 5", stateWord: "Draft", stateTone: "amber", whoText: "Written by a person", whenText: "today 10:00", noteText: null, sizeText: "6 files, 1.2 KB", openLabel: "Open", exportLabel: "Export this version", exportHref: "/x?version=5", publish: PUBLISH, restore: null },
    { version: 2, title: "Version 2", stateWord: "Published earlier", stateTone: "grey", whoText: "Written by a person", whenText: "today 09:00", noteText: null, sizeText: "6 files, 1.2 KB", openLabel: "Open", exportLabel: "Export this version", exportHref: "/x?version=2", publish: null, restore: RESTORE },
  ],
  emptyText: null,
  nextBefore: null,
  olderLabel: null,
  newestLabel: null,
  closeLabel: "Back to the definition",
};

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/factories/:factory/:tab?" element={<FactoryView />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  pageClient.getFactoryPage.mockResolvedValue(PAGE);
});

describe("the Definition tab", () => {
  it("is offered by the Gateway, and the old Documents address opens it", async () => {
    client.getFactoryDefinitionTab.mockResolvedValue(UNDEFINED);
    renderAt("/factories/qa-outreach/documents");

    expect(await screen.findByTestId("fa-definition")).toBeTruthy();
    expect(client.getFactoryDefinitionTab).toHaveBeenCalledWith("qa-outreach", expect.anything());
  });

  it("shows a factory with no definition in the Gateway's words, what an export leaves out, and no Publish", async () => {
    client.getFactoryDefinitionTab.mockResolvedValue(UNDEFINED);
    renderAt("/factories/qa-outreach/definition");

    expect(await screen.findByText("QA Outreach has no definition on the Gateway.")).toBeTruthy();
    expect(screen.getByTestId("fa-def-empty").textContent).toBe(UNDEFINED.emptyText);
    expect(within(screen.getByTestId("fa-def-leaves-out")).getByText("factory.yaml: The goal is held in a file.")).toBeTruthy();
    expect(screen.queryByTestId("fa-def-publish")).toBeNull();
    expect(screen.queryByTestId("fa-def-history")).toBeNull();

    fireEvent.click(screen.getByTestId("fa-def-export"));
    await waitFor(() => expect(client.downloadFactoryDefinitionZip).toHaveBeenCalledWith("/gateway/factories/qa-outreach/definition/export"));
  });

  it("shows what changes as the Gateway compared it, and Publish sends the versions the confirm named", async () => {
    client.getFactoryDefinitionTab.mockResolvedValue(DRAFTED);
    client.publishFactoryDefinition.mockResolvedValue({ factory: "qa-outreach", version: 5, findings: [], changes: null, applied: ["Definition: version 5 published.", "Registry: seat writer renamed Writer -> Author."] });
    renderAt("/factories/qa-outreach/definition");

    const changes = await screen.findByTestId("fa-def-what-changes");
    expect(within(changes).getByText("1 seat changed.")).toBeTruthy();
    expect(within(changes).getByText("name Writer -> Author")).toBeTruthy();
    expect(within(changes).getByText("+ Write one post, and link the last one.")).toBeTruthy();
    expect(screen.getByText("Registry: seat writer renamed Writer -> Author.")).toBeTruthy();

    fireEvent.click(screen.getByTestId("fa-def-publish"));
    const dialog = await screen.findByRole("alertdialog");
    expect(within(dialog).getByText(PUBLISH.confirmLines[0])).toBeTruthy();
    fireEvent.click(within(dialog).getByRole("button", { name: "Publish" }));

    await waitFor(() => expect(client.publishFactoryDefinition).toHaveBeenCalledWith("qa-outreach", PUBLISH));
    const outcome = await screen.findByTestId("fa-def-outcome");
    expect(within(outcome).getByText(PUBLISH.doneText)).toBeTruthy();
    expect(within(outcome).getByText("Definition: version 5 published.")).toBeTruthy();
  });

  it("leaves a refused publish on the tab with the Gateway's sentence and every finding by file and line", async () => {
    client.getFactoryDefinitionTab.mockResolvedValue(DRAFTED);
    client.publishFactoryDefinition.mockRejectedValue(
      new FactoryDefinitionRefusal(422, "Version 5 was not published: the check found 1 error in the definition of qa-outreach.", "CheckFailed", [
        { severity: "error", file: "seats/writer.yaml", line: 9, message: "A start with a check cannot be published yet." },
      ]),
    );
    renderAt("/factories/qa-outreach/definition");

    fireEvent.click(await screen.findByTestId("fa-def-publish"));
    fireEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Publish" }));

    const refused = await screen.findByTestId("fa-def-refused");
    expect(refused.textContent).toContain("Version 5 was not published: the check found 1 error");
    expect(within(refused).getByText("seats/writer.yaml:9")).toBeTruthy();
    expect(within(refused).getByText("A start with a check cannot be published yet.")).toBeTruthy();
    expect(screen.queryByRole("alertdialog")).toBeNull();
  });

  it("opens the history with the Gateway's buttons, and Restore sends the version, the newest seen and the published one", async () => {
    client.getFactoryDefinitionTab.mockResolvedValue(DRAFTED);
    client.getFactoryDefinitionHistory.mockResolvedValue(HISTORY);
    client.restoreFactoryDefinition.mockResolvedValue({ factory: "qa-outreach", version: 6, findings: [], changes: null, applied: [] });
    renderAt("/factories/qa-outreach/definition");

    fireEvent.click(await screen.findByTestId("fa-def-history"));
    await screen.findByTestId("fa-def-history-table");
    expect(screen.queryByTestId("fa-def-row-restore-5")).toBeNull();
    expect(screen.queryByTestId("fa-def-row-publish-2")).toBeNull();

    fireEvent.click(screen.getByTestId("fa-def-row-restore-2"));
    fireEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Restore" }));

    await waitFor(() => expect(client.restoreFactoryDefinition).toHaveBeenCalledWith("qa-outreach", RESTORE));
    expect(await screen.findByText(RESTORE.doneText)).toBeTruthy();
  });

  it("sends the chosen zip to the Gateway's import address and shows its sentence", async () => {
    client.getFactoryDefinitionTab.mockResolvedValue(UNDEFINED);
    client.importFactoryDefinitionZip.mockResolvedValue({ factory: "qa-outreach", version: 1, doneText: "Imported 6 files as version 1, a draft.", findings: [] });
    renderAt("/factories/qa-outreach/definition");

    const input = (await screen.findByTestId("fa-def-import-file")) as HTMLInputElement;
    const file = new File(["zip"], "qa-outreach.zip", { type: "application/zip" });
    fireEvent.change(input, { target: { files: [file] } });

    await waitFor(() => expect(client.importFactoryDefinitionZip).toHaveBeenCalledWith(IMPORT.href, file));
    expect(await screen.findByText("Imported 6 files as version 1, a draft.")).toBeTruthy();
  });
});
