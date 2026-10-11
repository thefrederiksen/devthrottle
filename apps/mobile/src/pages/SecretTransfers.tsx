import { Link } from "react-router-dom";
import { SecretTransfersPanel, SecretTransfersProvider } from "@devthrottle/client-core/secrets/SecretTransfers";

// The page a secret transfer's push notification opens (Secret Handoff, issue #2943, phase 5): the transfers waiting
// for the owner's one approval, on the same shared card the Cockpit shows, answered here "on the phone". The phone
// supplies only the frame - the back link and the app bar.

export function SecretTransfers() {
  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Secret transfers</h1>
      </header>
      <SecretTransfersProvider>
        <SecretTransfersPanel where="phone" emptyText="Nothing is waiting for your answer." />
      </SecretTransfersProvider>
    </div>
  );
}
