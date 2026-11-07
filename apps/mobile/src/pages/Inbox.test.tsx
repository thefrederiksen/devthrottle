// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router-dom";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { oddList, oddThread } from "@devthrottle/client-core/factoryinbox/factoryInboxFixture.testkit";
import { oddYourCount, oddYourList, oddYourMessage } from "@devthrottle/client-core/yourinbox/yourInboxFixture.testkit";

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
  discardFactoryRecording: vi.fn(),
  sendFactoryRecording: vi.fn(),
}));
const yours = vi.hoisted(() => ({ unread: 3, label: "three fresh ones (gateway)" }));
vi.mock("@devthrottle/client-core/yourinbox/yourInboxClient", () => ({
  getYourInbox: vi.fn(async () => oddYourList()),
  getYourInboxMessage: vi.fn(async () => oddYourMessage({ unread: false, state: "read" })),
  setYourInboxState: vi.fn(),
  replyYourInbox: vi.fn(),
  getYourInboxCount: vi.fn(async () => oddYourCount({ unread: yours.unread, label: yours.label })),
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", async (importActual) => ({
  ...(await importActual<typeof import("@devthrottle/client-core/errors/reportClientError")>()),
  reportClientError: vi.fn(),
}));

import { resetFactoryInboxOfferCache } from "@devthrottle/client-core/factoryinbox/useFactoryInboxOffered";
import { resetYourInboxCount } from "@devthrottle/client-core/yourinbox/useYourInboxCount";
import { NavDrawer } from "../components/NavDrawer";
import { phoneInboxHref } from "./Inbox";
import { MOBILE_BASENAME, MOBILE_ROUTES } from "../routes";

function mountAt(entry: string) {
  const router = createMemoryRouter(MOBILE_ROUTES, { basename: MOBILE_BASENAME, initialEntries: [entry] });
  return { router, ...render(<RouterProvider router={router} />) };
}

beforeEach(() => {
  gw.factoriesOn = true;
  resetFactoryInboxOfferCache();
  resetYourInboxCount();
  yours.unread = 3;
  yours.label = "three fresh ones (gateway)";
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

  // Your inbox (Factory Inbox mission, step C; mockup C): the list, tap to read, back to the list.
  it("Inbox_YourInboxTab_ShowsTheListInThePhonesFrameWithTheGatewaysCountOnTheTab", async () => {
    const { container } = mountAt("/mobile/inbox?tab=yours");

    expect(await screen.findAllByTestId("yi-row")).toHaveLength(2);
    expect(container.querySelector(".screen > .app-bar > h1")?.textContent).toBe("Inbox");
    expect(screen.getByRole("tab", { name: /Your inbox/ }).getAttribute("aria-selected")).toBe("true");
    expect((await screen.findByTestId("yi-tab-count")).textContent).toBe("three fresh ones (gateway)");
  });

  it("Inbox_YourInboxRowPressed_OpensTheMessageWithItsSubjectInTheAppBarAndBackToTheList", async () => {
    const { router, container } = mountAt("/mobile/inbox?tab=yours");

    fireEvent.click((await screen.findAllByTestId("yi-row"))[0]);

    expect(router.state.location.pathname + router.state.location.search).toBe("/mobile/inbox?tab=yours&message=y-1");
    expect((await screen.findByTestId("yi-body")).textContent).toContain("The quokka notes are filed.");
    expect(container.querySelector(".screen > .app-bar > h1")?.textContent).toBe("Wombat ledger is ready (gateway)");
    expect(container.querySelector(".yi-subject")).toBeNull(); // the app bar holds it; it is not printed twice
    expect(container.querySelector(".screen > .app-bar > .back-link")?.getAttribute("href")).toBe("/mobile/inbox?tab=yours");
    expect(screen.getAllByTestId("yi-link")[0].getAttribute("href")).toBe("/mobile/session/11111111-1111-4111-8111-111111111111");
  });

  // The end-to-end proof of 6 November 2026, defect 5: a long subject pushed the bell off the right edge. A test page
  // has no layout, so this holds the two halves of the fix in place - the frame names its bar, and the phone's sheet
  // gives that bar's title the room left beside the bell, starting from no width, so nothing else has to shrink. The real-browser proof is the rig in
  // docs/qa/factory-inbox/proof-findings-polish, which fails if the bell leaves the bar.
  it("Inbox_AppBar_GivesALongTitleOnlyTheRoomBesideTheBell", async () => {
    const { container } = mountAt("/mobile/inbox?tab=yours&message=y-1");
    await screen.findByTestId("yi-body");

    expect(container.querySelector(".screen > .app-bar.fi-phone-bar > h1")).not.toBeNull();
    const sheet = readFileSync(join(__dirname, "..", "styles.css"), "utf8");
    const rule = (selector: string) => sheet.match(new RegExp(`${selector.replace(/[.]/g, "\\.")}\\s*\\{([^}]*)\\}`))?.[1] ?? "";
    expect(rule(".screen .fi-phone-bar h1")).toContain("flex: 1 1 0");
    expect(rule(".screen .fi-phone-bar h1")).toContain("min-width: 0");
    expect(rule(".screen .fi-phone-bar h1")).toContain("overflow-wrap: anywhere");
  });

  // Section 7 of the mission: the unread count on the Inbox entry equals the Gateway's count.
  it("NavDrawer_Inbox_CarriesTheGatewaysUnreadCountInItsWords", async () => {
    const router = createMemoryRouter([{ path: "/", element: <NavDrawer /> }]);
    render(<RouterProvider router={router} />);
    fireEvent.click(screen.getByRole("button", { name: "Open menu" }));

    expect((await screen.findByTestId("inbox-count")).textContent).toBe("three fresh ones (gateway)");
  });

  it("NavDrawer_NothingUnread_CarriesNoCount", async () => {
    yours.unread = 0;
    yours.label = "";
    const router = createMemoryRouter([{ path: "/", element: <NavDrawer /> }]);
    render(<RouterProvider router={router} />);
    fireEvent.click(screen.getByRole("button", { name: "Open menu" }));

    await screen.findByRole("link", { name: "Inbox" });
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByTestId("inbox-count")).toBeNull();
  });

  it("phoneInboxHref_EachKind_IsThePhonesOwnAddress", () => {
    expect(phoneInboxHref({ kind: "session", label: "", target: "s1" })).toBe("/session/s1");
    expect(phoneInboxHref({ kind: "report", label: "", target: "r1" })).toBe("/report/r1");
    expect(phoneInboxHref({ kind: "factory", label: "", target: "zeta-works" })).toBe("/inbox/zeta-works");
    expect(phoneInboxHref({ kind: "unknown-kind", label: "", target: "x" })).toBeNull();
  });
});
