import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { GatewayError } from "../api/client";
import type { RulePageAnswer } from "./rulesClient";
import { RULE_A, RULE_B, rulePageWithFirings, rulePageWithoutFirings } from "./rulesFixture.testkit";
import type { RulesFrameProps } from "./rulesShared";

// One rule's page (wire P2): the fields drawn as the Gateway ordered them and nothing added, the firing record with
// what each firing overrode, and the change history - each with the Gateway's own empty text, and the Gateway's own
// sentence on a failure (ruling T9).

const read = vi.hoisted(() => ({ next: null as (() => Promise<RulePageAnswer>) | null, ids: [] as string[] }));
vi.mock("./rulesClient", () => ({
  getRulePage: vi.fn((id: string) => {
    read.ids.push(id);
    if (read.next === null) throw new Error("no rule answer staged");
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
import { RulePage } from "./RulePage";

function Frame({ title, backHref, backLabel, children }: RulesFrameProps) {
  return (
    <section data-testid="frame" data-back={backHref} data-back-label={backLabel}>
      <h1>{title}</h1>
      {children}
    </section>
  );
}

function renderRule(id = RULE_A) {
  return render(
    <MemoryRouter>
      <RulePage
        id={id}
        Frame={Frame}
        errorClassName="test-error-banner"
        ruleHref={(other) => `/test-rules/${other}`}
        listHref="/test-rules"
      />
    </MemoryRouter>,
  );
}

function answers(answer: RulePageAnswer) {
  read.next = () => Promise.resolve(answer);
}

beforeEach(() => {
  read.next = null;
  read.ids = [];
  vi.mocked(describeAndReport).mockClear();
});

afterEach(() => {
  cleanup();
});

describe("RulePage", () => {
  it("RulePage_BeforeTheAnswer_ShowsLoadingAtOnceWithBackToTheList", () => {
    read.next = () => new Promise(() => {});

    renderRule();

    expect(screen.getByRole("status").textContent).toBe("Loading the rule...");
    expect(screen.getByTestId("frame").getAttribute("data-back")).toBe("/test-rules");
    expect(read.ids).toEqual([RULE_A]);
  });

  it("RulePage_Answer_DrawsEveryFieldInTheGatewaysOrderAndNothingElse", async () => {
    const page = rulePageWithFirings();
    answers(page);

    renderRule();

    const fields = await screen.findByTestId("rule-fields");
    const pairs = Array.from(fields.querySelectorAll(".rules-field")).map((row) => [
      row.querySelector("dt")?.textContent,
      row.querySelector("dd")?.textContent,
    ]);
    expect(pairs).toEqual(page.fields.map((field) => [field.label, field.value]));
  });

  it("RulePage_WithFirings_DrawsEachFiringVerbatimWithItsTone", async () => {
    const page = rulePageWithFirings();
    answers(page);

    renderRule();

    const firings = await screen.findAllByTestId("rule-firing");
    expect(firings).toHaveLength(2);
    page.firings.forEach((firing, index) => {
      const card = firings[index];
      for (const word of [
        firing.decisionLabel,
        firing.whenLabel,
        firing.sessionLabel,
        firing.whereLabel,
        firing.subject,
        firing.outcomeLabel,
      ]) {
        expect(card.textContent, word).toContain(word);
      }
      expect(within(card).getByTestId("rule-firing-decision").getAttribute("data-tone")).toBe(firing.tone);
    });
    expect(screen.queryByTestId("rule-firings-empty")).toBeNull();
  });

  it("RulePage_FiringThatOverrode_NamesTheGatewaysLabelAndLinksTheOverriddenRule", async () => {
    answers(rulePageWithFirings());

    renderRule();

    const firings = await screen.findAllByTestId("rule-firing");
    const overrode = within(firings[0]).getByTestId("rule-firing-overrode");
    expect(overrode.textContent).toContain("Overrode one other rule");
    const link = within(overrode).getByRole("link", { name: "Allow anything zorpy here." });
    expect(link.getAttribute("href")).toBe(`/test-rules/${RULE_B}`);
    // A firing that overrode nothing has a blank label and draws no override block.
    expect(within(firings[1]).queryByTestId("rule-firing-overrode")).toBeNull();
  });

  it("RulePage_WithFirings_DrawsTheChanges", async () => {
    answers(rulePageWithFirings());

    renderRule();

    const change = await screen.findByTestId("rule-change");
    expect(change.textContent).toBe("Today-ish at 09:15TouchedSession 8a1e (zorp_code)");
    expect(screen.queryByTestId("rule-changes-empty")).toBeNull();
  });

  it("RulePage_WithoutFirings_ShowsTheGatewaysEmptyTextsForFiringsAndChanges", async () => {
    answers(rulePageWithoutFirings());

    renderRule(RULE_B);

    expect((await screen.findByTestId("rule-firings-empty")).textContent).toBe("This rule has not gone off yet.");
    expect(screen.getByTestId("rule-changes-empty").textContent).toBe("No touches recorded.");
    expect(screen.queryAllByTestId("rule-firing")).toHaveLength(0);
    expect(screen.getByTestId("rule-page").textContent).toContain("Allow anything zorpy here.");
  });

  it("RulePage_UnknownRule_ShowsTheGateways404SentenceAndReportsIt", async () => {
    read.next = () =>
      Promise.reject(new GatewayError(404, "x", { reason: "No such rule on this account (Gateway words).", retryable: false }));

    renderRule("nope");

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toBe("No such rule on this account (Gateway words).");
    expect(alert.className).toBe("test-error-banner");
    expect(describeAndReport).toHaveBeenCalledWith("rules", "load the rule", expect.any(GatewayError));
    expect(screen.queryByTestId("rule-page")).toBeNull();
  });
});
