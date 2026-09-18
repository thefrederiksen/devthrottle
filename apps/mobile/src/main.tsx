import React from "react";
import ReactDOM from "react-dom/client";
import { RouterProvider, createBrowserRouter } from "react-router-dom";
import { MOBILE_BASENAME, MOBILE_ROUTES } from "./routes";
import { ensureGatewayCookie, configureUnauthorizedRedirect, mobileSignInRedirect } from "@devthrottle/client-core/api/client";
import { ensurePushSubscribed } from "@devthrottle/client-core/push/register";
import { installGlobalErrorReporting } from "@devthrottle/client-core/errors/reportClientError";
import { CreditsNotice } from "./components/CreditsNotice";
import { recordingSession } from "@devthrottle/client-core/recorder/recordingSession";
import "./styles.css";

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

// The route table and the mount live in ./routes so the tests can mount the one the app mounts -
// importing THIS module starts the whole app.
const router = createBrowserRouter(MOBILE_ROUTES, { basename: MOBILE_BASENAME });

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
