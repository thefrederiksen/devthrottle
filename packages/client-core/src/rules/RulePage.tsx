import { useCallback, useEffect, useState } from "react";
import { describeAndReport } from "../errors/reportClientError";
import { getRulePage, type RuleFiring, type RulePageAnswer } from "./rulesClient";
import { RuleReferences, RULES_SURFACE, Toned, type RulesShellProps } from "./rulesShared";
import "../settings/settings.css";
import "./rules.css";

// ONE RULE'S PAGE (Session Rules part 2, phase 3; wire P2): the rule's fields, its firing record and its change
// history, read-only on both shells.
//
// IT RENDERS THE GATEWAY'S WORDS AND DECIDES NOTHING (rule 7, ruling T1). `fields` is ordered and complete, so it is
// drawn as a list and nothing is added to it. A firing's decision, its tone, what it overrode and what that meant are
// the Gateway's; a rule it overrode links to that rule's page by the id the Gateway sent, unless the Gateway says it is
// deleted. The override block is drawn when its label is not blank, its list when the list is not empty (Amendment 2).
//
// A rule that does not exist is the Gateway's 404, and its sentence is the error shown (ruling T9).

export interface RulePageProps extends RulesShellProps {
  /** The rule's id, from the shell's address. */
  id: string;
  /** The shell's address of the Rules list, where Back leads. */
  listHref: string;
}

export function RulePage({ id, Frame, errorClassName, ruleHref, listHref }: RulePageProps) {
  const [answer, setAnswer] = useState<RulePageAnswer | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(
    (signal?: AbortSignal) => {
      setError(null);
      setAnswer(null);
      getRulePage(id, signal).then(
        (read) => {
          if (signal?.aborted !== true) setAnswer(read);
        },
        (err: unknown) => {
          if (signal?.aborted === true) return;
          setError(describeAndReport(RULES_SURFACE, "load the rule", err));
        },
      );
    },
    [id],
  );

  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal);
    return () => controller.abort();
  }, [load]);

  const frame = { title: "Rule", backHref: listHref, backLabel: "Rules" };

  if (error !== null) {
    return (
      <Frame {...frame}>
        <div className={errorClassName} role="alert" data-testid="rule-error">
          {error}
        </div>
        <button type="button" className="settings-btn rules-retry" onClick={() => load()}>
          Try again
        </button>
      </Frame>
    );
  }
  if (answer === null) {
    return (
      <Frame {...frame}>
        <p className="settings-loading" role="status" data-testid="rule-loading">
          Loading the rule...
        </p>
      </Frame>
    );
  }

  const { rule } = answer;
  return (
    <Frame {...frame}>
      <div className="rules" data-testid="rule-page">
        <section className="settings-card">
          <h2 className="settings-h2 rules-sentence-heading">
            {rule.sentence} <span className="settings-pill">{rule.kindLabel}</span>
          </h2>
          <p className="rules-row-meta rules-page-meta">
            <Toned tone={rule.modeTone}>{rule.modeLabel}</Toned>
            <span>{rule.firedLabel}</span>
            {rule.changedLabel.length > 0 && <span>{rule.changedLabel}</span>}
          </p>
          <dl className="rules-fields" data-testid="rule-fields">
            {answer.fields.map((field, index) => (
              <div key={`${index}-${field.label}`} className="rules-field">
                <dt>{field.label}</dt>
                <dd>{field.value}</dd>
              </div>
            ))}
          </dl>
        </section>

        <section className="settings-card" data-testid="rule-firings">
          <h2 className="settings-h2 rules-section-heading">Firing record</h2>
          {answer.firings.length === 0 ? (
            <p className="rules-empty" data-testid="rule-firings-empty">
              {answer.firingsEmptyText}
            </p>
          ) : (
            <ul className="rules-firings">
              {answer.firings.map((firing) => (
                <li key={firing.id}>
                  <Firing firing={firing} ruleHref={ruleHref} />
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="settings-card" data-testid="rule-changes">
          <h2 className="settings-h2 rules-section-heading">Changes</h2>
          {answer.changes.length === 0 ? (
            <p className="rules-empty" data-testid="rule-changes-empty">
              {answer.changesEmptyText}
            </p>
          ) : (
            <ul className="rules-changes">
              {answer.changes.map((change, index) => (
                <li key={`${index}-${change.whenLabel}`} className="rules-change" data-testid="rule-change">
                  <span className="rules-change-when">{change.whenLabel}</span>
                  <span className="rules-change-action">{change.actionLabel}</span>
                  <span className="rules-change-by">{change.byLabel}</span>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </Frame>
  );
}

function Firing({ firing, ruleHref }: { firing: RuleFiring; ruleHref: (id: string) => string }) {
  return (
    <article className="rules-firing" data-testid="rule-firing" data-tone={firing.tone}>
      <header className="rules-firing-head">
        <Toned tone={firing.tone} testId="rule-firing-decision">
          {firing.decisionLabel}
        </Toned>
        <span className="rules-firing-when">{firing.whenLabel}</span>
      </header>
      <p className="rules-row-meta">
        <span>{firing.sessionLabel}</span>
        <span>{firing.whereLabel}</span>
      </p>
      {firing.subject.length > 0 && <pre className="rules-subject">{firing.subject}</pre>}
      {firing.overrodeLabel.length > 0 && (
        <div className="rules-overrode" data-testid="rule-firing-overrode">
          <span className="rules-overrode-label">{firing.overrodeLabel}</span>
          {firing.overrode.length > 0 && <RuleReferences items={firing.overrode} ruleHref={ruleHref} />}
        </div>
      )}
      <p className="rules-outcome">{firing.outcomeLabel}</p>
    </article>
  );
}
