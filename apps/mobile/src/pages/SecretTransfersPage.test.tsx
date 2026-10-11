// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The page a secret transfer's push opens (Secret Handoff, issue #2943, phase 5): the shared approval card, answered
// "on the phone", and a plain line when nothing waits.

const { listSecretTransfers, answerSecretTransfer } = vi.hoisted(() => ({
  listSecretTransfers: vi.fn(),
  answerSecretTransfer: vi.fn(),
}));

vi.mock("@devthrottle/client-core/secrets/secretTransfersClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/secrets/secretTransfersClient");
  return { ...actual, listSecretTransfers, answerSecretTransfer };
});

import { SecretTransfers } from "./SecretTransfers";

const TRANSFER = {
  transferId: "tr-1",
  entry: "vercel-bypass",
  targetName: "vercel-bypass",
  fromMachine: "SOREN_NORTH",
  toMachine: "devthrottle-mac-mini",
  replace: false,
  askedBySessionId: null,
  askedBy: "The cc-secrets window or a terminal on devthrottle-mac-mini",
  reason: "",
  state: "waiting",
  createdAtUtc: "2026-10-10T11:58:00Z",
  expiresAtUtc: "2099-10-10T12:13:00Z",
  summary: "vercel-bypass from SOREN_NORTH to devthrottle-mac-mini",
  replaceNote: null,
  statusText: "Waiting for your answer. Asked by The cc-secrets window or a terminal on devthrottle-mac-mini.",
  canAnswer: true,
};

function renderPage() {
  return render(
    <MemoryRouter>
      <SecretTransfers />
    </MemoryRouter>,
  );
}

describe("phone secret transfers page", () => {
  beforeEach(() => {
    listSecretTransfers.mockReset().mockResolvedValue({ transfers: [TRANSFER], finishedWithinHours: 24 });
    answerSecretTransfer.mockReset().mockResolvedValue({ transfer: { ...TRANSFER, canAnswer: false, state: "approved" }, note: "" });
  });
  afterEach(() => cleanup());

  it("SecretTransfers_Approve_IsAnsweredOnThePhone", async () => {
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Approve" }));
    await waitFor(() => expect(answerSecretTransfer).toHaveBeenCalledWith("tr-1", true, "phone"));
  });

  it("SecretTransfers_NothingWaits_SaysSo", async () => {
    listSecretTransfers.mockResolvedValue({ transfers: [], finishedWithinHours: 24 });
    renderPage();
    expect(await screen.findByText("Nothing is waiting for your answer.")).toBeTruthy();
  });
});
