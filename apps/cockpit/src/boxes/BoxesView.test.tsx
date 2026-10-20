// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Outlet, useRoutes } from "react-router-dom";
import { GatewayError } from "@devthrottle/client-core/api/client";
import type { AccountStatus } from "@devthrottle/client-core/account/accountClient";
import type { BoxesList, DesktopTicket } from "@devthrottle/client-core/boxes/boxesClient";

// The Cockpit's Boxes address (Factory Boxes mission, phase 1) driven through the REAL route table the app mounts
// (COCKPIT_ROUTES), with the real page, the Cockpit's real "Page not found" and its real components. Only the
// Gateway's answers are staged, as fakes: GET /account/status, GET /boxes and POST /boxes/{boxId}/desktop-ticket.
// No network, no sleeps - a staged answer is a promise this test resolves or rejects itself.

vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => true,
  getDeviceKey: () => "a-device-key",
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));
// The shell frame (rail, header, live connection) is tested in AppShell.test.tsx; here it is only the outlet.
vi.mock("../AppShell", () => ({ AppShell: () => <Outlet /> }));

const gateway = vi.hoisted(() => ({
  status: null as (() => Promise<AccountStatus>) | null,
  list: null as (() => Promise<BoxesList>) | null,
  ticket: null as ((boxId: string) => Promise<DesktopTicket>) | null,
  ticketCalls: [] as string[],
}));
vi.mock("@devthrottle/client-core/account/accountClient", () => ({
  getAccountStatus: vi.fn(() => {
    if (gateway.status === null) return new Promise<AccountStatus>(() => undefined);
    return gateway.status();
  }),
}));
vi.mock("@devthrottle/client-core/boxes/boxesClient", () => ({
  getBoxes: vi.fn(() => {
    if (gateway.list === null) return new Promise<BoxesList>(() => undefined);
    return gateway.list();
  }),
  getDesktopTicket: vi.fn((boxId: string) => {
    gateway.ticketCalls.push(boxId);
    if (gateway.ticket === null) throw new Error("no ticket answer staged");
    return gateway.ticket(boxId);
  }),
}));
// Reporting is the real describeAndReport (its sentence is what the page shows), watched so a test can see it was asked;
// the send itself is stood in for, so nothing leaves the process.
vi.mock("@devthrottle/client-core/errors/reportClientError", async (importActual) => {
  const actual = await importActual<typeof import("@devthrottle/client-core/errors/reportClientError")>();
  return {
    ...actual,
    reportClientError: vi.fn(),
    describeAndReport: vi.fn(actual.describeAndReport),
  };
});

import { describeAndReport, reportClientError } from "@devthrottle/client-core/errors/reportClientError";
import { resetBoxesSwitchCache } from "@devthrottle/client-core/boxes/useBoxesSwitch";
import { COCKPIT_ROUTES } from "../routes";
import { OPENING_TEXT } from "./BoxesView";

const ON: AccountStatus = { signedIn: true, boxes: true };
const OFF: AccountStatus = { signedIn: true, boxes: false };

// Deliberately odd values, so a field that is reworded, trimmed, re-cased or swapped with another is caught.
const ONE: BoxesList = {
  count: 2,
  boxes: [
    {
      boxId: "demo-desktop-soren",
      displayName: "Soren's demo desktop",
      directorId: "4f6c2a3e-0000-0000-0000-000000000000",
      directorName: "devlinux slot 1",
      machineName: "devlinux",
      state: "Online",
      relativeDesktopPath: "/boxes/demo-desktop-soren/desktop/",
    },
    {
      boxId: "b2",
      displayName: "Second box",
      directorId: "9a9a9a9a-0000-0000-0000-000000000000",
      directorName: "",
      machineName: "CLOUD-Box-7",
      state: "a state phase 1 has never heard of",
      relativeDesktopPath: "/boxes/b2/desktop/",
    },
  ],
};

function Routes() {
  return <>{useRoutes(COCKPIT_ROUTES)}</>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes />
    </MemoryRouter>,
  );
}

/** A deferred answer the test settles itself. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

/** The tab window.open hands back: about:blank with a document, a location and close(). */
function fakeTab() {
  const doc = document.implementation.createHTMLDocument("");
  const tab = {
    document: doc,
    location: { href: "about:blank" },
    opener: window as Window | null,
    close: vi.fn(),
  };
  return tab;
}

beforeEach(() => {
  resetBoxesSwitchCache();
  gateway.status = null;
  gateway.list = null;
  gateway.ticket = null;
  gateway.ticketCalls = [];
  vi.mocked(reportClientError).mockClear();
  vi.mocked(describeAndReport).mockClear();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("the Cockpit's Boxes address", () => {
  it("BoxesView_SwitchOff_LooksExactlyLikeAnUnknownAddress", async () => {
    gateway.status = () => Promise.resolve(OFF);
    const unknown = renderAt("/no-such-page");
    const expected = unknown.container.innerHTML;
    expect(expected).toContain("Page not found");
    cleanup();

    const view = renderAt("/boxes");

    await screen.findByText("Page not found");
    expect(view.container.innerHTML).toBe(expected);
  });

  it("BoxesView_SwitchOff_NeverAsksForTheList", async () => {
    gateway.status = () => Promise.resolve(OFF);
    const { getBoxes } = await import("@devthrottle/client-core/boxes/boxesClient");

    renderAt("/boxes");
    await screen.findByText("Page not found");

    expect(vi.mocked(getBoxes)).not.toHaveBeenCalled();
  });

  it("BoxesView_BeforeTheSwitchAnswers_ShowsLoadingAtOnce", () => {
    renderAt("/boxes");

    expect(screen.getByRole("status").textContent).toBe("Loading...");
  });

  it("BoxesView_SwitchUnreadable_ShowsTheFailureNotPageNotFound", async () => {
    gateway.status = () => Promise.reject(new GatewayError(500, "fault", { reason: "The account store is not answering." }));

    renderAt("/boxes");

    const banner = await screen.findByRole("alert");
    expect(banner.textContent).toContain("The account store is not answering.");
    expect(screen.queryByText("Page not found")).toBeNull();
  });

  it("BoxesView_SwitchOn_ShowsLoadingAtOnceWhileTheListIsAsked", async () => {
    gateway.status = () => Promise.resolve(ON);

    renderAt("/boxes");

    await screen.findByRole("heading", { name: "Boxes" });
    expect(screen.getByRole("status").textContent).toBe("Loading...");
  });

  it("BoxesView_List_RendersEveryFieldExactlyAsTheGatewaySentIt", async () => {
    gateway.status = () => Promise.resolve(ON);
    gateway.list = () => Promise.resolve(ONE);

    renderAt("/boxes");

    const rows = await screen.findAllByTestId("box-row");
    expect(rows).toHaveLength(2);
    const [first, second] = rows;
    expect(within(first).getByTestId("box-name").textContent).toBe("Soren's demo desktop");
    expect(within(first).getByTestId("box-id").textContent).toBe("demo-desktop-soren");
    expect(within(first).getByTestId("box-machine").textContent).toBe("devlinux");
    expect(within(first).getByTestId("box-director").textContent).toBe("devlinux slot 1");
    expect(within(first).getByTestId("box-state").textContent).toBe("Online");
    expect(within(first).getByRole("button", { name: "Open" })).toBeTruthy();

    expect(within(second).getByTestId("box-name").textContent).toBe("Second box");
    expect(within(second).getByTestId("box-id").textContent).toBe("b2");
    expect(within(second).getByTestId("box-machine").textContent).toBe("CLOUD-Box-7");
    // A Director with no display name: the Gateway sent an empty name, and nothing is made up in its place.
    expect(within(second).queryByTestId("box-director")).toBeNull();
    expect(within(second).getByTestId("box-state").textContent).toBe("a state phase 1 has never heard of");
  });

  it("BoxesView_EmptyList_SaysPlainlyNoDirectorHostsADesktop", async () => {
    gateway.status = () => Promise.resolve(ON);
    gateway.list = () => Promise.resolve({ count: 0, boxes: [] });

    renderAt("/boxes");

    expect(await screen.findByText(/No Director in your account is hosting a desktop\./)).toBeTruthy();
    expect(screen.getByText(/The Gateway lists 0 boxes\./)).toBeTruthy();
    expect(screen.queryAllByTestId("box-row")).toHaveLength(0);
  });

  it("BoxesView_ListFails_ShowsTheGatewaysMessageAndReportsIt", async () => {
    gateway.status = () => Promise.resolve(ON);
    gateway.list = () => Promise.reject(new GatewayError(503, "fault", { reason: "The Gateway is starting." }));

    const { container } = renderAt("/boxes");

    const banner = await screen.findByRole("alert");
    expect(banner.className).toContain("ui-error-banner");
    expect(banner.textContent).toContain("The Gateway is starting.");
    expect(container.querySelector(".pane")).not.toBeNull();
    expect(vi.mocked(describeAndReport).mock.calls[0].slice(0, 2)).toEqual(["boxes", "read the boxes"]);
  });

  it("BoxesView_ListFails_TryAgainAsksAgain", async () => {
    gateway.status = () => Promise.resolve(ON);
    gateway.list = () => Promise.reject(new GatewayError(503, "fault", { reason: "The Gateway is starting." }));
    renderAt("/boxes");
    await screen.findByRole("alert");

    gateway.list = () => Promise.resolve(ONE);
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));

    expect(await screen.findAllByTestId("box-row")).toHaveLength(2);
  });
});

describe("opening a box", () => {
  async function renderList() {
    gateway.status = () => Promise.resolve(ON);
    gateway.list = () => Promise.resolve(ONE);
    renderAt("/boxes");
    return (await screen.findAllByTestId("box-row"))[0];
  }

  it("BoxesView_Open_OpensTheTabInsideTheClickBeforeAskingForTheTicket", async () => {
    const row = await renderList();
    const tab = fakeTab();
    const order: string[] = [];
    vi.spyOn(window, "open").mockImplementation(() => {
      order.push("open");
      return tab as unknown as Window;
    });
    const answer = deferred<DesktopTicket>();
    gateway.ticket = () => {
      order.push("ticket");
      return answer.promise;
    };

    fireEvent.click(within(row).getByRole("button", { name: "Open" }));

    // The tab exists, synchronously, before the ticket is asked for - so no popup blocker stops it.
    expect(order).toEqual(["open", "ticket"]);
    expect(window.open).toHaveBeenCalledWith("", "_blank");
    expect(tab.document.body.textContent).toBe(OPENING_TEXT);
    expect(tab.document.title).toBe(OPENING_TEXT);
    expect(within(row).getByRole("button", { name: "Opening..." }).hasAttribute("disabled")).toBe(true);
  });

  it("BoxesView_Open_SendsTheTabToTheReturnedUrlExactlyAndCutsItsOpener", async () => {
    const row = await renderList();
    const tab = fakeTab();
    vi.spyOn(window, "open").mockReturnValue(tab as unknown as Window);
    // An address the Cockpit could never build: another host, a ticket, odd casing - carried byte for byte.
    const url = "https://Desktop.Example.test:8443/boxes/demo-desktop-soren/desktop/?ticket=A_b-9%2Fz&x=1";
    gateway.ticket = () => Promise.resolve({ url, expiresAtUtc: "2026-10-20T08:30:00Z" });

    await act(async () => {
      fireEvent.click(within(row).getByRole("button", { name: "Open" }));
    });

    expect(gateway.ticketCalls).toEqual(["demo-desktop-soren"]);
    expect(tab.location.href).toBe(url);
    expect(tab.opener).toBeNull();
    expect(tab.close).not.toHaveBeenCalled();
    expect(within(row).getByRole("button", { name: "Open" }).hasAttribute("disabled")).toBe(false);
  });

  it("BoxesView_OpenTicketFails_ClosesTheTabAndShowsTheGatewaysMessageOnTheRow", async () => {
    const row = await renderList();
    const tab = fakeTab();
    vi.spyOn(window, "open").mockReturnValue(tab as unknown as Window);
    gateway.ticket = () =>
      Promise.reject(
        new GatewayError(404, "fault", { reason: "No connected Director in your account hosts a desktop for that box." }),
      );

    await act(async () => {
      fireEvent.click(within(row).getByRole("button", { name: "Open" }));
    });

    expect(tab.close).toHaveBeenCalledTimes(1);
    expect(tab.location.href).toBe("about:blank");
    const error = within(row).getByTestId("box-error");
    expect(error.textContent).toContain("No connected Director in your account hosts a desktop for that box.");
    expect(vi.mocked(describeAndReport).mock.calls[0].slice(0, 2)).toEqual(["boxes", "open the desktop"]);
    // Only that row carries it.
    const other = screen.getAllByTestId("box-row")[1];
    expect(within(other).queryByTestId("box-error")).toBeNull();
  });

  it("BoxesView_OpenAgainAfterAFailure_ClearsTheOldMessage", async () => {
    const row = await renderList();
    vi.spyOn(window, "open").mockImplementation(() => fakeTab() as unknown as Window);
    gateway.ticket = () => Promise.reject(new GatewayError(503, "fault", { reason: "Try later." }));
    await act(async () => {
      fireEvent.click(within(row).getByRole("button", { name: "Open" }));
    });
    expect(within(row).getByTestId("box-error")).toBeTruthy();

    gateway.ticket = () => Promise.resolve({ url: "https://x.test/", expiresAtUtc: "2026-10-20T08:30:00Z" });
    await act(async () => {
      fireEvent.click(within(row).getByRole("button", { name: "Open" }));
    });

    await waitFor(() => expect(within(row).queryByTestId("box-error")).toBeNull());
  });

  it("BoxesView_OpenBlockedByTheBrowser_SaysSoAndAsksForNoTicket", async () => {
    const row = await renderList();
    vi.spyOn(window, "open").mockReturnValue(null);

    fireEvent.click(within(row).getByRole("button", { name: "Open" }));

    expect(gateway.ticketCalls).toEqual([]);
    expect(within(row).getByTestId("box-error").textContent).toContain("The browser did not open a new tab");
  });

  it("BoxesView_Open_NeverPutsTheDesktopInAFrame", async () => {
    const row = await renderList();
    vi.spyOn(window, "open").mockReturnValue(fakeTab() as unknown as Window);
    gateway.ticket = () => Promise.resolve({ url: "https://x.test/", expiresAtUtc: "2026-10-20T08:30:00Z" });

    await act(async () => {
      fireEvent.click(within(row).getByRole("button", { name: "Open" }));
    });

    expect(document.querySelector("iframe")).toBeNull();
  });
});
