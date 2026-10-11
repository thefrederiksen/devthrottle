// @vitest-environment jsdom
// The secret transfer approval card (Secret Handoff, issue #2943, phase 5), rendered against a mocked client.
//
// What these prove that a Gateway test cannot: the card SHOWS the Gateway's sentences verbatim, offers Approve and Deny
// only while the Gateway says the transfer can be answered, SENDS the answer with the place the shell names, keeps the
// outcome in view once answered, shows a refusal in the Gateway's words - and carries no value, because none reaches it.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { SecretTransfer } from "./secretTransfersClient";

const { listSecretTransfers, answerSecretTransfer } = vi.hoisted(() => ({
  listSecretTransfers: vi.fn(),
  answerSecretTransfer: vi.fn(),
}));

vi.mock("./secretTransfersClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("./secretTransfersClient");
  return { ...actual, listSecretTransfers, answerSecretTransfer };
});

import { SecretTransfersPanel, SecretTransfersProvider, useSecretTransfers } from "./SecretTransfers";
import { GatewayError } from "../api/client";

const NOW = Date.parse("2026-10-10T12:00:00Z");
const ASKER = "11111111-2222-3333-4444-555555555555";

function transfer(overrides: Partial<SecretTransfer> = {}): SecretTransfer {
  return {
    transferId: "tr-1",
    entry: "vercel-bypass",
    targetName: "vercel-bypass",
    fromMachine: "SOREN_NORTH",
    toMachine: "devthrottle-mac-mini",
    replace: true,
    askedBySessionId: ASKER,
    askedBy: 'Session 042 "Rig - Developer"',
    reason: "The preview deploy needs it",
    state: "waiting",
    createdAtUtc: "2026-10-10T11:58:00Z",
    expiresAtUtc: "2026-10-10T12:13:00Z",
    summary: "vercel-bypass from SOREN_NORTH to devthrottle-mac-mini",
    replaceNote: "Replaces the entry already on devthrottle-mac-mini.",
    statusText: 'Waiting for your answer. Asked by Session 042 "Rig - Developer".',
    canAnswer: true,
    ...overrides,
  };
}

function renderPanel(where: "phone" | "cockpit" = "phone", emptyText?: string) {
  return render(
    <SecretTransfersProvider now={() => NOW}>
      <SecretTransfersPanel where={where} emptyText={emptyText} />
    </SecretTransfersProvider>,
  );
}

describe("secret transfer approval card", () => {
  beforeEach(() => {
    listSecretTransfers.mockReset().mockResolvedValue({ transfers: [transfer()], finishedWithinHours: 24 });
    answerSecretTransfer.mockReset();
  });
  afterEach(() => cleanup());

  it("SecretTransferCard_Waiting_ShowsTheGatewaysSentencesVerbatim", async () => {
    renderPanel();
    const card = await screen.findByTestId("secret-transfer-card");
    expect(card.textContent).toContain("vercel-bypass from SOREN_NORTH to devthrottle-mac-mini");
    expect(card.textContent).toContain("Replaces the entry already on devthrottle-mac-mini.");
    expect(card.textContent).toContain('"The preview deploy needs it"');
    expect(card.textContent).toContain('Asked by Session 042 "Rig - Developer"');
    expect(card.textContent).toContain('Waiting for your answer. Asked by Session 042 "Rig - Developer".');
    expect(card.textContent).toContain("13 minutes left");
    expect(screen.getByRole("button", { name: "Approve" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Deny" })).toBeTruthy();
  });

  it("SecretTransferCard_CannotAnswer_OffersNoApproveOrDeny", async () => {
    listSecretTransfers.mockResolvedValue({ transfers: [transfer({ canAnswer: false, state: "approved" })], finishedWithinHours: 24 });
    renderPanel("phone", "Nothing is waiting for your answer.");
    await screen.findByText("Nothing is waiting for your answer.");
    expect(screen.queryByRole("button", { name: "Approve" })).toBeNull();
  });

  it("SecretTransfersPanel_NothingWaits_DrawsNothing", async () => {
    listSecretTransfers.mockResolvedValue({ transfers: [], finishedWithinHours: 24 });
    const { container } = renderPanel();
    await waitFor(() => expect(listSecretTransfers).toHaveBeenCalled());
    expect(container.innerHTML).toBe("");
  });

  it.each([
    ["Approve", true, "phone"],
    ["Deny", false, "phone"],
    ["Approve", true, "cockpit"],
  ] as const)("'%s' answers approve=%s with where=%s, and the outcome stays in view", async (label, approve, where) => {
    const answered = transfer({ canAnswer: false, state: approve ? "approved" : "denied", statusText: approve ? "Approved on the phone. Delivering." : "Denied on the phone. Nothing was moved." });
    answerSecretTransfer.mockResolvedValue({ transfer: answered, note: "" });
    renderPanel(where);
    fireEvent.click(await screen.findByRole("button", { name: label }));
    listSecretTransfers.mockResolvedValue({ transfers: [answered], finishedWithinHours: 24 });

    await waitFor(() => expect(answerSecretTransfer).toHaveBeenCalledWith("tr-1", approve, where));
    await waitFor(() => expect(screen.getByTestId("secret-transfer-card").textContent).toContain(answered.statusText));
    expect(screen.queryByRole("button", { name: "Approve" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(screen.queryByTestId("secret-transfer-card")).toBeNull();
  });

  it("SecretTransferCard_Refused_ShowsTheGatewaysSentence", async () => {
    answerSecretTransfer.mockRejectedValue(
      new GatewayError(409, "answer failed", { reason: "This transfer was already approved in the cc-secrets window. Nothing changed." }),
    );
    renderPanel();
    fireEvent.click(await screen.findByRole("button", { name: "Approve" }));
    await waitFor(() => expect(screen.getByText(/already approved in the cc-secrets window\. Nothing changed\./)).toBeTruthy());
  });

  it("SecretTransfersPanel_ReadFails_SaysSoInTheCard", async () => {
    listSecretTransfers.mockRejectedValue(new GatewayError(503, "down", { reason: "The Gateway is restarting." }));
    renderPanel();
    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("Could not read the secret transfers"));
  });

  it("waitingFrom_ListsOnlyWaitingTransfersAskedByThatSession", async () => {
    listSecretTransfers.mockResolvedValue({
      transfers: [
        transfer({ transferId: "a" }),
        transfer({ transferId: "b", askedBySessionId: "other" }),
        transfer({ transferId: "c", canAnswer: false }),
      ],
      finishedWithinHours: 24,
    });
    function Probe() {
      const ids = useSecretTransfers().waitingFrom(ASKER.toUpperCase()).map((t) => t.transferId);
      return <span data-testid="probe">{ids.join(",")}</span>;
    }
    render(
      <SecretTransfersProvider>
        <Probe />
      </SecretTransfersProvider>,
    );
    await waitFor(() => expect(screen.getByTestId("probe").textContent).toBe("a"));
  });
});
