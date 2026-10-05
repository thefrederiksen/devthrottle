// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// "Let this session talk to..." (issue #3548). The owner sets up a link and the sessions decide what to send.
// What these tests hold down: the menu offers it; the request carries THIS session as the sender and the chosen
// session and amount; every line names the two sessions next to the Gateway's own summary (which never names
// them); a refusal is the Gateway's sentence; a list that fails to reload never hides a click that worked; and
// a removal says what the Gateway says happened. The summaries below are the Gateway's real ones.

const SELF = "11111111-2222-3333-4444-555555555555";
const OTHER = "66666666-7777-8888-9999-000000000000";
const THIRD = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

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

function link(id: string, recipient: string, amount: string, summary: string, status = "live") {
  return {
    linkId: id,
    senderSessionId: SELF,
    recipientSessionId: recipient,
    amount,
    status,
    summary,
    setUpBy: "owner",
    setUpAtUtc: "2026-10-05T10:00:00Z",
  };
}

const TO_OTHER = link("0123456789abcdef0123456789abcdef", OTHER, "once-with-reply", "One message and a reply. Live.");
const TO_THIRD = link("fedcba9876543210fedcba9876543210", THIRD, "once-with-reply", "One message and a reply. Live.");

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
    sessions: [session(SELF, "Investigator"), session(OTHER, "Coordinator"), session(THIRD, "Reviewer")],
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

const status = () => screen.getByRole("status").textContent ?? "";

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
    expect(options).toContain("Reviewer (aaaaaaaa)");
    expect(options).not.toContain("Investigator (11111111)");
    await waitFor(() => expect(screen.getByText("None.")).toBeTruthy());
  });

  it("sets up a link from this session, with the chosen session and amount, and says which two it joins", async () => {
    const ongoing = link(TO_OTHER.linkId, OTHER, "ongoing", "Talk as much as needed, both ways. Live.");
    setUpMessageLink.mockResolvedValue({ link: ongoing, replaced: [] });
    openDialog();
    const setUp = screen.getByRole("button", { name: "Set up link" }) as HTMLButtonElement;
    expect(setUp.disabled).toBe(true);

    fireEvent.change(screen.getByRole("combobox"), { target: { value: OTHER } });
    fireEvent.click(screen.getByRole("radio", { name: /As much as they need/ }));
    fireEvent.click(setUp);

    await waitFor(() => expect(setUpMessageLink).toHaveBeenCalledWith(SELF, OTHER, "ongoing"));
    // The status line itself - not the list, which reloads empty here - carries the names and the summary.
    await waitFor(() =>
      expect(status()).toBe(
        "Set up. Investigator (11111111) and Coordinator (66666666): Talk as much as needed, both ways. Live.",
      ),
    );
  });

  it("tells two links with the same summary apart by the session each one is with", async () => {
    listMessageLinks.mockResolvedValue({ links: [TO_OTHER, TO_THIRD], stoppedWithinDays: 30 });
    openDialog();
    await waitFor(() =>
      expect(screen.getByText("Investigator (11111111) to Coordinator (66666666): One message and a reply. Live.")).toBeTruthy(),
    );
    expect(screen.getByText("Investigator (11111111) to Reviewer (aaaaaaaa): One message and a reply. Live.")).toBeTruthy();
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
    await waitFor(() => expect(screen.getByText(/may already message each other\. No link was set up\./)).toBeTruthy());
  });

  it("never hides a set up that worked behind a list that failed to reload", async () => {
    setUpMessageLink.mockResolvedValue({ link: TO_OTHER, replaced: [] });
    openDialog();
    await waitFor(() => expect(screen.getByText("None.")).toBeTruthy());
    listMessageLinks.mockRejectedValue(new GatewayError(503, "list failed", { reason: "The Gateway is restarting." }));
    fireEvent.change(screen.getByRole("combobox"), { target: { value: OTHER } });
    fireEvent.click(screen.getByRole("button", { name: "Set up link" }));

    await waitFor(() => expect(screen.getByText(/The Gateway is restarting/)).toBeTruthy());
    expect(status()).toBe("Set up. Investigator (11111111) to Coordinator (66666666): One message and a reply. Live.");
  });

  it("removes a live link and says what the Gateway says happened", async () => {
    listMessageLinks.mockResolvedValue({ links: [TO_OTHER], stoppedWithinDays: 30 });
    // A message used the link a moment before: the Gateway answers with it as it stands, "Used.", not removed.
    removeMessageLink.mockResolvedValue({ ...TO_OTHER, status: "used", summary: "One message and a reply. Used." });
    openDialog();
    await waitFor(() => expect(screen.getByRole("button", { name: "Remove" })).toBeTruthy());

    listMessageLinks.mockResolvedValue({ links: [], stoppedWithinDays: 30 });
    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(removeMessageLink).toHaveBeenCalledWith(TO_OTHER.linkId));
    await waitFor(() =>
      expect(status()).toBe("Investigator (11111111) to Coordinator (66666666): One message and a reply. Used."),
    );
    await waitFor(() => expect(screen.getByText("None.")).toBeTruthy());
  });
});
