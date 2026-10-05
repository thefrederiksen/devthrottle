// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// "Let this session talk to..." (issue #3548). The owner sets up a link and the sessions decide what to send.
// What these tests hold down: the menu offers it, the request carries THIS session as the sender and the chosen
// session and amount, the Gateway's own sentence is what the owner reads after either outcome, and a live link
// can be removed from the same place.

const SELF = "11111111-2222-3333-4444-555555555555";
const OTHER = "66666666-7777-8888-9999-000000000000";

function session(id: string, name: string): SessionDto {
  return {
    sessionId: id,
    directorId: "d1",
    machineName: "SORENLAPTOP",
    repoPath: "D:/Repos/scratch",
    agent: "ClaudeCode",
    activityState: "Waiting",
    createdAt: "2026-10-05T10:00:00Z",
    sortOrder: 0,
    name,
    driverCapabilities: [],
  } as unknown as SessionDto;
}

const LIVE = {
  linkId: "0123456789abcdef0123456789abcdef",
  senderSessionId: SELF,
  recipientSessionId: OTHER,
  amount: "ongoing",
  status: "live",
  summary: "Investigator and Coordinator may message each other as much as they need.",
  setUpBy: "owner",
  setUpAtUtc: "2026-10-05T10:00:00Z",
};

const { listMessageLinks, setUpMessageLink, removeMessageLink } = vi.hoisted(() => ({
  listMessageLinks: vi.fn(),
  setUpMessageLink: vi.fn(),
  removeMessageLink: vi.fn(),
}));

vi.mock("@devthrottle/client-core/fleet/messageLinksClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/fleet/messageLinksClient");
  return { ...actual, listMessageLinks, setUpMessageLink, removeMessageLink };
});

vi.mock("@devthrottle/client-core/fleet/rosterStore", () => ({
  useSharedRoster: () => ({
    sessions: [session(SELF, "Investigator"), session(OTHER, "Coordinator")],
    directors: [],
    error: null,
    refreshNow: () => {},
  }),
}));

vi.mock("@devthrottle/client-core/settings/snoozeOptions", () => ({
  useSnoozeOptions: () => null,
}));

import { SessionMenu } from "./SessionMenu";
import { StopSessionProvider } from "./StopSessionProvider";
import { GatewayError } from "@devthrottle/client-core/api/client";

function openDialog() {
  render(
    <StopSessionProvider>
      <SessionMenu session={session(SELF, "Investigator")} />
    </StopSessionProvider>,
  );
  fireEvent.click(screen.getByLabelText("Session menu"));
  fireEvent.click(screen.getByRole("menuitem", { name: "Let this session talk to..." }));
}

describe("Let this session talk to...", () => {
  beforeEach(() => {
    listMessageLinks.mockReset().mockResolvedValue({ links: [], stoppedWithinDays: 30 });
    setUpMessageLink.mockReset();
    removeMessageLink.mockReset();
  });
  afterEach(() => cleanup());

  it("offers every other session, never this one", async () => {
    openDialog();
    const options = screen.getAllByRole("option").map((o) => o.textContent);
    expect(options).toContain("Coordinator (66666666)");
    expect(options).not.toContain("Investigator (11111111)");
    await waitFor(() => expect(screen.getByText("None.")).toBeTruthy());
  });

  it("sets up a link from this session, with the chosen session and amount, and shows the Gateway's summary", async () => {
    setUpMessageLink.mockResolvedValue({ link: LIVE, replaced: [] });
    openDialog();
    const setUp = screen.getByRole("button", { name: "Set up link" }) as HTMLButtonElement;
    expect(setUp.disabled).toBe(true);

    fireEvent.change(screen.getByRole("combobox"), { target: { value: OTHER } });
    fireEvent.click(screen.getByRole("radio", { name: /As much as they need/ }));
    listMessageLinks.mockResolvedValue({ links: [LIVE], stoppedWithinDays: 30 });
    fireEvent.click(setUp);

    await waitFor(() => expect(setUpMessageLink).toHaveBeenCalledWith(SELF, OTHER, "ongoing"));
    await waitFor(() => expect(screen.getAllByText(LIVE.summary).length).toBeGreaterThan(0));
  });

  it("shows the Gateway's refusal in its own words", async () => {
    setUpMessageLink.mockRejectedValue(
      new GatewayError(409, "set up failed", {
        reason: "One of these sessions started the other, so they may already message each other. No link was set up.",
      }),
    );
    openDialog();
    fireEvent.change(screen.getByRole("combobox"), { target: { value: OTHER } });
    fireEvent.click(screen.getByRole("button", { name: "Set up link" }));
    await waitFor(() => expect(screen.getByText(/may already message each other/)).toBeTruthy());
  });

  it("lists this session's live links and removes one", async () => {
    listMessageLinks.mockResolvedValue({ links: [LIVE], stoppedWithinDays: 30 });
    removeMessageLink.mockResolvedValue({ ...LIVE, status: "removed" });
    openDialog();
    await waitFor(() => expect(screen.getByText(LIVE.summary)).toBeTruthy());

    listMessageLinks.mockResolvedValue({ links: [{ ...LIVE, status: "removed" }], stoppedWithinDays: 30 });
    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(removeMessageLink).toHaveBeenCalledWith(LIVE.linkId));
    await waitFor(() => expect(screen.getByText("None.")).toBeTruthy());
  });
});
