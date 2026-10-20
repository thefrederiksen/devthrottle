// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router-dom";
import { GatewayError } from "@devthrottle/client-core/api/client";
import type { MailDeskAnswer } from "@devthrottle/client-core/maildesk/mailDeskClient";
import { oddChannels } from "@devthrottle/client-core/maildesk/mailDeskFixture.testkit";

// The phone's Mail Desk address (Mail Desk mission, phase 7) driven through the REAL route table the app mounts
// (MOBILE_ROUTES, at the REAL basename), with the real page and the phone's real frame, "Page not found" and error
// banner. Only the Gateway's answer is staged. The page's own behaviour is tested in client-core; here it is the
// phone's half: the address exists, the switch off looks exactly like an unknown address, and the page sits in the
// phone's screen under its app bar.

vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => true,
  getDeviceKey: () => "a-device-key",
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));
// The gated layout's app-level machinery cannot run in jsdom and is not the subject.
vi.mock("../hooks/useScreenWakeLock", () => ({ useScreenWakeLock: () => {} }));
vi.mock("../hooks/useVisibleViewportHeight", () => ({ useVisibleViewportHeight: () => {} }));
vi.mock("@devthrottle/client-core/net/useKeepWarm", () => ({ useKeepWarm: () => {} }));
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({ resumePendingDictations: vi.fn(async () => {}) }));
vi.mock("@devthrottle/client-core/recorder/ingestUpload", () => ({ resumePendingRecordingUploads: vi.fn(async () => {}) }));
vi.mock("../components/ConnectionBanner", () => ({ ConnectionBanner: () => null }));
vi.mock("../components/VoiceModeBanner", () => ({ VoiceModeBanner: () => null }));
vi.mock("../components/RecordingBanner", () => ({ RecordingBanner: () => null }));

const read = vi.hoisted(() => ({ answer: null as (() => Promise<MailDeskAnswer>) | null }));
vi.mock("@devthrottle/client-core/maildesk/mailDeskClient", () => ({
  getMailDeskChannels: vi.fn(() => {
    if (read.answer === null) throw new Error("no Mail Desk answer staged");
    return read.answer();
  }),
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", async (importActual) => ({
  ...(await importActual<typeof import("@devthrottle/client-core/errors/reportClientError")>()),
  reportClientError: vi.fn(),
}));

import { MOBILE_BASENAME, MOBILE_ROUTES } from "../routes";

function mountAt(entry: string) {
  const router = createMemoryRouter(MOBILE_ROUTES, { basename: MOBILE_BASENAME, initialEntries: [entry] });
  return render(<RouterProvider router={router} />);
}

beforeEach(() => {
  read.answer = null;
  window.sessionStorage.clear();
});

afterEach(() => {
  cleanup();
});

describe("the phone's Mail Desk address", () => {
  it("MailDesk_SwitchOn_ShowsTheGatewaysTitleInTheAppBarWithOneCardPerChannel", async () => {
    const envelope = oddChannels();
    read.answer = () => Promise.resolve({ kind: "channels", envelope });

    const { container } = mountAt("/mobile/mail-desk");

    expect(await screen.findAllByTestId("mail-desk-channel")).toHaveLength(envelope.channels.length);
    expect(container.querySelector(".screen > .app-bar > h1")?.textContent).toBe(envelope.title);
    expect(container.querySelector(".screen > .app-bar > .back-link")?.getAttribute("href")).toBe("/mobile");
    expect(container.querySelector(".screen .mail-desk")).not.toBeNull();
  });

  it("MailDesk_SwitchOff_LooksExactlyLikeAnUnknownAddress", async () => {
    // An unknown address on the phone first reloads once to rule out a stale shell; after that one reload it reads
    // "Page not found". That second look is the ordinary one, and the one the switched-off page must match.
    window.sessionStorage.setItem("cc.staleShellRecovered", "1");
    const unknown = mountAt("/mobile/no-such-page");
    await screen.findByText("Page not found");
    const expected = unknown.container.innerHTML;
    cleanup();

    read.answer = () => Promise.resolve({ kind: "off" });
    const view = mountAt("/mobile/mail-desk");

    await screen.findByText("Page not found");
    expect(view.container.innerHTML).toBe(expected);
  });

  it("MailDesk_FailedRead_ShowsThePhonesErrorBanner", async () => {
    read.answer = () => Promise.reject(new GatewayError(500, "fault", { reason: "The Mail Desk store is not answering." }));

    const { container } = mountAt("/mobile/mail-desk");

    const banner = await screen.findByRole("alert");
    expect(banner.className).toBe("banner banner-error");
    expect(banner.textContent).toContain("The Mail Desk store is not answering.");
    expect(container.querySelector(".screen > .app-bar > .back-link")).not.toBeNull();
  });
});
