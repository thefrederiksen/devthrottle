import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { describeAndReport } from "../errors/reportClientError";
import {
  getRulesPage,
  getRulesPreview,
  type PreviewRule,
  type PreviewSuggestions,
  type RuleReference,
  type RulesPreviewAnswer,
} from "./rulesClient";
import { RuleReferences, RULES_SURFACE, Toned, type RulesShellProps } from "./rulesShared";
import "../settings/settings.css";
import "./rules.css";

// PREVIEW A MACHINE AND REPOSITORY (Session Rules part 2, phase 3; wire P3): which rules reach that machine in that
// repository, most specific first, which rule overrides which, and - given a sample command - what the hook would
// decide. The decision is the hook's own resolver on the Gateway (ruling T6), so it cannot differ from what the hook
// does.
//
// IT RENDERS THE GATEWAY'S WORDS AND DECIDES NOTHING (rule 7, ruling T1). The headline, every rank, result and verdict,
// the overrides, the decision and its summary are the Gateway's. The rows the decision names are marked by the ids the
// Gateway sent, as a style only; the words saying so are the Gateway's summary.
//
// The form's values ride in the address (?machine=&repo=&command=), so a preview can be linked and Back returns to it.
// The machines and repositories to offer are the Rules page answer's `previewSuggestions` (plan Amendment 1), read when
// the preview opens - also when it is opened straight from a link - and then each preview answer's `suggestions`.
// Free text is always allowed.
//
// PRESSING PREVIEW ALWAYS READS (pages review, finding 1). The read is keyed on the address AND on a count of presses,
// so pressing Preview with unchanged values - the retry after a failure - reads again and shows the loading state.

export interface RulesPreviewProps extends RulesShellProps {
  /** The shell's address of the Rules list, where Back leads. */
  listHref: string;
}

export function RulesPreview({ Frame, errorClassName, ruleHref, listHref }: RulesPreviewProps) {
  const [params, setParams] = useSearchParams();
  const asked = {
    machine: params.get("machine") ?? "",
    repo: params.get("repo") ?? "",
    command: params.get("command") ?? "",
  };
  const [machine, setMachine] = useState(asked.machine);
  const [repo, setRepo] = useState(asked.repo);
  const [command, setCommand] = useState(asked.command);
  const [answer, setAnswer] = useState<RulesPreviewAnswer | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  // What the form offers: the Rules page answer's lists until a preview answers, then the latest preview's.
  const [suggestions, setSuggestions] = useState<PreviewSuggestions>({ machines: [], repos: [] });
  const [suggestionsError, setSuggestionsError] = useState<string | null>(null);
  const previewAnswered = useRef(false);
  // Bumped by every press of Preview, so a press reads even when the address did not change.
  const [presses, setPresses] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    getRulesPage(controller.signal).then(
      (page) => {
        // A preview that answered first carries lists at least as new; they are kept.
        if (controller.signal.aborted || previewAnswered.current) return;
        setSuggestions(page.previewSuggestions);
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        setSuggestionsError(describeAndReport(RULES_SURFACE, "load the machines and repositories to offer", err));
      },
    );
    return () => controller.abort();
  }, []);

  // A preview is read whenever the address names a machine: on arrival from a link, and after each press of Preview.
  useEffect(() => {
    if (asked.machine.length === 0) return;
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    getRulesPreview(asked, controller.signal).then(
      (read) => {
        if (controller.signal.aborted) return;
        setAnswer(read);
        previewAnswered.current = true;
        setSuggestions(read.suggestions);
        setLoading(false);
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        setAnswer(null);
        setLoading(false);
        setError(describeAndReport(RULES_SURFACE, "preview the rules", err));
      },
    );
    return () => controller.abort();
    // The three values are the read's whole input; `asked` is rebuilt every render from them. `presses` makes a press
    // with unchanged values read again.
  }, [asked.machine, asked.repo, asked.command, presses]);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setParams({ machine: machine.trim(), repo: repo.trim(), command }, { replace: true });
    setPresses((count) => count + 1);
  };

  return (
    <Frame title="Preview a machine and repository" backHref={listHref} backLabel="Rules">
      <div className="rules" data-testid="rules-preview">
        <form className="settings-card rules-preview-form" onSubmit={submit} aria-label="Preview">
          <div className="settings-field">
            <label htmlFor="rules-preview-machine">Machine</label>
            <input
              id="rules-preview-machine"
              className="settings-input"
              list="rules-preview-machines"
              value={machine}
              onChange={(event) => setMachine(event.target.value)}
              autoComplete="off"
            />
            <datalist id="rules-preview-machines" data-testid="rules-preview-machines">
              {suggestions.machines.map((name) => (
                <option key={name} value={name} />
              ))}
            </datalist>
          </div>
          <div className="settings-field">
            <label htmlFor="rules-preview-repo">Repository</label>
            <input
              id="rules-preview-repo"
              className="settings-input"
              list="rules-preview-repos"
              value={repo}
              onChange={(event) => setRepo(event.target.value)}
              placeholder="Optional"
              autoComplete="off"
            />
            <datalist id="rules-preview-repos" data-testid="rules-preview-repos">
              {suggestions.repos.map((name) => (
                <option key={name} value={name} />
              ))}
            </datalist>
          </div>
          <div className="settings-field">
            <label htmlFor="rules-preview-command">Sample command</label>
            <input
              id="rules-preview-command"
              className="settings-input"
              value={command}
              onChange={(event) => setCommand(event.target.value)}
              placeholder="Optional"
              autoComplete="off"
            />
          </div>
          <div className="settings-actions">
            <button type="submit" className="settings-btn primary" disabled={machine.trim().length === 0 || loading}>
              {loading ? "Previewing..." : "Preview"}
            </button>
          </div>
        </form>

        {suggestionsError !== null && (
          <div className={errorClassName} role="alert" data-testid="rules-preview-suggestions-error">
            {suggestionsError}
          </div>
        )}
        {error !== null && (
          <div className={errorClassName} role="alert" data-testid="rules-preview-error">
            {error}
          </div>
        )}
        {loading && (
          <p className="settings-loading" role="status" data-testid="rules-preview-loading">
            Reading the rules for {asked.machine}...
          </p>
        )}
        {!loading && answer !== null && <PreviewResult answer={answer} ruleHref={ruleHref} />}
      </div>
    </Frame>
  );
}

function PreviewResult({ answer, ruleHref }: { answer: RulesPreviewAnswer; ruleHref: (id: string) => string }) {
  const { decision } = answer;
  return (
    <>
      {decision !== null && (
        <section className="settings-card rules-decision" data-testid="rules-preview-decision" data-tone={decision.tone}>
          <h2 className="settings-h2 rules-decision-head">
            <Toned tone={decision.tone} testId="rules-preview-verdict">
              {decision.verdictLabel}
            </Toned>
          </h2>
          <pre className="rules-subject">{decision.command}</pre>
          <p className="rules-decision-summary">{decision.summary}</p>
        </section>
      )}
      <p className="settings-hint rules-headline" data-testid="rules-preview-headline">
        {answer.headline}
      </p>
      {answer.rules.length === 0 ? (
        <div className="settings-card">
          <p className="rules-empty" data-testid="rules-preview-empty">
            {answer.emptyText}
          </p>
        </div>
      ) : (
        <ul className="rules-rows">
          {answer.rules.map((rule) => (
            <li key={rule.id}>
              <PreviewRuleCard
                rule={rule}
                ruleHref={ruleHref}
                deciding={decision?.decidingRuleId === rule.id}
                overridden={decision?.overriddenRuleIds.includes(rule.id) === true}
                matched={decision?.matchedRuleIds.includes(rule.id) === true}
              />
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

function PreviewRuleCard({
  rule,
  ruleHref,
  deciding,
  overridden,
  matched,
}: {
  rule: PreviewRule;
  ruleHref: (id: string) => string;
  deciding: boolean;
  overridden: boolean;
  matched: boolean;
}) {
  return (
    <article
      className="settings-card rules-row rules-preview-rule"
      data-testid="rules-preview-rule"
      data-rule-id={rule.id}
      data-deciding={deciding || undefined}
      data-overridden={overridden || undefined}
      data-matched={matched || undefined}
    >
      <span className="rules-row-head">
        <Link className="rules-sentence" to={ruleHref(rule.id)}>
          {rule.sentence}
        </Link>
        <span className="settings-pill rules-kind">{rule.kindLabel}</span>
      </span>
      <span className="rules-row-meta">
        <span>{rule.scopeLabel}</span>
        <span>{rule.rankLabel}</span>
        <span>{rule.resultLabel}</span>
        <span>{rule.modeLabel}</span>
      </span>
      <span className="rules-row-handler" data-testid="rules-preview-rule-verdict">
        {rule.verdictLabel}
      </span>
      <References id="overrides" heading="Overrides" items={rule.overrides} ruleHref={ruleHref} />
      <References id="overridden-by" heading="Overridden by" items={rule.overriddenBy} ruleHref={ruleHref} />
    </article>
  );
}

// The rules a rule overrides or is overridden by, each linking to its page. The verdict line above says it in the
// Gateway's words; this is the list of the rules it names, under the list's name.
function References({
  id,
  heading,
  items,
  ruleHref,
}: {
  id: string;
  heading: string;
  items: RuleReference[];
  ruleHref: (id: string) => string;
}) {
  if (items.length === 0) return null;
  return (
    <div className="rules-overrode" data-testid={`rules-preview-${id}`}>
      <span className="rules-overrode-label">{heading}</span>
      <RuleReferences items={items} ruleHref={ruleHref} />
    </div>
  );
}
