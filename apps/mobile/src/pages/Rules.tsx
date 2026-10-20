import { Link, useParams } from "react-router-dom";
import { RulesListPage } from "@devthrottle/client-core/rules/RulesListPage";
import { RulePage } from "@devthrottle/client-core/rules/RulePage";
import type { RulesFrameProps } from "@devthrottle/client-core/rules/rulesShared";

// The phone's Rules screens (Session Rules part 2, phase 3): the list and one rule's page, read-only. The SAME pages
// the Cockpit shows, from the same client-core components (rule 8, ruling T8); this file is only the phone's frame -
// the screen, the Back link and the app bar title - its error banner's class, and the phone's addresses. The preview
// of a machine and repository is on the Cockpit only. The layout re-tuning for touch is in styles.css, under `.screen`.

const RULES_PATH = "/rules";
const ruleHref = (id: string) => `/rules/${encodeURIComponent(id)}`;

function PhoneFrame({ title, backHref, children }: RulesFrameProps) {
  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to={backHref ?? "/"}>
          Back
        </Link>
        <h1>{title}</h1>
      </header>
      {children}
    </div>
  );
}

const ERROR_CLASS = "banner banner-error";

export function Rules() {
  return <RulesListPage Frame={PhoneFrame} errorClassName={ERROR_CLASS} ruleHref={ruleHref} />;
}

export function Rule() {
  const { id } = useParams();
  if (id === undefined) throw new Error("Rule: the route has no :id");
  return <RulePage id={id} Frame={PhoneFrame} errorClassName={ERROR_CLASS} ruleHref={ruleHref} listHref={RULES_PATH} />;
}
