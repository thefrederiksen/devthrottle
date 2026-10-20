// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter, Outlet, useLocation, useRoutes } from "react-router-dom";
import { GatewayError } from "@devthrottle/client-core/api/client";
import type { RulePageAnswer, RulesPageAnswer, RulesPreviewAnswer } from "@devthrottle/client-core/rules/rulesClient";
import {
  previewWithDecision,
  RULE_A,
  RULE_B,
  rulePageWithFirings,
  rulesPage,
} from "@devthrottle/client-core/rules/rulesFixture.testkit";

// The Cockpit's Rules addresses (Session Rules part 2, phase 3) driven through the REAL route table the app mounts
// (COCKPIT_ROUTES), with the real client-core pages and the Cockpit's real frame and error banner class. Only the
// Gateway's answers are staged. The pages' own behaviour is tested in client-core; here it is the Cockpit's half: the
// three addresses exist, /rules/preview is the preview and not a rule called "preview", the list offers the preview,
// and every link stays inside the Cockpit's own /rules addresses.

vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => true,
  getDeviceKey: () => "a-device-key",
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));
// The shell frame (rail, header, live connection) cannot run in jsdom and is not the subject.
vi.mock("../AppShell", () => ({ AppShell: () => <Outlet /> }));

const read = vi.hoisted(() => ({
  page: null as (() => Promise<RulesPageAnswer>) | null,
  rule: null as (() => Promise<RulePageAnswer>) | null,
  preview: null as (() => Promise<RulesPreviewAnswer>) | null,
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
  getRulesPreview: vi.fn(() => {
    if (read.preview === null) throw new Error("no preview answer staged");
    return read.preview();
  }),
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", async (importActual) => ({
  ...(await importActual<typeof import("@devthrottle/client-core/errors/reportClientError")>()),
  reportClientError: vi.fn(),
}));

import { COCKPIT_ROUTES } from "../routes";

function Shell() {
  const location = useLocation();
  const element = useRoutes(COCKPIT_ROUTES);
  return (
    <>
      <div data-testid="where">{location.pathname}</div>
      {element}
    </>
  );
}

function renderAt(path: string) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Shell />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  read.page = null;
  read.rule = null;
  read.preview = null;
  read.ruleIds = [];
});

afterEach(() => {
  cleanup();
});

describe("the Cockpit's Rules addresses", () => {
  it("RulesView_List_ShowsTheRulesUnderTheCockpitsHeaderWithThePreviewOffered", async () => {
    read.page = () => Promise.resolve(rulesPage());

    renderAt("/rules");

    const rows = await screen.findAllByTestId("rules-row");
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Rules");
    expect(rows.map((row) => row.getAttribute("href"))).toEqual(rulesPage().rules.map((rule) => `/rules/${rule.id}`));
    expect(screen.getByRole("link", { name: "Preview a machine and repository" }).getAttribute("href")).toBe("/rules/preview");
    expect(screen.getAllByRole("tab")).toHaveLength(6);
  });

  it("RulesView_ListFails_ShowsTheGatewaysSentenceInTheCockpitsBanner", async () => {
    read.page = () =>
      Promise.reject(new GatewayError(503, "x", { reason: "The rule store is resting (Gateway words).", retryable: false }));

    renderAt("/rules");

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toBe("The rule store is resting (Gateway words).");
    expect(alert.className).toBe("ui-error-banner");
  });

  it("RuleView_OneRule_ReadsItsIdAndOffersTheWayBackToTheList", async () => {
    read.rule = () => Promise.resolve(rulePageWithFirings());

    renderAt(`/rules/${RULE_A}`);

    await screen.findByTestId("rule-page");
    expect(read.ruleIds).toEqual([RULE_A]);
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Rule");
    expect(screen.getByRole("link", { name: "Rules" }).getAttribute("href")).toBe("/rules");
    // The rule a firing overrode links to its own page in the Cockpit.
    const overrode = within(screen.getAllByTestId("rule-firing")[0]).getByTestId("rule-firing-overrode");
    expect(within(overrode).getByRole("link").getAttribute("href")).toBe(`/rules/${RULE_B}`);
  });

  it("RulesPreviewView_PreviewAddress_IsThePreviewNotARuleCalledPreview", async () => {
    read.preview = () => Promise.resolve(previewWithDecision());
    read.page = () => Promise.resolve(rulesPage());

    renderAt("/rules/preview?machine=devbox&command=zorp%20-rf%20build");

    expect(await screen.findByTestId("rules-preview-decision")).toBeTruthy();
    expect(read.ruleIds).toEqual([]);
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Preview a machine and repository");
    expect(screen.getByRole("link", { name: "Rules" }).getAttribute("href")).toBe("/rules");
    expect(screen.getByTestId("where").textContent).toBe("/rules/preview");
  });
});
