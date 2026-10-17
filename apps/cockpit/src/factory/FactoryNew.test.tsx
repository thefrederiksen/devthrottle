// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";

// New factory on the Factories screen (Factory Definition mission, phase 6b). What is held down:
//   * New factory sends the chosen zip to the Gateway's address and opens the page the Gateway names;
//   * a refusal stays under the button in the Gateway's words, with every finding by file and line;
//   * the factories not published yet are rows under the list, in the Gateway's words, each opening its page;
//   * on a factory's page, a tab the Gateway sends a "not yet" sentence for shows that sentence and reads nothing.
// Only the Gateway calls are replaced; the refusal class is the real one.

const screenClient = vi.hoisted(() => ({
  getFactoryManagerCard: vi.fn(() => new Promise(() => {})),
  startFactoryManagerTalk: vi.fn(),
  getFactoriesList: vi.fn(),
  getFactoryPage: vi.fn(),
  getFactorySeats: vi.fn(),
  getFactoryFloor: vi.fn(() => new Promise(() => {})),
  startFactoryTalk: vi.fn(),
  runFactoryOwnerAction: vi.fn(),
}));
vi.mock("@devthrottle/client-core/factory/factoriesScreenClient", () => screenClient);

const cronClient = vi.hoisted(() => ({
  getCronPause: vi.fn(async () => ({ paused: false, sinceUtc: null, banner: null, actionLabel: "Pause everything", actionTitle: "t" })),
  setCronPause: vi.fn(),
  getCronJob: vi.fn(),
  setCronJobEnabled: vi.fn(),
}));
vi.mock("@devthrottle/client-core/schedule/scheduleClient", () => cronClient);

const definitionClient = vi.hoisted(() => ({
  importFactoryDefinitionZip: vi.fn(),
  getFactoryDefinitionTab: vi.fn(() => new Promise(() => {})),
}));
vi.mock("@devthrottle/client-core/factory/factoryDefinitionClient", async (importOriginal) => {
  const real = await importOriginal<typeof import("@devthrottle/client-core/factory/factoryDefinitionClient")>();
  return { ...real, ...definitionClient };
});

import { FactoryDefinitionRefusal } from "@devthrottle/client-core/factory/factoryDefinitionClient";
import { FactoriesView } from "./FactoriesView";
import { FactoryView } from "./FactoryView";
import { FACTORY_LIST, FACTORY_PAGE } from "./fixtures";

function Where() {
  const loc = useLocation();
  return <div data-testid="where">{loc.pathname}</div>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/factories" element={<FactoriesView />} />
        <Route path="/factories/:factory" element={<FactoryView />} />
        <Route path="/factories/:factory/:tab" element={<FactoryView />} />
      </Routes>
      <Where />
    </MemoryRouter>,
  );
}

const ZIP = new File(["PK"], "eric-factory.zip", { type: "application/zip" });

const ERIC = {
  id: "eric",
  title: "Eric",
  text: "Eric - draft, not published yet",
  lineText: "l",
  href: "/factories/eric/definition",
  stateWord: "DRAFT (fixture)",
  stateTone: "grey" as const,
  seatCountText: "3 seats (fixture)",
};

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  window.localStorage.clear();
  screenClient.getFactoriesList.mockResolvedValue(FACTORY_LIST);
});

describe("New factory on the Factories screen", () => {
  it("sends the chosen zip to the Gateway's address and opens the page the Gateway names", async () => {
    definitionClient.importFactoryDefinitionZip.mockResolvedValue({
      factory: "eric",
      version: 1,
      doneText: "Imported 3 files as version 1, a draft. (fixture)",
      findings: [],
      openHref: "/factories/eric/definition",
    });
    screenClient.getFactoryPage.mockResolvedValue({ ...FACTORY_PAGE, id: "eric", title: "Eric" });
    renderAt("/factories");

    const button = await screen.findByTestId("fa-new-factory-button");
    expect(button.textContent).toBe("New factory");
    expect(screen.getByText("New factory takes a zip of a definition folder. (fixture)")).toBeTruthy();
    fireEvent.change(screen.getByTestId("fa-new-factory-file"), { target: { files: [ZIP] } });

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/factories/eric/definition"));
    expect(definitionClient.importFactoryDefinitionZip).toHaveBeenCalledWith("/gateway/factories/new", ZIP);
  });

  it("keeps a refusal under the button in the Gateway's words, with every finding by file and line", async () => {
    definitionClient.importFactoryDefinitionZip.mockRejectedValue(
      new FactoryDefinitionRefusal(422, "The folder was not imported: the check found 1 error (fixture).", "CheckFailed", [
        { severity: "error", file: "seats/mail-desk.yaml", line: 7, message: "colour is not a field of a seat (fixture)" },
      ]),
    );
    renderAt("/factories");

    fireEvent.change(await screen.findByTestId("fa-new-factory-file"), { target: { files: [ZIP] } });

    const refused = await screen.findByTestId("fa-new-factory-refused");
    expect(refused.textContent).toContain("The folder was not imported: the check found 1 error (fixture).");
    expect(within(refused).getByText("seats/mail-desk.yaml:7")).toBeTruthy();
    expect(within(refused).getByText("colour is not a field of a seat (fixture)")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/factories");
  });

  it("shows the factories not published yet as cards under the list, in the Gateway's words, each opening its page", async () => {
    screenClient.getFactoriesList.mockResolvedValue({
      ...FACTORY_LIST,
      draftTitle: "Not published yet",
      draftRows: [{ ...ERIC, lineText: "Version 1, a draft, 3 files. (fixture)" }],
    });
    renderAt("/factories");

    const drafts = await screen.findByTestId("fa-drafts");
    expect(within(drafts).getByText("Not published yet")).toBeTruthy();
    const card = within(drafts).getByTestId("fa-draft-eric");
    expect(within(card).getByText("Version 1, a draft, 3 files. (fixture)")).toBeTruthy();
    expect(within(card).getByText("DRAFT (fixture)")).toBeTruthy();
    expect(within(card).getByText("3 seats (fixture)")).toBeTruthy();
    const name = within(card).getByText("Eric");
    expect(name.getAttribute("href")).toBe("/factories/eric/definition");
    expect(name.getAttribute("title")).toBe("Eric - draft, not published yet");
  });

  it("leads with the drafts when nothing is published, the Gateway's empty sentence under them", async () => {
    screenClient.getFactoriesList.mockResolvedValue({
      ...FACTORY_LIST,
      rows: [],
      emptyText: "No factory yet (fixture).",
      draftRows: [ERIC],
    });
    renderAt("/factories");

    expect(await screen.findByTestId("fa-new-factory-button")).toBeTruthy();
    const draft = screen.getByTestId("fa-draft-eric");
    const note = screen.getByTestId("fa-empty-note");
    expect(note.textContent).toBe("No factory yet (fixture).");
    // The drafts come first; the empty sentence, then the secondary part, follow.
    expect(draft.compareDocumentPosition(note) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(note.compareDocumentPosition(screen.getByTestId("fa-secondary")) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("shows the empty state itself when there is neither a factory nor a draft", async () => {
    screenClient.getFactoriesList.mockResolvedValue({ ...FACTORY_LIST, rows: [], emptyText: "No factory yet (fixture).", draftRows: [] });
    renderAt("/factories");

    expect(await screen.findByText("No factory yet (fixture).")).toBeTruthy();
    expect(screen.queryByTestId("fa-empty-note")).toBeNull();
  });

  it("shows no draft section when the Gateway sends no draft row", async () => {
    renderAt("/factories");
    await screen.findByTestId("fa-new-factory-button");
    expect(screen.queryByTestId("fa-drafts")).toBeNull();
  });
});

describe("The page of a factory that is not published yet", () => {
  const NOT_YET = "This starts when Eric is published. (fixture)";
  const DRAFT_PAGE = {
    ...FACTORY_PAGE,
    id: "eric",
    title: "Eric",
    tabs: FACTORY_PAGE.tabs.map((t) => (t.key === "definition" ? t : { ...t, notYetText: NOT_YET })),
  };

  it("shows the Gateway's sentence on a tab it marked, and never reads the Seats tab", async () => {
    screenClient.getFactoryPage.mockResolvedValue(DRAFT_PAGE);
    renderAt("/factories/eric/seats");

    expect((await screen.findByTestId("fa-tab-not-yet")).textContent).toBe(NOT_YET);
    expect(screenClient.getFactorySeats).not.toHaveBeenCalled();
    expect(screen.queryByTestId("fa-overview")).toBeNull();
  });

  it("shows the Overview's sentence in place of its cards", async () => {
    screenClient.getFactoryPage.mockResolvedValue(DRAFT_PAGE);
    renderAt("/factories/eric");

    expect((await screen.findByTestId("fa-tab-not-yet")).textContent).toBe(NOT_YET);
    expect(screen.queryByTestId("fa-overview")).toBeNull();
  });

  it("opens the Definition tab, which carries no sentence", async () => {
    screenClient.getFactoryPage.mockResolvedValue(DRAFT_PAGE);
    renderAt("/factories/eric/definition");

    await waitFor(() => expect(definitionClient.getFactoryDefinitionTab).toHaveBeenCalled());
    expect(screen.queryByTestId("fa-tab-not-yet")).toBeNull();
  });
});
