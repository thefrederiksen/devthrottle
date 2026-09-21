// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// Issue #2167 - Compact. It MOVED from the permanent driver row under the conversation into the session
// menu, under a "Context" heading (owner ruling, 2026-09-20), because it is rare and it sits next to one
// that destroys the conversation. Everything these tests hold down is about the verb, not its address,
// so they moved with it:
//
// Compaction summarizes the conversation and carries on; clearing throws it away. They sit next to each
// other, so the tests that matter here are the ones about the DIFFERENCE: compaction never clears, the
// follow-up only goes to a driver that can time it, and a compaction nobody watched is never reported as
// "Compacted".

const { sendCompactContext, sendClearContext } = vi.hoisted(() => ({
  sendCompactContext: vi.fn(async () => ({
    submitted: true,
    compactionObserved: true,
    waitedSeconds: 41,
    continued: true,
    detail: "Compacted in 41 seconds, then sent the follow-up.",
  })),
  sendClearContext: vi.fn(async () => {}),
}));

vi.mock("@devthrottle/client-core/api/client", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/api/client");
  return {
    ...actual,
    sendCompactContext,
    sendClearContext,
    sendEscape: vi.fn(async () => {}),
    sendInterrupt: vi.fn(async () => {}),
    sendHistoryPicker: vi.fn(async () => {}),
    holdSession: () => Promise.resolve({ onHold: false, pending: false }),
    getHandover: () => Promise.resolve(null),
  };
});

// The lengths cache would otherwise reach for the Gateway on mount. Null is a real state and the menu is
// fully usable in it.
vi.mock("@devthrottle/client-core/settings/snoozeOptions", () => ({
  useSnoozeOptions: () => null,
}));

import { SessionMenu } from "./SessionMenu";
import { StopSessionProvider } from "./StopSessionProvider";

const SESSION = "11111111-2222-3333-4444-555555555555";
const CLAUDE_CAPS = ["Cancel", "Interrupt", "ClearContext", "CompactContext", "CompactCompletionReport"];

function session(caps: string[]): SessionDto {
  return {
    sessionId: SESSION,
    directorId: "d1",
    machineName: "SORENLAPTOP",
    repoPath: "D:/Repos/scratch",
    agent: "ClaudeCode",
    activityState: "Waiting",
    createdAt: "2026-09-09T10:00:00Z",
    sortOrder: 0,
    name: "throwaway",
    driverCapabilities: caps,
  } as unknown as SessionDto;
}

function renderMenu(caps: string[] = CLAUDE_CAPS) {
  return render(
    <StopSessionProvider>
      <SessionMenu session={session(caps)} />
    </StopSessionProvider>,
  );
}

function openMenu() {
  fireEvent.click(screen.getByLabelText("Session menu"));
}

/** The menu's own item, distinguished from the dialog's confirm button, which legitimately reads alike. */
function compactItem(): HTMLElement {
  const match = document.querySelector<HTMLElement>("button.session-menu-item[title^='Summarize']");
  if (match === null) throw new Error("the session menu has no Compact item");
  return match;
}

function dialogConfirmButton(): HTMLElement {
  const match = document.querySelector<HTMLElement>(".ui-confirm-actions button:last-of-type");
  if (match === null) throw new Error("no confirmation dialog is open");
  return match;
}

async function clickCompactAndConfirm() {
  openMenu();
  fireEvent.click(compactItem());
  await waitFor(() => expect(document.querySelector(".ui-confirm")).toBeTruthy());
  fireEvent.click(dialogConfirmButton());
}

describe("Compact, in the session menu", () => {
  beforeEach(() => {
    // This project runs vitest without globals, so testing-library's automatic cleanup is not
    // registered - without this, each render leaks into the next test's document.
    cleanup();
    vi.clearAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  it("is shown for a driver that declares compaction, and hidden for one that does not", () => {
    const { unmount } = renderMenu();
    openMenu();
    expect(compactItem()).toBeTruthy();
    unmount();

    renderMenu(["Cancel", "ClearContext"]);
    openMenu();
    expect(document.querySelector("button.session-menu-item[title^='Summarize']")).toBeNull();
  });

  it("asks before compacting, and sends nothing if the question is not answered", () => {
    renderMenu();
    openMenu();

    fireEvent.click(compactItem());

    expect(sendCompactContext).not.toHaveBeenCalled();
  });

  // Compact stops there. A person clicking it has a composer in front of them and can say what happens
  // next; putting words into their session unasked is not the verb's business. Compact-AND-CONTINUE is a
  // separate verb on the command line, for an agent rescuing a stuck session with nobody at its keyboard.
  it("compacts and sends the session nothing", async () => {
    renderMenu();

    await clickCompactAndConfirm();

    await waitFor(() => expect(sendCompactContext).toHaveBeenCalledWith(SESSION));
  });

  it("sends nothing even for a driver that could time a follow-up", async () => {
    renderMenu();

    await clickCompactAndConfirm();

    await waitFor(() => expect(sendCompactContext).toHaveBeenCalled());
    // One argument only: no continuation, whatever the driver is capable of.
    expect(sendCompactContext.mock.calls[0]).toHaveLength(1);
  });

  it("never clears when asked to compact", async () => {
    renderMenu();

    await clickCompactAndConfirm();

    await waitFor(() => expect(sendCompactContext).toHaveBeenCalled());
    expect(sendClearContext).not.toHaveBeenCalled();
  });

  it("says what the dialog promises - nothing is sent to the session", async () => {
    renderMenu();
    openMenu();

    fireEvent.click(compactItem());

    await waitFor(() => expect(document.querySelector(".ui-confirm")).toBeTruthy());
    const dialog = document.querySelector(".ui-confirm");
    expect(dialog?.textContent).toMatch(/nothing is sent to it/);
    expect(dialog?.textContent).not.toMatch(/cannot be undone/);
  });

  it("shows the Gateway's own sentence, verbatim", async () => {
    renderMenu();

    await clickCompactAndConfirm();

    expect(await screen.findByText("Compacted in 41 seconds, then sent the follow-up.")).toBeTruthy();
  });

  // A compaction that was submitted but never watched is NOT a compaction anyone can vouch for. The
  // Gateway says so in its own sentence; rendering that verbatim is what keeps the screen honest, and
  // composing a cheerful message here is exactly how "Compacted" ends up on screen for one nobody saw.
  it("does not claim a compaction that was never observed", async () => {
    sendCompactContext.mockResolvedValueOnce({
      submitted: true,
      compactionObserved: false,
      waitedSeconds: 0,
      continued: false,
      detail: "Compaction submitted. Codex cannot report when it finishes, so this was not watched.",
    });
    renderMenu(["ClearContext", "CompactContext"]);

    await clickCompactAndConfirm();

    const status = await screen.findByText(/Compaction submitted/);
    expect(status.textContent).not.toMatch(/^Compacted/);
  });

  // The two verbs live side by side in one menu now, so the ONE thing that must never blur is which of
  // them is the destructive one. Clear carries the danger class; Compact does not.
  it("draws Clear context as the dangerous one and Compact as an ordinary item", () => {
    renderMenu();
    openMenu();

    const clear = document.querySelector<HTMLElement>("button.session-menu-item[title^='Reset the conversation']");
    expect(clear?.className).toMatch(/danger/);
    expect(compactItem().className).not.toMatch(/danger/);
  });
});
