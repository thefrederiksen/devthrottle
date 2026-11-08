import { useEffect, useId, useState, type ComponentType, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { describeAndReport } from "../errors/reportClientError";
import {
  getMailDeskChannels,
  type MailDeskAnswer,
  type MailDeskChannel,
  type MailDeskChannels,
} from "./mailDeskClient";
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
// with the command, `cc-devthrottle maildesk`. The one link is "How does this work?", to the shared documentation page
// (MailDeskHowItWorksPage); each shell passes its address, so no shell renders a dead link.

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

export interface MailDeskMainPageProps extends MailDeskPageProps {
  /** The shell's address of the "How does this work?" page. */
  howItWorksHref: string;
}

export function MailDeskPage({ Frame, notFound, errorClassName, howItWorksHref }: MailDeskMainPageProps) {
  return (
    <MailDeskRead Frame={Frame} notFound={notFound} errorClassName={errorClassName}>
      {(envelope) => (
        <Frame title={envelope.title}>
          <div className="mail-desk" data-testid="mail-desk">
            <div className="mail-desk-intro">
              <p className="settings-hint mail-desk-summary">{envelope.summary}</p>
              <Link className="mail-desk-how-link" to={howItWorksHref}>
                {envelope.howItWorks.button}
              </Link>
            </div>
            {envelope.channels.length === 0 ? (
              <div className="settings-card">
                <p className="mail-desk-empty">{envelope.emptyLabel}</p>
              </div>
            ) : (
              envelope.channels.map((channel) => <ChannelCard key={channel.id} channel={channel} />)
            )}
          </div>
        </Frame>
      )}
    </MailDeskRead>
  );
}

/** The documentation page "How does this work?" opens: the Gateway's heading and its steps, in order. */
export function MailDeskHowItWorksPage({ Frame, notFound, errorClassName }: MailDeskPageProps) {
  return (
    <MailDeskRead Frame={Frame} notFound={notFound} errorClassName={errorClassName}>
      {({ howItWorks }) => (
        <Frame title={howItWorks.title}>
          <div className="mail-desk" data-testid="mail-desk-how-it-works">
            <ol className="settings-card mail-desk-steps">
              {howItWorks.steps.map((step) => (
                <li key={step}>{step}</li>
              ))}
            </ol>
          </div>
        </Frame>
      )}
    </MailDeskRead>
  );
}

/** Reads the Mail Desk once per visit and draws the shell's not-found, the error banner, the loading line, or the page. */
function MailDeskRead({
  Frame,
  notFound,
  errorClassName,
  children,
}: MailDeskPageProps & { children: (envelope: MailDeskChannels) => ReactNode }) {
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

  return <>{children(answer.envelope)}</>;
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
        <Fact name="Checked by" help={channel.checkedBy.help}>
          {channel.checkedBy.label}
          {channel.checkedBy.note !== null && (
            <>
              {" - "}
              <span className="mail-desk-note" data-tone={channel.checkedBy.noteTone ?? undefined}>
                {channel.checkedBy.note}
              </span>
            </>
          )}
        </Fact>
        <Fact name="Signed in" tone={channel.signedIn.tone}>
          {channel.signedIn.label}
        </Fact>
        <Fact name="Sign-in held">{channel.signInHeld}</Fact>
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

function Fact({ name, tone, help, children }: { name: string; tone?: string; help?: string; children: ReactNode }) {
  return (
    <div className="mail-desk-fact">
      <dt>
        {name}
        {help !== undefined && <Help text={help} />}
      </dt>
      <dd data-tone={tone}>{children}</dd>
    </div>
  );
}

/** The "?" beside a fact: its words show on hover, on keyboard focus, and on a tap (a phone has no hover). */
function Help({ text }: { text: string }) {
  const [open, setOpen] = useState(false);
  const id = useId();
  return (
    <span className="mail-desk-help">
      <button
        type="button"
        className="mail-desk-help-button"
        aria-describedby={id}
        aria-expanded={open}
        onClick={() => setOpen((was) => !was)}
        onBlur={() => setOpen(false)}
      >
        ?
      </button>
      <span id={id} className="mail-desk-help-text" role="tooltip" data-open={open ? "true" : undefined}>
        {text}
      </span>
    </span>
  );
}
