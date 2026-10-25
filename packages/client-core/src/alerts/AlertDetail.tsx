import type { ReactNode } from "react";
import type { SecretTransferPlace } from "../secrets/secretTransfersClient";
import { RestartRequestActions } from "./actions/RestartRequestActions";
import { SecretTransferActions } from "./actions/SecretTransferActions";
import { MessageRequestActions } from "./actions/MessageRequestActions";
import { alertClock, toneClass, type AlertDto } from "./alertsClient";
import "./alerts.css";

// ONE ALERT, WITH ITS ANSWER ON IT (the Alerts mission, sections 5.3 and phase 5): the plain question as its heading,
// the Gateway's one sentence of who asked and why, when it expires, the kind's action buttons, and the machine
// diagnostics behind a collapsed "Details". One tap answers - there is no second screen to open first.
//
// Every sentence is the Gateway's. The reason a session gave is already in that one sentence, so it is never quoted a
// second time below it. The action blocks are the feature cards' own, moved here, and they call each feature's
// existing answer route.
//
// It is the card in "Needs you", the body of an Earlier row opened in place (`headless`, the row already shows the
// title), and the popover of a session's own ask chip in the Cockpit.

export interface AlertDetailProps {
  alert: AlertDto;
  /** Where a secret transfer answered here is recorded: "phone", "cockpit" or "badge". */
  where: SecretTransferPlace;
  /** Called after an answer landed, so the list is read again at once. */
  onAnswered: () => void;
  /** Leave out the heading and the summary - an Earlier row opened in place already shows them. */
  headless?: boolean;
}

export function AlertDetail({ alert, where, onAnswered, headless = false }: AlertDetailProps) {
  const headingId = `alert-heading-${alert.id}`;
  return (
    <article
      className="alert-detail"
      data-kind={alert.kind}
      data-testid="alert-detail"
      aria-labelledby={headless ? undefined : headingId}
      aria-label={headless ? alert.title : undefined}
    >
      {headless ? null : (
        <>
          <h3 className="alert-detail-title" id={headingId}>
            {alert.seen ? null : <span className="alerts-visually-hidden">New: </span>}
            {alert.title}
          </h3>
          <p className="alert-line alert-summary">{alert.summary}</p>
          {alert.needsAnswer ? null : <p className={`alert-line alert-status ${toneClass(alert.tone)}`}>{alert.status}</p>}
        </>
      )}
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
          {alert.needsAnswer && t.replaceNote ? <p className="alert-line alert-strong">{t.replaceNote}</p> : null}
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

/** The machine diagnostics, collapsed. Every line wraps; nothing here can push the card wider. */
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
