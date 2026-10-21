import type { ReactNode } from "react";
import "./factory.css";
import { SUBSCRIBED_ALREADY_HINT } from "@devthrottle/client-core/plan/RemotePlanNotice";
import { Button, ErrorBanner, LoadingState } from "../components";
import { useFactorySwitch } from "./useFactorySwitch";

// The Factories pages show the area only while the Gateway says it is on. FACTORIES IS ALWAYS IN THE MENU (owner, 8 Oct
// 2026: "the thing we sell should be fourth from the top, not hidden behind a switch"), so while the area is off the
// page says so and how to start, in the Gateway's own sentence - which differs between a self-hosted Gateway and the
// hosted one, so the Cockpit never writes it (rule 7).
//
// When the hosted Gateway says the account's plan does not include Factories ("plan_required"), the page is the upgrade
// page instead: the Gateway's heading and sentence, "See Pro" to the Gateway's pricing address, and Try again for after
// subscribing. The only words the Cockpit writes there are the two button labels and the hint, the same hint the Remote
// plan notice shows (client-core/src/plan/RemotePlanNotice.tsx).
export function FactoryAreaGate({ children }: { children: ReactNode }) {
  const { state, reason, howToStart, title, subscribeUrl, error, askAgain } = useFactorySwitch();
  // error-reported-by: useFactorySwitch
  if (error !== null) return <ErrorBanner message={error} />;
  if (state === "unknown") return <LoadingState />;
  if (state === "off" && reason === "plan_required") {
    // A "plan_required" without its heading or address never reaches here: useFactorySwitch turns it into a reported
    // error.
    return (
      <section className="pane factories-upgrade" data-testid="factories-upgrade">
        <h1 className="pane-title" data-testid="factories-upgrade-title">{title}</h1>
        <p className="pane-note" data-testid="factories-upgrade-body">{howToStart}</p>
        <div className="factories-upgrade-actions">
          <a
            className="ui-btn ui-btn-primary"
            href={subscribeUrl ?? undefined}
            target="_blank"
            rel="noopener noreferrer"
            data-testid="factories-upgrade-see-pro"
          >
            See Pro
          </a>
          <Button variant="secondary" onClick={askAgain} data-testid="factories-upgrade-try-again">
            Try again
          </Button>
        </div>
        <p className="pane-note factories-upgrade-hint" data-testid="factories-upgrade-hint">
          {SUBSCRIBED_ALREADY_HINT}
        </p>
      </section>
    );
  }
  if (state === "off") {
    // An "off" without the Gateway's sentence never reaches here: useFactorySwitch turns it into a reported error.
    return (
      <section className="pane" data-testid="factories-off">
        <h1 className="pane-title">Factories</h1>
        <p className="pane-note">{howToStart}</p>
      </section>
    );
  }
  return <>{children}</>;
}
