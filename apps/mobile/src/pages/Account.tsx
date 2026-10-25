// The phone's Account screen (devthrottle_internal #1507, #1509). Until this existed the mobile app
// had no account destination at all and no way to sign out: the only ways off a signed-in phone were
// clearing browser storage by hand or revoking the device on devthrottle.com.
//
// The screen is a FRAME, not a page: everything inside it is the shared AccountsPanel the desktop
// Cockpit mounts too, so the two surfaces cannot drift (CLAUDE.md rule 8). The phone supplies the back
// link, the app bar, and the route that "Add account" leads to.
import { Link, useNavigate } from "react-router-dom";
import { AccountsPanel } from "@devthrottle/client-core/auth/AccountsPanel";
import { AppBarAlerts } from "../components/AppBarAlerts";

export function Account() {
  const navigate = useNavigate();

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Account</h1>
        <div className="app-bar-spacer" />
        <AppBarAlerts />
      </header>

      <section className="account-screen" aria-label="Accounts on this phone">
        <AccountsPanel onAddAccount={() => navigate("/signin")} />
      </section>
    </div>
  );
}
