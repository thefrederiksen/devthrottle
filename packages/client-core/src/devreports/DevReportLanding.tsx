import { useEffect, useRef, useState } from "react";
import { getDevReport } from "./devReportsClient";
import "./devReports.css";

// THE ONE LANDING THAT NEEDS ONLY A REPORT ID (dev reports mission, phase 3b, issue #3025).
//
// The printed address is `<gateway>/r/<report id>` and the Gateway decides only WHICH APP opens it, because
// it has to answer with nobody signed in and so cannot look anything up. Everything else is this screen's:
// it reads the report through the authenticated API - it sits inside each shell's device-key gate, so the
// call carries a credential - takes the session off the record, and lands in that report.
//
// It is SHARED because both shells do exactly the same thing here and would otherwise drift apart: the phone
// mounts it at `/report/:reportId` under the `/mobile` basename, the Cockpit at `/report/:reportId` at the
// root, and each passes in the one thing that differs - where that report lives in ITS app.
//
// SIGNED OUT IS NOT THIS SCREEN'S PROBLEM, which is the whole reason the design moved here. `/report/{id}` is
// an IN-SHELL route, so each shell's own gate sends the browser to its own sign-in with
// `next=/report/{id}` - and at the end of the round trip that `next` is handed to the ROUTER, which can
// resolve it. A Gateway path in `next` could not be resolved by either router, which is how the signed-out
// case used to land nowhere.
//
// A REPORT THAT DOES NOT APPEAR IS SAID, NOT GUESSED. The Gateway answers 404 for a report that is not in
// this account - the same answer whether it belongs to somebody else or to nobody - and this screen says so
// and stops. It never redirects to a session it has not been told about.

export type DevReportLandingState =
  | { kind: "opening" }
  | { kind: "notFound" }
  | { kind: "error"; message: string };

export interface DevReportLandingProps {
  /** The report id out of the address. Undefined means the route matched without one, which is a defect. */
  reportId: string | undefined;
  /** Where this report lives in THIS app. Called once, with the session read off the record. */
  onFound: (sessionId: string, reportId: string) => void;
}

/**
 * Read the report and hand the shell its session. Returns what to draw while that is happening, or why it
 * did not happen. `onFound` is held in a ref so a shell that passes a fresh function each render does not
 * re-run the read - the read is keyed on the report id and nothing else.
 */
export function useDevReportLanding(
  reportId: string | undefined,
  onFound: (sessionId: string, reportId: string) => void,
): DevReportLandingState {
  const [state, setState] = useState<DevReportLandingState>({ kind: "opening" });
  const found = useRef(onFound);
  found.current = onFound;

  useEffect(() => {
    if (!reportId) {
      setState({ kind: "error", message: "This address carries no report." });
      return;
    }
    setState({ kind: "opening" });
    const controller = new AbortController();
    getDevReport(reportId, controller.signal)
      .then((detail) => {
        if (controller.signal.aborted) return;
        if (detail === null) {
          setState({ kind: "notFound" });
          return;
        }
        found.current(detail.report.sessionId, detail.report.id);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setState({ kind: "error", message: err instanceof Error ? err.message : String(err) });
      });
    return () => controller.abort();
  }, [reportId]);

  return state;
}

/** The sentence this landing shows while it works, and the one it shows instead when it cannot. */
export function DevReportLanding({ reportId, onFound }: DevReportLandingProps) {
  const state = useDevReportLanding(reportId, onFound);

  return (
    <div className="dev-report-landing" data-testid="dev-report-landing" data-state={state.kind}>
      {state.kind === "opening" && <p className="dev-report-landing-line">Opening the report...</p>}
      {state.kind === "notFound" && (
        <p className="dev-report-landing-line" data-testid="dev-report-landing-not-found">
          This report does not appear.
        </p>
      )}
      {state.kind === "error" && (
        <p className="dev-report-landing-line" role="alert" data-testid="dev-report-landing-error">
          The report could not be opened: {state.message}
        </p>
      )}
    </div>
  );
}
