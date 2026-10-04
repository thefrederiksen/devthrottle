import { useState } from "react";
import { activeAccount } from "@devthrottle/client-core/auth/accountStore";
import { signOutAccount } from "@devthrottle/client-core/auth/accountActions";
import { ConfirmDialog } from "../../components";
import "./collaborator.css";

// THE FOOT OF A PAGES-ONLY RAIL (screens S8-S10, devthrottle_internal#2306, review finding F3): who is signed in, their
// role in the team on screen - the Gateway's label, verbatim - and Sign out. A Collaborator's app has no Account page,
// and on a shared computer a person must be able to see who is signed in and leave. Sign out is the shared one
// (client-core accountActions): it clears the Gateway's cookie first and signs nothing out if that fails.
export function TeamPagesFoot({ role }: { role: string }) {
  const account = activeAccount();
  const [confirming, setConfirming] = useState(false);
  const who = account === null ? "" : account.email ?? account.label;

  const signOut = async () => {
    if (account === null) throw new Error("No account is signed in on this browser.");
    const result = await signOutAccount(account.id);
    if (!result.ok) throw new Error(result.reason);
  };

  return (
    <div className="team-pages-foot" data-testid="team-pages-foot">
      <div className="team-pages-foot-who" title={who}>
        {who}
      </div>
      <div className="team-pages-foot-role">{role}</div>
      <button type="button" className="team-pages-foot-signout" onClick={() => setConfirming(true)}>
        Sign out
      </button>
      <ConfirmDialog
        open={confirming}
        title="Sign out of DevThrottle?"
        message={`${who === "" ? "This account" : who} will be signed out of this browser.`}
        confirmLabel="Sign out"
        busyLabel="Signing out..."
        danger={false}
        action="sign out"
        onConfirm={signOut}
        onClose={() => setConfirming(false)}
      />
    </div>
  );
}
