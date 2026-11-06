import { useNavigate, useParams } from "react-router-dom";
import { FactoryInboxListView } from "@devthrottle/client-core/factoryinbox/FactoryInboxListView";
import { FactoryInboxThreadView } from "@devthrottle/client-core/factoryinbox/FactoryInboxThreadView";
import { InboxTabs } from "@devthrottle/client-core/factoryinbox/InboxTabs";

// THE COCKPIT'S INBOX (Factory Inbox mission, phase 4; mockup D): the Factory inbox list on the left and the open
// factory's thread as the reading pane. The list, the thread and the composer are client-core's, the same the phone
// shows (rule 8); this file is only the Cockpit's frame - the pane, its title and the two columns. The route sits inside
// FactoryAreaGate, so with factories off for the account this page says so and how to start, as /factories does.
//
// With no factory open the composer sits under the list, so a message can be left without opening anything; with one
// open it sits in the reading pane, under that factory's thread.
export function InboxView() {
  const { factory } = useParams();
  const navigate = useNavigate();
  const open = factory ?? null;
  return (
    <section className="pane" data-testid="inbox-page">
      <h1 className="pane-title">Inbox</h1>
      <InboxTabs active="factories" onSelect={() => undefined} />
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
    </section>
  );
}
