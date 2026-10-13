// The one "Remote plan needed" notice both shells show (the Pricing Change mission). It is the same
// screen in the two places the Gateway refuses a Free account's phone or browser: at sign-in (the
// shared DeviceCallback renders it) and in the middle of a signed-in session (RemotePlanGate renders it
// in place of the app).
//
// It follows the pattern of the phone's CreditsNotice: the Gateway's sentence is shown as it is, and the
// button exists only when the Gateway sent an address to open. This client writes no sentence about
// plans. It is laid out like the shared Sign in and DeviceCallback screens it sits beside, so it needs
// no stylesheet from either shell.
import { useSyncExternalStore, type ReactNode } from "react";
import {
  clearRemotePlanRequired,
  currentRemotePlanRequired,
  subscribeRemotePlanRequired,
  type RemotePlanRequired,
} from "./remotePlan";

const container = { maxWidth: 420, margin: "0 auto", padding: "2.5rem 1.25rem", textAlign: "center" as const };

const button = {
  display: "block", width: "100%", boxSizing: "border-box" as const,
  padding: "0.9rem 1rem", fontSize: "1rem", fontWeight: 600,
  borderRadius: 10, border: "none", cursor: "pointer",
  textAlign: "center" as const, textDecoration: "none",
};

interface RemotePlanNoticeProps {
  /** What the Gateway said. */
  info: RemotePlanRequired;
  /** What "try again" does where this notice is shown: sign in again, or load the app again. */
  onTryAgain: () => void;
}

export function RemotePlanNotice({ info, onTryAgain }: RemotePlanNoticeProps) {
  return (
    <div style={container} role="alert" data-testid="remote-plan-notice">
      <h1>Remote plan needed</h1>
      <p style={{ opacity: 0.8, marginBottom: "1.5rem" }} data-testid="remote-plan-message">{info.message}</p>
      {info.subscribeUrl !== null && (
        <a
          href={info.subscribeUrl}
          target="_blank"
          rel="noopener noreferrer"
          data-testid="remote-plan-subscribe"
          style={{ ...button, background: "#0066B8", color: "#FFFFFF", marginBottom: "0.75rem" }}
        >
          Open the pricing page
        </a>
      )}
      <button type="button" onClick={onTryAgain} style={button} data-testid="remote-plan-try-again">
        Try again
      </button>
      <p style={{ opacity: 0.7, marginTop: "1.25rem", fontSize: "0.9rem" }}>
        Subscribed already? Press Try again.
      </p>
    </div>
  );
}

function reloadTheApp(): void {
  window.location.reload();
}

/**
 * Wraps a shell's whole app. While the Gateway's refusal is in effect it shows the notice INSTEAD of
 * the app, so the screens that poll are unmounted: nothing keeps asking, nothing shows a raw error,
 * and nothing loops. "Try again" forgets the refusal and loads the app afresh; if the account still
 * has no plan, the first request is refused again and the notice returns.
 */
export function RemotePlanGate({ children, reload = reloadTheApp }: { children: ReactNode; reload?: () => void }) {
  const info = useSyncExternalStore(subscribeRemotePlanRequired, currentRemotePlanRequired);
  if (info === null) return <>{children}</>;
  return (
    // error-report-exempt: shows the Gateway's own refusal and the sentence it handed back (rule 7) - the account's plan is the person's state, nothing failed in this client
    <RemotePlanNotice
      info={info}
      onTryAgain={() => {
        clearRemotePlanRequired();
        reload();
      }}
    />
  );
}
