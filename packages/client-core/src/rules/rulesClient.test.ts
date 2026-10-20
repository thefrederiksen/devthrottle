import { afterEach, describe, expect, it, vi } from "vitest";
import { GatewayError, gatewayErrorMessage } from "../api/client";
import { getRulePage, getRulesPage, getRulesPreview, viewerTimeZone } from "./rulesClient";
import {
  previewEmpty,
  previewWithDecision,
  RULE_A,
  RULE_B,
  RULE_C,
  rulePageWithDeletedOverride,
  rulePageWithFirings,
  rulesPage,
} from "./rulesFixture.testkit";

// The Rules page's three reads against wire P1 to P3 of the phase 3 plan. The client hands the Gateway's words over
// verbatim, sends the viewer's own time zone as `tz` (ruling T3), carries the Gateway's own error sentence (ruling
// T9), and throws an answer that breaks the contract rather than drawing a guess.

function answering(body: unknown, status = 200) {
  const fetchMock = vi.fn().mockResolvedValue(
    new Response(typeof body === "string" ? body : JSON.stringify(body), {
      status,
      headers: { "Content-Type": "application/json" },
    }),
  );
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function calledUrl(fetchMock: ReturnType<typeof vi.fn>): URL {
  return new URL(String(fetchMock.mock.calls[0][0]), "http://gateway.test");
}

async function rejection(read: Promise<unknown>): Promise<unknown> {
  return read.then(
    () => {
      throw new Error("the read was expected to throw");
    },
    (err: unknown) => err,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("getRulesPage", () => {
  it("getRulesPage_Ok_SendsTheViewersTimeZoneAndHandsTheAnswerOverVerbatim", async () => {
    const fetchMock = answering(rulesPage());

    const answer = await getRulesPage();

    const url = calledUrl(fetchMock);
    expect(url.pathname).toBe("/gateway/rules/page");
    expect(url.searchParams.get("tz")).toBe(Intl.DateTimeFormat().resolvedOptions().timeZone);
    expect(url.searchParams.get("tz")).toBe(viewerTimeZone());
    expect(answer).toEqual(rulesPage());
  });

  it("getRulesPage_GatewayRefusesTheZone_ThrowsWithTheGatewaysSentence", async () => {
    answering({ error: "The time zone Mars/Olympus is not one this Gateway knows." }, 400);

    const err = await rejection(getRulesPage());

    expect(err).toBeInstanceOf(GatewayError);
    expect((err as GatewayError).status).toBe(400);
    expect(gatewayErrorMessage(err, "load the rules")).toBe("The time zone Mars/Olympus is not one this Gateway knows.");
  });

  it("getRulesPage_ToneOutsideTheContract_IsThrownNotDrawn", async () => {
    const page = rulesPage();
    answering({ ...page, rules: [{ ...page.rules[0], modeTone: "purple" }] });

    const err = await rejection(getRulesPage());

    expect(err).toBeInstanceOf(GatewayError);
    expect((err as GatewayError).message).toContain("purple");
  });

  it("getRulesPage_Ok_ReadsThePreviewSuggestions", async () => {
    answering(rulesPage());

    const answer = await getRulesPage();

    expect(answer.previewSuggestions).toEqual({
      machines: ["devbox", "PAGE_BOX"],
      repos: ["zorp.example/acme", "zorp.example/page"],
    });
  });

  it("getRulesPage_MissingPreviewSuggestions_IsThrownNotDrawn", async () => {
    const { previewSuggestions: _suggestions, ...rest } = rulesPage();
    answering(rest);

    const err = await rejection(getRulesPage());

    expect((err as GatewayError).message).toContain("previewSuggestions");
  });

  it("getRulesPage_MissingTabs_IsThrownNotDrawn", async () => {
    const { tabs: _tabs, ...rest } = rulesPage();
    answering(rest);

    const err = await rejection(getRulesPage());

    expect((err as GatewayError).message).toContain("tabs");
  });

  it("getRulesPage_BodyNotJson_SaysTheGatewaySentSomethingUnreadable", async () => {
    answering("<html>not the contract</html>");

    const err = await rejection(getRulesPage());

    expect((err as GatewayError).status).toBe(502);
    expect((err as GatewayError).message).toContain("not valid JSON");
  });
});

describe("getRulePage", () => {
  it("getRulePage_Ok_ReadsTheRulesOwnRouteWithTheTimeZone", async () => {
    const fetchMock = answering(rulePageWithFirings());

    const answer = await getRulePage(RULE_A);

    const url = calledUrl(fetchMock);
    expect(url.pathname).toBe(`/gateway/rules/${RULE_A}/page`);
    expect(url.searchParams.get("tz")).toBe(viewerTimeZone());
    expect(answer).toEqual(rulePageWithFirings());
  });

  it("getRulePage_DeletedReference_ReadsTheDeletedFlag", async () => {
    answering(rulePageWithDeletedOverride());

    const answer = await getRulePage(RULE_A);

    expect(answer.firings[0].overrode).toEqual([{ id: RULE_C, sentence: `(deleted rule ${RULE_C})`, deleted: true }]);
  });

  it("getRulePage_ReferenceWithoutDeletedFlag_IsThrownNotDrawn", async () => {
    const page = rulePageWithFirings();
    answering({
      ...page,
      firings: [{ ...page.firings[0], overrode: [{ id: RULE_B, sentence: "Allow anything zorpy here." }] }],
    });

    const err = await rejection(getRulePage(RULE_A));

    expect((err as GatewayError).message).toContain("deleted");
  });

  it("getRulePage_UnknownRule_ThrowsTheGateways404Sentence", async () => {
    answering({ error: "No rule with the id nope exists on this account." }, 404);

    const err = await rejection(getRulePage("nope"));

    expect((err as GatewayError).status).toBe(404);
    expect(gatewayErrorMessage(err, "load the rule")).toBe("No rule with the id nope exists on this account.");
  });
});

describe("getRulesPreview", () => {
  it("getRulesPreview_WithCommand_SendsAllThreeAndReadsTheDecision", async () => {
    const fetchMock = answering(previewWithDecision());

    const answer = await getRulesPreview({
      machine: "devbox",
      repo: "git@zorp.example:acme/web.git",
      command: "zorp -rf build",
    });

    const url = calledUrl(fetchMock);
    expect(url.pathname).toBe("/gateway/rules/preview");
    expect(url.searchParams.get("machine")).toBe("devbox");
    expect(url.searchParams.get("repo")).toBe("git@zorp.example:acme/web.git");
    expect(url.searchParams.get("command")).toBe("zorp -rf build");
    expect(answer).toEqual(previewWithDecision());
  });

  it("getRulesPreview_NoCommand_ReadsANullDecision", async () => {
    answering(previewEmpty());

    const answer = await getRulesPreview({ machine: "lonely-box", repo: "", command: "" });

    expect(answer.decision).toBeNull();
    expect(answer).toEqual(previewEmpty());
  });

  it("getRulesPreview_CommandMatchedNothing_ReadsAnEmptyDecidingRuleId", async () => {
    const preview = previewWithDecision();
    const decision = {
      command: "ls",
      verdictLabel: "No rule matched",
      tone: "neutral",
      summary: "No rule matched this command.",
      decidingRuleId: "",
      overriddenRuleIds: [],
      matchedRuleIds: [],
    };
    answering({ ...preview, decision });

    const answer = await getRulesPreview({ machine: "devbox", repo: "", command: "ls" });

    expect(answer.decision).toEqual(decision);
  });

  it("getRulesPreview_DecisionMissing_IsThrownNotReadAsNoCommand", async () => {
    const { decision: _decision, ...rest } = previewWithDecision();
    answering(rest);

    const err = await rejection(getRulesPreview({ machine: "devbox", repo: "", command: "zorp" }));

    expect((err as GatewayError).message).toContain("decision");
  });

  it("getRulesPreview_NoMachine_ThrowsTheGateways400Sentence", async () => {
    answering({ error: "Name a machine to preview." }, 400);

    const err = await rejection(getRulesPreview({ machine: "", repo: "", command: "" }));

    expect(gatewayErrorMessage(err, "preview the rules")).toBe("Name a machine to preview.");
  });
});
