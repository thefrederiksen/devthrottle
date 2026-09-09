import { useEffect, useState } from "react";
import { NavLink, Outlet, useLocation } from "react-router-dom";
import { useKeepWarm } from "@devthrottle/client-core/net/useKeepWarm";
import { getSuggestionCount } from "@devthrottle/client-core/dictation/dictionaryClient";
import { resumePendingDictations } from "@devthrottle/client-core/dictation/backgroundSend";
import { NavIcon, type NavIconName } from "./components";
import { CockpitStatusPill } from "./network/CockpitStatusPill";
import { StopSessionProvider } from "./sessions/StopSessionProvider";

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
// Sessions is first, then Fleet Map, then Assistant: the sessions are the work, so the destination you
// reach for most sits at the top of the rail, with the whole-fleet picture and the assistant behind it.
// The Fleet Map remains the default landing (issue #1303): a fresh boot at "/" redirects to it, so the
// Cockpit still opens on the whole-fleet picture (main.tsx). Sessions lives at its own /sessions
// home. `subtree` marks a destination active for a route family that does NOT share its path prefix:
// the session detail routes into "/session/:id" - a different path from "/sessions" - so Sessions
// needs an explicit subtree to stay highlighted while a session is being driven (the Directors item
// does not, because "/directors/:id" already shares the "/directors" prefix NavLink matches by
// default).
interface NavItem {
  to: string;
  label: string;
  icon: NavIconName;
  subtree?: string;
  // When set, the item is an EXTERNAL link opened in a new tab rather than an in-app route: it renders
  // a plain anchor to this absolute URL instead of a NavLink, and `to` is ignored. Used for Help, which
  // leaves the app for the public documentation site.
  href?: string;
  // A red attention count rendered as a badge on the row (devthrottle #2075). The value is computed on
  // the Gateway and rendered here verbatim - the client never re-derives it (rule 7: the client is dumb).
  // Only the Dictionary item carries one today (pending dictionary suggestions).
  badge?: number;
}

// The fleet work: what is running, how it is driven, and the corpora and tools it reads and writes.
// Workflows sits with Schedule on purpose - Schedule is what runs when, Workflows is how work runs,
// and it is next to the place you start work rather than filed away under settings.
const NAV_MAIN: ReadonlyArray<NavItem> = [
  { to: "/sessions", label: "Sessions", icon: "sessions", subtree: "/session" },
  { to: "/fleet-map", label: "Fleet Map", icon: "fleet-map" },
  { to: "/assistant", label: "Assistant", icon: "assistant" },
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

export function AppShell() {
  const location = useLocation();
  // Keep-warm heartbeat (P2): hold the direct LAN path open during active use.
  useKeepWarm();

  // Resume any recorded-but-unsent dictation once this enrolled shell mounts, exactly like the mobile
  // GatedLayout does (issue #1006): a clip whose upload was interrupted by a refresh / closed tab /
  // dropped connection is re-driven to its session from the durable on-device queue. Without this, the
  // Cockpit's fire-and-forget Speak Send (SessionComposer / VoiceTab) could persist a clip and then
  // never deliver it after a reload - saved forever, sent never.
  useEffect(() => {
    void resumePendingDictations();
  }, []);

  // The pending dictionary-suggestions count (devthrottle #2075) - the Gateway-owned verdict rendered as a
  // red badge on the Dictionary nav item. Polled here so the whole app shows the attention signal without
  // opening the page, and re-read on every route change so applying/dismissing on the page updates it
  // promptly (leaving the page re-polls). The client renders the number; it never decides it.
  const [suggestCount, setSuggestCount] = useState(0);
  useEffect(() => {
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
  }, [location.pathname]);

  const mainNav = NAV_MAIN.map((item) =>
    item.to === "/dictionary" ? { ...item, badge: suggestCount } : item,
  );

  // THE STOP ANSWER IS OWNED HERE, above every roster row and every session page (mission "Stop a
  // session", inspection finding I4). A stop removes the row it was started from, and the shared roster
  // poll unmounts that row within two seconds - so a stop dialog owned by the row would be torn down,
  // unread, by the refresh that the stop itself caused. This provider outlives every route change, so
  // neither the outstanding request nor the Gateway's answer can go with the row.
  return (
    <StopSessionProvider>
      <div className="shell">
        <nav className="rail rail-left" aria-label="Primary">
          <div className="brand">DevThrottle</div>
          <CockpitStatusPill />
          <div className="nav">
            <NavList items={mainNav} pathname={location.pathname} />
            <NavList items={NAV_FOOT} pathname={location.pathname} className="nav-list-foot" />
          </div>
          <div className="rail-foot">Cockpit (React)</div>
        </nav>

        <main className="main-pane" aria-label="Main">
          <Outlet />
        </main>
      </div>
    </StopSessionProvider>
  );
}

function NavList({
  items,
  pathname,
  className,
}: {
  items: ReadonlyArray<NavItem>;
  pathname: string;
  className?: string;
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
              >
                <NavIcon name={item.icon} />
                <span className="nav-link-label">{item.label}</span>
              </a>
            ) : (
              <NavLink
                to={item.to}
                end={item.to === "/"}
                className={({ isActive }) =>
                  isActive || inSubtree ? "nav-link nav-link-active" : "nav-link"
                }
              >
                <NavIcon name={item.icon} />
                <span className="nav-link-label">{item.label}</span>
                {item.badge !== undefined && item.badge > 0 && (
                  <span className="nav-badge" title={`${item.badge} pending`}>
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
