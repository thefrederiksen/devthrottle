import { Link } from "react-router-dom";
import { MailDeskPage, type MailDeskFrameProps } from "@devthrottle/client-core/maildesk/MailDeskPage";
import { PageNotFound } from "../components/StaleShellRecovery";

// The phone's Mail Desk screen (Mail Desk mission, phase 7). The SAME page the Cockpit shows, from the same client-core
// component (rule 8); this file is only the phone's frame - the screen, the Back link and the app bar title - and the
// phone's own "Page not found" and its error banner's class. The layout re-tuning for touch is in styles.css, under `.screen`.
function PhoneFrame({ title, children }: MailDeskFrameProps) {
  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        {title !== null && <h1>{title}</h1>}
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
    />
  );
}
