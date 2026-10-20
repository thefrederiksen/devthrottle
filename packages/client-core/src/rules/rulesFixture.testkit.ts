// TEST SUPPORT ONLY - never imported by product code.
//
// Gateway answers for the three Rules routes (wire P1 to P3 of
// docs/missions/session-rules-hooks-2026-10-18/phase-3-plan.md), shaped exactly as the contract writes them. The words
// are deliberately odd - no page could write them itself - so a view that shows any word of its own in place of the
// Gateway's fails, and a view that drops a field fails too. The client-core, Cockpit and phone tests all read these.
import type { RulePageAnswer, RulesPageAnswer, RulesPreviewAnswer } from "./rulesClient";

export const RULE_A = "3f2c0000-0000-4000-8000-00000000000a";
export const RULE_B = "3f2c0000-0000-4000-8000-00000000000b";
export const RULE_C = "3f2c0000-0000-4000-8000-00000000000c";

/** The Rules list: three rules, one tab (Session start) empty, and every tab's words the Gateway's. */
export function rulesPage(): RulesPageAnswer {
  return {
    today: "Moonday 20 Octember",
    tabs: [
      { id: "all", label: "Every-rule tab", count: 3, emptyText: "Nothing on the every-rule tab." },
      { id: "screen", label: "Screenish", count: 1, emptyText: "No screenish rules." },
      { id: "hook", label: "Hookish", count: 2, emptyText: "No hookish rules." },
      {
        id: "session_start",
        label: "Start-of-session",
        count: 0,
        emptyText: "No start-of-session rules: they are not built yet (Gateway words).",
      },
      { id: "fired_today", label: "Went off today", count: 1, emptyText: "Nothing went off today." },
      { id: "changed_by_agents", label: "Touched by agents", count: 1, emptyText: "No agent touched a rule." },
    ],
    rules: [
      {
        id: RULE_A,
        sentence: "Never zorp the build folder.",
        kindLabel: "Hookish kind",
        triggerLabel: "Before a zorp command",
        scopeLabel: "In zorp.example/acme on devbox",
        modeLabel: "Practice run",
        modeTone: "neutral",
        handlerLabel: "Match \"zorp -rf\" then refuse",
        firedLabel: "Went off 3 times today, last at 10:42",
        changedLabel: "Created by session 8a1e (zorp_code) at 09:15",
        tabs: ["all", "hook", "fired_today", "changed_by_agents"],
      },
      {
        id: RULE_B,
        sentence: "Allow anything zorpy here.",
        kindLabel: "Hookish kind",
        triggerLabel: "Before a zorp command",
        scopeLabel: "Everywhere at all",
        modeLabel: "In force",
        modeTone: "live",
        handlerLabel: "Match \"zorp\" then permit",
        firedLabel: "Never went off",
        changedLabel: "",
        tabs: ["all", "hook"],
      },
      {
        id: RULE_C,
        sentence: "Answer the screen's question with yes.",
        kindLabel: "Screenish kind",
        triggerLabel: "When the screen asks",
        scopeLabel: "Everywhere at all",
        modeLabel: "In force",
        modeTone: "live",
        handlerLabel: "Type \"y\"",
        firedLabel: "Never went off",
        changedLabel: "",
        tabs: ["all", "screen"],
      },
    ],
    previewSuggestions: { machines: ["devbox", "PAGE_BOX"], repos: ["zorp.example/acme", "zorp.example/page"] },
  };
}

/** The Rules list of an account with no rules: every tab empty, each with its own text. */
export function noRules(): RulesPageAnswer {
  const page = rulesPage();
  return { ...page, rules: [], tabs: page.tabs.map((tab) => ({ ...tab, count: 0 })) };
}

/** One rule's page with two firings, the first of which overrode another rule. */
export function rulePageWithFirings(): RulePageAnswer {
  const rule = rulesPage().rules[0];
  return {
    rule,
    fields: [
      { label: "Sentence-ish", value: "Never zorp the build folder." },
      { label: "Kindness", value: "Hookish - before a zorp command" },
      { label: "Reach", value: "In zorp.example/acme on devbox (most specific: repo on machine)" },
      { label: "Manner", value: "Practice run - records what it would have done" },
      { label: "Handling", value: "Match: the command holds \"zorp -rf\". Result: refuse" },
      { label: "Promoted by", value: "Not promoted at all" },
      { label: "Made", value: "18 Octember at 14:02" },
    ],
    firings: [
      {
        id: "firing-1",
        whenLabel: "Today-ish at 10:42",
        decisionLabel: "Would have refused",
        tone: "deny",
        sessionLabel: "Session 8a1e (zorp_code)",
        whereLabel: "devbox - zorp.example/acme",
        subject: "zorp -rf build",
        overrodeLabel: "Overrode one other rule",
        overrode: [{ id: RULE_B, sentence: "Allow anything zorpy here.", deleted: false }],
        outcomeLabel: "Practice run: the zorp was not blocked.",
      },
      {
        id: "firing-2",
        whenLabel: "Yesterday-ish at 18:00",
        decisionLabel: "Would have refused",
        tone: "deny",
        sessionLabel: "Session 77c0 (zorp_code)",
        whereLabel: "devbox - zorp.example/acme",
        subject: "zorp -rf dist",
        overrodeLabel: "",
        overrode: [],
        outcomeLabel: "Practice run: the zorp was not blocked, again.",
      },
    ],
    firingsEmptyText: "This rule has not gone off yet.",
    changes: [{ whenLabel: "Today-ish at 09:15", actionLabel: "Touched", byLabel: "Session 8a1e (zorp_code)" }],
    changesEmptyText: "No touches recorded.",
  };
}

/** One rule's page whose only firing overrode a rule that has since been deleted (plan Amendment 2). */
export function rulePageWithDeletedOverride(): RulePageAnswer {
  const page = rulePageWithFirings();
  return {
    ...page,
    firings: [
      {
        ...page.firings[0],
        overrode: [{ id: RULE_C, sentence: `(deleted rule ${RULE_C})`, deleted: true }],
      },
    ],
  };
}

/** One rule's page with no firings and no changes. */
export function rulePageWithoutFirings(): RulePageAnswer {
  const page = rulePageWithFirings();
  return { ...page, rule: rulesPage().rules[1], firings: [], changes: [] };
}

/** The preview with a sample command: B (repo) overrides A (everywhere) and decides. */
export function previewWithDecision(): RulesPreviewAnswer {
  return {
    machine: "devbox",
    repo: "zorp.example/acme",
    headline: "2 rules reach devbox in zorp.example/acme (Gateway words).",
    rules: [
      {
        id: RULE_B,
        sentence: "Allow zorp -rf in this repo.",
        kindLabel: "Hookish kind",
        scopeLabel: "In zorp.example/acme",
        rankLabel: "Repo (rank three)",
        resultLabel: "Permit",
        modeLabel: "Practice run",
        overrides: [{ id: RULE_A, sentence: "Never zorp at all.", deleted: false }],
        overriddenBy: [],
        verdictLabel: "Overrides 1 rule: \"Never zorp at all.\"",
      },
      {
        id: RULE_A,
        sentence: "Never zorp at all.",
        kindLabel: "Hookish kind",
        scopeLabel: "Everywhere at all",
        rankLabel: "Everywhere (rank zero)",
        resultLabel: "Refuse",
        modeLabel: "Practice run",
        overrides: [],
        overriddenBy: [{ id: RULE_B, sentence: "Allow zorp -rf in this repo.", deleted: false }],
        verdictLabel: "Overridden by 1 rule: \"Allow zorp -rf in this repo.\"",
      },
    ],
    emptyText: "No rule reaches devbox in zorp.example/acme.",
    decision: {
      command: "zorp -rf build",
      verdictLabel: "Would permit",
      tone: "allow",
      summary: "\"Allow zorp -rf in this repo.\" (repo) decides. It overrode \"Never zorp at all.\" (everywhere).",
      decidingRuleId: RULE_B,
      overriddenRuleIds: [RULE_A],
      matchedRuleIds: [RULE_B, RULE_A],
    },
    suggestions: { machines: ["devbox", "OTHER_BOX"], repos: ["zorp.example/acme"] },
  };
}

/** The preview where the overriding rule's own overrides list names a rule since deleted (plan Amendment 2). */
export function previewWithDeletedReference(): RulesPreviewAnswer {
  const preview = previewWithDecision();
  return {
    ...preview,
    rules: [
      { ...preview.rules[0], overrides: [{ id: RULE_C, sentence: `(deleted rule ${RULE_C})`, deleted: true }] },
      preview.rules[1],
    ],
  };
}

/** The preview of a machine no rule reaches, with no command. */
export function previewEmpty(): RulesPreviewAnswer {
  return {
    machine: "lonely-box",
    repo: "",
    headline: "No rules reach lonely-box (Gateway words).",
    rules: [],
    emptyText: "No rule reaches lonely-box anywhere.",
    decision: null,
    suggestions: { machines: ["devbox"], repos: [] },
  };
}
