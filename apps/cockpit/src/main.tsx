import React from "react";
import ReactDOM from "react-dom/client";
import { RouterProvider, createBrowserRouter } from "react-router-dom";
import { COCKPIT_ROUTES } from "./routes";
import { ensureGatewayCookie, configureUnauthorizedRedirect, cockpitSignInRedirect } from "@devthrottle/client-core/api/client";
import { configureEnrollment, COCKPIT_ENROLLMENT_PROFILE } from "@devthrottle/client-core/auth/enrollRequest";
import { ensurePushSubscribed } from "@devthrottle/client-core/push/register";
import { installGlobalErrorReporting } from "@devthrottle/client-core/errors/reportClientError";
import { registerCockpitServiceWorker } from "./push/registerSw";
import "./styles.css";
import "./components/components.css";
import "./assistant/assistant.css";
import "./fleet/fleet.css";
import "./fleet/fleetmap.css";
import "./history/history.css";
import "./missions/missions.css";
import "./schedule/schedule.css";
import "./workflows/workflows.css";
import "./dictionary/dictionary.css";
import "./transcripts/transcripts.css";
import "./throttle/throttle.css";
import "./transcription/transcriptionhealth.css";
import "./account/account.css";
import "./about/about.css";
import "./settings/settings.css";
import "./settings/injectedtext.css";

// Browser device enrollment is the front door (issue #1088): this desktop shell authenticates with
// the SAME shared client-core device-auth flow the phone shipped with (#908). The Cockpit installs its
// own enrollment profile - the /device-callback return path, the "browser" platform, a
// human-recognizable "Edge on Windows"-style device name - so the shared SignIn/DeviceCallback screens
// enroll THIS browser as a device on the account. The device key is the only standing credential.
configureEnrollment(COCKPIT_ENROLLMENT_PROFILE);

// Mirror the enrolled per-device key into the cc-gateway-token cookie at startup so the live terminal
// WebSocket (which cannot carry an Authorization header) authenticates same-origin to the Gateway.
// This is the same startup call the mobile shell makes; it exposes nothing the page does not already
// hold, and it is a no-op before this browser has enrolled.
ensureGatewayCookie();

// Re-gate a mid-session 401 (a device key revoked from the website's "Your devices") through the
// DESKTOP shell's own sign-in entry - the shared /signin enrollment flow, carrying the current route
// in next= - instead of the mobile /m/signin route baked into shared client-core (issue #1024) and
// instead of the retired /login token wall (issue #1088: a revoke returns the browser to the shared
// sign-in flow, never to login.html).
configureUnauthorizedRedirect(cockpitSignInRedirect);

// The client error channel: uncaught browser errors and un-awaited promise failures are reported to
// the Gateway (POST /client-errors) so they land in the server log - no error exists only in a user's
// devtools console. Pages that handle and render errors report those explicitly at their call sites.
installGlobalErrorReporting("cockpit");

// Browser notifications (issue #1257): register the Cockpit's push service worker, then - only if the
// user already granted notification permission on a previous visit - silently refresh this browser's
// push subscription so the Gateway's record stays current across subscription rotations. Neither call
// prompts (that needs a user gesture: the Settings > Notifications toggle). Both are fire-and-forget and
// non-fatal - the Cockpit works fully without notifications. This reuses the exact plumbing the phone
// shipped with (#905); there is no new Gateway code.
void registerCockpitServiceWorker().then(() => ensurePushSubscribed());

// The app is the Gateway's canonical Cockpit, served at the site root "/" (issue #979 cutover). A
// hard navigation to a deep link (e.g. /fleet) is served the index.html shell by the Gateway and the
// router then resolves it client-side. The route table itself lives in ./routes so the tests can mount the
// one the app mounts - importing THIS module starts the whole app.
const router = createBrowserRouter(COCKPIT_ROUTES, {
  // Opt in to the React Router v7 behaviours now so the transition is a no-op and the six
  // per-page future-flag console warnings (one per unset flag) are silenced. v7_startTransition
  // is a RouterProvider flag (set on <RouterProvider> below); the rest are data-router flags.
  future: {
    v7_fetcherPersist: true,
    v7_normalizeFormMethod: true,
    v7_partialHydration: true,
    v7_relativeSplatPath: true,
    v7_skipActionErrorRevalidation: true,
  },
});

const rootElement = document.getElementById("root");
if (rootElement === null) {
  throw new Error("Root element #root not found in the document");
}

ReactDOM.createRoot(rootElement).render(
  <React.StrictMode>
    <RouterProvider router={router} future={{ v7_startTransition: true }} />
  </React.StrictMode>
);
