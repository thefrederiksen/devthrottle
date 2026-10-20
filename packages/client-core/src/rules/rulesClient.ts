// The Rules page's reads (Session Rules part 2, phase 3): the typed, same-origin client for the three read-only Gateway
// routes in docs/missions/session-rules-hooks-2026-10-18/phase-3-plan.md, wire P1 to P3. Every field here is one of
// theirs, with the same name.
//
// CRITICAL RULE 7 - THE GATEWAY DECIDES EVERY WORD (ruling T1). Every label, tab membership, count, empty text, tone
// and every "decides / overrode" verdict is folded once on the Gateway. This client carries them verbatim and never
// turns a kind, a mode or a decision into words of its own.
//
// "TODAY" IS THE VIEWER'S DAY (ruling T3). The list and the rule page send the browser's own IANA time zone as `tz`;
// that is an input, not a verdict, and the Gateway works out the day and every time label in it.
//
// AN ANSWER THAT BREAKS THE CONTRACT IS THROWN, NEVER DRAWN AS A GUESS: a missing list, a label that is not a string,
// a tone the contract does not list. Each would otherwise reach the screen as something plausible.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/** The only thing a client turns into a colour (wire contract): it maps a tone to a style token and nothing more. */
export type RuleTone = "neutral" | "live" | "warn" | "deny" | "allow";

/** One tab of the Rules list, in the Gateway's order, with its count and its empty text. */
export interface RulesTab {
  id: string;
  label: string;
  count: number;
  emptyText: string;
}

/** One rule as the list shows it (wire P1). */
export interface RuleRow {
  id: string;
  sentence: string;
  kindLabel: string;
  triggerLabel: string;
  scopeLabel: string;
  modeLabel: string;
  modeTone: RuleTone;
  handlerLabel: string;
  /** Never blank: "Never fired" when there is no firing. */
  firedLabel: string;
  /** Blank when no session changed the rule. */
  changedLabel: string;
  /** The ids of the tabs this rule is on, as the Gateway decided. */
  tabs: string[];
}

/** GET /gateway/rules/page (wire P1). */
export interface RulesPageAnswer {
  today: string;
  tabs: RulesTab[];
  /** Newest first. */
  rules: RuleRow[];
}

/** One label and its value on a rule's page, in the Gateway's order. */
export interface RuleField {
  label: string;
  value: string;
}

/** A rule named by another (an overridden or overriding rule), with its sentence. */
export interface RuleReference {
  id: string;
  sentence: string;
}

/** One firing of a rule (wire P2). */
export interface RuleFiring {
  id: string;
  whenLabel: string;
  decisionLabel: string;
  tone: RuleTone;
  sessionLabel: string;
  whereLabel: string;
  /** The command it read, or for a screen firing the screen text. */
  subject: string;
  /** Blank when this firing overrode nothing. */
  overrodeLabel: string;
  overrode: RuleReference[];
  outcomeLabel: string;
}

/** One row of a rule's change history (wire P2). */
export interface RuleChange {
  whenLabel: string;
  actionLabel: string;
  byLabel: string;
}

/** GET /gateway/rules/{id}/page (wire P2). */
export interface RulePageAnswer {
  rule: RuleRow;
  fields: RuleField[];
  firings: RuleFiring[];
  firingsEmptyText: string;
  changes: RuleChange[];
  changesEmptyText: string;
}

/** One rule that reaches the previewed machine and repository (wire P3). */
export interface PreviewRule {
  id: string;
  sentence: string;
  kindLabel: string;
  scopeLabel: string;
  rankLabel: string;
  resultLabel: string;
  modeLabel: string;
  overrides: RuleReference[];
  overriddenBy: RuleReference[];
  verdictLabel: string;
}

/** What the hook would decide for the sample command (wire P3); the hook's own resolver, ruling T6. */
export interface PreviewDecision {
  command: string;
  verdictLabel: string;
  tone: RuleTone;
  summary: string;
  /** Null when no rule matched. */
  decidingRuleId: string | null;
  overriddenRuleIds: string[];
  matchedRuleIds: string[];
}

/** The machines and repositories the account's rules and firings name, for the form to offer. */
export interface PreviewSuggestions {
  machines: string[];
  repos: string[];
}

/** GET /gateway/rules/preview (wire P3). */
export interface RulesPreviewAnswer {
  machine: string;
  repo: string;
  headline: string;
  /** Most specific first. */
  rules: PreviewRule[];
  emptyText: string;
  /** Null when no command was given. */
  decision: PreviewDecision | null;
  suggestions: PreviewSuggestions;
}

/** What the preview form asks for. `repo` and `command` may be empty; the Gateway refuses an empty `machine`. */
export interface RulesPreviewQuery {
  machine: string;
  repo: string;
  command: string;
}

/** The viewer's IANA time zone, sent as `tz` so "today" and every time label are the viewer's (ruling T3). */
export function viewerTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/** The Rules list. */
export async function getRulesPage(signal?: AbortSignal): Promise<RulesPageAnswer> {
  const query = new URLSearchParams({ tz: viewerTimeZone() });
  const body = await readJson(`/gateway/rules/page?${query.toString()}`, "load the rules", signal);
  return readRulesPage(body);
}

/** One rule's page. A rule that does not exist is the Gateway's 404, thrown with its sentence. */
export async function getRulePage(id: string, signal?: AbortSignal): Promise<RulePageAnswer> {
  const query = new URLSearchParams({ tz: viewerTimeZone() });
  const body = await readJson(
    `/gateway/rules/${encodeURIComponent(id)}/page?${query.toString()}`,
    "load the rule",
    signal,
  );
  return readRulePage(body);
}

/** The effective rules for a machine and repository, and the decision for a sample command when one is given. */
export async function getRulesPreview(input: RulesPreviewQuery, signal?: AbortSignal): Promise<RulesPreviewAnswer> {
  const query = new URLSearchParams({ machine: input.machine, repo: input.repo, command: input.command });
  const body = await readJson(`/gateway/rules/preview?${query.toString()}`, "preview the rules", signal);
  return readPreview(body);
}

// ---- reading an answer ---------------------------------------------------------------------------------------------

const UNREADABLE = "The Gateway sent a Rules answer this app cannot read";

async function readJson(path: string, what: string, signal: AbortSignal | undefined): Promise<unknown> {
  const res = await gatewayFetch(path, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, what);
  // The Gateway WAS reached; a body that is not JSON is its fault and is said so, never left to surface as a
  // SyntaxError, which the error wording reads as "cannot reach the Gateway".
  const text = await res.text();
  try {
    return JSON.parse(text);
  } catch {
    throw new GatewayError(502, `${UNREADABLE}: its answer is not valid JSON.`);
  }
}

function fail(what: string): never {
  throw new GatewayError(502, `${UNREADABLE}: ${what}.`);
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function object(value: unknown, where: string): Record<string, unknown> {
  if (!isObject(value)) fail(`${where} is not an object`);
  return value;
}

/** A string field. Blank is allowed: where the contract says a label may be blank, the page shows nothing for it. */
function text(source: Record<string, unknown>, field: string, where: string): string {
  const value = source[field];
  if (typeof value !== "string") fail(`${where} has no ${field}`);
  return value;
}

function list(source: Record<string, unknown>, field: string, where: string): unknown[] {
  const value = source[field];
  if (!Array.isArray(value)) fail(`${where} has no list of ${field}`);
  return value;
}

function strings(source: Record<string, unknown>, field: string, where: string): string[] {
  return list(source, field, where).map((item, index) => {
    if (typeof item !== "string") fail(`${where}, ${field} entry ${index + 1} is not a string`);
    return item;
  });
}

function count(source: Record<string, unknown>, field: string, where: string): number {
  const value = source[field];
  if (typeof value !== "number" || !Number.isInteger(value) || value < 0) fail(`${where} has no ${field}`);
  return value;
}

const TONES: ReadonlyArray<RuleTone> = ["neutral", "live", "warn", "deny", "allow"];

function tone(source: Record<string, unknown>, field: string, where: string): RuleTone {
  const value = source[field];
  if (typeof value !== "string" || !(TONES as ReadonlyArray<string>).includes(value)) {
    fail(`${where} has the tone ${JSON.stringify(value)}, which is not one of ${TONES.join(", ")}`);
  }
  return value as RuleTone;
}

function readReference(value: unknown, where: string): RuleReference {
  const source = object(value, where);
  return { id: text(source, "id", where), sentence: text(source, "sentence", where) };
}

function references(source: Record<string, unknown>, field: string, where: string): RuleReference[] {
  return list(source, field, where).map((item, index) => readReference(item, `${where}, ${field} entry ${index + 1}`));
}

function readRule(value: unknown, where: string): RuleRow {
  const source = object(value, where);
  return {
    id: text(source, "id", where),
    sentence: text(source, "sentence", where),
    kindLabel: text(source, "kindLabel", where),
    triggerLabel: text(source, "triggerLabel", where),
    scopeLabel: text(source, "scopeLabel", where),
    modeLabel: text(source, "modeLabel", where),
    modeTone: tone(source, "modeTone", where),
    handlerLabel: text(source, "handlerLabel", where),
    firedLabel: text(source, "firedLabel", where),
    changedLabel: text(source, "changedLabel", where),
    tabs: strings(source, "tabs", where),
  };
}

export function readRulesPage(value: unknown): RulesPageAnswer {
  const source = object(value, "its answer");
  return {
    today: text(source, "today", "its answer"),
    tabs: list(source, "tabs", "its answer").map((item, index) => {
      const where = `tab ${index + 1}`;
      const tab = object(item, where);
      return {
        id: text(tab, "id", where),
        label: text(tab, "label", where),
        count: count(tab, "count", where),
        emptyText: text(tab, "emptyText", where),
      };
    }),
    rules: list(source, "rules", "its answer").map((item, index) => readRule(item, `rule ${index + 1}`)),
  };
}

export function readRulePage(value: unknown): RulePageAnswer {
  const source = object(value, "its answer");
  return {
    rule: readRule(source.rule, "its rule"),
    fields: list(source, "fields", "its answer").map((item, index) => {
      const where = `field ${index + 1}`;
      const field = object(item, where);
      return { label: text(field, "label", where), value: text(field, "value", where) };
    }),
    firings: list(source, "firings", "its answer").map((item, index) => {
      const where = `firing ${index + 1}`;
      const firing = object(item, where);
      return {
        id: text(firing, "id", where),
        whenLabel: text(firing, "whenLabel", where),
        decisionLabel: text(firing, "decisionLabel", where),
        tone: tone(firing, "tone", where),
        sessionLabel: text(firing, "sessionLabel", where),
        whereLabel: text(firing, "whereLabel", where),
        subject: text(firing, "subject", where),
        overrodeLabel: text(firing, "overrodeLabel", where),
        overrode: references(firing, "overrode", where),
        outcomeLabel: text(firing, "outcomeLabel", where),
      };
    }),
    firingsEmptyText: text(source, "firingsEmptyText", "its answer"),
    changes: list(source, "changes", "its answer").map((item, index) => {
      const where = `change ${index + 1}`;
      const change = object(item, where);
      return {
        whenLabel: text(change, "whenLabel", where),
        actionLabel: text(change, "actionLabel", where),
        byLabel: text(change, "byLabel", where),
      };
    }),
    changesEmptyText: text(source, "changesEmptyText", "its answer"),
  };
}

function readDecision(value: unknown): PreviewDecision | null {
  if (value === null) return null;
  const where = "its decision";
  const source = object(value, where);
  const deciding = source.decidingRuleId;
  if (deciding !== null && typeof deciding !== "string") fail(`${where} has no decidingRuleId`);
  return {
    command: text(source, "command", where),
    verdictLabel: text(source, "verdictLabel", where),
    tone: tone(source, "tone", where),
    summary: text(source, "summary", where),
    decidingRuleId: deciding,
    overriddenRuleIds: strings(source, "overriddenRuleIds", where),
    matchedRuleIds: strings(source, "matchedRuleIds", where),
  };
}

export function readPreview(value: unknown): RulesPreviewAnswer {
  const source = object(value, "its answer");
  const suggestions = object(source.suggestions, "its suggestions");
  if (!("decision" in source)) fail("its answer has no decision");
  return {
    machine: text(source, "machine", "its answer"),
    repo: text(source, "repo", "its answer"),
    headline: text(source, "headline", "its answer"),
    rules: list(source, "rules", "its answer").map((item, index) => {
      const where = `preview rule ${index + 1}`;
      const rule = object(item, where);
      return {
        id: text(rule, "id", where),
        sentence: text(rule, "sentence", where),
        kindLabel: text(rule, "kindLabel", where),
        scopeLabel: text(rule, "scopeLabel", where),
        rankLabel: text(rule, "rankLabel", where),
        resultLabel: text(rule, "resultLabel", where),
        modeLabel: text(rule, "modeLabel", where),
        overrides: references(rule, "overrides", where),
        overriddenBy: references(rule, "overriddenBy", where),
        verdictLabel: text(rule, "verdictLabel", where),
      };
    }),
    emptyText: text(source, "emptyText", "its answer"),
    decision: readDecision(source.decision),
    suggestions: {
      machines: strings(suggestions, "machines", "its suggestions"),
      repos: strings(suggestions, "repos", "its suggestions"),
    },
  };
}
