import { useState, type ReactNode } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { FactoryInboxListView } from "@devthrottle/client-core/factoryinbox/FactoryInboxListView";
import { FactoryInboxThreadView } from "@devthrottle/client-core/factoryinbox/FactoryInboxThreadView";
import { InboxTabs } from "@devthrottle/client-core/factoryinbox/InboxTabs";
import { useFactoryInboxOffered } from "@devthrottle/client-core/factoryinbox/useFactoryInboxOffered";
import { PageNotFound } from "../components/StaleShellRecovery";
import { AppBarAlerts } from "../components/AppBarAlerts";

// THE PHONE'S INBOX (Factory Inbox mission, phase 4; mockups A and B). The SAME list, thread and composer the Cockpit
// shows, from client-core (rule 8); this file is only the phone's frame - the screen, the Back link and the app bar - and
// the phone's own "Page not found" while the factory area switch is off for this account.

function PhoneFrame({ title, back, children }: { title: string; back: string; children: ReactNode }) {
  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to={back}>
          Back
        </Link>
        <h1>{title}</h1>
        <div className="app-bar-spacer" />
        <AppBarAlerts />
      </header>
      <div className="fi-phone-body">{children}</div>
    </div>
  );
}

function Gate({ children }: { children: ReactNode }) {
  const { offer, error } = useFactoryInboxOffered();
  if (offer === "off") return <PageNotFound />;
  if (offer === "unknown") {
    return (
      <PhoneFrame title="Inbox" back="/">
        {error !== null ? (
          // error-reported-by: useFactoryInboxOffered
          <div className="banner banner-error" role="alert">
            {error}
          </div>
        ) : (
          <p role="status">Loading...</p>
        )}
      </PhoneFrame>
    );
  }
  return <>{children}</>;
}

/** Mockup A: the Factory inbox list, with the composer on the list itself. */
export function Inbox() {
  const navigate = useNavigate();
  return (
    <Gate>
      <PhoneFrame title="Inbox" back="/">
        <InboxTabs active="factories" onSelect={() => undefined} />
        <FactoryInboxListView surface="mobile" onOpen={(f) => navigate(`/inbox/${encodeURIComponent(f)}`)} />
      </PhoneFrame>
    </Gate>
  );
}

/** Mockup B: one factory's thread. */
export function InboxThread() {
  const { factory = "" } = useParams();
  const [title, setTitle] = useState(factory);
  return (
    <Gate>
      <PhoneFrame title={title} back="/inbox">
        <FactoryInboxThreadView factory={factory} surface="mobile" showTitle={false} onTitle={setTitle} />
      </PhoneFrame>
    </Gate>
  );
}
