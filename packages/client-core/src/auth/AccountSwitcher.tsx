// The "who am I" chip (devthrottle_internal #1509) - the compact identity marker both shells put in
// reach of the work, as opposed to the full AccountsPanel which is a destination.
//
// It renders NOTHING until this browser holds more than one account. A single-login browser has no
// question to answer, and a chip that always says the same thing is noise you learn to stop reading -
// which is exactly the moment it stops protecting you. The chip exists for one specific mistake: with
// two fleets on one phone, a message typed into what looks like the work fleet and sent to the personal
// one. So it appears precisely when that mistake becomes possible.
import { useAccounts } from "./useAccounts";
import "./accounts.css";

export interface AccountSwitcherProps {
  /** Open the Accounts screen, where switching actually happens. Routed by the shell. */
  onOpen: () => void;
  /** Extra class from the shell, for its own placement (the app bar, the menu header). */
  className?: string;
}

// A ROUND INITIALS BADGE, not the label itself (the owner, 8 November 2026). The chip used to print the label and was
// cut to "sor..." on a phone, which told him nothing; two letters in a circle fit in a 44px tap target at the right end
// of the bar, and the whole label is still the button's name and its tooltip.
export function AccountSwitcher({ onOpen, className }: AccountSwitcherProps) {
  const { active, many } = useAccounts();
  if (!many || !active) return null;

  return (
    <button
      type="button"
      className={`accts-chip${className ? ` ${className}` : ""}`}
      onClick={onOpen}
      aria-label={`Signed in as ${active.label}. Switch account.`}
      title={active.label}
      data-testid="account-badge"
    >
      <span className="accts-chip-initials" aria-hidden="true">
        {accountInitials(active.label)}
      </span>
    </button>
  );
}

/**
 * Up to two capital letters for a label: the first letter of each of its first two words, where an email address is
 * read up to its "@" and split on dots, dashes and underscores ("soren.frederiksen@..." is "SF", "soren@..." is "S",
 * "Account 2" is "A2"). A label with no letter or digit at all gives "?".
 */
export function accountInitials(label: string): string {
  const at = label.indexOf("@");
  const name = at > 0 ? label.slice(0, at) : label;
  const words = name.split(/[\s._-]+/).filter((w) => /[\p{L}\p{N}]/u.test(w));
  const letters = words.slice(0, 2).map((w) => (w.match(/[\p{L}\p{N}]/u) as RegExpMatchArray)[0]);
  return letters.length === 0 ? "?" : letters.join("").toUpperCase();
}
