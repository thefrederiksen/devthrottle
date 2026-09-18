import { useState } from "react";
import { Link } from "react-router-dom";
import { useAccounts } from "@devthrottle/client-core/auth/useAccounts";

// The hamburger menu button + slide-in nav drawer for the mobile app-bar. Self-contained: it owns its
// open/closed state and renders the drawer plus a dimming overlay as a fixed layer above the page.
// Tapping the overlay or any link closes it. Kept minimal and REAL - only routes that exist on the
// mobile PWA (no dead links).
export function NavDrawer() {
  const [open, setOpen] = useState(false);
  const close = () => setOpen(false);
  const { active, many } = useAccounts();

  return (
    <>
      <button type="button" className="hamburger" aria-label="Open menu" aria-expanded={open} onClick={() => setOpen(true)}>
        <span />
        <span />
        <span />
      </button>

      {open && (
        <div className="drawer-overlay" onClick={close} role="presentation">
          <nav className="drawer" aria-label="Navigation" onClick={(e) => e.stopPropagation()}>
            <div className="drawer-head">
              <span className="drawer-title">DevThrottle</span>
              <button type="button" className="drawer-close" aria-label="Close menu" onClick={close}>
                x
              </button>
            </div>
            {/* WHO IS SIGNED IN, at the top of the menu (devthrottle_internal #1509). With two accounts
                on one phone the fleet you are looking at is no longer obvious, and the menu is where you
                are already heading when you want to change something about it. It is a LINK to the
                Account screen rather than an inline switcher: switching reloads the whole app, so it
                belongs behind a deliberate tap on a screen that says so, not on a menu row a thumb
                brushes past on the way to Sessions. */}
            {active && (
              <Link className="drawer-account" to="/account" onClick={close}>
                <span className="drawer-account-label">{active.label}</span>
                <span className="drawer-account-sub">{many ? "Switch account" : "Signed in"}</span>
              </Link>
            )}

            <Link className="drawer-item" to="/" onClick={close}>
              Sessions
            </Link>
            <Link className="drawer-item" to="/new" onClick={close}>
              New session
            </Link>
            <Link className="drawer-item" to="/recorder" onClick={close}>
              Voice Recorder
            </Link>
            <Link className="drawer-item" to="/throttle" onClick={close}>
              Your Throttle
            </Link>
            <Link className="drawer-item" to="/repos" onClick={close}>
              Repos
            </Link>
            <div className="drawer-sep" />
            {/* Account sits with Settings and About - the destinations about the app and this phone
                rather than about the fleet - and mirrors the Cockpit's own Account rail item, which the
                phone had no counterpart for at all. */}
            <Link className="drawer-item" to="/account" onClick={close}>
              Account
            </Link>
            {/* One Settings item, not three. "Test microphone" and "Test transcription" were menu
                entries of their own while they were screens of their own; they are cards on the
                Transcription tab now, alongside the transcription model and the background microphone
                measurements - the same tab the Cockpit shows. */}
            <Link className="drawer-item" to="/settings" onClick={close}>
              Settings
            </Link>
            <Link className="drawer-item" to="/diagnostics" onClick={close}>
              Diagnostics
            </Link>
            <Link className="drawer-item" to="/about" onClick={close}>
              About
            </Link>
            {/* Sign out, reachable in one tap from any screen, because the menu is the only thing on
                every screen. It LINKS to the Account screen instead of signing out where it stands:
                this row sits directly beneath three ordinary navigation rows, and a mis-tap that ends
                the session is not one you can take back without another round trip through
                devthrottle.com. The confirmation is on the Account screen, one tap further. */}
            <div className="drawer-sep" />
            <Link className="drawer-item drawer-item-signout" to="/account" onClick={close}>
              Sign out
            </Link>
          </nav>
        </div>
      )}
    </>
  );
}
