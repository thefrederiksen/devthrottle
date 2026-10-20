import type { ComponentType, ReactNode } from "react";
import { Link } from "react-router-dom";
import type { RuleReference, RuleTone } from "./rulesClient";

// The pieces the three Rules views share: the frame each shell wraps them in, and the toned text.
//
// ONE PAGE, TWO SHELLS (repository rule 8, ruling T8). The Rules list, a rule's page and the preview are written once,
// here, and both shells mount them. Each shell supplies only its frame - the Cockpit's page header, the phone's screen
// with its app bar and Back link - its error banner's class, and the addresses of its own routes.

/** Where a Rules failure is filed. One page on both shells, so one surface. */
export const RULES_SURFACE = "rules";

/** What a shell wraps a Rules view in. The title is the page's name; `backHref` is where its Back link goes. */
export interface RulesFrameProps {
  title: string;
  /** Where Back leads from this view, when it leads somewhere other than the shell's own home. */
  backHref?: string;
  /** What Back is called, when there is a `backHref`. */
  backLabel?: string;
  children: ReactNode;
}

/** The parts of a Rules view that come from the shell. */
export interface RulesShellProps {
  /** The shell's page frame. */
  Frame: ComponentType<RulesFrameProps>;
  /** The class of the shell's own error banner ("ui-error-banner" on the Cockpit, "banner banner-error" on the phone). */
  errorClassName: string;
  /** The shell's address of one rule's page. */
  ruleHref: (id: string) => string;
}

/**
 * The rules one rule names (what a firing overrode, what a rule overrides or is overridden by), each linking to its
 * page - except a deleted rule, which has no page and is shown as the Gateway's text only (plan Amendment 2). The
 * Gateway says which is deleted; this reads its flag and never the sentence.
 */
export function RuleReferences({ items, ruleHref }: { items: RuleReference[]; ruleHref: (id: string) => string }) {
  return (
    <ul className="rules-references">
      {items.map((other) => (
        <li key={other.id}>
          {other.deleted ? (
            <span className="rules-reference-deleted" data-testid="rules-reference-deleted">
              {other.sentence}
            </span>
          ) : (
            <Link to={ruleHref(other.id)}>{other.sentence}</Link>
          )}
        </li>
      ))}
    </ul>
  );
}

/**
 * The Gateway's words in the Gateway's tone. The tone reaches the stylesheet as a data attribute and the stylesheet
 * picks the shell's token (rules.css); this decides nothing about what a tone means.
 */
export function Toned({ tone, children, testId }: { tone: RuleTone; children: ReactNode; testId?: string }) {
  return (
    <span className="rules-toned" data-tone={tone} data-testid={testId}>
      {children}
    </span>
  );
}
