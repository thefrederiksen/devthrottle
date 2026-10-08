import type { ReactNode } from "react";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { ErrorBanner, LoadingState } from "../components";
import { useFactorySwitch } from "./useFactorySwitch";

// The Factories pages show the area only while the Gateway says it is on. FACTORIES IS ALWAYS IN THE MENU (owner, 8 Oct
// 2026: "the thing we sell should be fourth from the top, not hidden behind a switch"), so while the area is off the
// page says so and how to start, in the Gateway's own sentence - which differs between a self-hosted Gateway and the
// hosted one, so the Cockpit never writes it (rule 7).
export function FactoryAreaGate({ children }: { children: ReactNode }) {
  const { state, howToStart, error } = useFactorySwitch();
  if (error !== null) return <ErrorBanner message={gatewayErrorMessage(error, "ask the Gateway whether Factories is on")} />;
  if (state === "unknown") return <LoadingState />;
  if (state === "off") {
    // The Gateway's contract is a sentence with every "off". Without one, say exactly that - in the page, as an error,
    // rather than unmounting the whole app (the Cockpit has no error boundary).
    if (howToStart === null) {
      return <ErrorBanner message="The Gateway says Factories is off, but did not say how to start it. This is a fault in the Gateway; please report it." />;
    }
    return (
      <section className="pane" data-testid="factories-off">
        <h1 className="pane-title">Factories</h1>
        <p className="pane-note">{howToStart}</p>
      </section>
    );
  }
  return <>{children}</>;
}
