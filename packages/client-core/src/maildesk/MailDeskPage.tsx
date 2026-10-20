import { useEffect, useState, type ComponentType, type ReactNode } from "react";
import { describeAndReport } from "../errors/reportClientError";
import { getMailDeskChannels, type MailDeskAnswer, type MailDeskChannel } from "./mailDeskClient";
import "../settings/settings.css";
import "./mailDesk.css";

// THE MAIL DESK PAGE (Mail Desk mission, phase 7; docs/qa/mail-desk/phase-7/CONTRACT.md section 3): every mailbox the
// Mail Desk screens, one card each, in the Gateway's order. One implementation for both shells (repository rule 8):
// each shell passes only its frame - the Cockpit's pane, the phone's screen with its app bar and Back link - its own
// "Page not found", and its error banner's class, so the page looks like the shell it is in. The banner is drawn here,
// not by the shell, so the sentence it shows is reported where it is made (the error display scan in each shell).
//
// IT RENDERS THE GATEWAY'S WORDS AND DECIDES NOTHING (rule 7). Every sentence on a card, and the tone that colours it,
// is the Gateway's (MailDeskChannelFold). The tone reaches the stylesheet as a data attribute and the stylesheet picks
// the shell's token; no field is hidden because its value looks empty.
//
// IT LISTS; IT NEVER EDITS (mission 5.11). There is no control of any kind: a mailbox is added, removed or switched
// with the command, `cc-devthrottle maildesk`.

/** Where a Mail Desk page failure is filed. One page on both shells, so one surface. */
const SURFACE = "mail-desk";

/** What a shell wraps the page in. `title` is the Gateway's, and null until it has answered. */
export interface MailDeskFrameProps {
  title: string | null;
  children: ReactNode;
}

export interface MailDeskPageProps {
  /** The shell's page frame. */
  Frame: ComponentType<MailDeskFrameProps>;
  /** The shell's ordinary "Page not found", shown exactly as an unknown address shows it when the switch is off. */
  notFound: ReactNode;
  /** The class of the shell's own error banner ("ui-error-banner" on the Cockpit, "banner banner-error" on the phone). */
  errorClassName: string;
}

export function MailDeskPage({ Frame, notFound, errorClassName }: MailDeskPageProps) {
  const [answer, setAnswer] = useState<MailDeskAnswer | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    getMailDeskChannels(controller.signal).then(
      (read) => {
        if (!controller.signal.aborted) setAnswer(read);
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        // Shown and reported in one act, once: the page reads once per visit.
        setError(describeAndReport(SURFACE, "read the Mail Desk", err));
      },
    );
    return () => controller.abort();
  }, []);

  if (answer?.kind === "off") return <>{notFound}</>;
  if (error !== null) {
    return (
      <Frame title={null}>
        <div className={errorClassName} role="alert">
          {error}
        </div>
      </Frame>
    );
  }
  if (answer === null) {
    return (
      <Frame title={null}>
        <p className="settings-loading" role="status">
          Loading...
        </p>
      </Frame>
    );
  }

  const { envelope } = answer;
  return (
    <Frame title={envelope.title}>
      <div className="mail-desk" data-testid="mail-desk">
        <p className="settings-hint mail-desk-summary">{envelope.summary}</p>
        {envelope.channels.length === 0 ? (
          <div className="settings-card">
            <p className="mail-desk-empty">{envelope.emptyLabel}</p>
          </div>
        ) : (
          envelope.channels.map((channel) => <ChannelCard key={channel.id} channel={channel} />)
        )}
      </div>
    </Frame>
  );
}

// The fact names below are the page's layout, not a ruling: what each fact SAYS is the Gateway's sentence.
function ChannelCard({ channel }: { channel: MailDeskChannel }) {
  return (
    <article className="settings-card mail-desk-card" data-testid="mail-desk-channel">
      <header className="mail-desk-card-head">
        <h2 className="settings-h2 mail-desk-address">{channel.address}</h2>
        <span className="settings-pill mail-desk-provider">{channel.providerLabel}</span>
      </header>
      <dl className="mail-desk-facts">
        <Fact name="Mode" tone={channel.modeTone}>
          {channel.modeLabel}
        </Fact>
        <Fact name="Machines">{channel.machinesLabel}</Fact>
        <Fact name="Signed in" tone={channel.signedIn.tone}>
          {channel.signedIn.label}
        </Fact>
        <Fact name="Last check" tone={channel.lastCheck.tone}>
          {channel.lastCheck.label}
        </Fact>
      </dl>
      <h3 className="mail-desk-today">{channel.today.label}</h3>
      <ul className="mail-desk-counts">
        {channel.today.counts.map((entry) => (
          <li key={entry.decision} className="mail-desk-count-row">
            <span className="mail-desk-count-label">{entry.label}</span>
            <span className="mail-desk-count">{entry.count}</span>
          </li>
        ))}
      </ul>
    </article>
  );
}

function Fact({ name, tone, children }: { name: string; tone?: string; children: ReactNode }) {
  return (
    <div className="mail-desk-fact">
      <dt>{name}</dt>
      <dd data-tone={tone}>{children}</dd>
    </div>
  );
}
