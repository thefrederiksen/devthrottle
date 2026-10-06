import { useEffect, useRef, useState } from "react";
import { NavLink, Outlet, useLocation, useNavigate } from "react-router-dom";
import { useKeepWarm } from "@devthrottle/client-core/net/useKeepWarm";
import { getSuggestionCount } from "@devthrottle/client-core/dictation/dictionaryClient";
import { resumePendingDictations } from "@devthrottle/client-core/dictation/backgroundSend";
import { Chevron, NavIcon, type NavIconName } from "./components";
import { CockpitStatusPill } from "./network/CockpitStatusPill";
import { StopSessionProvider } from "./sessions/StopSessionProvider";
import { useFleetManagerWaitingCount } from "./fleetmanager/useWaitingCount";
import { useFactorySwitch } from "./factory/useFactorySwitch";
import { CurrentTeamProvider, useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import type { TeamPagesApp, TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import { TeamSwitcher } from "./teams/TeamSwitcher";
import { isTeamPageAddress, TeamPagesOnly } from "./teams/collaborator/TeamPagesOnly";
import { TeamPagesFoot } from "./teams/collaborator/TeamPagesFoot";
import { TeamChooser } from "./teams/collaborator/TeamChooser";
import "./teams/collaborator/collaborator.css";
import { Button, LoadingState } from "./components";
import { useMentorEntry } from "./mentor/useMentorEntry";

// The desktop layout frame (epic #967): a two-region shell - a left rail (navigation) and the main
// pane (the routed page). The main pane fills all remaining width. Desktop-first: the frame stays
// usable down to a small laptop, which is the seam to the mobile shell.
//
// There is intentionally NO static right rail (issue #1022): an earlier port left a hardcoded
// "Awareness" placeholder rail that shipped empty on every route, duplicated the real Awareness tab
// on /session/:id, and stole ~300px of width from every page (which also worsened the terminal
// clipping). Per-page detail regions (roster, dock, awareness) belong to the routed pages
// themselves - see SessionsView - not to this frame.

// The left-rail destinations. This was three LABELED sections - Fleet, Data, System (issue #1247) -
// until the labels were removed (issue #1617). They were dim uppercase headers that lost to the item
// labels beneath them, so instead of chunking the list they added three rows of noise you scanned
// past. Raising their contrast was the obvious fix and the wrong one: it makes them louder without
// making them useful, because the grouping was not earning its keep - "Dictionary, Voice Recorder,
// Transcription, Network, Learning" under "DATA" is not a category anyone feels, it is a category
// invented to justify a header. So the list is flat now, which is what comparable product navigation
// does at this size.
//
// Every destination carries an ICON, and that is what replaced the headers rather than merely
// decorating them: in a flat rail the scan is carried by SHAPE - you find the row by silhouette
// before you read the word - which is the job the dim headers were failing to do. See NavIcon.
//
// The one surviving grouping is positional, not labeled: the destinations about the app itself
// (Account, Your Throttle, Settings, About) sit in a second list pinned to the BOTTOM of the rail,
// away from the fleet work. That is a grouping you feel without being told.
//
// Executables was in this rail too - issue #1247 put it there - and has been deleted: it was a
// DEVELOPER page (the Director processes on the Gateway's own machine, and the local_builds slots), so
// putting it in an end-user rail was the mistake, not leaving it out.
//
// The Fleet Manager is first, then Sessions, then Fleet Map: the sessions are the work, the Fleet Manager is where
// the owner asks for it, and the whole-fleet picture sits behind them. The Fleet Manager is the default landing (step
// 6 of its mission; it was the Fleet Map, issue #1303): a fresh boot at "/" redirects to it (routes.tsx). Sessions
// lives at its own /sessions
// home. `subtree` marks a destination active for a route family that does NOT share its path prefix:
// the session detail routes into "/session/:id" - a different path from "/sessions" - so Sessions
// needs an explicit subtree to stay highlighted while a session is being driven (the Directors item
// does not, because "/directors/:id" already shares the "/directors" prefix NavLink matches by
// default).
interface NavItem {
  to: string;
  label: string;
  icon?: NavIconName;
  subtree?: string;
  // When set, the item is an EXTERNAL link opened in a new tab rather than an in-app route: it renders
  // a plain anchor to this absolute URL instead of a NavLink, and `to` is ignored. Used for Help, which
  // leaves the app for the public documentation site.
  href?: string;
  // A red attention count rendered as a badge on the row (devthrottle #2075). The value is computed on
  // the Gateway and rendered here verbatim - the client never re-derives it (rule 7: the client is dumb).
  // Only the Dictionary item carries one today (pending dictionary suggestions).
  badge?: number;
  /** The badge's hover text; defaults to "N pending". */
  badgeTitle?: string;
}

// The fleet work: what is running, how it is driven, and the corpora and tools it reads and writes.
// Workflows sits with Schedule on purpose - Schedule is what runs when, Workflows is how work runs,
// and it is next to the place you start work rather than filed away under settings.
//
// The Fleet Manager sits at the TOP (the Fleet Manager mission, step 6): it is the one place the owner talks to
// about all the work, and the page the Cockpit opens on. It replaced the Assistant, which step 9 removed from the
// product; the old /assistant address redirects here. Its red
// badge is the Gateway's count of what is waiting on the owner.
const NAV_MAIN: ReadonlyArray<NavItem> = [
  { to: "/fleet-manager", label: "Fleet Manager", icon: "fleet-manager" },
  { to: "/sessions", label: "Sessions", icon: "sessions", subtree: "/session" },
  { to: "/fleet-map", label: "Fleet Map", icon: "fleet-map" },
  // History sits right behind the live views: Sessions and the Fleet Map are "what is happening",
  // History is "what happened" (issue #2194) - the same record, one step back in time.
  { to: "/history", label: "History", icon: "history" },
  { to: "/directors", label: "Directors", icon: "directors" },
  { to: "/schedule", label: "Schedule", icon: "schedule" },
  { to: "/workflows", label: "Workflows", icon: "workflows" },
  // Skills sits beside Workflows: two lists on one shelf. A workflow governs how a whole mission is
  // run; a skill is a capability an agent reaches for mid-task (devthrottle_internal issue 995).
  { to: "/skills", label: "Skills", icon: "skills" },
  { to: "/dictionary", label: "Dictionary", icon: "dictionary" },
  { to: "/transcripts", label: "Voice Recorder", icon: "voice-recorder" },
  { to: "/transcription", label: "Transcription", icon: "transcription" },
  { to: "/network", label: "Network", icon: "network" },
];

// Factories (once "Factory Agents") sits after Fleet Map and before History - but only while the GATEWAY says the
// area is on (factoryAgents.enabled). The rail never decides that itself (rule 7).
const FACTORIES_ITEM: NavItem = {
  to: "/factories",
  label: "Factories",
  icon: "factories",
  subtree: "/factories",
};

// The Mentor's weekly page for the team on screen (devthrottle_internal#2305), after Skills - the mockups put it
// beside the team's skills and workflows. Offered only while the GATEWAY answers the Mentor read for the current team
// with a page: a Collaborator, a person on their own account and a Gateway with Teams off never see it (rule 7).
const MENTOR_ITEM: NavItem = { to: "/mentor", label: "Mentor", icon: "mentor" };

// The public documentation site. devthrottle.com is a PUBLIC website (NOT a Director), so this external
// link does not violate the Gateway-only-ingress rule; it is the same intended absolute-URL exception the
// sign-in redirect and the desktop app's own Documentation menu item carry.
// eslint-disable-next-line no-restricted-syntax -- documented Gateway-only-ingress exception (#967/#968): public docs site, not a Director
const DOCS_URL = "https://devthrottle.com/docs";

// This browser's account and the app's own settings - pinned to the bottom of the rail. Help sits last:
// it is the only item that leaves the app, opening the public documentation site in a new tab.
//
// "Injected text" used to sit here, directly beneath Settings (issue #550). It is a setting, so it
// belongs UNDER Settings rather than beside it, and it is a tab there now - a Cockpit-only one. Its old
// /injected-text route still resolves, as a redirect into that tab, so existing bookmarks keep working.
const NAV_FOOT: ReadonlyArray<NavItem> = [
  { to: "/account", label: "Account", icon: "account" },
  // Phone (devthrottle_internal #1508): how to get DevThrottle onto a phone. It sits beside Account
  // because it is about THIS PERSON'S devices rather than about the fleet. A ROUTE, not an external
  // link: the job is reaching a DIFFERENT device, and a link would only open the narrow layout in this
  // desktop browser - the one thing that does not help.
  { to: "/phone", label: "Phone", icon: "phone" },
  { to: "/your-throttle", label: "Your Throttle", icon: "throttle" },
  { to: "/settings", label: "Settings", icon: "settings" },
  { to: "/about", label: "About", icon: "about" },
  { to: DOCS_URL, label: "Help", icon: "help", href: DOCS_URL },
];

// THE RAIL COLLAPSES TO ITS ICONS (issue #3074). On a screen whose whole point is the thing in the middle -
// a dev report is the case that forced this - the rail, the session list and the queue dock were spending
// 860 pixels of a 1920 screen on chrome and leaving the report about 700. The rail gives back 164 of them
// without losing a destination: every row already carries an icon that was drawn to be found by SHAPE
// (see NavIcon), so collapsed it is the same list with the words hidden, not a different navigation.
//
// The choice is remembered per browser, like the roster's ordering: a reader who works collapsed should not
// re-collapse it on every load. Storage that is unavailable or holds something else reads as expanded.
const RAIL_STORAGE_KEY = "cockpit.railCollapsed";

function initialRailCollapsed(): boolean {
  try {
    return window.localStorage.getItem(RAIL_STORAGE_KEY) === "true";
  } catch {
    return false;
  }
}

// THE TEAM PAGES' ICONS (devthrottle_internal#2306). Which pages a person may open in a team, their names and their
// addresses are the Gateway's verdict (rule 7); only the drawing is the Cockpit's. A page the Gateway names that has
// no drawing here is still listed, with its name alone.
const TEAM_PAGE_ICONS: Readonly<Record<string, NavIconName>> = {
  questions: "questions",
  requests: "requests",
  reports: "reports",
};

/** The rail of a Cockpit that is only the Gateway's pages: those, in the Gateway's order, and nothing else. */
function teamPagesNav(app: Pick<TeamPagesApp, "pages">): NavItem[] {
  return app.pages.map((page) => ({ to: page.path, label: page.label, icon: TEAM_PAGE_ICONS[page.id] }));
}

/** The team's verdict when it makes the Cockpit only some pages; null when it does not, or there is no team. */
function pagesOnly(team: TeamSummary | null): TeamPagesApp | null {
  return team !== null && !team.app.full ? team.app : null;
}

// THE CURRENT TEAM IS OWNED HERE (devthrottle_internal#2312): the switcher in the rail writes it and the routed pages
// read it, so it must outlive every route change. The frame inside reads it to draw the rail and the main pane
// (devthrottle_internal#2306).
export function AppShell() {
  return (
    <CurrentTeamProvider>
      <ShellFrame />
    </CurrentTeamProvider>
  );
}

function ShellFrame() {
  const location = useLocation();
  const navigate = useNavigate();
  const team = useCurrentTeam();
  // A team whose verdict is "only these pages" - a Collaborator's (devthrottle_internal#2306). Null for the person's own
  // account, for a person with no team, and for a team where they get the whole app: all of those see today's Cockpit.
  const teamPages = pagesOnly(team.current);
  // THE WHOLE APP IS ON SCREEN: not a pages-only team, not waiting to learn which, not the chooser. Only then does the
  // shell ask the Gateway for what the whole app's rail shows (the five reads below) - a Collaborator's three pages
  // ask for none of it (devthrottle_internal#2306, review finding F2).
  const wholeApp = !team.resolving && !team.choosing && teamPages === null;
  // Keep-warm heartbeat (P2): hold the direct LAN path open during active use.
  useKeepWarm(wholeApp);

  // Resume any recorded-but-unsent dictation once this enrolled shell mounts, exactly like the mobile
  // GatedLayout does (issue #1006): a clip whose upload was interrupted by a refresh / closed tab /
  // dropped connection is re-driven to its session from the durable on-device queue. Without this, the
  // Cockpit's fire-and-forget Speak Send (SessionComposer / VoiceTab) could persist a clip and then
  // never deliver it after a reload - saved forever, sent never.
  // Once per shell, the first time the whole app is on screen - not again on every switch back from a pages-only team
  // (delta review D8).
  const resumed = useRef(false);
  useEffect(() => {
    if (!wholeApp || resumed.current) return;
    resumed.current = true;
    void resumePendingDictations();
  }, [wholeApp]);

  // The pending dictionary-suggestions count (devthrottle #2075) - the Gateway-owned verdict rendered as a
  // red badge on the Dictionary nav item. Polled here so the whole app shows the attention signal without
  // opening the page, and re-read on every route change so applying/dismissing on the page updates it
  // promptly (leaving the page re-polls). The client renders the number; it never decides it.
  const [suggestCount, setSuggestCount] = useState(0);
  useEffect(() => {
    if (!wholeApp) return undefined;
    let cancelled = false;
    const poll = () => void getSuggestionCount().then((n) => {
      if (!cancelled) setSuggestCount(n);
    });
    poll();
    const id = window.setInterval(poll, 45_000);
    return () => {
      cancelled = true;
      window.clearInterval(id);
    };
  }, [location.pathname, wholeApp]);

  const waitingCount = useFleetManagerWaitingCount(location.pathname, wholeApp);

  const [railCollapsed, setRailCollapsed] = useState(initialRailCollapsed);
  const toggleRail = () => {
    setRailCollapsed((current) => {
      const next = !current;
      try {
        window.localStorage.setItem(RAIL_STORAGE_KEY, next ? "true" : "false");
      } catch {
        // A browser with storage turned off still collapses; it just forgets on the next load.
      }
      return next;
    });
  };

  const factorySwitch = useFactorySwitch(wholeApp).state;
  const railItems =
    factorySwitch === "on"
      ? NAV_MAIN.flatMap((item) => (item.to === "/fleet-map" ? [item, FACTORIES_ITEM] : [item]))
      : NAV_MAIN;

  // A TEAM WHERE THE PERSON GETS THE WHOLE APP still has the team's own pages (devthrottle_internal#2309, Tech Lead
  // ruling; #2306 review F12): a Developer reads and sends their team reports at /reports, and reaches it here rather
  // than by typing the address. Which pages, their names and order are the Gateway's verdict (rule 7); with no team on
  // screen there are none, so the own account's rail is exactly as it was.
  const wholeAppTeamPages = team.current !== null && team.current.app.full ? teamPagesNav(team.current.app) : [];
  const fullNav = [...railItems, ...wholeAppTeamPages].map((item) =>
    item.to === "/dictionary"
      ? { ...item, badge: suggestCount }
      : item.to === "/fleet-manager"
        ? { ...item, badge: waitingCount, badgeTitle: `${waitingCount} waiting on you` }
        : item,
  );

  // THE STOP ANSWER IS OWNED HERE, above every roster row and every session page (mission "Stop a
  // session", inspection finding I4). A stop removes the row it was started from, and the shared roster
  // poll unmounts that row within two seconds - so a stop dialog owned by the row would be torn down,
  // unread, by the refresh that the stop itself caused. This provider outlives every route change, so
  // neither the outstanding request nor the Gateway's answer can go with the row.
  //
  //
  // While this browser remembers a team the Gateway has not confirmed yet (CurrentTeam's `resolving`), the rail and the
  // page wait: drawing the whole app first would flash pages a Collaborator may not open. It never happens to a browser
  // that remembers no team, or the own account - so a Gateway with Teams dark, and a person with no team, never wait
  // (delta review D1).
  const mainNav = team.resolving || team.choosing ? [] : teamPages !== null ? teamPagesNav(teamPages) : fullNav;
  // A pages-only rail never collapses (review finding F4): three rows need no room back, and at phone width the bar
  // hides the collapse control - a remembered collapse would otherwise leave no switcher and no way back.
  // The chooser (S11) has no rail rows either, so it takes the same short rail: no collapse, and a bar at phone width.
  const shortRail = teamPages !== null || team.choosing;
  const collapsed = railCollapsed && !shortRail;
  const shellClass = ["shell", collapsed ? "shell-rail-collapsed" : "", shortRail ? "shell-team-pages" : ""]
    .filter((c) => c.length > 0)
    .join(" ");

  // Picking a team opens it where it starts: a pages-only team on its landing page; and leaving one goes back to the
  // Cockpit's own start, since the page on screen was one only that team had.
  const onSwitched = (now: TeamSummary | null, before: TeamSummary | null) => {
    const nowPages = pagesOnly(now);
    if (nowPages !== null) navigate(nowPages.landing);
    else if (pagesOnly(before) !== null) navigate("/");
  };

  // The chooser (S11) opens the picked team where it starts, the same way the switcher does - except that a person who
  // arrived at one of that team's pages (a mailed link to /reports, say) stays on it (delta review D7).
  const onChosen = (teamId: string | null) => {
    const now = team.choose(teamId);
    const nowPages = pagesOnly(now);
    if (nowPages === null) navigate("/");
    else if (!isTeamPageAddress(nowPages, location.pathname)) navigate(nowPages.landing);
  };

  // The own account was drawn at once (nothing remembered never waits, delta review D1), and then the Gateway's start
  // put a pages-only team on screen: open that team where it starts, unless the address is already one of its pages.
  // Keyed to the own account having been on screen, so a typed address in a team that was remembered still shows the
  // not-available sentence rather than being moved.
  const ownWasOnScreen = useRef(false);
  useEffect(() => {
    if (teamPages !== null && ownWasOnScreen.current && !isTeamPageAddress(teamPages, location.pathname)) {
      navigate(teamPages.landing, { replace: true });
    }
    ownWasOnScreen.current = wholeApp && team.current === null;
    // Only a change of what is on screen moves the person; a change of address alone never does.
  }, [teamPages, wholeApp, team.current]);

  // Who is signed in, and Sign out, wherever the person cannot reach the Account page: a pages-only team, the chooser,
  // and a team that could not be opened (review finding F3, delta review D6).
  // Not drawn in a collapsed rail (only "could not be opened" can be collapsed); the role is shown only in a pages-only
  // team, the one place a team is on screen.
  const signOutFoot = teamPages !== null || team.choosing || (team.resolving && team.status === "error");
  const pagesTeam = teamPages === null ? null : team.current;

  return (
    <StopSessionProvider>
      <div className={shellClass}>
        <nav className="rail rail-left" aria-label="Primary">
          <div className="rail-head">
            {!collapsed && <div className="brand">DevThrottle</div>}
            {/* The collapse control lives in the rail it collapses, and stays put when it does: collapsed, it
                is the one row still in reach, pointing the way back. */}
            {!shortRail && (
            <button
              type="button"
              className="rail-toggle"
              data-testid="rail-toggle"
              aria-expanded={!collapsed}
              aria-label={collapsed ? "Expand the menu" : "Collapse the menu"}
              title={collapsed ? "Expand the menu" : "Collapse the menu"}
              onClick={toggleRail}
            >
              <Chevron pointing={collapsed ? "right" : "left"} />
            </button>
            )}
          </div>
          {/* The team switcher sits at the top of the rail on every screen (S11). It renders nothing for a person
              with no team, so their rail is exactly as it was. */}
          {/* While the chooser is on screen it IS the choice; a switcher beside it would claim "Your own account". */}
          {!collapsed && !team.choosing && <TeamSwitcher onSwitched={onSwitched} />}
          {!collapsed && teamPages === null && <CockpitStatusPill />}
          <div className="nav">
            {teamPages !== null ? (
              <NavList items={mainNav} pathname={location.pathname} collapsed={collapsed} />
            ) : (
              <MainNavList items={mainNav} pathname={location.pathname} collapsed={collapsed} />
            )}
            {/* A pages-only Cockpit is those pages and nothing else: no account, settings or help rows. */}
            {wholeApp && (
              <NavList items={NAV_FOOT} pathname={location.pathname} className="nav-list-foot" collapsed={collapsed} />
            )}
          </div>
          {signOutFoot && !collapsed ? (
            <TeamPagesFoot role={pagesTeam?.role ?? null} />
          ) : (
            !collapsed && <div className="rail-foot">Cockpit (React)</div>
          )}
        </nav>

        <main className="main-pane" aria-label="Main">
          {team.resolving && team.status === "error" ? (
            <TeamUnreadable error={team.error} onOwnAccount={team.openOwnAccountForThisLoad} />
          ) : team.resolving ? (
            <LoadingState message="Loading your team..." />
          ) : team.choosing ? (
            <TeamChooser teams={team.teams} onOpen={onChosen} />
          ) : teamPages !== null ? (
            <TeamPagesOnly app={teamPages} />
          ) : (
            <Outlet />
          )}
        </main>
      </div>
    </StopSessionProvider>
  );
}

// The team this browser remembers could not be confirmed because the list of teams could not be read. Which pages the
// person may open there is unknown, so none is drawn; the read is being asked again by itself, and the person can go
// to their own account meanwhile rather than wait on a screen with nothing in it.
function TeamUnreadable({ error, onOwnAccount }: { error: string | null; onOwnAccount: () => void }) {
  return (
    <section className="pane" data-testid="team-unreadable">
      <h1 className="pane-title">Your team could not be opened just now</h1>
      <p className="pane-note">
        {error ?? "Your teams could not be read."} DevThrottle is trying again by itself.
      </p>
      <div className="team-unreadable-actions">
        <Button onClick={onOwnAccount}>Open your own account instead</Button>
      </div>
    </section>
  );
}

// The main list, with the Mentor entry when the Gateway offers it. A component of its own because the current team
// is read under the CurrentTeamProvider the shell mounts.
function MainNavList({ items, pathname, collapsed }: { items: ReadonlyArray<NavItem>; pathname: string; collapsed: boolean }) {
  const mentorOffered = useMentorEntry();
  const withMentor = mentorOffered
    ? items.flatMap((item) => (item.to === "/skills" ? [item, MENTOR_ITEM] : [item]))
    : items;
  return <NavList items={withMentor} pathname={pathname} collapsed={collapsed} />;
}

function NavList({
  items,
  pathname,
  className,
  collapsed,
}: {
  items: ReadonlyArray<NavItem>;
  pathname: string;
  className?: string;
  collapsed: boolean;
}) {
  return (
    <ul className={className === undefined ? "nav-list" : `nav-list ${className}`}>
      {items.map((item) => {
        const inSubtree = item.subtree !== undefined && pathname.startsWith(item.subtree);
        return (
          <li key={item.to}>
            {item.href !== undefined ? (
              // External destination (Help): a plain anchor that opens the public docs in a new tab. It
              // is never "active" - it does not correspond to an in-app route - so it takes the resting
              // nav-link style only.
              <a
                className="nav-link"
                href={item.href}
                target="_blank"
                rel="noopener noreferrer"
                title={collapsed ? item.label : undefined}
              >
                {item.icon !== undefined && <NavIcon name={item.icon} />}
                <span className="nav-link-label">{item.label}</span>
              </a>
            ) : (
              <NavLink
                to={item.to}
                end={item.to === "/"}
                className={({ isActive }) =>
                  isActive || inSubtree ? "nav-link nav-link-active" : "nav-link"
                }
                /* Collapsed, the word is hidden but the row must still be able to say what it is. The label
                   stays in the DOM for the accessible name; the hover text is for the eye. */
                title={collapsed ? item.label : undefined}
              >
                {item.icon !== undefined && <NavIcon name={item.icon} />}
                <span className="nav-link-label">{item.label}</span>
                {item.badge !== undefined && item.badge > 0 && (
                  <span className="nav-badge" title={item.badgeTitle ?? `${item.badge} pending`}>
                    {item.badge}
                  </span>
                )}
              </NavLink>
            )}
          </li>
        );
      })}
    </ul>
  );
}
