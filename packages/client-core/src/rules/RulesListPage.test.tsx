import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { GatewayError } from "../api/client";
import type { RulesPageAnswer } from "./rulesClient";
import { noRules, RULE_A, RULE_B, RULE_C, rulesPage } from "./rulesFixture.testkit";
import type { RulesFrameProps } from "./rulesShared";

// The Rules list (wire P1) against a fixture whose words are deliberately odd: a page that writes any word of its own
// in place of the Gateway's fails here. The client is stood in for, so loading, an answer and a failure can each be
// staged; the error wording is the real one, so the Gateway's own sentence is what must reach the screen (ruling T9).

const read = vi.hoisted(() => ({ next: null as (() => Promise<RulesPageAnswer>) | null }));
vi.mock("./rulesClient", () => ({
  getRulesPage: vi.fn(() => {
    if (read.next === null) throw new Error("no Rules answer staged");
    return read.next();
  }),
}));

vi.mock("../errors/reportClientError", async (importActual) => {
  const { gatewayErrorMessage } = await import("../api/client");
  return {
    ...(await importActual<typeof import("../errors/reportClientError")>()),
    describeAndReport: vi.fn((_surface: string, action: string, err: unknown) => gatewayErrorMessage(err, action)),
  };
});

import { describeAndReport } from "../errors/reportClientError";
import { RulesListPage } from "./RulesListPage";

function Frame({ title, children }: RulesFrameProps) {
  return (
    <section data-testid="frame">
      <h1>{title}</h1>
      {children}
    </section>
  );
}

function Address() {
  const location = useLocation();
  return <span data-testid="address">{location.pathname + location.search}</span>;
}

function renderList(at = "/rules", previewHref?: string) {
  return render(
    <MemoryRouter initialEntries={[at]}>
      <Routes>
        <Route
          path="*"
          element={
            <>
              <RulesListPage
                Frame={Frame}
                errorClassName="test-error-banner"
                ruleHref={(id) => `/test-rules/${id}`}
                previewHref={previewHref}
              />
              <Address />
            </>
          }
        />
      </Routes>
    </MemoryRouter>,
  );
}

function answers(answer: RulesPageAnswer) {
  read.next = () => Promise.resolve(answer);
}

function shownRuleIds(): string[] {
  return screen.queryAllByTestId("rules-row").map((row) => row.getAttribute("data-rule-id") ?? "");
}

beforeEach(() => {
  read.next = null;
  vi.mocked(describeAndReport).mockClear();
});

afterEach(() => {
  cleanup();
});

describe("RulesListPage", () => {
  it("RulesListPage_BeforeTheAnswer_ShowsLoadingAtOnce", () => {
    read.next = () => new Promise(() => {});

    renderList();

    expect(screen.getByRole("status").textContent).toBe("Loading the rules...");
    expect(screen.queryByTestId("rules-list")).toBeNull();
  });

  it("RulesListPage_Answer_DrawsTheSixTabsInTheGatewaysOrderWithTheirCounts", async () => {
    answers(rulesPage());

    renderList();

    const tabs = await screen.findAllByRole("tab");
    expect(tabs.map((tab) => tab.textContent)).toEqual([
      "Every-rule tab3",
      "Screenish1",
      "Hookish2",
      "Start-of-session0",
      "Went off today1",
      "Touched by agents1",
    ]);
    expect(screen.getByTestId("rules-today").textContent).toBe("Moonday 20 Octember");
    // The first tab is open, holding every rule in the Gateway's order (newest first).
    expect(tabs[0].getAttribute("aria-selected")).toBe("true");
    expect(shownRuleIds()).toEqual([RULE_A, RULE_B, RULE_C]);
  });

  it("RulesListPage_Answer_DrawsEveryLabelOfARuleVerbatimAndLinksToItsPage", async () => {
    const rule = rulesPage().rules[0];
    answers(rulesPage());

    renderList();

    const row = (await screen.findAllByTestId("rules-row"))[0];
    for (const word of [
      rule.sentence,
      rule.kindLabel,
      rule.triggerLabel,
      rule.scopeLabel,
      rule.modeLabel,
      rule.handlerLabel,
      rule.firedLabel,
      rule.changedLabel,
    ]) {
      expect(row.textContent, word).toContain(word);
    }
    expect(row.getAttribute("href")).toBe(`/test-rules/${RULE_A}`);
    // The mode's tone reaches the style as the Gateway's word, nothing more.
    expect(within(row).getByText(rule.modeLabel).getAttribute("data-tone")).toBe("neutral");
  });

  it("RulesListPage_BlankChangedLabel_DrawsNoChangedLine", async () => {
    answers(rulesPage());

    renderList();

    const rows = await screen.findAllByTestId("rules-row");
    expect(within(rows[0]).queryByTestId("rules-row-changed")).not.toBeNull();
    expect(within(rows[1]).queryByTestId("rules-row-changed")).toBeNull();
  });

  it("RulesListPage_ChoosingATab_ShowsOnlyTheRulesTheGatewayPutOnItAndWritesItToTheAddress", async () => {
    answers(rulesPage());

    renderList();

    fireEvent.click(await screen.findByTestId("rules-tab-fired_today"));

    expect(shownRuleIds()).toEqual([RULE_A]);
    expect(screen.getByTestId("rules-tab-fired_today").getAttribute("aria-selected")).toBe("true");
    expect(screen.getByTestId("address").textContent).toBe("/rules?tab=fired_today");

    fireEvent.click(screen.getByTestId("rules-tab-screen"));
    expect(shownRuleIds()).toEqual([RULE_C]);
  });

  it("RulesListPage_TabInTheAddress_OpensThatTab", async () => {
    answers(rulesPage());

    renderList("/rules?tab=hook");

    await screen.findAllByTestId("rules-row");
    expect(shownRuleIds()).toEqual([RULE_A, RULE_B]);
  });

  it("RulesListPage_SessionStartTab_ShowsTheGatewaysEmptyText", async () => {
    answers(rulesPage());

    renderList("/rules?tab=session_start");

    expect((await screen.findByTestId("rules-empty")).textContent).toBe(
      "No start-of-session rules: they are not built yet (Gateway words).",
    );
    expect(shownRuleIds()).toEqual([]);
  });

  it("RulesListPage_NoRules_ShowsEachTabsOwnEmptyText", async () => {
    answers(noRules());

    renderList();

    expect((await screen.findByTestId("rules-empty")).textContent).toBe("Nothing on the every-rule tab.");
    fireEvent.click(screen.getByTestId("rules-tab-changed_by_agents"));
    expect(screen.getByTestId("rules-empty").textContent).toBe("No agent touched a rule.");
  });

  it("RulesListPage_GatewayError_ShowsTheGatewaysSentenceInTheShellsBannerAndReportsIt", async () => {
    read.next = () =>
      Promise.reject(new GatewayError(503, "x", { reason: "The rule store is resting (Gateway words).", retryable: false }));

    renderList();

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toBe("The rule store is resting (Gateway words).");
    expect(alert.className).toBe("test-error-banner");
    expect(describeAndReport).toHaveBeenCalledWith("rules", "load the rules", expect.any(GatewayError));
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("RulesListPage_GatewayErrorWithNoSentence_ShowsTheStatus", async () => {
    read.next = () => Promise.reject(new GatewayError(500, "x"));

    renderList();

    expect((await screen.findByRole("alert")).textContent).toContain("error 500");
  });

  it("RulesListPage_TryAgainAfterAnError_ReadsAgainAndShowsTheList", async () => {
    read.next = () => Promise.reject(new GatewayError(500, "x"));
    renderList();
    await screen.findByRole("alert");

    answers(rulesPage());
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));

    expect(await screen.findAllByTestId("rules-row")).toHaveLength(3);
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("RulesListPage_PreviewHref_DrawsTheLinkOnlyWhenTheShellHasOne", async () => {
    answers(rulesPage());
    renderList("/rules", "/test-rules/preview");
    expect((await screen.findByRole("link", { name: "Preview a machine and repository" })).getAttribute("href")).toBe(
      "/test-rules/preview",
    );
    cleanup();

    answers(rulesPage());
    renderList("/rules");
    await screen.findAllByTestId("rules-row");
    expect(screen.queryByRole("link", { name: "Preview a machine and repository" })).toBeNull();
  });
});
