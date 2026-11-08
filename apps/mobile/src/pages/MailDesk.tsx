import { Link } from "react-router-dom";
import { MailDeskHowItWorksPage, MailDeskPage, type MailDeskFrameProps } from "@devthrottle/client-core/maildesk/MailDeskPage";
import { PageNotFound } from "../components/StaleShellRecovery";
import { AppBarAlerts } from "../components/AppBarAlerts";

// The phone's Mail Desk screen (Mail Desk mission, phase 7). The SAME page the Cockpit shows, from the same client-core
// component (rule 8); this file is only the phone's frame - the screen, the Back link and the app bar title - and the
// phone's own "Page not found" and its error banner's class. The layout re-tuning for touch is in styles.css, under `.screen`.
function PhoneFrame({ title, children }: MailDeskFrameProps) {
  return <Screen title={title} back="/">{children}</Screen>;
}

/** The "How does this work?" page goes Back to the Mail Desk, not to the home screen. */
function HowItWorksFrame({ title, children }: MailDeskFrameProps) {
  return <Screen title={title} back="/mail-desk">{children}</Screen>;
}

function Screen({ title, back, children }: MailDeskFrameProps & { back: string }) {
  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to={back}>
          Back
        </Link>
        {title !== null && <h1>{title}</h1>}
        <div className="app-bar-spacer" />
        <AppBarAlerts />
      </header>
      {children}
    </div>
  );
}

export function MailDesk() {
  return (
    <MailDeskPage
      Frame={PhoneFrame}
      notFound={<PageNotFound />}
      errorClassName="banner banner-error"
      howItWorksHref={MAIL_DESK_HOW_IT_WORKS_PATH}
    />
  );
}

/** Where "How does this work?" goes on the phone. */
export const MAIL_DESK_HOW_IT_WORKS_PATH = "/mail-desk/how-it-works";

/** The phone's "How the Mail Desk checks your mail" screen. */
export function MailDeskHowItWorks() {
  return <MailDeskHowItWorksPage Frame={HowItWorksFrame} notFound={<PageNotFound />} errorClassName="banner banner-error" />;
}
