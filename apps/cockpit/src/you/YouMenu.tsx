import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { switchAccount } from "@devthrottle/client-core/auth/accountActions";
import { useAccounts } from "@devthrottle/client-core/auth/useAccounts";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import { SignOutDialog, accountName } from "./SignOutDialog";
import "./you.css";

// YOU, AT THE BOTTOM OF THE RAIL (owner, 8 Oct 2026, following how ChatGPT and Claude lay out their screens). A card with
// your initials, your email and where you are working - Personal, or a team and your role in it. Clicking it opens the
// one menu that holds everything about YOU rather than about the fleet:
//
//   - Working in: Personal, or one of your teams - the team switch that used to be a box at the top of the rail. It is
//     the same switch (client-core's CurrentTeam), in a new place, and "Your own account" is called "Personal" now.
//   - Accounts on this browser: switch to another signed-in account, or add one - what the Account page's accounts panel
//     did (client-core's switchAccount; the add is the shell's own /signin enrollment).
//   - Settings, Usage, Connect your phone, Help, About - the rows that used to sit at the foot of the rail.
//   - One sign-out, which names the account it signs out of.
//
// Two kinds of switching, kept apart in two groups: Working in picks Personal or a team INSIDE the account you are
// signed in as; Accounts on this browser swaps the whole account (office versus private), and reloads the app as it.
//
// The list of teams, your role in each and whether Teams is offered at all are the Gateway's (rule 7): a person with no
// team sees Personal and Create a team; a Gateway that offers no teams shows no Working in group at all.
//
// It is a MENU BUTTON (WAI-ARIA): Enter, Space or the arrow keys open it, the arrow keys, Home and End move through it,
// Escape closes it and gives focus back to the card, and so does a click anywhere outside it. It works the same with
// the rail collapsed, where the card is just the initials. The menu is placed with fixed coordinates from the card, so
// the rail's own scrolling cannot clip it and it can be wider than a collapsed rail.

// The public documentation site - a PUBLIC website, not a Director, so this absolute link is the documented exception
// to the Gateway-only rule (#967/#968), exactly as the rail's Help row was.
// eslint-disable-next-line no-restricted-syntax -- documented Gateway-only-ingress exception (#967/#968): public docs site, not a Director
export const DOCS_URL = "https://devthrottle.com/docs";

/** Two letters for the card: the first letters of the first two parts of the email's name ("soren.f@..." -> "SF"), or,
 *  for a one-part name, its first letter and the first letter of its domain - so two accounts with the same name on
 *  different domains ("soren@centerconsulting.com", "soren@duksrevo.com") are told apart: "SC", "SD". */
export function initialsFor(name: string): string {
  const [local, domain = ""] = name.split("@");
  const parts = local.split(/[._\-+\s]+/).filter((p) => p.length > 0);
  if (parts.length === 0) return "?";
  const letters = parts.length >= 2 ? parts[0][0] + parts[1][0] : parts[0][0] + (domain[0] ?? "");
  return letters.toUpperCase();
}

export interface YouMenuProps {
  /** The rail is collapsed to its icons: the card shows only the initials. */
  collapsed: boolean;
  /** The whole app is on screen. Settings, Usage, Phone and About are offered only then - a Collaborator's pages-only
   *  app, the team chooser and a team that could not be opened have none of those pages. */
  wholeApp: boolean;
  /** Dictionary suggestions waiting (the Gateway's count, rendered verbatim). Shown on the card and as a row of the
   *  menu, now that Dictionary is a tab of Settings rather than a row of the rail. */
  suggestions: number;
  /** Called after the person picks Personal (null) or a team, with the team that was on screen before - the shell
   *  opens the new team where it starts. */
  onSwitched: (now: TeamSummary | null, before: TeamSummary | null) => void;
}

type Placement = { left: number; top?: number; bottom?: number; maxHeight: number };

const MENU_GAP = 6;

export function YouMenu({ collapsed, wholeApp, suggestions, onSwitched }: YouMenuProps) {
  const navigate = useNavigate();
  const { accounts, active } = useAccounts();
  const team = useCurrentTeam();
  const [open, setOpen] = useState(false);
  const [placement, setPlacement] = useState<Placement | null>(null);
  const [signingOut, setSigningOut] = useState(false);
  const [openAt, setOpenAt] = useState<"first" | "last">("first");
  const cardRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  const who = active === null ? "Not signed in" : accountName(active);
  // Where you are working, for the card's second line. Nothing about teams while the Gateway offers none.
  const working =
    team.current !== null
      ? `${team.current.name} - ${team.current.role}`
      : team.status === "ready"
        ? "Personal"
        : null;

  const close = (refocus: boolean) => {
    setOpen(false);
    if (refocus) cardRef.current?.focus();
  };

  // Placed from the card each time it opens: upward when the card is in the lower half of the window (the rail's
  // foot), downward when it is in the upper half (the bar a pages-only app becomes at a narrow width).
  useLayoutEffect(() => {
    if (!open || cardRef.current === null) return;
    const rect = cardRef.current.getBoundingClientRect();
    const below = rect.top < window.innerHeight / 2;
    setPlacement(
      below
        ? { left: rect.left, top: rect.bottom + MENU_GAP, maxHeight: window.innerHeight - rect.bottom - MENU_GAP * 2 }
        : { left: rect.left, bottom: window.innerHeight - rect.top + MENU_GAP, maxHeight: rect.top - MENU_GAP * 2 },
    );
  }, [open]);

  // Focus the first item once the menu is drawn, so the keyboard is in it from the start - or the last, when ArrowUp on
  // the card opened it (the menu button pattern).
  useEffect(() => {
    if (!open || placement === null) return;
    const list = items();
    list[openAt === "last" ? list.length - 1 : 0]?.focus();
  }, [open, placement]);

  // A click anywhere outside the card and the menu closes it, without taking focus anywhere.
  useEffect(() => {
    if (!open) return undefined;
    const onDown = (e: MouseEvent) => {
      const target = e.target as Node;
      if (menuRef.current?.contains(target) || cardRef.current?.contains(target)) return;
      close(false);
    };
    document.addEventListener("mousedown", onDown);
    return () => document.removeEventListener("mousedown", onDown);
  }, [open]);

  const items = (): HTMLElement[] =>
    menuRef.current === null
      ? []
      : Array.from(menuRef.current.querySelectorAll<HTMLElement>("[role^='menuitem']"));

  const onMenuKey = (e: KeyboardEvent<HTMLDivElement>) => {
    const list = items();
    const at = list.indexOf(document.activeElement as HTMLElement);
    const move = (to: number) => {
      e.preventDefault();
      list[(to + list.length) % list.length]?.focus();
    };
    if (e.key === "ArrowDown") move(at + 1);
    else if (e.key === "ArrowUp") move(at - 1);
    else if (e.key === "Home") move(0);
    else if (e.key === "End") move(list.length - 1);
    else if (e.key === "Escape") {
      e.preventDefault();
      close(true);
    } else if (e.key === "Tab") close(false);
  };

  const onCardKey = (e: KeyboardEvent<HTMLButtonElement>) => {
    if (e.key === "ArrowUp" || e.key === "ArrowDown") {
      e.preventDefault();
      setOpenAt(e.key === "ArrowUp" ? "last" : "first");
      setOpen(true);
    }
  };

  // Every action closes the menu first, then does its work.
  const act = (run: () => void) => () => {
    close(false);
    run();
  };

  const pickTeam = (teamId: string | null) =>
    act(() => {
      const before = team.current;
      const now = team.choose(teamId);
      onSwitched(now, before);
    });

  const style: CSSProperties | undefined =
    placement === null
      ? { visibility: "hidden" }
      : { left: placement.left, top: placement.top, bottom: placement.bottom, maxHeight: placement.maxHeight };

  return (
    <div className={collapsed ? "you you-collapsed" : "you"} data-testid="you">
      <button
        ref={cardRef}
        type="button"
        className="you-card"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? "you-menu" : undefined}
        aria-label={working === null ? `${who}. Open your menu.` : `${who}, working in ${working}. Open your menu.`}
        title={collapsed ? (working === null ? who : `${who} - ${working}`) : undefined}
        onClick={() => {
          setOpenAt("first");
          setOpen((o) => !o);
        }}
        onKeyDown={onCardKey}
        data-testid="you-card"
      >
        <span className="you-initials" aria-hidden="true">
          {active === null ? "?" : initialsFor(who)}
          {suggestions > 0 && <span className="you-dot" />}
        </span>
        {!collapsed && (
          <span className="you-text" aria-hidden="true">
            <span className="you-name">{who}</span>
            {working !== null && <span className="you-working">{working}</span>}
          </span>
        )}
        {!collapsed && (
          <span className="you-caret" aria-hidden="true">
            {open ? "v" : "^"}
          </span>
        )}
      </button>

      {open && (
        <div
          ref={menuRef}
          id="you-menu"
          className="you-menu"
          role="menu"
          aria-label="Your menu"
          style={style}
          onKeyDown={onMenuKey}
          data-testid="you-menu"
        >
          <div className="you-menu-email" role="presentation">
            {who}
          </div>

          {/* Working in: only where the Gateway offers teams, and not while the chooser is on screen - it IS the
              choice then. */}
          {team.status === "error" && team.resolving ? (
            // A disabled item, not decoration: a screen reader walking the menu reaches it, and the reason is on screen.
            <div className="you-menu-note" role="menuitem" aria-disabled="true" tabIndex={-1} data-testid="you-menu-teams-error">
              {`Your teams could not be read just now: ${team.error ?? "no reason was given"}`}
            </div>
          ) : (
            team.status === "ready" &&
            !team.choosing && (
              <Group label="Working in">
                <Choice checked={team.current === null} onPick={pickTeam(null)} mark="P" main="Personal" />
                {team.teams.map((t) => (
                  <Choice
                    key={t.id}
                    checked={team.current?.id === t.id}
                    onPick={pickTeam(t.id)}
                    mark={initialsFor(t.name.replace(/\s+/g, "."))}
                    main={t.name}
                    sub={`${t.role} - ${t.people}`}
                  />
                ))}
                {wholeApp && (
                  <Item onPick={act(() => navigate("/settings?tab=account#create-a-team"))}>+ Create a team</Item>
                )}
              </Group>
            )
          )}

          <Group label="Accounts on this browser">
            {accounts.map((account) => (
              <Choice
                key={account.id}
                checked={account.id === active?.id}
                onPick={act(() => {
                  if (account.id !== active?.id) void switchAccount(account.id);
                })}
                mark={initialsFor(accountName(account))}
                main={accountName(account)}
              />
            ))}
            <Item onPick={act(() => navigate("/signin"))}>+ Add another account</Item>
          </Group>

          <div className="you-menu-rule" role="separator" />
          {wholeApp && (
            <>
              {suggestions > 0 && (
                <Item onPick={act(() => navigate("/settings?tab=dictionary"))}>
                  Dictionary suggestions
                  <span className="you-menu-badge" title={`${suggestions} pending`}>
                    {suggestions}
                  </span>
                </Item>
              )}
              <Item onPick={act(() => navigate("/settings"))}>Settings</Item>
              <Item onPick={act(() => navigate("/settings?tab=usage"))}>Usage (Your Throttle)</Item>
              <Item onPick={act(() => navigate("/settings?tab=devices"))}>Connect your phone</Item>
            </>
          )}
          <a className="you-menu-item" role="menuitem" tabIndex={-1} href={DOCS_URL} target="_blank" rel="noopener noreferrer" onClick={() => close(false)}
            // Space activates every other row; a link answers only Enter by itself.
            onKeyDown={(e) => {
              if (e.key !== " ") return;
              e.preventDefault();
              e.currentTarget.click();
            }}
          >
            Help
          </a>
          {wholeApp && <Item onPick={act(() => navigate("/about"))}>About DevThrottle</Item>}
          <div className="you-menu-rule" role="separator" />
          {active !== null && (
            <Item onPick={act(() => setSigningOut(true))}>{`Sign out of ${who}`}</Item>
          )}
        </div>
      )}

      <SignOutDialog open={signingOut} onClose={() => setSigningOut(false)} />
    </div>
  );
}

function Group({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="you-menu-group" role="group" aria-label={label}>
      <div className="you-menu-heading" role="presentation">
        {label}
      </div>
      {children}
    </div>
  );
}

function Item({ onPick, children }: { onPick: () => void; children: ReactNode }) {
  return (
    <button type="button" className="you-menu-item" role="menuitem" tabIndex={-1} onClick={onPick}>
      {children}
    </button>
  );
}

function Choice({
  checked,
  onPick,
  mark,
  main,
  sub,
}: {
  checked: boolean;
  onPick: () => void;
  mark: string;
  main: string;
  sub?: string;
}) {
  return (
    <button
      type="button"
      className={checked ? "you-menu-item you-menu-choice is-current" : "you-menu-item you-menu-choice"}
      role="menuitemradio"
      aria-checked={checked}
      tabIndex={-1}
      onClick={onPick}
    >
      <span className="you-menu-mark" aria-hidden="true">
        {mark}
      </span>
      <span className="you-menu-choice-text">
        <span className="you-menu-main">{main}</span>
        {sub !== undefined && <span className="you-menu-sub">{sub}</span>}
      </span>
      {checked && (
        <span className="you-menu-check" aria-hidden="true">
          *
        </span>
      )}
    </button>
  );
}
