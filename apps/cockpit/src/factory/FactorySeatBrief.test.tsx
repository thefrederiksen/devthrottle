// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";

// The brief panel of a seat's page (Factory Seats mission, phase 2b). What is held down, at the points where it is
// hard:
//   * a seat with NO brief on the Gateway keeps where its file is and offers to write the brief here, and the first
//     save expects version 0;
//   * a seat with drafts and nothing published says so, shows no text as the brief, and opens the newest draft;
//   * a STALE SAVE shows the newer version beside the owner's own text, keeps his text in the box, saves nothing
//     more until he says so, and then saves after the newer version;
//   * Publish asks first in the Gateway's sentences, saves a changed text first and publishes THAT version, and a
//     REFUSED publish stays on screen in the Gateway's words with nothing reloaded and nothing saved twice;
//   * a LONG HISTORY is read one page at a time, asking for exactly the page the Gateway named;
//   * Bring back sends the version the confirm named AND the newest version the owner was looking at;
//   * the TWENTY WAITING DRAFTS are said in the Gateway's own sentence, on the panel, the editor and the history.
// Every word is the Gateway's: the fixtures carry "(fixture)" where the panel could otherwise pass with its own.
// Only the Gateway calls are replaced; the refusal class is the real one, so the panel's stale check meets the same
// object the client throws.

const client = vi.hoisted(() => ({
  getFactoryBriefEditor: vi.fn(),
  getFactoryBriefHistory: vi.fn(),
  getFactoryBriefVersion: vi.fn(),
  saveFactoryBriefDraft: vi.fn(),
  publishFactoryBrief: vi.fn(),
  bringBackFactoryBrief: vi.fn(),
}));

vi.mock("@devthrottle/client-core/factory/factoryBriefClient", async (importOriginal) => {
  const real = await importOriginal<typeof import("@devthrottle/client-core/factory/factoryBriefClient")>();
  return { ...real, ...client };
});

// The real describeAndReport formats the sentence; only the report it makes is captured.
const reported = vi.hoisted(() => vi.fn());
vi.mock("@devthrottle/client-core/errors/reportClientError", async (importOriginal) => {
  const real = await importOriginal<typeof import("@devthrottle/client-core/errors/reportClientError")>();
  return {
    ...real,
    describeAndReport: (surface: string, action: string, err: unknown, context?: { sessionId?: string }) => {
      reported({ surface, action, err });
      return real.describeAndReport(surface, action, err, context);
    },
  };
});

import { FactoryBriefRefusal, type FactoryBriefVersion } from "@devthrottle/client-core/factory/factoryBriefClient";
import type { FactorySeatBrief } from "@devthrottle/client-core/factory/factoriesScreenClient";
import { FactorySeatBriefPanel } from "./FactorySeatBrief";
import {
  BRIEF_DRAFTS,
  BRIEF_EDITOR,
  BRIEF_EDITOR_FIRST,
  BRIEF_HISTORY_NEWEST,
  BRIEF_HISTORY_OLDEST,
  BRIEF_PUBLISHED,
  BRIEF_VERSION_43,
  BRIEF_WAITING,
  SEAT_PAGE_SCHEDULED,
} from "./fixtures";

const SESSION = "73000000-0000-4000-8000-000000000001";

function version(over: Partial<FactoryBriefVersion> = {}): FactoryBriefVersion {
  return {
    factory: "warmforward",
    seat: "scout",
    version: 4,
    text: "saved text",
    state: "draft",
    authorKind: "person",
    authorId: "device browser abc",
    writtenAtUtc: "2026-10-13T15:00:00Z",
    publishedAtUtc: null,
    publishedBy: null,
    newestVersion: 4,
    publishedVersion: 3,
    ...over,
  };
}

const onChanged = vi.fn();

function renderPanel(brief: FactorySeatBrief) {
  return render(<FactorySeatBriefPanel factory="warmforward" seat="scout" brief={brief} onChanged={onChanged} />);
}

async function openEditor(brief: FactorySeatBrief = BRIEF_PUBLISHED) {
  renderPanel(brief);
  fireEvent.click(screen.getByTestId("fa-seat-brief-edit"));
  return (await screen.findByTestId("fa-brief-text")) as HTMLTextAreaElement;
}

function type(box: HTMLTextAreaElement, text: string) {
  fireEvent.change(box, { target: { value: text } });
}

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  client.getFactoryBriefEditor.mockResolvedValue(BRIEF_EDITOR);
  client.getFactoryBriefHistory.mockResolvedValue(BRIEF_HISTORY_NEWEST);
});

describe("What this seat does", () => {
  it("a published brief shows its text, its version and who published it, with Edit and History - and no file", () => {
    renderPanel(BRIEF_PUBLISHED);

    expect(screen.getByTestId("fa-seat-brief-text").textContent).toBe("You are the Scout of WarmForward. Scan every morning.");
    expect(screen.getByTestId("fa-seat-brief-version").textContent).toBe("Version 3, published by a person (device browser abc) today 14:00. (fixture)");
    expect(screen.getAllByRole("button").map((b) => b.textContent)).toEqual(["Edit (fixture)", "History (fixture)"]);
    expect(screen.queryByTestId("fa-seat-brief-path")).toBeNull();
    expect(screen.queryByTestId("fa-seat-brief-draft")).toBeNull();
    expect(screen.queryByTestId("fa-seat-brief-waiting")).toBeNull();
  });

  it("a seat with drafts and nothing published says so, shows no text as its brief, and still says where its file is", () => {
    renderPanel(BRIEF_DRAFTS);

    expect(screen.getByTestId("fa-seat-brief-draft").textContent).toBe("This seat has 2 drafts on the Gateway and none is published. (fixture)");
    expect(screen.queryByTestId("fa-seat-brief-text")).toBeNull();
    expect(screen.queryByTestId("fa-seat-brief-version")).toBeNull();
    expect(screen.getByTestId("fa-seat-brief-path").textContent).toBe("/home/soren/factories/warmforward/agents/scout.yaml");
    expect(screen.getAllByRole("button").map((b) => b.textContent)).toEqual(["Open the newest draft (fixture)", "History (fixture)"]);
  });

  it("a seat with no brief on the Gateway keeps its file's path and computer, and offers to write the brief here - with no History", () => {
    renderPanel(SEAT_PAGE_SCHEDULED.brief);

    expect(screen.getByTestId("fa-seat-brief-path").textContent).toBe("/home/soren/factories/warmforward/agents/scout.yaml");
    expect(screen.getByText("The brief is a file on SOREN_NORTH. It is not held on the Gateway yet. (fixture)")).toBeTruthy();
    expect(screen.getAllByRole("button").map((b) => b.textContent)).toEqual(["Write the brief here (fixture)"]);
  });

  it("says in the Gateway's own sentence that twenty drafts are waiting", () => {
    renderPanel(BRIEF_WAITING);

    expect(screen.getByTestId("fa-seat-brief-waiting").textContent).toBe("20 drafts are waiting that nobody has published (versions 4 to 23). (fixture)");
    expect(screen.getByTestId("fa-seat-brief-draft").textContent).toBe("A newer draft, version 23, was written by session 7300 today 13:00. (fixture)");
  });
});

describe("Writing a first brief", () => {
  it("starts empty, says so in the Gateway's words, and the first save expects version 0", async () => {
    client.getFactoryBriefEditor.mockResolvedValue(BRIEF_EDITOR_FIRST);
    client.saveFactoryBriefDraft.mockResolvedValue(version({ version: 1, newestVersion: 1, publishedVersion: null }));
    const box = await openEditor(SEAT_PAGE_SCHEDULED.brief);

    expect(client.getFactoryBriefEditor).toHaveBeenCalledWith("warmforward", "scout", expect.anything());
    expect(screen.getByRole("heading", { level: 3 }).textContent).toBe("Write the brief of Scout (fixture)");
    expect(screen.getByTestId("fa-brief-start").textContent).toBe("Scout has no brief on the Gateway yet. Write it here. (fixture)");
    expect(box.value).toBe("");

    type(box, "You are the Scout.");
    fireEvent.click(screen.getByTestId("fa-brief-save"));

    await waitFor(() => expect(onChanged).toHaveBeenCalledWith("Saved as a draft. Talks and runs are still given version 3. (fixture)"));
    expect(client.saveFactoryBriefDraft).toHaveBeenCalledWith("warmforward", "scout", "You are the Scout.", 0);
    // Saved: the editor closes and the panel is back.
    expect(screen.queryByTestId("fa-brief-editor")).toBeNull();
    expect(client.publishFactoryBrief).not.toHaveBeenCalled();
  });
});

describe("Editing", () => {
  it("starts from what the Gateway says it starts from, and a save states that version", async () => {
    client.saveFactoryBriefDraft.mockResolvedValue(version());
    const box = await openEditor();

    expect(screen.getByTestId("fa-brief-start").textContent).toBe("This starts from version 3, the published brief. (fixture)");
    expect(box.value).toBe("You are the Scout of WarmForward. Scan every morning.");
    expect(box.getAttribute("aria-label")).toBe("The brief of Scout");

    type(box, "You are the Scout. Scan twice a day.");
    fireEvent.click(screen.getByRole("button", { name: "Save draft (fixture)" }));

    await waitFor(() => expect(client.saveFactoryBriefDraft).toHaveBeenCalledWith("warmforward", "scout", "You are the Scout. Scan twice a day.", 3));
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
  });

  it("closing without saving sends nothing and tells the page nothing", async () => {
    const box = await openEditor();
    type(box, "half a thought");

    fireEvent.click(screen.getByRole("button", { name: "Close without saving (fixture)" }));

    expect(screen.queryByTestId("fa-brief-editor")).toBeNull();
    expect(client.saveFactoryBriefDraft).not.toHaveBeenCalled();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("a save the Gateway refuses for its own reason shows the Gateway's sentence and keeps the text", async () => {
    const sentence = "the brief takes 70000 bytes; a brief takes at most 65536";
    client.saveFactoryBriefDraft.mockRejectedValue(new FactoryBriefRefusal(400, sentence, "TooLarge", null));
    const box = await openEditor();
    type(box, "far too long");

    fireEvent.click(screen.getByTestId("fa-brief-save"));

    expect((await screen.findByTestId("fa-brief-error")).textContent).toContain(sentence);
    expect(box.value).toBe("far too long");
    expect(screen.queryByTestId("fa-brief-conflict")).toBeNull();
    expect(onChanged).not.toHaveBeenCalled();
    expect(reported).toHaveBeenCalledWith(expect.objectContaining({ action: "save the draft" }));
  });

  it("shows the twenty waiting drafts in the Gateway's sentence above the text", async () => {
    client.getFactoryBriefEditor.mockResolvedValue({ ...BRIEF_EDITOR, waitingText: "20 drafts are waiting. (fixture)" });
    await openEditor();

    expect(screen.getByTestId("fa-brief-waiting").textContent).toBe("20 drafts are waiting. (fixture)");
  });
});

describe("A stale save", () => {
  const refusal = "the brief of 'scout' is at version 4, not 3; read it again, merge, and save once more";
  const newer = version({ version: 4, text: "What a session wrote meanwhile.", authorKind: "session", authorId: SESSION });

  it("shows the newer version beside the owner's text, keeps his text in the box, and saves nothing more", async () => {
    client.saveFactoryBriefDraft.mockRejectedValueOnce(new FactoryBriefRefusal(409, refusal, "Stale", newer));
    const box = await openEditor();
    type(box, "The owner's own text.");

    fireEvent.click(screen.getByTestId("fa-brief-save"));

    const conflict = await screen.findByTestId("fa-brief-conflict");
    // The Gateway's refusal leads, then the Gateway's sentence for what to do about it.
    expect(within(conflict).getByRole("alert").textContent).toContain(refusal);
    expect(within(conflict).getByText("Nothing was saved. Your text is still in the box. (fixture)")).toBeTruthy();
    // Both texts, each under the Gateway's own heading: nothing typed is lost.
    expect(box.value).toBe("The owner's own text.");
    expect(screen.getByText("Your text (fixture)")).toBeTruthy();
    expect(screen.getByText("The newer version (fixture)")).toBeTruthy();
    expect(screen.getByTestId("fa-brief-theirs").textContent).toBe("What a session wrote meanwhile.");
    // Nothing is overwritten silently: one refused save, and no second one until the owner says.
    expect(client.saveFactoryBriefDraft).toHaveBeenCalledTimes(1);
    expect(onChanged).not.toHaveBeenCalled();
    expect(screen.queryByTestId("fa-brief-save")).toBeNull();
    expect(reported).toHaveBeenCalledWith(expect.objectContaining({ action: "save the draft" }));
  });

  it("saving after the newer version sends the owner's text against THAT version", async () => {
    client.saveFactoryBriefDraft
      .mockRejectedValueOnce(new FactoryBriefRefusal(409, refusal, "Stale", newer))
      .mockResolvedValueOnce(version({ version: 5 }));
    const box = await openEditor();
    type(box, "The owner's own text.");
    fireEvent.click(screen.getByTestId("fa-brief-save"));
    await screen.findByTestId("fa-brief-conflict");

    type(box, "The owner's own text, with what the session added.");
    fireEvent.click(screen.getByRole("button", { name: "Save my text after the newer version (fixture)" }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(client.saveFactoryBriefDraft).toHaveBeenLastCalledWith("warmforward", "scout", "The owner's own text, with what the session added.", 4);
  });

  it("taking the newer version puts its text in the box and sends nothing; the next save is against it", async () => {
    client.saveFactoryBriefDraft
      .mockRejectedValueOnce(new FactoryBriefRefusal(409, refusal, "Stale", newer))
      .mockResolvedValueOnce(version({ version: 5 }));
    const box = await openEditor();
    type(box, "The owner's own text.");
    fireEvent.click(screen.getByTestId("fa-brief-save"));
    await screen.findByTestId("fa-brief-conflict");

    fireEvent.click(screen.getByRole("button", { name: "Take the newer version and drop my text (fixture)" }));

    expect(box.value).toBe("What a session wrote meanwhile.");
    expect(screen.queryByTestId("fa-brief-conflict")).toBeNull();
    expect(client.saveFactoryBriefDraft).toHaveBeenCalledTimes(1);

    type(box, "What a session wrote meanwhile. And one line more.");
    fireEvent.click(screen.getByTestId("fa-brief-save"));
    await waitFor(() => expect(client.saveFactoryBriefDraft).toHaveBeenLastCalledWith("warmforward", "scout", "What a session wrote meanwhile. And one line more.", 4));
  });

  it("a second writer in between is shown again, never saved over", async () => {
    const newest = version({ version: 5, text: "And a third hand wrote this." });
    client.saveFactoryBriefDraft
      .mockRejectedValueOnce(new FactoryBriefRefusal(409, refusal, "Stale", newer))
      .mockRejectedValueOnce(new FactoryBriefRefusal(409, "the brief of 'scout' is at version 5, not 4", "Stale", newest));
    const box = await openEditor();
    type(box, "The owner's own text.");
    fireEvent.click(screen.getByTestId("fa-brief-save"));
    await screen.findByTestId("fa-brief-conflict");

    fireEvent.click(screen.getByTestId("fa-brief-save-over"));

    await waitFor(() => expect(screen.getByTestId("fa-brief-theirs").textContent).toBe("And a third hand wrote this."));
    expect(box.value).toBe("The owner's own text.");
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("met while publishing, publishes nothing and shows the two texts", async () => {
    client.saveFactoryBriefDraft.mockRejectedValueOnce(new FactoryBriefRefusal(409, refusal, "Stale", newer));
    const box = await openEditor();
    type(box, "The owner's own text.");
    fireEvent.click(screen.getByTestId("fa-brief-publish"));
    fireEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Publish it (fixture)" }));

    await screen.findByTestId("fa-brief-conflict");
    expect(client.publishFactoryBrief).not.toHaveBeenCalled();
    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(box.value).toBe("The owner's own text.");
    expect(onChanged).not.toHaveBeenCalled();
  });
});

describe("Publish", () => {
  it("asks first, in the Gateway's sentences, and sends nothing until confirmed", async () => {
    await openEditor();

    fireEvent.click(screen.getByRole("button", { name: "Publish (fixture)" }));

    const dialog = await screen.findByRole("alertdialog");
    expect(dialog.getAttribute("aria-label")).toBe("Publish this brief for Scout? (fixture)");
    expect(within(dialog).getByTestId("fa-confirm-lines").textContent).toBe(
      "Every later talk and every later run of Scout will be given this text. (fixture)It replaces version 3, which is kept. (fixture)",
    );
    expect(client.saveFactoryBriefDraft).not.toHaveBeenCalled();
    expect(client.publishFactoryBrief).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(client.publishFactoryBrief).not.toHaveBeenCalled();
  });

  it("a changed text is saved first and THAT version is published; the page gets the Gateway's sentence", async () => {
    client.saveFactoryBriefDraft.mockResolvedValue(version({ version: 4 }));
    client.publishFactoryBrief.mockResolvedValue(version({ version: 4, state: "published", publishedVersion: 4 }));
    const box = await openEditor();
    type(box, "You are the Scout. Scan twice a day.");

    fireEvent.click(screen.getByTestId("fa-brief-publish"));
    fireEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Publish it (fixture)" }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledWith("Published. Every later talk and every later run of Scout is given this text. (fixture)"));
    expect(client.saveFactoryBriefDraft).toHaveBeenCalledWith("warmforward", "scout", "You are the Scout. Scan twice a day.", 3);
    expect(client.publishFactoryBrief).toHaveBeenCalledWith("warmforward", "scout", 4);
    expect(screen.queryByTestId("fa-brief-editor")).toBeNull();
  });

  it("a text that was not changed is published as the version it started from, with no new version saved", async () => {
    client.getFactoryBriefEditor.mockResolvedValue({ ...BRIEF_EDITOR, expectedVersion: 5, publish: { ...BRIEF_EDITOR.publish, version: 5 } });
    client.publishFactoryBrief.mockResolvedValue(version({ version: 5, state: "published" }));
    await openEditor();

    fireEvent.click(screen.getByTestId("fa-brief-publish"));
    fireEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Publish it (fixture)" }));

    await waitFor(() => expect(client.publishFactoryBrief).toHaveBeenCalledWith("warmforward", "scout", 5));
    expect(client.saveFactoryBriefDraft).not.toHaveBeenCalled();
  });

  it("refused: the Gateway's own sentence stays in the confirm, nothing is shown as published and the page is not told", async () => {
    const sentence = "version 3 is already the published brief of 'scout'";
    client.publishFactoryBrief.mockRejectedValue(new FactoryBriefRefusal(409, sentence, "AlreadyPublished", null));
    await openEditor();

    fireEvent.click(screen.getByTestId("fa-brief-publish"));
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Publish it (fixture)" }));

    await waitFor(() => expect(dialog.querySelector(".ui-confirm-error")?.textContent ?? "").toContain(sentence));
    expect(screen.getByRole("alertdialog")).toBeTruthy();
    expect(onChanged).not.toHaveBeenCalled();
    expect(screen.getByTestId("fa-brief-editor")).toBeTruthy();
  });

  it("refused after the text was saved: a second press publishes the saved version and does not save the text twice", async () => {
    const sentence = "a seat's brief is written by a person, or by a session of its factory";
    client.saveFactoryBriefDraft.mockResolvedValue(version({ version: 4 }));
    client.publishFactoryBrief
      .mockRejectedValueOnce(new FactoryBriefRefusal(403, sentence, null, null))
      .mockResolvedValueOnce(version({ version: 4, state: "published" }));
    const box = await openEditor();
    type(box, "You are the Scout. Scan twice a day.");

    fireEvent.click(screen.getByTestId("fa-brief-publish"));
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Publish it (fixture)" }));
    await waitFor(() => expect(dialog.querySelector(".ui-confirm-error")?.textContent ?? "").toContain(sentence));

    fireEvent.click(within(dialog).getByRole("button", { name: "Publish it (fixture)" }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(client.saveFactoryBriefDraft).toHaveBeenCalledTimes(1);
    expect(client.publishFactoryBrief).toHaveBeenLastCalledWith("warmforward", "scout", 4);
  });
});

describe("History", () => {
  async function openHistory() {
    renderPanel(BRIEF_PUBLISHED);
    fireEvent.click(screen.getByTestId("fa-seat-brief-history"));
    return screen.findByTestId("fa-brief-versions");
  }

  it("shows the newest page first: each version's state, who wrote it, when and its size, in the Gateway's words", async () => {
    const table = await openHistory();

    expect(client.getFactoryBriefHistory).toHaveBeenCalledWith("warmforward", "scout", null, expect.anything());
    expect(screen.getByRole("heading", { level: 3 }).textContent).toBe("History of the brief of Scout (fixture)");
    expect(screen.getByTestId("fa-brief-history-summary").textContent).toBe("45 versions. Version 44 is published. (fixture) Versions 45 to 43 of 45. (fixture)");
    expect(within(table).getAllByRole("columnheader").map((h) => h.textContent)).toEqual(["Version", "State", "Written by", "When", "Size"]);
    const cells = within(screen.getByTestId("fa-brief-version-45")).getAllByRole("cell").map((c) => c.textContent);
    expect(cells.slice(0, 5)).toEqual(["Version 45 (fixture)", "Draft (fixture)", `session ${SESSION}`, "today 13:00", "1.2 KB"]);
    expect(within(screen.getByTestId("fa-brief-version-45")).getByText("Draft (fixture)").className).toContain("fa-tone-amber");
  });

  it("offers only the buttons the Gateway sent: Publish on the draft, Bring back on the earlier one, neither on the published one", async () => {
    await openHistory();

    const labels = (v: number) => within(screen.getByTestId(`fa-brief-version-${v}`)).getAllByRole("button").map((b) => b.textContent);
    expect(labels(45)).toEqual(["Open (fixture)", "Publish"]);
    expect(labels(44)).toEqual(["Open (fixture)"]);
    expect(labels(43)).toEqual(["Open (fixture)", "Bring back"]);
  });

  it("a long history is read one page at a time: Older asks for exactly the page the Gateway named, and Newest goes back", async () => {
    await openHistory();
    client.getFactoryBriefHistory.mockResolvedValue(BRIEF_HISTORY_OLDEST);

    fireEvent.click(screen.getByRole("button", { name: "Older versions (fixture)" }));

    await screen.findByTestId("fa-brief-version-1");
    expect(client.getFactoryBriefHistory).toHaveBeenLastCalledWith("warmforward", "scout", 43, expect.anything());
    expect(screen.getByTestId("fa-brief-history-summary").textContent).toContain("Versions 2 to 1 of 45. (fixture)");
    expect(screen.queryByTestId("fa-brief-version-45")).toBeNull();
    // The Gateway said there is no older page, so there is no button for one.
    expect(screen.queryByTestId("fa-brief-older")).toBeNull();

    client.getFactoryBriefHistory.mockResolvedValue(BRIEF_HISTORY_NEWEST);
    fireEvent.click(screen.getByRole("button", { name: "Newest versions (fixture)" }));

    await screen.findByTestId("fa-brief-version-45");
    expect(client.getFactoryBriefHistory).toHaveBeenLastCalledWith("warmforward", "scout", null, expect.anything());
    expect(screen.queryByTestId("fa-brief-newest")).toBeNull();
  });

  it("Open reads one version's text, and shows it with the Gateway's line", async () => {
    client.getFactoryBriefVersion.mockResolvedValue(BRIEF_VERSION_43);
    await openHistory();

    fireEvent.click(screen.getByTestId("fa-brief-open-43"));

    const opened = await screen.findByTestId("fa-brief-opened");
    expect(client.getFactoryBriefVersion).toHaveBeenCalledWith("warmforward", "scout", 43);
    expect(within(opened).getByRole("heading").textContent).toBe("Version 43 (fixture)");
    expect(within(opened).getByText("Published earlier. Written by session 7300 today 13:00. 1.2 KB. (fixture)")).toBeTruthy();
    expect(within(opened).getByText("The text of version 43.")).toBeTruthy();

    fireEvent.click(within(opened).getByRole("button", { name: "Close (fixture)" }));
    expect(screen.queryByTestId("fa-brief-opened")).toBeNull();
  });

  it("Bring back asks first, then sends the version the confirm named and the newest version the owner was looking at", async () => {
    client.bringBackFactoryBrief.mockResolvedValue(version({ version: 46, state: "published" }));
    await openHistory();

    fireEvent.click(screen.getByTestId("fa-brief-back-43"));
    const dialog = await screen.findByRole("alertdialog");
    expect(dialog.getAttribute("aria-label")).toBe("Bring back version 43? (fixture)");
    expect(within(dialog).getByTestId("fa-confirm-lines").textContent).toContain("Nothing is removed. (fixture)");
    expect(client.bringBackFactoryBrief).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole("button", { name: "Bring back it (fixture)" }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledWith("Bring back of version 43 is done. (fixture)"));
    expect(client.bringBackFactoryBrief).toHaveBeenCalledWith("warmforward", "scout", 43, 45);
    expect(client.publishFactoryBrief).not.toHaveBeenCalled();
    // The history is read again from its newest page, so the new version is on screen.
    await waitFor(() => expect(client.getFactoryBriefHistory).toHaveBeenCalledTimes(2));
    expect(client.getFactoryBriefHistory).toHaveBeenLastCalledWith("warmforward", "scout", null, expect.anything());
  });

  it("Publish on a draft sends that version, and the page gets the Gateway's sentence", async () => {
    client.publishFactoryBrief.mockResolvedValue(version({ version: 45, state: "published" }));
    await openHistory();

    fireEvent.click(screen.getByTestId("fa-brief-publish-45"));
    fireEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Publish it (fixture)" }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledWith("Publish of version 45 is done. (fixture)"));
    expect(client.publishFactoryBrief).toHaveBeenCalledWith("warmforward", "scout", 45);
    expect(client.bringBackFactoryBrief).not.toHaveBeenCalled();
  });

  it("a refused bring back stays in the confirm in the Gateway's words; the page is not told and nothing is read again", async () => {
    const sentence = "the brief of 'scout' is at version 46, not 45; read it again, merge, and save once more";
    client.bringBackFactoryBrief.mockRejectedValue(new FactoryBriefRefusal(409, sentence, "Stale", version({ version: 46 })));
    await openHistory();

    fireEvent.click(screen.getByTestId("fa-brief-back-43"));
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Bring back it (fixture)" }));

    await waitFor(() => expect(dialog.querySelector(".ui-confirm-error")?.textContent ?? "").toContain(sentence));
    expect(screen.getByRole("alertdialog")).toBeTruthy();
    expect(onChanged).not.toHaveBeenCalled();
    expect(client.getFactoryBriefHistory).toHaveBeenCalledTimes(1);
  });

  it("says the twenty waiting drafts in the Gateway's own sentence", async () => {
    client.getFactoryBriefHistory.mockResolvedValue({ ...BRIEF_HISTORY_NEWEST, waitingText: "20 drafts are waiting that nobody has published. (fixture)" });
    await openHistory();

    expect(screen.getByTestId("fa-brief-waiting").textContent).toBe("20 drafts are waiting that nobody has published. (fixture)");
  });

  it("a seat with no version says so and shows no table", async () => {
    client.getFactoryBriefHistory.mockResolvedValue({
      ...BRIEF_HISTORY_NEWEST,
      summaryText: "No version yet. (fixture)",
      pageText: null,
      rows: [],
      emptyText: "This seat has no brief on the Gateway, so it has no history. (fixture)",
      nextBefore: null,
      olderLabel: null,
    });
    renderPanel(BRIEF_PUBLISHED);
    fireEvent.click(screen.getByTestId("fa-seat-brief-history"));

    expect(await screen.findByText("This seat has no brief on the Gateway, so it has no history. (fixture)")).toBeTruthy();
    expect(screen.queryByTestId("fa-brief-versions")).toBeNull();
  });

  it("Back to the brief closes the history", async () => {
    await openHistory();

    fireEvent.click(screen.getByRole("button", { name: "Back to the brief (fixture)" }));

    expect(screen.queryByTestId("fa-brief-history")).toBeNull();
    expect(screen.getByTestId("fa-seat-brief-text")).toBeTruthy();
  });

  it("a history that cannot be read shows the Gateway's sentence and can be tried again", async () => {
    const sentence = "WarmForward has no seat 'scout'. (fixture)";
    client.getFactoryBriefHistory.mockRejectedValueOnce(new FactoryBriefRefusal(404, sentence, null, null));
    renderPanel(BRIEF_PUBLISHED);
    fireEvent.click(screen.getByTestId("fa-seat-brief-history"));

    expect((await screen.findByRole("alert")).textContent).toContain(sentence);
    expect(reported).toHaveBeenCalledWith(expect.objectContaining({ action: "read the brief's history" }));
  });
});
