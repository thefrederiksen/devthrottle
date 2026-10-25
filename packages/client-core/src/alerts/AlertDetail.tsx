import type { ReactNode } from "react";
import type { SecretTransferPlace } from "../secrets/secretTransfersClient";
import { RestartRequestActions } from "./actions/RestartRequestActions";
import { SecretTransferActions } from "./actions/SecretTransferActions";
import { MessageRequestActions } from "./actions/MessageRequestActions";
import { alertClock, type AlertDto } from "./alertsClient";
import "./alerts.css";

// ONE ALERT, OPENED (the Alerts mission, section 5.3): its title and status, the Gateway's one-sentence summary, the
// lines the owner needs before he answers, the kind's action block, and the machine diagnostics behind a collapsed
// "Details". Every sentence is the Gateway's; the action blocks are the feature cards' own, moved here, and they call
// each feature's existing answer route.

export interface AlertDetailProps {
  alert: AlertDto;
  /** Where a secret transfer answered here is recorded: "phone" on the phone, "cockpit" on the Cockpit. */
  where: SecretTransferPlace;
  /** Called after an answer landed, so the list is read again at once. */
  onAnswered: () => void;
}

export function AlertDetail({ alert, where, onAnswered }: AlertDetailProps) {
  return (
    <article className="alert-detail" data-kind={alert.kind} data-testid="alert-detail" aria-label={alert.title}>
      <header className="alert-detail-head">
        <h3 className="alert-detail-title">{alert.title}</h3>
        <span className={`alert-status ${alert.needsAnswer ? "alert-status-needs" : ""}`}>{alert.status}</span>
      </header>
      <p className="alert-line alert-summary">{alert.summary}</p>
      {alert.needsAnswer && alert.expiresAtUtc ? (
        <p className="alert-line alert-dim">Expires {alertClock(alert.expiresAtUtc)}</p>
      ) : null}
      <KindBody alert={alert} where={where} onAnswered={onAnswered} />
    </article>
  );
}

function KindBody({ alert, where, onAnswered }: AlertDetailProps) {
  switch (alert.kind) {
    case "restart-request": {
      const r = alert.detail;
      return (
        <>
          {alert.needsAnswer && r.liveSessionsSentence ? <p className="alert-line">{r.liveSessionsSentence}</p> : null}
          <RestartRequestActions request={r} onAnswered={onAnswered} />
          <Details>
            <DetailLine label="Director">{r.directorName} on {r.machine}</DetailLine>
            {r.capability?.launcherVersion ? <DetailLine label="Launcher version">{r.capability.launcherVersion}</DetailLine> : null}
            {r.capability ? <DetailLine label="Capability check">{r.capability.reason}</DetailLine> : null}
            {r.capability?.guardedRestartReason ? <DetailLine label="Guarded restart">{r.capability.guardedRestartReason}</DetailLine> : null}
            {r.stateReason ? <DetailLine label="Outcome">{r.stateReason}</DetailLine> : null}
            {r.workspaceId ? <DetailLine label="Record">workspace {r.workspaceId}</DetailLine> : null}
          </Details>
        </>
      );
    }
    case "secret-transfer": {
      const t = alert.detail;
      return (
        <>
          {t.replaceNote ? <p className="alert-line alert-strong">{t.replaceNote}</p> : null}
          {t.reason ? <p className="alert-line alert-quote">"{t.reason}"</p> : null}
          <SecretTransferActions transfer={t} where={where} onAnswered={onAnswered} />
          <Details>
            <DetailLine label="Moves">{t.summary}</DetailLine>
            <DetailLine label="Asked by">{t.askedBy}</DetailLine>
            <DetailLine label="Where it stands">{t.statusText}</DetailLine>
          </Details>
        </>
      );
    }
    case "message-request": {
      const m = alert.detail;
      return (
        <>
          {m.reason ? <p className="alert-line alert-quote">"{m.reason}"</p> : null}
          <MessageRequestActions request={m} onAnswered={onAnswered} />
          <Details>
            {m.requesterLabel ? <DetailLine label="Asking session">{m.requesterLabel}</DetailLine> : null}
            {m.targetLabel ? <DetailLine label="Session to message">{m.targetLabel}</DetailLine> : null}
          </Details>
        </>
      );
    }
    default: {
      // A Gateway newer than this app can list a kind this app has no answer for. Say so plainly, never guess.
      return <p className="alert-line alert-dim">This version of the app cannot answer this kind of alert. Update the app to answer it here.</p>;
    }
  }
}

/** The machine diagnostics, collapsed. Every line wraps; nothing here can push the panel wider. */
function Details({ children }: { children: ReactNode }) {
  return (
    <details className="alert-details">
      <summary className="alert-details-summary">Details</summary>
      <div className="alert-details-body">{children}</div>
    </details>
  );
}

function DetailLine({ label, children }: { label: string; children: ReactNode }) {
  return (
    <p className="alert-line alert-detail-line">
      <span className="alert-detail-label">{label}:</span> {children}
    </p>
  );
}
