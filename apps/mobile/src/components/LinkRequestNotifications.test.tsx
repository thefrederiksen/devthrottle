// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, waitFor, cleanup } from "@testing-library/react";

// A message request's push notification is closed once the Gateway lists the request as answered (issue #4373). The
// list is the Alerts list now (the Alerts mission): the gated layout mounts the one AlertsProvider and this closer
// inside it, so a request answered at the Director or in the Cockpit leaves the lock screen wherever the phone is.

const { listAlerts } = vi.hoisted(() => ({ listAlerts: vi.fn() }));

vi.mock("@devthrottle/client-core/alerts/alertsClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/alerts/alertsClient");
  return { ...actual, listAlerts };
});

import { AlertsProvider } from "@devthrottle/client-core/alerts/AlertsProvider";
import { messageAlert, secretAlert } from "@devthrottle/client-core/alerts/alertsFixtures.testkit";
import { CloseAnsweredLinkRequestNotifications, closeAnsweredNotifications } from "./LinkRequestNotifications";

function stubNotifications(tags: string[]) {
  const shown = tags.map((tag) => ({ tag, close: vi.fn() }));
  const registration = { getNotifications: vi.fn(async () => shown) };
  vi.stubGlobal("navigator", { serviceWorker: { getRegistration: vi.fn(async () => registration) } });
  return shown;
}

describe("closing the notification of an answered request", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    listAlerts.mockReset();
  });

  it("closes only the notifications of requests the Gateway lists as answered", async () => {
    const [answered, unlisted, other] = stubNotifications([
      "devthrottle-link-request-req-old",
      "devthrottle-link-request-req-brand-new",
      "devthrottle-secret-transfer-tr-1",
    ]);

    const closed = await closeAnsweredNotifications(new Set(["req-old"]));

    expect(closed).toBe(1);
    expect(answered.close).toHaveBeenCalled();
    // A request the list has not shown yet - made just after the read left - keeps its fresh notification.
    expect(unlisted.close).not.toHaveBeenCalled();
    expect(other.close).not.toHaveBeenCalled();
  });

  it("CloseAnsweredLinkRequestNotifications_ReadsTheAlertsList_AndClosesOnlyAnsweredMessageRequests", async () => {
    const [answered, waiting, secret] = stubNotifications([
      "devthrottle-link-request-req-done",
      "devthrottle-link-request-req-waiting",
      "devthrottle-secret-transfer-tr-1",
    ]);
    listAlerts.mockResolvedValue({
      alerts: [
        messageAlert({ id: "message-request:req-done", needsAnswer: false, status: "Allowed" }, { requestId: "req-done", status: "approved" }),
        messageAlert({ id: "message-request:req-waiting" }, { requestId: "req-waiting", status: "pending" }),
        secretAlert({ id: "secret-transfer:tr-1", needsAnswer: false, status: "Approved" }),
      ],
      needsAnswerCount: 1,
    });

    render(
      <AlertsProvider>
        <CloseAnsweredLinkRequestNotifications />
      </AlertsProvider>,
    );

    await waitFor(() => expect(answered.close).toHaveBeenCalled());
    expect(waiting.close).not.toHaveBeenCalled();
    expect(secret.close).not.toHaveBeenCalled();
  });
});
