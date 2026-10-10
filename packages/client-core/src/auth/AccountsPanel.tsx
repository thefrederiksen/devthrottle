// The Accounts panel (devthrottle_internal #1507, #1509): who is signed in on this browser, switch
// between them, add another, and sign out. ONE component, mounted by both shells - the phone's Account
// screen and the desktop Cockpit's Account page - so the two surfaces cannot drift into two different
// products for one account (CLAUDE.md rule 8). Each shell supplies only its own frame around it.
//
// Signing out here is THIS BROWSER forgetting a key. It is deliberately not the same action as the
// Cockpit Account page's "Log out", which clears the GATEWAY's own link to a DevThrottle account
// (POST /account/logout) - a different thing that happens to a different machine. The two are worded
// apart on purpose; a shared word would make one button look like the other.
import { useState } from "react";
import { signOutAccount, signOutAllAccounts, switchAccount } from "./accountActions";
import { useAccounts } from "./useAccounts";
import { reportShownError } from "../errors/reportClientError";
import "./accounts.css";

export interface AccountsPanelProps {
  /**
   * Start the add-an-account sign-in. The shells route to their own sign-in entry (the phone's
   * /mobile/signin, the Cockpit's /signin), which client-core must not hardcode for the other shell.
   */
  onAddAccount: () => void;
}

export function AccountsPanel({ onAddAccount }: AccountsPanelProps) {
  const { accounts, active, many } = useAccounts();
  // Which destructive action is awaiting confirmation, if any. Signing out costs a round trip through
  // devthrottle.com to undo, so it asks first - but only once it has been asked for, so the ordinary
  // screen is not carrying a warning nobody needed.
  const [confirming, setConfirming] = useState<"one" | "all" | null>(null);
  // In flight, and what the server said if it refused. A sign-out now needs the Gateway (the cookie is
  // HttpOnly and only the server can clear it), so it can fail - and a failure means NOTHING was signed
  // out. That has to be visible; a silent local sign-out beside a live credential is the exact lie this
  // screen exists to avoid.
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);

  async function commit(run: () => Promise<{ ok: true } | { ok: false; reason: string }>) {
    setBusy(true);
    setProblem(null);
    const result = await run();
    // On success the app is already navigating away, so there is nothing to put back.
    if (!result.ok) {
      setProblem(reportShownError("accounts", "sign out", result.reason));
      setBusy(false);
    }
  }

  if (accounts.length === 0) {
    return (
      <div className="accts">
        <p className="accts-note">This browser is not signed in to any account.</p>
        <button type="button" className="accts-btn primary" onClick={onAddAccount}>
          Sign in
        </button>
      </div>
    );
  }

  const activeLabel = active?.label ?? "this account";

  return (
    <div className="accts">
      <div className="accts-list">
        {accounts.map((account) => {
          const isActive = account.id === active?.id;
          return (
            <button
              key={account.id}
              type="button"
              className={`accts-item${isActive ? " is-active" : ""}`}
              disabled={isActive}
              aria-current={isActive ? "true" : undefined}
              onClick={() => void switchAccount(account.id)}
            >
              {/* A DRAWN dot, carrying no character at all - so nothing reads it out beside the account
                  name, and it never competes with the label for width. Which account is active is said
                  in words on the line below, where a screen reader will find it. */}
              <span className="accts-mark" aria-hidden="true" />
              <span className="accts-body">
                <span className="accts-label">{account.label}</span>
                {/* The status line is its OWN block beneath the label. Both were inline spans when this
                    shipped, which put them on one line and ran a long email off the edge of the phone -
                    see accounts.css. The email is only repeated here when the label is something else
                    (a renamed account), so the row never says the same string twice. */}
                <span className="accts-sub">
                  {account.email && account.email !== account.label
                    ? `${isActive ? "Signed in now" : "Tap to switch"} - ${account.email}`
                    : isActive ? "Signed in now" : "Tap to switch"}
                </span>
              </span>
            </button>
          );
        })}
      </div>

      {many && (
        <p className="accts-note">
          Switching reloads the app as that account. Both stay signed in, so it needs no password and
          works with no connection.
        </p>
      )}

      {confirming === null && (
        <div className="accts-actions">
          <button type="button" className="accts-btn primary" onClick={onAddAccount}>
            Add account
          </button>
          {/* "Sign out of {email}" until 2026-08-11, which wrapped a full email address across two lines
              inside a button on a real phone. A button label has to be BOUNDED; the account this signs
              out is named in the confirmation, where the text can wrap safely. */}
          <button type="button" className="accts-btn danger" onClick={() => setConfirming("one")}>
            {many ? "Sign out of this account" : "Sign out"}
          </button>
          {many && (
            <button type="button" className="accts-btn danger" onClick={() => setConfirming("all")}>
              Sign out of all
            </button>
          )}
        </div>
      )}

      {confirming !== null && (
        <div className="accts-confirm">
          <p>
            {confirming === "all"
              ? "Sign every account out of this browser? Signing back in means going through devthrottle.com again for each one."
              : many
                ? `Sign ${activeLabel} out of this browser? You stay signed in to your other account, and the app switches to it.`
                : `Sign ${activeLabel} out of this browser? You will need to sign in through devthrottle.com to use the app again.`}
          </p>
          <p className="accts-note">
            This browser forgets the key. The device stays on your account at devthrottle.com until you
            remove it there.
          </p>
          {/* The Gateway's own reason, rendered verbatim, when it refused. Nothing was signed out in that
              case - said plainly, because a screen that goes quiet after a failed sign-out is how someone
              walks away believing a live credential is gone. */}
          {problem !== null && (
            <p className="accts-problem" role="alert">
              {problem}
            </p>
          )}
          <div className="accts-confirm-row">
            <button
              type="button"
              className="accts-btn danger"
              disabled={busy}
              onClick={() => {
                void commit(() =>
                  confirming === "all"
                    ? signOutAllAccounts()
                    : active
                      ? signOutAccount(active.id)
                      : Promise.resolve({ ok: true as const }),
                );
              }}
            >
              {/* An ANSWER to the question above it, not a repeat of the button that opened it. Both said
                  "Sign out" at first, which reads as the same control appearing twice and leaves no way
                  to tell from the screen which one commits. */}
              {busy ? "Signing out..." : "Yes, sign out"}
            </button>
            <button type="button" className="accts-btn" onClick={() => setConfirming(null)}>
              Cancel
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
