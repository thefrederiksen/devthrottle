import { useNavigate, useParams, useSearchParams } from "react-router-dom";
import { FactoryInboxListView } from "@devthrottle/client-core/factoryinbox/FactoryInboxListView";
import { FactoryInboxThreadView } from "@devthrottle/client-core/factoryinbox/FactoryInboxThreadView";
import { InboxTabs } from "@devthrottle/client-core/factoryinbox/InboxTabs";
import { YourInboxListView } from "@devthrottle/client-core/yourinbox/YourInboxListView";
import { YourInboxMessageView } from "@devthrottle/client-core/yourinbox/YourInboxMessageView";
import type { YourInboxLink } from "@devthrottle/client-core/yourinbox/yourInboxClient";

// THE COCKPIT'S INBOX (Factory Inbox mission, phase 4 and step C; mockup D): two tabs, each a list on the left and a
// reading pane on the right. Factory inbox: the factories and the open factory's thread. Your inbox: the messages to the
// owner and the open message. The lists, the thread, the message and the composers are client-core's, the same the phone
// shows (rule 8); this file is only the Cockpit's frame - the pane, its title, the two columns, and the Cockpit's own
// addresses. The route sits inside FactoryAreaGate, so with factories off for the account this page says so and how to
// start, as /factories does.
//
// ADDRESSES. A factory's thread is /inbox/<factory>, as before. Your inbox is /inbox?tab=yours, and an open message
// /inbox?tab=yours&message=<id> - a query, not a path, so no factory's name can ever collide with the tab.

/** Where a Your inbox link opens in the Cockpit: a session, a dev report, or a factory's thread on the other tab. */
export function cockpitInboxHref(link: YourInboxLink): string | null {
  const target = encodeURIComponent(link.target);
  switch (link.kind) {
    case "session":
      return `/sessions/${target}`;
    case "report":
      return `/report/${target}`;
    case "factory":
      return `/inbox/${target}`;
    default:
      return null;
  }
}

export function InboxView() {
  const { factory } = useParams();
  const [search] = useSearchParams();
  const navigate = useNavigate();
  const tab = factory === undefined && search.get("tab") === "yours" ? "yours" : "factories";
  const open = factory ?? null;
  const message = search.get("message");
  return (
    <section className="pane" data-testid="inbox-page">
      <h1 className="pane-title">Inbox</h1>
      <InboxTabs active={tab} onSelect={(t) => navigate(t === "yours" ? "/inbox?tab=yours" : "/inbox")} />
      {tab === "factories" ? (
        <div className="fi-split">
          <FactoryInboxListView
            surface="cockpit"
            selected={open}
            showComposer={open === null}
            onOpen={(f) => navigate(`/inbox/${encodeURIComponent(f)}`)}
          />
          <div className="fi-reading" data-testid="inbox-reading-pane">
            {open === null ? (
              <p className="fi-reading-empty">Pick a factory to read its messages and what was decided on each.</p>
            ) : (
              <FactoryInboxThreadView factory={open} surface="cockpit" />
            )}
          </div>
        </div>
      ) : (
        <div className="fi-split">
          <YourInboxListView
            selected={message}
            onOpen={(id) => navigate(`/inbox?tab=yours&message=${encodeURIComponent(id)}`)}
          />
          <div className="fi-reading" data-testid="inbox-reading-pane">
            {message === null ? (
              <p className="fi-reading-empty">Pick a message to read it, reply to it, or archive it.</p>
            ) : (
              <YourInboxMessageView id={message} surface="cockpit" hrefFor={cockpitInboxHref} />
            )}
          </div>
        </div>
      )}
    </section>
  );
}
