import type { ReactNode } from "react";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { ErrorBanner, LoadingState } from "../components";
import { NotFound } from "../panes/NotFound";
import { useFactorySwitch } from "./useFactorySwitch";

// The Factory Agents routes exist only while the Gateway says the area is on. Off, a route shows nothing of the
// area - the ordinary "Page not found" - exactly as if the pages were not built.
export function FactoryAreaGate({ children }: { children: ReactNode }) {
  const { state, error } = useFactorySwitch();
  if (error !== null) return <ErrorBanner message={gatewayErrorMessage(error, "ask the Gateway whether Factory Agents is on")} />;
  if (state === "unknown") return <LoadingState />;
  if (state === "off") return <NotFound />;
  return <>{children}</>;
}
