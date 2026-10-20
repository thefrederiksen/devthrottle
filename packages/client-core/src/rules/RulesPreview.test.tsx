import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { GatewayError } from "../api/client";
import type { RulesPreviewAnswer, RulesPreviewQuery } from "./rulesClient";
import { previewEmpty, previewWithDecision, RULE_A, RULE_B } from "./rulesFixture.testkit";
import type { RulesFrameProps } from "./rulesShared";

// The preview of a machine and repository (wire P3): the form, the suggestions the Gateway offers, the rules most
// specific first with which overrides which, and the hook's decision for a sample command - every word the Gateway's.

const read = vi.hoisted(() => ({
  next: null as (() => Promise<RulesPreviewAnswer>) | null,
  queries: [] as RulesPreviewQuery[],
}));
vi.mock("./rulesClient", () => ({
  getRulesPreview: vi.fn((query: RulesPreviewQuery) => {
    read.queries.push(query);
    if (read.next === null) throw new Error("no preview answer staged");
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
import { RulesPreview } from "./RulesPreview";

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
  return <span data-testid="address">{location.search}</span>;
}

function renderPreview(at = "/rules/preview") {
  return render(
    <MemoryRouter initialEntries={[at]}>
      <Routes>
        <Route
          path="*"
          element={
            <>
              <RulesPreview
                Frame={Frame}
                errorClassName="test-error-banner"
                ruleHref={(id) => `/test-rules/${id}`}
                listHref="/test-rules"
              />
              <Address />
            </>
          }
        />
      </Routes>
    </MemoryRouter>,
  );
}

function answers(answer: RulesPreviewAnswer) {
  read.next = () => Promise.resolve(answer);
}

function previewButton(): HTMLButtonElement {
  return screen.getByRole("button", { name: "Preview" }) as HTMLButtonElement;
}

beforeEach(() => {
  read.next = null;
  read.queries = [];
  vi.mocked(describeAndReport).mockClear();
});

afterEach(() => {
  cleanup();
});

describe("RulesPreview", () => {
  it("RulesPreview_NoMachineYet_ReadsNothingAndCannotBeSubmitted", () => {
    renderPreview();

    expect(previewButton().disabled).toBe(true);
    expect(read.queries).toEqual([]);
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("RulesPreview_Submit_SendsTheFormWritesItToTheAddressAndShowsLoadingAtOnce", async () => {
    read.next = () => new Promise(() => {});
    renderPreview();

    fireEvent.change(screen.getByLabelText("Machine"), { target: { value: " devbox " } });
    fireEvent.change(screen.getByLabelText("Repository"), { target: { value: "git@zorp.example:acme/web.git" } });
    fireEvent.change(screen.getByLabelText("Sample command"), { target: { value: "zorp -rf build" } });
    fireEvent.click(previewButton());

    expect(await screen.findByRole("status")).toBeTruthy();
    expect(read.queries).toEqual([{ machine: "devbox", repo: "git@zorp.example:acme/web.git", command: "zorp -rf build" }]);
    const address = new URLSearchParams(screen.getByTestId("address").textContent ?? "");
    expect(address.get("machine")).toBe("devbox");
    expect(address.get("command")).toBe("zorp -rf build");
  });

  it("RulesPreview_AddressNamesAMachine_ReadsOnArrival", async () => {
    answers(previewWithDecision());

    renderPreview("/rules/preview?machine=devbox&repo=zorp.example%2Facme&command=zorp%20-rf%20build");

    await screen.findByTestId("rules-preview-headline");
    expect(read.queries).toEqual([{ machine: "devbox", repo: "zorp.example/acme", command: "zorp -rf build" }]);
    expect((screen.getByLabelText("Machine") as HTMLInputElement).value).toBe("devbox");
  });

  it("RulesPreview_WithADecision_ShowsTheVerdictToneAndSummaryVerbatim", async () => {
    const answer = previewWithDecision();
    answers(answer);

    renderPreview("/rules/preview?machine=devbox&command=zorp%20-rf%20build");

    const decision = await screen.findByTestId("rules-preview-decision");
    expect(within(decision).getByTestId("rules-preview-verdict").textContent).toBe("Would permit");
    expect(within(decision).getByTestId("rules-preview-verdict").getAttribute("data-tone")).toBe("allow");
    expect(decision.textContent).toContain("zorp -rf build");
    expect(decision.textContent).toContain(answer.decision?.summary ?? "missing");
    expect(screen.getByTestId("rules-preview-headline").textContent).toBe(answer.headline);
  });

  it("RulesPreview_WithAnOverride_DrawsTheRulesInOrderWithWhoOverridesWhomLinked", async () => {
    answers(previewWithDecision());

    renderPreview("/rules/preview?machine=devbox&command=zorp");

    const rows = await screen.findAllByTestId("rules-preview-rule");
    expect(rows.map((row) => row.getAttribute("data-rule-id"))).toEqual([RULE_B, RULE_A]);
    for (const word of ["In zorp.example/acme", "Repo (rank three)", "Permit", "Practice run", "Overrides 1 rule: \"Never zorp at all.\""]) {
      expect(rows[0].textContent, word).toContain(word);
    }
    const overrides = within(rows[0]).getByTestId("rules-preview-overrides");
    expect(within(overrides).getByRole("link", { name: "Never zorp at all." }).getAttribute("href")).toBe(`/test-rules/${RULE_A}`);
    const overriddenBy = within(rows[1]).getByTestId("rules-preview-overridden-by");
    expect(within(overriddenBy).getByRole("link", { name: "Allow zorp -rf in this repo." }).getAttribute("href")).toBe(
      `/test-rules/${RULE_B}`,
    );
    expect(within(rows[0]).queryByTestId("rules-preview-overridden-by")).toBeNull();
  });

  it("RulesPreview_WithADecision_MarksTheRowsByTheGatewaysIds", async () => {
    answers(previewWithDecision());

    renderPreview("/rules/preview?machine=devbox&command=zorp");

    const rows = await screen.findAllByTestId("rules-preview-rule");
    expect(rows[0].hasAttribute("data-deciding")).toBe(true);
    expect(rows[0].hasAttribute("data-overridden")).toBe(false);
    expect(rows[1].hasAttribute("data-deciding")).toBe(false);
    expect(rows[1].hasAttribute("data-overridden")).toBe(true);
    expect(rows.every((row) => row.hasAttribute("data-matched"))).toBe(true);
  });

  it("RulesPreview_Answer_OffersTheGatewaysSuggestions", async () => {
    answers(previewWithDecision());

    renderPreview("/rules/preview?machine=devbox");

    await screen.findByTestId("rules-preview-headline");
    const machines = Array.from(screen.getByTestId("rules-preview-machines").querySelectorAll("option")).map((o) => o.value);
    const repos = Array.from(screen.getByTestId("rules-preview-repos").querySelectorAll("option")).map((o) => o.value);
    expect(machines).toEqual(["devbox", "OTHER_BOX"]);
    expect(repos).toEqual(["zorp.example/acme"]);
  });

  it("RulesPreview_NoRuleAndNoCommand_ShowsTheGatewaysEmptyTextAndNoDecision", async () => {
    answers(previewEmpty());

    renderPreview("/rules/preview?machine=lonely-box");

    expect((await screen.findByTestId("rules-preview-empty")).textContent).toBe("No rule reaches lonely-box anywhere.");
    expect(screen.queryByTestId("rules-preview-decision")).toBeNull();
    expect(screen.queryAllByTestId("rules-preview-rule")).toHaveLength(0);
  });

  it("RulesPreview_GatewayError_ShowsTheGatewaysSentenceAndKeepsTheForm", async () => {
    read.next = () =>
      Promise.reject(new GatewayError(400, "x", { reason: "That machine name is not one the Gateway accepts.", retryable: false }));

    renderPreview("/rules/preview?machine=%3F%3F");

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toBe("That machine name is not one the Gateway accepts.");
    expect(alert.className).toBe("test-error-banner");
    expect(describeAndReport).toHaveBeenCalledWith("rules", "preview the rules", expect.any(GatewayError));
    expect(screen.getByLabelText("Machine")).toBeTruthy();
    expect(screen.queryByTestId("rules-preview-headline")).toBeNull();
  });
});
