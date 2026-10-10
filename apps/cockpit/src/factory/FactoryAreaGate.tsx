import type { ReactNode } from "react";
import { ErrorBanner, LoadingState } from "../components";
import { useFactorySwitch } from "./useFactorySwitch";

// The Factories pages show the area only while the Gateway says it is on. FACTORIES IS ALWAYS IN THE MENU (owner, 8 Oct
// 2026: "the thing we sell should be fourth from the top, not hidden behind a switch"), so while the area is off the
// page says so and how to start, in the Gateway's own sentence - which differs between a self-hosted Gateway and the
// hosted one, so the Cockpit never writes it (rule 7).
export function FactoryAreaGate({ children }: { children: ReactNode }) {
  const { state, howToStart, error } = useFactorySwitch();
  // error-reported-by: useFactorySwitch
  if (error !== null) return <ErrorBanner message={error} />;
  if (state === "unknown") return <LoadingState />;
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
