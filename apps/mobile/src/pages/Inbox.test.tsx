// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router-dom";
import { oddList, oddThread } from "@devthrottle/client-core/factoryinbox/factoryInboxFixture.testkit";

// The phone's Inbox addresses (Factory Inbox mission, phase 4) driven through the REAL route table the app mounts, at
// the REAL basename, with the real shared screens inside the phone's own frame. Only the Gateway's answers are staged.
// The screens' own behaviour is tested in client-core; here it is the phone's half: the addresses exist, they sit behind
// the factory area switch (off looks exactly like an unknown address), a row opens its thread, and the menu offers the
// Inbox only while the switch is on.

vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => true,
  getDeviceKey: () => "a-device-key",
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));
vi.mock("../hooks/useScreenWakeLock", () => ({ useScreenWakeLock: () => {} }));
vi.mock("../hooks/useVisibleViewportHeight", () => ({ useVisibleViewportHeight: () => {} }));
vi.mock("@devthrottle/client-core/net/useKeepWarm", () => ({ useKeepWarm: () => {} }));
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({ resumePendingDictations: vi.fn(async () => {}) }));
vi.mock("@devthrottle/client-core/recorder/ingestUpload", () => ({ resumePendingRecordingUploads: vi.fn(async () => {}) }));
vi.mock("../components/ConnectionBanner", () => ({ ConnectionBanner: () => null }));
vi.mock("../components/VoiceModeBanner", () => ({ VoiceModeBanner: () => null }));
vi.mock("../components/RecordingBanner", () => ({ RecordingBanner: () => null }));

const gw = vi.hoisted(() => ({ factoriesOn: true }));
vi.mock("@devthrottle/client-core/factory/factoryAgentsClient", () => ({
  getFactoryAgentsSwitch: vi.fn(async () => ({ enabled: gw.factoriesOn, howToStart: gw.factoriesOn ? null : "off" })),
}));
vi.mock("@devthrottle/client-core/factoryinbox/factoryInboxClient", () => ({
  getFactoryInboxList: vi.fn(async () => oddList()),
  getFactoryInboxThread: vi.fn(async () => oddThread()),
  getFactoryInboxSummary: vi.fn(),
  sendFactoryInboxMessage: vi.fn(),
}));
vi.mock("@devthrottle/client-core/factoryinbox/factoryInboxVoice", () => ({
  listFactoryRecordings: vi.fn(async () => []),
  saveFactoryRecording: vi.fn(),
  deleteFactoryRecording: vi.fn(),
  sendFactoryRecording: vi.fn(),
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", async (importActual) => ({
  ...(await importActual<typeof import("@devthrottle/client-core/errors/reportClientError")>()),
  reportClientError: vi.fn(),
}));

import { resetFactoryInboxOfferCache } from "@devthrottle/client-core/factoryinbox/useFactoryInboxOffered";
import { NavDrawer } from "../components/NavDrawer";
import { MOBILE_BASENAME, MOBILE_ROUTES } from "../routes";

function mountAt(entry: string) {
  const router = createMemoryRouter(MOBILE_ROUTES, { basename: MOBILE_BASENAME, initialEntries: [entry] });
  return { router, ...render(<RouterProvider router={router} />) };
}

beforeEach(() => {
  gw.factoriesOn = true;
  resetFactoryInboxOfferCache();
  window.sessionStorage.clear();
});

afterEach(() => {
  cleanup();
});

describe("the phone's Inbox", () => {
  it("Inbox_SwitchOn_ShowsTheFactoryInboxListWithItsComposerInThePhonesFrame", async () => {
    const { container } = mountAt("/mobile/inbox");

    expect(await screen.findAllByTestId("fi-row")).toHaveLength(2);
    expect(container.querySelector(".screen > .app-bar > h1")?.textContent).toBe("Inbox");
    expect(container.querySelector(".screen > .app-bar > .back-link")?.getAttribute("href")).toBe("/mobile");
    expect(screen.getByRole("tab", { name: "Factory inbox" })).toBeTruthy();
    expect(screen.getByTestId("fi-composer")).toBeTruthy();
  });

  it("Inbox_RowPressed_OpensThatFactorysThreadWithItsTitleInTheAppBar", async () => {
    const { router, container } = mountAt("/mobile/inbox");

    fireEvent.click((await screen.findAllByTestId("fi-row"))[0]);

    expect(router.state.location.pathname).toBe("/mobile/inbox/zeta-works");
    expect(await screen.findAllByTestId("fi-message")).toHaveLength(2);
    expect(container.querySelector(".screen > .app-bar > h1")?.textContent).toBe("Zeta Works (gateway)");
    expect(container.querySelector(".screen > .app-bar > .back-link")?.getAttribute("href")).toBe("/mobile/inbox");
  });

  it("Inbox_SwitchOff_LooksExactlyLikeAnUnknownAddress", async () => {
    window.sessionStorage.setItem("cc.staleShellRecovered", "1");
    const unknown = mountAt("/mobile/no-such-page");
    await screen.findByText("Page not found");
    const expected = unknown.container.innerHTML;
    cleanup();

    gw.factoriesOn = false;
    resetFactoryInboxOfferCache();
    const view = mountAt("/mobile/inbox");

    await screen.findByText("Page not found");
    expect(view.container.innerHTML).toBe(expected);
  });

  it("NavDrawer_SwitchOn_OffersTheInbox_SwitchOff_DoesNot", async () => {
    const router = createMemoryRouter([{ path: "/", element: <NavDrawer /> }]);
    render(<RouterProvider router={router} />);
    fireEvent.click(screen.getByRole("button", { name: "Open menu" }));
    expect(await screen.findByRole("link", { name: "Inbox" })).toBeTruthy();
    cleanup();

    gw.factoriesOn = false;
    resetFactoryInboxOfferCache();
    const off = createMemoryRouter([{ path: "/", element: <NavDrawer /> }]);
    render(<RouterProvider router={off} />);
    fireEvent.click(screen.getByRole("button", { name: "Open menu" }));
    await screen.findByRole("link", { name: "Sessions" });
    await new Promise((r) => setTimeout(r, 0));
    expect(screen.queryByRole("link", { name: "Inbox" })).toBeNull();
  });
});
