import { signOutAccount } from "@devthrottle/client-core/auth/accountActions";
import { useAccounts } from "@devthrottle/client-core/auth/useAccounts";
import { ConfirmDialog } from "../components";

// THE ONE SIGN-OUT (owner, 8 Oct 2026): this browser forgets the account on screen. It is the same action from the
// menu behind your name and from Settings, Account, and it always NAMES the account it signs out of - the Account page
// used to carry two buttons ("Sign out" and "Log out") that did different things to different machines.
//
// It is the shared client-core sign-out: the Gateway's cookie is cleared first, and if that fails nothing is signed
// out and the dialog stays open with the Gateway's reason (accountActions.signOutAccount).

/** The account's name as the sign-out names it: its email once known, otherwise its label. */
export function accountName(account: { email: string | null; label: string }): string {
  return account.email ?? account.label;
}

export function SignOutDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { active, many } = useAccounts();
  const who = active === null ? "this account" : accountName(active);

  const signOut = async () => {
    if (active === null) throw new Error("No account is signed in on this browser.");
    const result = await signOutAccount(active.id);
    if (!result.ok) throw new Error(result.reason);
  };

  return (
    <ConfirmDialog
      open={open}
      title={`Sign out of ${who}?`}
      message={
        many
          ? `This browser forgets ${who}. Your other accounts on this browser stay signed in, and DevThrottle opens one of them.`
          : `This browser forgets ${who}. You will need to sign in through devthrottle.com to use DevThrottle here again.`
      }
      confirmLabel="Sign out"
      busyLabel="Signing out..."
      danger={false}
      action="sign out"
      onConfirm={signOut}
      onClose={onClose}
    />
  );
}
