import { useCallback, useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { describeAndReport } from "../errors/reportClientError";
import { getRulesPage, type RuleRow, type RulesPageAnswer } from "./rulesClient";
import { RULES_SURFACE, Toned, type RulesShellProps } from "./rulesShared";
import "../settings/settings.css";
import "./rules.css";

// THE RULES LIST (Session Rules part 2, phase 3; wire P1): every rule of the account, newest first, under the six tabs
// the Gateway names - All, Screen, Hook, Session start, Fired today, Changed by agents.
//
// IT RENDERS THE GATEWAY'S WORDS AND DECIDES NOTHING (rule 7, ruling T1). The tabs, their counts and their empty
// texts, which tabs a rule is on, and every label on a rule are the Gateway's. Choosing a tab only filters by the
// tab ids the Gateway put on each rule.
//
// IT LISTS; IT NEVER EDITS. Phase 3 has no write controls on either surface: agents and people change rules with
// `cc-devthrottle rules`.
//
// The chosen tab rides in ?tab= so the same address opens the same tab on either shell, and Back leaves the page
// rather than walking back through the tabs.

export interface RulesListPageProps extends RulesShellProps {
  /** The shell's address of the preview, on a shell that has one (the Cockpit); omitted, no link is drawn. */
  previewHref?: string;
}

export function RulesListPage({ Frame, errorClassName, ruleHref, previewHref }: RulesListPageProps) {
  const [answer, setAnswer] = useState<RulesPageAnswer | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [params, setParams] = useSearchParams();

  const load = useCallback((signal?: AbortSignal) => {
    setError(null);
    getRulesPage(signal).then(
      (read) => {
        if (signal?.aborted !== true) setAnswer(read);
      },
      (err: unknown) => {
        if (signal?.aborted === true) return;
        setError(describeAndReport(RULES_SURFACE, "load the rules", err));
      },
    );
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal);
    return () => controller.abort();
  }, [load]);

  if (error !== null) {
    return (
      <Frame title="Rules">
        <div className={errorClassName} role="alert" data-testid="rules-error">
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
      <Frame title="Rules">
        <p className="settings-loading" role="status" data-testid="rules-loading">
          Loading the rules...
        </p>
      </Frame>
    );
  }

  // A tab the address names that the Gateway did not send opens the first tab the Gateway did send.
  const asked = params.get("tab");
  const active = answer.tabs.find((tab) => tab.id === asked) ?? answer.tabs[0];
  const shown = active === undefined ? [] : answer.rules.filter((rule) => rule.tabs.includes(active.id));
  const choose = (id: string) => setParams({ tab: id }, { replace: true });

  return (
    <Frame title="Rules">
      <div className="rules" data-testid="rules-list">
        <div className="rules-toolbar">
          <p className="settings-hint rules-today" data-testid="rules-today">
            {answer.today}
          </p>
          {previewHref !== undefined && (
            <Link className="settings-btn rules-preview-link" to={previewHref}>
              Preview a machine and repository
            </Link>
          )}
        </div>
        <div className="settings-tabs rules-tabs" role="tablist" aria-label="Rule groups">
          {answer.tabs.map((tab) => (
            <button
              key={tab.id}
              type="button"
              role="tab"
              aria-selected={tab.id === active?.id}
              className={tab.id === active?.id ? "settings-tab active" : "settings-tab"}
              data-testid={`rules-tab-${tab.id}`}
              onClick={() => choose(tab.id)}
            >
              {tab.label}
              <span className="rules-tab-count">{tab.count}</span>
            </button>
          ))}
        </div>
        {active !== undefined && shown.length === 0 ? (
          <div className="settings-card">
            <p className="rules-empty" data-testid="rules-empty">
              {active.emptyText}
            </p>
          </div>
        ) : (
          <ul className="rules-rows">
            {shown.map((rule) => (
              <li key={rule.id}>
                <RuleCard rule={rule} href={ruleHref(rule.id)} />
              </li>
            ))}
          </ul>
        )}
      </div>
    </Frame>
  );
}

function RuleCard({ rule, href }: { rule: RuleRow; href: string }) {
  return (
    <Link className="settings-card rules-row" to={href} data-testid="rules-row" data-rule-id={rule.id}>
      <span className="rules-row-head">
        <span className="rules-sentence">{rule.sentence}</span>
        <span className="settings-pill rules-kind">{rule.kindLabel}</span>
      </span>
      <span className="rules-row-meta">
        <span>{rule.triggerLabel}</span>
        <span>{rule.scopeLabel}</span>
        <Toned tone={rule.modeTone}>{rule.modeLabel}</Toned>
      </span>
      <span className="rules-row-handler">{rule.handlerLabel}</span>
      <span className="rules-row-meta">
        <span data-testid="rules-row-fired">{rule.firedLabel}</span>
        {rule.changedLabel.length > 0 && <span data-testid="rules-row-changed">{rule.changedLabel}</span>}
      </span>
    </Link>
  );
}
