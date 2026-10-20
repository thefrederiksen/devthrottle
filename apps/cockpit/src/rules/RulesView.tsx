import { Link, useParams } from "react-router-dom";
import { RulesListPage } from "@devthrottle/client-core/rules/RulesListPage";
import { RulePage } from "@devthrottle/client-core/rules/RulePage";
import { RulesPreview } from "@devthrottle/client-core/rules/RulesPreview";
import type { RulesFrameProps } from "@devthrottle/client-core/rules/rulesShared";
import "./rules.css";

// The Cockpit's Rules pages (Session Rules part 2, phase 3): the list, one rule's page, and the preview of a machine
// and repository. The pages are client-core's, one implementation for both shells (rule 8, ruling T8); this file is
// only the Cockpit's frame - the page header with its crumb back to the list - its error banner's class
// (components/ErrorBanner.tsx), and the Cockpit's addresses.
//
// The preview is a route of its own, /rules/preview, rather than a panel on /rules: its form rides in the address, so
// a preview can be linked and Back returns to it. A static segment outranks /rules/:id in the router, and a rule id is
// never the word "preview".

export const RULES_PATH = "/rules";
export const RULES_PREVIEW_PATH = "/rules/preview";
export const ruleHref = (id: string) => `/rules/${encodeURIComponent(id)}`;

function CockpitFrame({ title, backHref, backLabel, children }: RulesFrameProps) {
  return (
    <div className="page rules-page">
      <header className="ui-page-header">
        <div className="ui-page-header-text">
          {backHref !== undefined && (
            <p className="rules-crumb">
              <Link to={backHref}>{backLabel ?? "Back"}</Link>
            </p>
          )}
          <h1 className="ui-page-title">{title}</h1>
        </div>
      </header>
      {children}
    </div>
  );
}

const ERROR_CLASS = "ui-error-banner";

export function RulesView() {
  return (
    <RulesListPage Frame={CockpitFrame} errorClassName={ERROR_CLASS} ruleHref={ruleHref} previewHref={RULES_PREVIEW_PATH} />
  );
}

export function RuleView() {
  const { id } = useParams();
  if (id === undefined) throw new Error("RuleView: the route has no :id");
  return <RulePage id={id} Frame={CockpitFrame} errorClassName={ERROR_CLASS} ruleHref={ruleHref} listHref={RULES_PATH} />;
}

export function RulesPreviewView() {
  return <RulesPreview Frame={CockpitFrame} errorClassName={ERROR_CLASS} ruleHref={ruleHref} listHref={RULES_PATH} />;
}
