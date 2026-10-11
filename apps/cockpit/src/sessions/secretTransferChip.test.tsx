// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";

// A session that asked for a secret transfer carries it on itself (Secret Handoff, issue #2943, phase 5): the asking
// session's roster card shows the amber key and no other card does; the open session's chip opens the shared approval
// card; an answer given there is recorded as "badge"; and with nothing waiting nothing is drawn.

const ASKER = "11111111-2222-3333-4444-555555555555";
const OTHER = "66666666-7777-8888-9999-000000000000";

const TRANSFER = {
  transferId: "tr-1",
  entry: "vercel-bypass",
  targetName: "vercel-bypass",
  fromMachine: "SOREN_NORTH",
  toMachine: "devthrottle-mac-mini",
  replace: false,
  askedBySessionId: ASKER,
  askedBy: 'Session 042 "Rig - Developer"',
  reason: "The preview deploy needs it",
  state: "waiting",
  createdAtUtc: "2026-10-10T11:58:00Z",
  expiresAtUtc: "2099-10-10T12:13:00Z",
  summary: "vercel-bypass from SOREN_NORTH to devthrottle-mac-mini",
  replaceNote: null,
  statusText: 'Waiting for your answer. Asked by Session 042 "Rig - Developer".',
  canAnswer: true,
};

const { listSecretTransfers, answerSecretTransfer } = vi.hoisted(() => ({
  listSecretTransfers: vi.fn(),
  answerSecretTransfer: vi.fn(),
}));

vi.mock("@devthrottle/client-core/secrets/secretTransfersClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/secrets/secretTransfersClient");
  return { ...actual, listSecretTransfers, answerSecretTransfer };
});

import { SecretTransfersProvider } from "@devthrottle/client-core/secrets/SecretTransfers";
import { RosterSecretBadge, SecretTransferChip } from "./SecretTransferChip";

function renderScreen(openSession: string = ASKER) {
  return render(
    <SecretTransfersProvider>
      <div data-testid={`card-${ASKER}`}>
        <RosterSecretBadge sessionId={ASKER} />
      </div>
      <div data-testid={`card-${OTHER}`}>
        <RosterSecretBadge sessionId={OTHER} />
      </div>
      <SecretTransferChip sessionId={openSession} />
    </SecretTransfersProvider>,
  );
}

describe("secret transfer badge on the asking session", () => {
  beforeEach(() => {
    listSecretTransfers.mockReset().mockResolvedValue({ transfers: [TRANSFER], finishedWithinHours: 24 });
    answerSecretTransfer.mockReset();
  });
  afterEach(() => cleanup());

  it("RosterSecretBadge_MarksTheAskingSessionOnly", async () => {
    renderScreen();
    await waitFor(() => expect(screen.getByTestId(`card-${ASKER}`).querySelector("[data-testid=roster-secret-badge]")?.textContent).toBe("1"));
    expect(screen.getByTestId(`card-${OTHER}`).textContent).toBe("");
  });

  it("SecretTransferChip_Click_OpensTheSharedCard", async () => {
    renderScreen();
    fireEvent.click(await screen.findByTestId("secret-transfer-chip"));
    const card = screen.getByTestId("secret-transfer-card");
    expect(card.textContent).toContain("vercel-bypass from SOREN_NORTH to devthrottle-mac-mini");
    expect(card.textContent).toContain('"The preview deploy needs it"');
  });

  it.each([
    ["Approve", true],
    ["Deny", false],
  ] as const)("'%s' from the chip is answered from the badge", async (label, approve) => {
    answerSecretTransfer.mockResolvedValue({ transfer: { ...TRANSFER, canAnswer: false, state: approve ? "approved" : "denied" }, note: "" });
    renderScreen();
    fireEvent.click(await screen.findByTestId("secret-transfer-chip"));
    listSecretTransfers.mockResolvedValue({ transfers: [{ ...TRANSFER, canAnswer: false }], finishedWithinHours: 24 });
    fireEvent.click(screen.getByRole("button", { name: label }));

    await waitFor(() => expect(answerSecretTransfer).toHaveBeenCalledWith("tr-1", approve, "badge"));
    await waitFor(() => expect(screen.queryByTestId("secret-transfer-chip")).toBeNull());
  });

  it("SecretTransferChip_AnotherSession_GetsNoChip", async () => {
    renderScreen(OTHER);
    await waitFor(() => expect(screen.getByTestId(`card-${ASKER}`).textContent).toBe("1"));
    expect(screen.queryByTestId("secret-transfer-chip")).toBeNull();
  });

  it("NothingWaits_DrawsNoBadgeAndNoChip", async () => {
    listSecretTransfers.mockResolvedValue({ transfers: [], finishedWithinHours: 24 });
    renderScreen();
    await waitFor(() => expect(listSecretTransfers).toHaveBeenCalled());
    expect(screen.queryByTestId("secret-transfer-chip")).toBeNull();
    expect(screen.queryByTestId("roster-secret-badge")).toBeNull();
  });
});
