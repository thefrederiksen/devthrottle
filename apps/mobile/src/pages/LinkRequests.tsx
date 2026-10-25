import { Link } from "react-router-dom";
import { LinkRequestsPanel, LinkRequestsPanelProvider } from "@devthrottle/client-core/fleet/LinkRequestsPanel";
import { CloseAnsweredLinkRequestNotifications } from "../components/LinkRequestNotifications";

// The page a message link request's push notification opens (issue #4373): the sessions asking to message a session
// they may not reach on their own, on the shared client-core card, approved or refused here with one tap. The phone
// supplies only the frame - the back link and the app bar.

export function LinkRequests() {
  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Message requests</h1>
      </header>
      <LinkRequestsPanelProvider>
        <CloseAnsweredLinkRequestNotifications />
        <LinkRequestsPanel emptyText="No session is waiting for your answer." />
      </LinkRequestsPanelProvider>
    </div>
  );
}
