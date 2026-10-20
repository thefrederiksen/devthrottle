// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router-dom";
import { GatewayError } from "@devthrottle/client-core/api/client";
import type { RulePageAnswer, RulesPageAnswer } from "@devthrottle/client-core/rules/rulesClient";
import { RULE_A, RULE_B, rulePageWithFirings, rulesPage } from "@devthrottle/client-core/rules/rulesFixture.testkit";

// The phone's Rules addresses (Session Rules part 2, phase 3) driven through the REAL route table the app mounts
// (MOBILE_ROUTES, at the REAL basename), with the real client-core pages and the phone's real frame and error banner.
// Only the Gateway's answers are staged. The pages' own behaviour is tested in client-core; here it is the phone's
// half: both addresses exist, they sit in the phone's screen under its app bar with Back, they are read-only, and the
// preview - a Cockpit page - is not offered.

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

const read = vi.hoisted(() => ({
  page: null as (() => Promise<RulesPageAnswer>) | null,
  rule: null as (() => Promise<RulePageAnswer>) | null,
  ruleIds: [] as string[],
}));
vi.mock("@devthrottle/client-core/rules/rulesClient", () => ({
  getRulesPage: vi.fn(() => {
    if (read.page === null) throw new Error("no Rules answer staged");
    return read.page();
  }),
  getRulePage: vi.fn((id: string) => {
    read.ruleIds.push(id);
    if (read.rule === null) throw new Error("no rule answer staged");
    return read.rule();
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
  read.page = null;
  read.rule = null;
  read.ruleIds = [];
  window.sessionStorage.clear();
});

afterEach(() => {
  cleanup();
});

describe("the phone's Rules addresses", () => {
  it("Rules_List_SitsInThePhonesScreenWithBackHomeAndNoPreview", async () => {
    read.page = () => Promise.resolve(rulesPage());

    const { container } = mountAt("/mobile/rules");

    const rows = await screen.findAllByTestId("rules-row");
    expect(container.querySelector(".screen .app-bar h1")?.textContent).toBe("Rules");
    expect(screen.getByRole("link", { name: "Back" }).getAttribute("href")).toBe("/mobile");
    expect(rows.map((row) => row.getAttribute("href"))).toEqual(rulesPage().rules.map((rule) => `/mobile/rules/${rule.id}`));
    expect(screen.getAllByRole("tab")).toHaveLength(6);
    expect(screen.queryByRole("link", { name: "Preview a machine and repository" })).toBeNull();
    // Read-only: apart from the tabs, which only choose what is shown, the page has no control at all.
    expect(screen.queryAllByRole("button")).toEqual([]);
  });

  it("Rules_LoadingAndThenAFailure_ShowsLoadingAtOnceThenTheGatewaysSentenceInThePhonesBanner", async () => {
    let fail: (err: unknown) => void = () => {};
    read.page = () => new Promise((_resolve, reject) => (fail = reject));

    mountAt("/mobile/rules");

    expect((await screen.findByRole("status")).textContent).toBe("Loading the rules...");
    fail(new GatewayError(503, "x", { reason: "The rule store is resting (Gateway words).", retryable: false }));
    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toBe("The rule store is resting (Gateway words).");
    expect(alert.className).toBe("banner banner-error");
  });

  it("Rule_OneRule_ReadsItsIdWithBackToTheListAndLinksStayOnThePhone", async () => {
    read.rule = () => Promise.resolve(rulePageWithFirings());

    const { container } = mountAt(`/mobile/rules/${RULE_A}`);

    await screen.findByTestId("rule-page");
    expect(read.ruleIds).toEqual([RULE_A]);
    expect(container.querySelector(".screen .app-bar h1")?.textContent).toBe("Rule");
    expect(screen.getByRole("link", { name: "Back" }).getAttribute("href")).toBe("/mobile/rules");
    const overrode = within(screen.getAllByTestId("rule-firing")[0]).getByTestId("rule-firing-overrode");
    expect(within(overrode).getByRole("link").getAttribute("href")).toBe(`/mobile/rules/${RULE_B}`);
  });
});
