import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider, Navigate, Outlet } from "react-router-dom";
import { Home } from "./pages/Home";
import { NewSession } from "./pages/NewSession";
import { Terminal } from "./pages/Terminal";
import { Chat } from "./pages/Chat";
import { FileView } from "./pages/FileView";
import { VoiceMode } from "./pages/VoiceMode";
import { DEV_REPORT_ROUTES } from "./pages/reportRoutes";
import { Assistant } from "./pages/Assistant";
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
import { ensureGatewayCookie, configureUnauthorizedRedirect, mobileSignInRedirect } from "@devthrottle/client-core/api/client";
import { ensurePushSubscribed } from "@devthrottle/client-core/push/register";
import { installGlobalErrorReporting } from "@devthrottle/client-core/errors/reportClientError";
import { CreditsNotice } from "./components/CreditsNotice";
import { ConnectionBanner } from "./components/ConnectionBanner";
import { VoiceModeBanner } from "./components/VoiceModeBanner";
import { RecordingBanner } from "./components/RecordingBanner";
import { recordingSession } from "@devthrottle/client-core/recorder/recordingSession";
import { useVisibleViewportHeight } from "./hooks/useVisibleViewportHeight";
import { useScreenWakeLock } from "./hooks/useScreenWakeLock";
import { useKeepWarm } from "@devthrottle/client-core/net/useKeepWarm";
import { resumePendingDictations } from "@devthrottle/client-core/dictation/backgroundSend";
import { resumePendingRecordingUploads } from "@devthrottle/client-core/recorder/ingestUpload";
import { RouteRecoveryBoundary, RootLayout } from "./components/StaleShellRecovery";
import "./styles.css";

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

// Mirror the enrolled per-device key into the cc-gateway-token cookie at startup so the live
// terminal WebSocket (which cannot carry an Authorization header) authenticates same-origin to the
// Gateway. The key is already in this origin's storage, so the cookie exposes nothing new; this is a
// no-op before the phone has enrolled. (No token is injected into the page - issue #908.)
ensureGatewayCookie();

// Re-gate a mid-session 401 (a revoked device key) through THIS shell's own /mobile/signin enrollment
// screen. This is the mobile default in shared client-core, but each shell installs its own redirect
// so the desktop Cockpit can install its own /signin flow instead (issues #1024/#1088); installing it
// here explicitly keeps the mobile shell self-documenting about its own re-gate entry.
configureUnauthorizedRedirect(mobileSignInRedirect);

// The client error channel: uncaught browser errors and un-awaited promise failures are reported to
// the Gateway (POST /client-errors) so they land in the server log - no error exists only on the
// phone's screen. Pages that handle and render errors report those explicitly at their call sites.
installGlobalErrorReporting("mobile");

// If the user already granted notification permission on a previous visit, silently refresh the push
// subscription so the Gateway's record stays current (subscriptions can rotate). Never prompts here -
// that needs a user gesture (the "Enable notifications" button on the roster). Non-fatal on failure.
void ensurePushSubscribed();

// Force the newest bundle into a long-lived Progressive Web App. The service worker is network-first for
// the shell + bundle (vite.config.ts), so a plain reopen already fetches the latest; this handles the
// other case - the app has been open in the background while a NEW build deployed. When the new service
// worker takes control (skipWaiting + clientsClaim on activate), reload ONCE so the page drops the old
// in-memory JS and runs the new build. Armed ONLY when the page is ALREADY controlled at load (a
// returning install): a first visit that starts uncontrolled is claimed by clientsClaim, which is NOT an
// update and must not trigger a reload. The one-shot flag prevents any reload loop.
if ("serviceWorker" in navigator && navigator.serviceWorker.controller !== null) {
  let reloadingForNewWorker = false;
  navigator.serviceWorker.addEventListener("controllerchange", () => {
    if (reloadingForNewWorker) return;
    // NEVER reload over a live recording (recorder-unlimited-capture mission): a deploy landing
    // mid-capture would kill the microphone and truncate the recording to force an update the user
    // never asked for. The stale shell keeps running; the new build takes over on the next open.
    if (recordingSession.isCapturing()) {
      console.log("[mobile] a new service worker took control - reload deferred, a recording is live");
      return;
    }
    reloadingForNewWorker = true;
    console.log("[mobile] a new service worker took control - reloading to run the latest build");
    window.location.reload();
  });
}

// The app is served under /mobile, so the router is rooted there. A hard navigation to a deep link
// (e.g. /mobile/session/<id>) is served the injected index.html by the Gateway and the router then
// resolves it client-side. The old /m mount is 301-redirected to /mobile by the Gateway, so an
// installed PWA or a bookmark on /m/... still lands here.
// All routes hang off a root layout that carries the errorElement, so a no-route match (the symptom
// of a stale service-worker shell whose router lacks the navigated route, issue #1155) is caught by
// RouteRecoveryBoundary and self-healed instead of dead-ending on React Router's raw 404.
const router = createBrowserRouter(
  [
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
            // /car was Car Mode, removed from the product (#1028). The route stays only to catch an
            // installed shortcut or a stale service-worker shell and land it on the Assistant - the surface
            // that still talks to the whole fleet by voice - rather than on a dead route or a blank screen.
            { path: "/car", element: <Navigate to="/assistant" replace /> },
            // The Assistant (fleet assistant build): fleet-level chat + voice, not tied to any
            // session - the phone view of the same client-core turn machine the cockpit mounts.
            // Distinct from Car Mode: button turns (tap to talk), no auto turn taking, hands-on.
            { path: "/assistant", element: <Assistant /> },
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
  ],
  { basename: "/mobile" }
);

const rootElement = document.getElementById("root");
if (rootElement === null) {
  throw new Error("Root element #root not found in the document");
}

ReactDOM.createRoot(rootElement).render(
  <React.StrictMode>
    <RouterProvider router={router} />
    <CreditsNotice />
  </React.StrictMode>
);
