import { useState, type ReactNode } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { FactoryInboxListView } from "@devthrottle/client-core/factoryinbox/FactoryInboxListView";
import { FactoryInboxThreadView } from "@devthrottle/client-core/factoryinbox/FactoryInboxThreadView";
import { InboxTabs } from "@devthrottle/client-core/factoryinbox/InboxTabs";
import { useFactoryInboxOffered } from "@devthrottle/client-core/factoryinbox/useFactoryInboxOffered";
import { YourInboxListView } from "@devthrottle/client-core/yourinbox/YourInboxListView";
import { YourInboxMessageView } from "@devthrottle/client-core/yourinbox/YourInboxMessageView";
import type { YourInboxLink } from "@devthrottle/client-core/yourinbox/yourInboxClient";
import { PageNotFound } from "../components/StaleShellRecovery";
import { AppBarAlerts } from "../components/AppBarAlerts";

// THE PHONE'S INBOX (Factory Inbox mission, phase 4 and step C; mockups A, B and C). The SAME lists, thread, message and
// composers the Cockpit shows, from client-core (rule 8); this file is only the phone's frame - the screen, the Back link
// and the app bar - the phone's own addresses, and the phone's own "Page not found" while the factory area switch is off
// for this account.
//
// ADDRESSES, the same as the Cockpit's: a factory's thread is /inbox/<factory>; Your inbox is /inbox?tab=yours, and one
// message /inbox?tab=yours&message=<id> - tap to read, Back to the list.

/** Where a Your inbox link opens on the phone: a session, a dev report, or a factory's thread. */
export function phoneInboxHref(link: YourInboxLink): string | null {
  const target = encodeURIComponent(link.target);
  switch (link.kind) {
    case "session":
      return `/session/${target}`;
    case "report":
      return `/report/${target}`;
    case "factory":
      return `/inbox/${target}`;
    default:
      return null;
  }
}

function PhoneFrame({ title, back, children }: { title: string; back: string; children: ReactNode }) {
  return (
    <div className="screen">
      <header className="app-bar fi-phone-bar">
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

/** Mockup A: the Factory inbox list, with the composer on the list itself; mockup C: Your inbox; and one message. */
export function Inbox() {
  const navigate = useNavigate();
  const [search] = useSearchParams();
  const tab = search.get("tab") === "yours" ? "yours" : "factories";
  const message = tab === "yours" ? search.get("message") : null;
  const [subject, setSubject] = useState<string | null>(null);
  if (message !== null) {
    return (
      <Gate>
        <PhoneFrame title={subject ?? "Inbox"} back="/inbox?tab=yours">
          <YourInboxMessageView
            id={message}
            surface="mobile"
            hrefFor={phoneInboxHref}
            showSubject={false}
            onSubject={setSubject}
          />
        </PhoneFrame>
      </Gate>
    );
  }
  return (
    <Gate>
      <PhoneFrame title="Inbox" back="/">
        <InboxTabs active={tab} onSelect={(t) => navigate(t === "yours" ? "/inbox?tab=yours" : "/inbox")} />
        {tab === "factories" ? (
          <FactoryInboxListView surface="mobile" onOpen={(f) => navigate(`/inbox/${encodeURIComponent(f)}`)} />
        ) : (
          <YourInboxListView onOpen={(id) => navigate(`/inbox?tab=yours&message=${encodeURIComponent(id)}`)} />
        )}
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
