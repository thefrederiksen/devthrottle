// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";

// A session that asked for a secret transfer carries it on itself (Secret Handoff, issue #2943, phase 5), read from the
// shared Alerts list (the Alerts mission): the asking session's roster card shows the amber key and no other card does;
// the open session's chip opens the transfer as the Alerts tray shows it; an answer given there is recorded as "badge";
// and with nothing waiting nothing is drawn.

const ASKER = "11111111-2222-3333-4444-555555555555";
const OTHER = "66666666-7777-8888-9999-000000000000";

const { listAlerts, answerSecretTransfer } = vi.hoisted(() => ({
  listAlerts: vi.fn(),
  answerSecretTransfer: vi.fn(),
}));

vi.mock("@devthrottle/client-core/alerts/alertsClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/alerts/alertsClient");
  return { ...actual, listAlerts };
});
vi.mock("@devthrottle/client-core/secrets/secretTransfersClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/secrets/secretTransfersClient");
  return { ...actual, answerSecretTransfer };
});

import { AlertsProvider } from "@devthrottle/client-core/alerts/AlertsProvider";
import { secretAlert } from "@devthrottle/client-core/alerts/alertsFixtures.testkit";
import { RosterSecretBadge, SecretTransferChip } from "./SecretTransferChip";

function waiting() {
  return secretAlert(
    { id: "secret-transfer:tr-1" },
    { transferId: "tr-1", askedBySessionId: ASKER, reason: "The preview deploy needs it", canAnswer: true },
  );
}

function answered() {
  const a = waiting();
  return { ...a, needsAnswer: false, status: "Approved", detail: { ...a.detail, canAnswer: false, state: "approved" } };
}

function renderScreen(openSession: string = ASKER) {
  return render(
    <AlertsProvider>
      <div data-testid={`card-${ASKER}`}>
        <RosterSecretBadge sessionId={ASKER} />
      </div>
      <div data-testid={`card-${OTHER}`}>
        <RosterSecretBadge sessionId={OTHER} />
      </div>
      <SecretTransferChip sessionId={openSession} />
    </AlertsProvider>,
  );
}

describe("secret transfer badge on the asking session", () => {
  beforeEach(() => {
    listAlerts.mockReset().mockResolvedValue({ alerts: [waiting()], needsAnswerCount: 1 });
    answerSecretTransfer.mockReset();
  });
  afterEach(() => cleanup());

  it("RosterSecretBadge_MarksTheAskingSessionOnly", async () => {
    renderScreen();
    await waitFor(() => expect(screen.getByTestId(`card-${ASKER}`).querySelector("[data-testid=roster-secret-badge]")?.textContent).toBe("1"));
    expect(screen.getByTestId(`card-${OTHER}`).textContent).toBe("");
  });

  it("SecretTransferChip_Click_OpensTheTransferAsTheTrayShowsIt", async () => {
    renderScreen();
    fireEvent.click(await screen.findByTestId("secret-transfer-chip"));
    const detail = screen.getByTestId("alert-detail");
    expect(detail.textContent).toContain("Move vercel-bypass to devthrottle-mac-mini?");
    expect(detail.textContent).toContain('"The preview deploy needs it"');
  });

  it.each([
    ["Approve", true],
    ["Deny", false],
  ] as const)("'%s' from the chip is answered from the badge", async (label, approve) => {
    answerSecretTransfer.mockResolvedValue({ transfer: answered().detail, note: "" });
    renderScreen();
    fireEvent.click(await screen.findByTestId("secret-transfer-chip"));
    listAlerts.mockResolvedValue({ alerts: [answered()], needsAnswerCount: 0 });
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
    listAlerts.mockResolvedValue({ alerts: [answered()], needsAnswerCount: 0 });
    renderScreen();
    await waitFor(() => expect(listAlerts).toHaveBeenCalled());
    expect(screen.queryByTestId("secret-transfer-chip")).toBeNull();
    expect(screen.queryByTestId("roster-secret-badge")).toBeNull();
  });
});
