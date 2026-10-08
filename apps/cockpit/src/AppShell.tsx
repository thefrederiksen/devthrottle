import { useEffect, useRef, useState, type ReactNode } from "react";
import { NavLink, Outlet, useLocation, useNavigate, type Location } from "react-router-dom";
import { useKeepWarm } from "@devthrottle/client-core/net/useKeepWarm";
import { useSuggestionCount } from "./dictionary/useSuggestionCount";
import { resumePendingDictations } from "@devthrottle/client-core/dictation/backgroundSend";
import { Chevron, NavIcon, type NavIconName } from "./components";
import { CockpitStatusPill } from "./network/CockpitStatusPill";
import { StopSessionProvider } from "./sessions/StopSessionProvider";
import { CurrentTeamProvider, useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import type { TeamPagesApp, TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import { useTeamPageCounts } from "./teams/useTeamPageCounts";
import { isTeamPageAddress, TeamPagesOnly } from "./teams/collaborator/TeamPagesOnly";
import { TeamChooser } from "./teams/collaborator/TeamChooser";
import { YouMenu } from "./you/YouMenu";
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

// THE MENU IS THREE LABELLED SECTIONS, EVERYTHING VISIBLE (owner, 8 Oct 2026; Mockup 1 of devthrottle_internal
// docs/teams/2026-10-08-cockpit-menu-mockups.html). WORK is the day to day: Sessions, Fleet Map, Fleet Manager,
// Factories, History, Voice Recorder - Fleet Manager and Factories promoted with a highlighted icon, Factories always
// shown (the thing we sell is fourth from the top, not hidden behind a switch; the page says how to start when the
// area is off). Under Work, only while a team is selected, THE TEAM'S BLOCK headed by its name. SET UP is the fleet you
// change now and then: Directors first, Skills, Workflows, Schedule, Network. YOU is the card at the bottom. The
// labels are readable this time, not the dim uppercase #1617 removed, because each section now has a plain meaning;
// collapsed, a thin line stands in for each label. Dictionary and Transcription are Settings tabs.
//
// The history this replaces, kept because it is why the labels had to earn their place:
//
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
// What is about YOU - Account, Phone, Your Throttle, Settings, About, Help - left the rail for the menu behind your
// name, which sits at the bottom of it (you/YouMenu).
//
// Executables was in this rail too - issue #1247 put it there - and has been deleted: it was a
// DEVELOPER page (the Director processes on the Gateway's own machine, and the local_builds slots), so
// putting it in an end-user rail was the mistake, not leaving it out.
//
// Sessions is first, then Fleet Map, then the Fleet Manager: what the owner uses every day comes first. Sessions is
// also the default landing (owner, 7 Oct 2026; it was the Fleet Manager, and before that the Fleet Map, issue #1303):
// a fresh boot at "/" redirects to its own /sessions home (routes.tsx). `subtree` marks a destination active for a
// route family that does NOT share its path prefix: the session detail routes into "/session/:id" - a different path
// from "/sessions" - so Sessions
// needs an explicit subtree to stay highlighted while a session is being driven (the Directors item
// does not, because "/directors/:id" already shares the "/directors" prefix NavLink matches by
// default).
interface NavItem {
  to: string;
  label: string;
  icon?: NavIconName;
  subtree?: string;
  // A red attention count rendered as a badge on the row (devthrottle #2075). The value is computed on
  // the Gateway and rendered here verbatim - the client never re-derives it (rule 7: the client is dumb).
  // The team pages carry one (what waits on the person there).
  badge?: number;
  /** The badge's hover text; defaults to "N pending". */
  badgeTitle?: string;
  /** For a destination that is a TAB of a page (`to` carries ?tab=): the tabs it stands for. It is active when the
   *  page is on one of them, not whenever the page is - NavLink alone matches the path and would light it on every
   *  tab. */
  tabs?: ReadonlyArray<string>;
  /** A headline feature the owner promotes: its icon is highlighted (Fleet Manager, Factories). */
  promoted?: boolean;
}

// WORK - the day to day, in the order the owner reaches for it (owner, 8 Oct 2026). The Cockpit opens on Sessions, the
// first item: the sessions are what you maintain and monitor. History is "what happened" (issue #2194), right behind
// the live views. Voice Recorder is content you go back to, not a setting. The Fleet Manager replaced the Assistant;
// the old /assistant address redirects here. It carries no badge for now: the owner had the waiting count taken off the
// rail until it means what he wants it to mean (7 Oct 2026); the count lives in fleetmanager/useWaitingCount.ts.
//
// FACTORIES IS ALWAYS SHOWN (owner, 8 Oct 2026). It used to appear only while the Gateway's factory switch was on, so a
// new customer might never see it. The pages still follow the switch: off, /factories says so and how to start, in
// the Gateway's own sentence (factory/FactoryAreaGate).
const NAV_WORK: ReadonlyArray<NavItem> = [
  { to: "/sessions", label: "Sessions", icon: "sessions", subtree: "/session" },
  { to: "/fleet-map", label: "Fleet Map", icon: "fleet-map" },
  { to: "/fleet-manager", label: "Fleet Manager", icon: "fleet-manager", promoted: true },
  { to: "/factories", label: "Factories", icon: "factories", subtree: "/factories", promoted: true },
  { to: "/history", label: "History", icon: "history" },
  { to: "/transcripts", label: "Voice Recorder", icon: "voice-recorder" },
];

// SET UP - the fleet, changed now and then (owner, 8 Oct 2026): Directors first (which computers are on the fleet),
// then Skills and Workflows - a skill is what an agent reaches for mid-task, a workflow governs how a whole mission is
// run (devthrottle_internal issue 995) - then the Schedule that runs them, then Network, how phones and Directors reach
// the Gateway.
const NAV_SETUP: ReadonlyArray<NavItem> = [
  { to: "/directors", label: "Directors", icon: "directors" },
  { to: "/skills", label: "Skills", icon: "skills" },
  { to: "/workflows", label: "Workflows", icon: "workflows" },
  { to: "/schedule", label: "Schedule", icon: "schedule" },
  { to: "/network", label: "Network", icon: "network" },
];

// The team's own Settings tabs - Members, Team plan for those the Gateway shows it to, and Governance - first in the team block
// (owner, 8 Oct 2026). This is the "way back to the Team page" the first version did not have.
const TEAM_ITEM: NavItem = { to: "/settings?tab=members", label: "Team", icon: "team", tabs: ["members", "teamplan", "governance"] };

// MENTOR AND REPORTS ARE IN WORK, FOR EVERYONE (owner, 8 Oct 2026, Screens 1 and 2 of devthrottle_internal
// docs/teams/2026-10-08-team-showcase-mockups.html): after Voice Recorder, on the person's own account and with a team
// on screen alike. On the own account they are about the person; with a team they show the team. The team block keeps
// only what exists because there is a team: Team, Questions, Requests.
//
// The Mentor is offered only while the GATEWAY answers its read with a page - the team's, or the person's own
// (useMentorEntry, rule 7): a Collaborator and a Gateway with Teams off never see it. Reports is always there on the own
// account (every account's sessions can send it reports); in a team it is there when the Gateway's page verdict for the
// team lists it.
const MENTOR_ITEM: NavItem = { to: "/mentor", label: "Mentor", icon: "mentor" };

/** The team page that moved into Work. */
const REPORTS_PAGE_ID = "reports";
const REPORTS_ITEM: NavItem = { to: "/reports", label: "Reports", icon: "reports" };

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

/** The rail of a Cockpit that is only the Gateway's pages: those, in the Gateway's order, and nothing else - each with
 *  the Gateway's count of what waits on the person there, for a page that has one (S8). */
function teamPagesNav(app: Pick<TeamPagesApp, "pages">, counts: Readonly<Record<string, number>>): NavItem[] {
  return app.pages.map((page) => {
    const count = counts[page.id];
    return count === undefined
      ? { to: page.path, label: page.label, icon: TEAM_PAGE_ICONS[page.id] }
      : { to: page.path, label: page.label, icon: TEAM_PAGE_ICONS[page.id], badge: count, badgeTitle: `${count} waiting on you` };
  });
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

  // The pending dictionary-suggestions count: a dot on your initials and a count on the Settings row of the menu
  // behind your name (see useSuggestionCount). Re-read on every route change.
  const suggestCount = useSuggestionCount(wholeApp, location.pathname);

  // The count beside a team page (S8): read from where the Gateway says, for the team on screen; none without a team.
  const teamPageCounts = useTeamPageCounts(team.current, location.pathname);

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

  // A TEAM WHERE THE PERSON GETS THE WHOLE APP has its own block (devthrottle_internal#2309, Tech Lead ruling; #2306
  // review F12; owner, 8 Oct 2026): Team and the team's pages. Reports and the Mentor sit in Work (see MENTOR_ITEM). Which
  // pages, their names and order are the Gateway's verdict (rule 7). With Personal on screen there is no block at all.
  const wholeAppTeam = team.current !== null && team.current.app.full ? team.current : null;

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
  const waiting = team.resolving || team.choosing;
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
          {!collapsed && teamPages === null && <CockpitStatusPill />}
          <div className="nav">
            {waiting ? null : teamPages !== null ? (
              <NavList items={teamPagesNav(teamPages, teamPageCounts)} location={location} collapsed={collapsed} />
            ) : (
              <>
                <WorkSection team={wholeAppTeam} counts={teamPageCounts} location={location} collapsed={collapsed} />
                {wholeAppTeam !== null && (
                  <TeamBlock team={wholeAppTeam} counts={teamPageCounts} location={location} collapsed={collapsed} />
                )}
                {/* "Set up" alone (owner, 8 Oct 2026): the line under it naming whose fleet it changes is gone in both states. */}
                <NavSection label="Set up" collapsed={collapsed} testId="nav-setup">
                  <NavList items={NAV_SETUP} location={location} collapsed={collapsed} />
                </NavSection>
              </>
            )}
          </div>
          {/* You, at the bottom, on every screen - the whole app, a Collaborator's pages, the chooser and a team that
              could not be opened alike: who is signed in, the team switch, the accounts and one sign-out are always in
              reach. */}
          <YouMenu collapsed={collapsed} wholeApp={wholeApp} suggestions={suggestCount} onSwitched={onSwitched} />
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

// WORK: the day to day, then the Mentor when the Gateway offers it, then Reports (see MENTOR_ITEM). A component of its
// own because the Mentor entry is read under the CurrentTeamProvider the shell mounts. With a team on screen, Reports is
// the team's page and carries its count, if the Gateway gives it one; on the own account it is the person's reports.
function WorkSection({
  team,
  counts,
  location,
  collapsed,
}: {
  team: TeamSummary | null;
  counts: Readonly<Record<string, number>>;
  location: Location;
  collapsed: boolean;
}) {
  const mentorOffered = useMentorEntry();
  const reports =
    team === null ? REPORTS_ITEM : teamPagesNav(team.app, counts).find((item) => item.to === reportsPath(team)) ?? null;
  const items = [...NAV_WORK, ...(mentorOffered ? [MENTOR_ITEM] : []), ...(reports !== null ? [reports] : [])];
  return (
    <NavSection label="Work" collapsed={collapsed} testId="nav-work">
      <NavList items={items} location={location} collapsed={collapsed} />
    </NavSection>
  );
}

/** The address of the team's Reports page, as the Gateway's verdict gives it; null when the verdict does not list it. */
function reportsPath(team: TeamSummary): string | null {
  return team.app.pages.find((p) => p.id === REPORTS_PAGE_ID)?.path ?? null;
}

// The team's block: its name as the heading, Team, and the team's own pages other than Reports, which is in Work.
function TeamBlock({
  team,
  counts,
  location,
  collapsed,
}: {
  team: TeamSummary;
  counts: Readonly<Record<string, number>>;
  location: Location;
  collapsed: boolean;
}) {
  const items = [TEAM_ITEM, ...teamPagesNav(team.app, counts).filter((item) => item.to !== reportsPath(team))];
  return (
    <NavSection label={team.name} collapsed={collapsed} testId="nav-team" team>
      <NavList items={items} location={location} collapsed={collapsed} />
    </NavSection>
  );
}

// One labelled section of the menu: a readable label, then its rows. Collapsed to
// icons, a thin line stands in for the label. The label names the group for a screen reader too.
function NavSection({
  label,
  collapsed,
  testId,
  team = false,
  children,
}: {
  label: string;
  collapsed: boolean;
  testId: string;
  team?: boolean;
  children: ReactNode;
}) {
  return (
    <div
      className={team ? "nav-section nav-section-team" : "nav-section"}
      role="group"
      aria-label={label}
      data-testid={testId}
    >
      {collapsed ? (
        <div className="nav-section-rule" aria-hidden="true" />
      ) : (
        <div className="nav-section-heading" aria-hidden="true" title={label} data-testid={`${testId}-heading`}>
          <span className="nav-section-label">{label}</span>
        </div>
      )}
      {children}
    </div>
  );
}

function NavList({
  items,
  location,
  collapsed,
}: {
  items: ReadonlyArray<NavItem>;
  location: Location;
  collapsed: boolean;
}) {
  const tabOnScreen = new URLSearchParams(location.search).get("tab");
  return (
    <ul className="nav-list">
      {items.map((item) => {
        const inSubtree = item.subtree !== undefined && location.pathname.startsWith(item.subtree);
        // A tab of a page is active on its own tabs only (see NavItem.tabs).
        const onTab =
          item.tabs === undefined
            ? null
            : location.pathname === item.to.split("?")[0] && tabOnScreen !== null && item.tabs.includes(tabOnScreen);
        return (
          <li key={item.to}>
            <NavLink
              to={item.to}
              end={item.to === "/"}
              className={({ isActive }) =>
                ((onTab ?? isActive) || inSubtree ? "nav-link nav-link-active" : "nav-link") +
                (item.promoted === true ? " nav-link-promoted" : "")
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
          </li>
        );
      })}
    </ul>
  );
}
