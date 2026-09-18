import React from "react";
import { Navigate, Outlet, type RouteObject } from "react-router-dom";
import { Home } from "./pages/Home";
import { NewSession } from "./pages/NewSession";
import { Terminal } from "./pages/Terminal";
import { Chat } from "./pages/Chat";
import { FileView } from "./pages/FileView";
import { VoiceMode } from "./pages/VoiceMode";
import { DEV_REPORT_ROUTES } from "./pages/reportRoutes";
import { Settings } from "./pages/Settings";
import { Recorder } from "./pages/Recorder";
import { About } from "./pages/About";
import { Diagnostics } from "./pages/Diagnostics";
import { YourThrottle } from "./pages/YourThrottle";
import { Repos } from "./pages/Repos";
import { Account } from "./pages/Account";
import { SignIn } from "@devthrottle/client-core/auth/SignIn";
import { DeviceCallback } from "@devthrottle/client-core/auth/DeviceCallback";
import { RequireDeviceKey } from "./components/RequireDeviceKey";
import { ConnectionBanner } from "./components/ConnectionBanner";
import { VoiceModeBanner } from "./components/VoiceModeBanner";
import { RecordingBanner } from "./components/RecordingBanner";
import { useVisibleViewportHeight } from "./hooks/useVisibleViewportHeight";
import { useScreenWakeLock } from "./hooks/useScreenWakeLock";
import { useKeepWarm } from "@devthrottle/client-core/net/useKeepWarm";
import { resumePendingDictations } from "@devthrottle/client-core/dictation/backgroundSend";
import { resumePendingRecordingUploads } from "@devthrottle/client-core/recorder/ingestUpload";
import { RouteRecoveryBoundary, RootLayout } from "./components/StaleShellRecovery";

// THE PHONE'S ROUTE TABLE, IN ONE PLACE THE TESTS CAN MOUNT (dev reports mission, phase 3b).
//
// It used to be a literal inside main.tsx, where nothing could reach it: importing that module starts the
// whole app - it mirrors the device key into a cookie, installs the 401 re-gate, registers global error
// reporting and refreshes the push subscription. So every routing test wrote its OWN little route table,
// and a test written that way says nothing about the table the app actually mounts. `/r/{id}` landing
// nowhere when signed out was exactly that shape of defect: the address was fine, the route did not exist,
// and no test could see it.
//
// main.tsx keeps every startup side effect and hands this array, and the basename, to createBrowserRouter.

/** The path the phone app is served under. The router is rooted here, so every route below is relative to it. */
export const MOBILE_BASENAME = "/mobile";

// The layout wrapping every gated page. It owns the app-level screen wake lock (issue #981) so the
// phone stays awake on ANY page (roster, Chat, Voice, New session, AI settings, Terminal) while the
// app is foregrounded. Because this layout stays mounted across navigations between gated child
// routes, the lock is acquired once (a single sentinel), not once per page.
function GatedLayout() {
  useScreenWakeLock();
  // THE viewport fit for the whole app: publishes the true visible height as --app-vh, which
  // .terminal-screen sizes itself from. Mounted ONCE here so no screen has to
  // solve "does it fit?" privately again - that is why this bug kept coming back. See the hook.
  useVisibleViewportHeight();
  // Keep-warm heartbeat (P2): hold the direct LAN path open during active use so it never idles back to the relay.
  useKeepWarm();
  // Resume any recorded-but-unsent dictation once the phone is enrolled (issue #1006): a clip whose
  // upload was interrupted by a refresh / crash / dropped connection is re-driven to the session here.
  React.useEffect(() => {
    void resumePendingDictations();
    // Same durable resume for long-form recordings (issue #958): a recording whose send was
    // interrupted by a refresh / crash / dropped connection is re-driven to the /ingest pipeline
    // here, from its durable IndexedDB copy. Recordings not yet sent are left alone.
    void resumePendingRecordingUploads();
  }, []);
  // The one global network banner (mobile-resilience mission): mounted once here so it pins to the top
  // of every gated screen and is the single voice for a network problem - unreachable, or the Gateway's
  // own "Slow" verdict. It renders NOTHING while the connection is fine, which is nearly always; the
  // pages keep their last-known content underneath either way.
  //
  // There is no network status pill any more, on any screen. There used to be one - fixed in the
  // top-right on most screens, rendered inline on the four screens that had a header to give it - and
  // it was removed entirely on 2026-07-26 (see ConnectionBanner for the history). It reported "good"
  // almost every second of its life while making every screen with a top-right control work around it.
  // A route list of "which screens suppress the pill" no longer exists, so a new screen cannot get this
  // wrong: add a route and it inherits the banner, which is the whole story.
  return (
    <>
      <ConnectionBanner />
      {/* The voice-mode banner is mounted HERE, beside the connection banner, for one reason: it has to be
          on every screen. While auto-speak is running you are on the roster for three seconds at a time and
          inside a session the rest of the time, so an off switch that lives only on the roster is a window
          to catch, not a way out. Here it is on the session screen auto-speak just dropped you into. It
          renders nothing at all when voice mode is off. */}
      <VoiceModeBanner />
      {/* The recording banner is mounted here for the same reason as the voice-mode banner: a live
          recording survives navigation (the session lives above the router), so its indicator must
          be on every screen too. Renders nothing while no recording is running, and nothing on the
          Recorder page itself. */}
      <RecordingBanner />
      <Outlet />
    </>
  );
}

// The app is served under /mobile, so the router is rooted there. A hard navigation to a deep link
// (e.g. /mobile/session/<id>) is served the injected index.html by the Gateway and the router then
// resolves it client-side. The old /m mount is 301-redirected to /mobile by the Gateway, so an
// installed PWA or a bookmark on /m/... still lands here.
// All routes hang off a root layout that carries the errorElement, so a no-route match (the symptom
// of a stale service-worker shell whose router lacks the navigated route, issue #1155) is caught by
// RouteRecoveryBoundary and self-healed instead of dead-ending on React Router's raw 404.
export const MOBILE_ROUTES: RouteObject[] = [
  {
    element: <RootLayout />,
    errorElement: <RouteRecoveryBoundary />,
    children: [
      // Ungated: reachable before the phone has enrolled.
      { path: "/signin", element: <SignIn /> },
      { path: "/device-callback", element: <DeviceCallback /> },
      // Gated: everything real requires an enrolled device key.
      {
        element: (
          <RequireDeviceKey>
            <GatedLayout />
          </RequireDeviceKey>
        ),
        children: [
          { path: "/", element: <Home /> },
          // /car was Car Mode (removed, #1028) and /assistant was the Assistant (removed by the Fleet
          // Manager mission, step 9; the Fleet Manager is on the Cockpit only). The routes stay only to
          // catch an installed shortcut or a stale service-worker shell and land it on the session list
          // rather than on a dead route or a blank screen.
          { path: "/car", element: <Navigate to="/" replace /> },
          { path: "/assistant", element: <Navigate to="/" replace /> },
          // Settings: the same tabbed page the Cockpit shows, from the same components
          // (client-core/settings). The tab rides in ?tab=.
          { path: "/settings", element: <Settings /> },
          // The two dictation checks used to be screens of their own, reached from the menu and from
          // links on the AI settings screen. They are cards on the Transcription tab now - the same
          // tab, with the same cards, that the desktop shows. These redirects keep every existing
          // bookmark, home-screen shortcut, and older service-worker shell working: they land on the
          // tab that holds what they asked for rather than on a dead route.
          { path: "/mic-test", element: <Navigate to="/settings?tab=transcription" replace /> },
          { path: "/transcription-test", element: <Navigate to="/settings?tab=transcription" replace /> },
          // The Voice Recorder (issue #958): long-form recording -> durable local segments ->
          // /ingest upload -> Gateway transcription -> the Cockpit's Voice Recorder page. /notes
          // and /record were the names the retired native apps and old bookmarks used; they land
          // on the recorder rather than on a dead route.
          { path: "/recorder", element: <Recorder /> },
          { path: "/notes", element: <Navigate to="/recorder" replace /> },
          { path: "/record", element: <Navigate to="/recorder" replace /> },
          // Account (devthrottle_internal #1507/#1509): who is signed in on this phone, switching
          // between logins, adding one, and signing out. Mirrors the Cockpit's Account destination -
          // the phone had none, which is why it had no way to sign out either.
          { path: "/account", element: <Account /> },
          { path: "/about", element: <About /> },
          // Diagnostics (auto-network-switching mission): a phone-side connection tester - route
          // (direct LAN vs Tailscale relay), latency, and download/upload throughput, with a verdict.
          { path: "/diagnostics", element: <Diagnostics /> },
          // Your Throttle (devthrottle-stats mission): the in-app port of the standalone Gateway
          // /stats page, reading the same GET /stats/data feed through client-core.
          { path: "/throttle", element: <YourThrottle /> },
          // Repos (devthrottle-stats mission): the PRIVATE per-repo split, its own page separate from
          // Your Throttle. Reads the same GET /stats/data feed (repos ride on it) through client-core.
          { path: "/repos", element: <Repos /> },
          { path: "/new", element: <NewSession /> },
          { path: "/session/:sessionId", element: <Chat /> },
          { path: "/session/:sessionId/chat", element: <Chat /> },
          { path: "/session/:sessionId/terminal", element: <Terminal /> },
          { path: "/session/:sessionId/voice", element: <VoiceMode /> },
          // Dev reports (dev reports mission, phase 3): the session's reports, and one report full screen
          // with its conversation in a bottom sheet. The list, the frame and the conversation are the
          // shared client-core view the Cockpit's Reports tab also mounts.
          ...DEV_REPORT_ROUTES,
          // Local Files (Phase 3): the full-screen file viewer. Reached from a clicked file path in
          // the session's Chat or Terminal; the absolute path rides as ?path=. Not a tab in ViewTabs
          // (it is a leaf view of the session, dismissed with Back), matching the Cockpit modal.
          { path: "/session/:sessionId/file", element: <FileView /> },
        ],
      },
    ],
  },
];
