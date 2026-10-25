// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import type { SessionManage } from "./useSessionManage";

// Rendered proof of the phone's RENAME control (mission "Mobile Rename"). The roster, the Cockpit's
// session menu and the CLI's `session rename` could all change a session's name; the phone could not,
// so a name picked badly at spawn stayed badly named until the owner reached a desktop. Pinned HERE:
//
//   * the overflow menu offers Rename session, above the destructive Stop and without its danger weight;
//   * the sheet prefills with the LIVE row's name and Save is armed only for a real one;
//   * a SUCCESS closes the sheet and the title changes AT ONCE - the hook adopts the Gateway's applied
//     name on the row, which is what the title renders, so nothing waits for a remount;
//   * a FAILURE keeps the sheet open with the Gateway's sentence inside it, above the Save it explains -
//     the same shape the stop sheet settled on (inspection finding I8), not a banner under a modal.
//
// The hook's own part of the deal (the optimistic row, the re-sync, the in-flight guard) is pinned in
// useSessionManageRename.test.tsx; this file is the sheet.

const navigateMock = vi.fn();
vi.mock("react-router-dom", () => ({
  useNavigate: () => navigateMock,
  useParams: () => ({ sessionId: "9c41e7a2-0000-4000-8000-000000000000" }),
  // The alerts bell in the app bar reads the address for its ?alert= deep link.
  useLocation: () => ({ pathname: "/session/9c41e7a2-0000-4000-8000-000000000000", search: "", hash: "", state: null, key: "default" }),
}));

import { SessionAppBar } from "./SessionAppBar";

const renameMock = vi.fn();
const setErrorMock = vi.fn();

// The hook's state, as the app bar sees it. Only the rename verb matters here; the stop and snooze
// surfaces are their own tested things.
function manage(over: Partial<SessionManage> = {}): SessionManage {
  return {
    session: null,
    onHold: false,
    held: false,
    deferred: false,
    snoozed: false,
    holdCountdown: null,
    sessionProblem: null,
    deliveryNotice: null,
    deliveryHistory: null,
    busy: false,
    error: null,
    setError: setErrorMock,
    toggleHold: () => Promise.resolve(true),
    holdFor: () => Promise.resolve(true),
    stopSession: () => Promise.reject(new Error("not this test's verb")),
    rename: (name: string) => renameMock(name) as Promise<boolean>,
    refreshNow: () => {},
    ...over,
  };
}

// A live row carrying the CURRENT name - what the title renders and the sheet prefills from.
function row(name: string) {
  return {
    sessionId: "9c41e7a2-0000-4000-8000-000000000000",
    name,
    activityState: "WaitingForInput",
    effectiveColor: "red",
    stateLabel: "Needs you",
    triageBucket: "needsYou",
  };
}

function openMenu() {
  fireEvent.click(screen.getByLabelText("Session menu"));
}

function openRenameSheet() {
  openMenu();
  fireEvent.click(screen.getByRole("menuitem", { name: "Rename session" }));
}

function renameInput() {
  return screen.getByLabelText("New name") as HTMLInputElement;
}

beforeEach(() => {
  renameMock.mockReset();
  navigateMock.mockReset();
  setErrorMock.mockReset();
});

afterEach(() => {
  cleanup();
});

describe("the phone's rename sheet", () => {
  it("offers Rename session in the overflow menu, above the destructive Stop", () => {
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openMenu();
    const items = screen.getAllByRole("menuitem").map((el) => el.textContent);
    expect(items).toContain("Rename session");
    expect(items.indexOf("Rename session")).toBeLessThan(items.indexOf("Stop session"));
    // Not destructive: no danger styling on the item that opens a sheet which confirms nothing.
    expect(screen.getByRole("menuitem", { name: "Rename session" }).className).not.toContain("menu-item-danger");
  });

  it("prefills with the live row's name and puts focus in the box", () => {
    render(<SessionAppBar title="stale title from mount" manage={manage({ session: row("Build the funnel") })} />);
    openRenameSheet();
    expect(renameInput().value).toBe("Build the funnel");
    expect(document.activeElement).toBe(renameInput());
  });

  it("falls back to the screen's own title when there is no row to prefill from", () => {
    render(<SessionAppBar title="Fallback name" manage={manage()} />);
    openRenameSheet();
    expect(renameInput().value).toBe("");
    // The title still stands while there is no row - a failed read is not a blank name.
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Fallback name");
  });

  it("refuses Save for an empty or whitespace-only name", () => {
    render(<SessionAppBar title="throwaway" manage={manage({ session: row("Old name") })} />);
    openRenameSheet();
    // The sheet opens prefilled with the current name, so Save starts armed; clearing the box disarms it.
    fireEvent.change(renameInput(), { target: { value: "" } });
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(renameInput(), { target: { value: "   " } });
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);
    expect(renameMock).not.toHaveBeenCalled();
  });

  it("sends the TRIMMED name, and on success closes the sheet and changes the title at once", async () => {
    renameMock.mockResolvedValueOnce(true);
    const { rerender } = render(
      <SessionAppBar title="throwaway" manage={manage({ session: row("Old name") })} />,
    );
    openRenameSheet();
    fireEvent.change(renameInput(), { target: { value: "  Mobile Rename - Worker - menu  " } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(renameMock).toHaveBeenCalledWith("Mobile Rename - Worker - menu");

    // The Gateway's applied name is what the hook adopted on the row, and the title renders the row -
    // it must change on the SAME render the sheet closes, not on the next poll or a remount.
    rerender(
      <SessionAppBar
        title="throwaway"
        manage={manage({ session: row("Mobile Rename - Worker - menu") })}
      />,
    );
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Mobile Rename - Worker - menu");
  });

  it("keeps the sheet open with the failure INSIDE it when the Gateway refuses the name", async () => {
    renameMock.mockResolvedValueOnce(false);
    // The hook surfaces the Gateway's own sentence on `error` - passed here exactly as it would be.
    const errorManage = manage({ session: row("Old name"), error: "The Director on SORENLAPTOP could not be reached." });
    render(<SessionAppBar title="throwaway" manage={errorManage} />);
    openRenameSheet();
    fireEvent.change(renameInput(), { target: { value: "New name" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(renameMock).toHaveBeenCalled());
    // Still open, and the Gateway's sentence is in the card above the Save it explains - not on the
    // banner behind a modal that declares aria-modal.
    expect(screen.getByRole("dialog", { name: "Rename session" })).toBeTruthy();
    expect(screen.getByRole("alert").textContent).toContain("SORENLAPTOP");
    // The banner behind the sheet stays quiet: one explanation, in the one place the operator is looking.
    expect(screen.queryByText("The Director on SORENLAPTOP could not be reached.")).toBeTruthy();
  });

  it("backs out on Cancel without sending anything, and Enter saves, Escape backs out", async () => {
    renameMock.mockResolvedValue(true);
    render(<SessionAppBar title="throwaway" manage={manage({ session: row("Old name") })} />);
    openRenameSheet();
    fireEvent.change(renameInput(), { target: { value: "Typed but abandoned" } });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(renameMock).not.toHaveBeenCalled();

    // Enter in the box is what the keyboard's Go key sends - it saves.
    openRenameSheet();
    fireEvent.change(renameInput(), { target: { value: "Enter saves" } });
    fireEvent.keyDown(renameInput(), { key: "Enter" });
    await waitFor(() => expect(renameMock).toHaveBeenCalledWith("Enter saves"));

    // Escape backs out.
    openRenameSheet();
    fireEvent.keyDown(renameInput(), { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("clears the error from a previous attempt when the sheet opens again - a fresh question starts clean", () => {
    render(
      <SessionAppBar
        title="throwaway"
        manage={manage({ session: row("Old name"), error: "left over from the failed attempt" })}
      />,
    );
    openRenameSheet();
    expect(setErrorMock).toHaveBeenCalledWith(null);
  });
});
