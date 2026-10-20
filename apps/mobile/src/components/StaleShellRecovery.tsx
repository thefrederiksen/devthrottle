import { useEffect } from "react";
import { Outlet, isRouteErrorResponse, useRouteError } from "react-router-dom";

// Self-healing recovery for a stale service-worker shell (issue #1155).
//
// The mobile app is an offline-capable Progressive Web App: its service worker precaches the app
// shell and serves navigations from that cache. When a new build adds a client-side route, a
// returning browser can still be served the PREVIOUSLY cached shell for a load (the service worker
// serves cache-first, and a new worker only takes control on a later load). If that cached shell's
// router does not know the route being navigated to - the device-enrollment callback
// (/mobile/device-callback) is the one that bit us - React Router renders its raw "404 Not Found" and the
// user dead-ends, unable to sign in.
//
// skipWaiting + clientsClaim (already on via registerType: "autoUpdate") do NOT prevent this: the old
// worker has already rendered the stale shell before the background update+reload can win the race.
// The only deterministic cure is to recover AT the point of failure: when a navigation matches no
// route, throw away the service worker and its caches and reload once, which forces the browser to
// fetch the current shell straight from the Gateway (the Gateway always serves the up-to-date
// index.html with no-cache). After that reload the router is current and the route resolves.
//
// A single per-session guard prevents a reload loop: a genuinely non-existent URL cannot self-heal,
// so after one refresh we stop and show a plain message instead of reloading forever.

const RECOVERED_KEY = "cc.staleShellRecovered";

function hasRecovered(): boolean {
  try {
    return sessionStorage.getItem(RECOVERED_KEY) === "1";
  } catch {
    return false;
  }
}

function markRecovered(): void {
  try {
    sessionStorage.setItem(RECOVERED_KEY, "1");
  } catch {
    /* private mode / storage disabled: the guard degrades to "may reload again", still bounded by
       the fact that a fresh shell resolves the route on the first reload in the real (stale) case. */
  }
}

function clearRecovered(): void {
  try {
    sessionStorage.removeItem(RECOVERED_KEY);
  } catch {
    /* best effort */
  }
}

// Drop every service worker registration and cache for this origin, then reload. Best effort: if any
// step throws we still reload, because a plain reload alone often lets the newly-activated worker
// serve the current shell.
async function purgeAndReload(): Promise<void> {
  try {
    if ("serviceWorker" in navigator) {
      const regs = await navigator.serviceWorker.getRegistrations();
      await Promise.all(regs.map((r) => r.unregister()));
    }
    if (typeof caches !== "undefined") {
      const keys = await caches.keys();
      await Promise.all(keys.map((k) => caches.delete(k)));
    }
  } catch {
    /* fall through to the reload regardless */
  }
  window.location.reload();
}

const container: React.CSSProperties = {
  maxWidth: 420,
  margin: "0 auto",
  padding: "2.5rem 1.25rem",
  textAlign: "center",
};

/**
 * The router's top-level errorElement. On a no-route match (a stale shell whose router lacks the
 * navigated route) it purges the service worker + caches and reloads once so the browser picks up the
 * current shell. Any other error, or a 404 that survived the one refresh, shows a plain message with
 * a way back to the app.
 */
export function RouteRecoveryBoundary() {
  const error = useRouteError();
  const isNotFound = isRouteErrorResponse(error) && error.status === 404;
  const recovered = hasRecovered();
  const willSelfHeal = isNotFound && !recovered;

  useEffect(() => {
    if (!willSelfHeal) return;
    markRecovered();
    void purgeAndReload();
  }, [willSelfHeal]);

  if (willSelfHeal) {
    return (
      <div style={container}>
        <h1>Updating&hellip;</h1>
        <p style={{ opacity: 0.8 }}>Loading the latest version of DevThrottle.</p>
      </div>
    );
  }

  // A 404 that survived the one self-heal refresh is a page that genuinely does not exist - the
  // shell is current and its router has no such route. Say exactly that and link home (issue #958):
  // telling the user to refresh here was a lie, because refreshing can never make a missing route
  // appear. The non-404 branch keeps its own copy - that one really is an unexpected error.
  return <RouteMessage notFound={isNotFound} />;
}

/**
 * The phone's ordinary "Page not found", exactly as an unknown address shows it. A page whose Gateway says there is
 * no such page for this account (the Mail Desk with its switch off) shows this same message, so it looks like any
 * other missing address. It never reloads: only the router's own no-match self-heals, above.
 */
export function PageNotFound() {
  return <RouteMessage notFound />;
}

function RouteMessage({ notFound }: { notFound: boolean }) {
  return (
    <div style={container}>
      <h1>{notFound ? "Page not found" : "Something went wrong"}</h1>
      <p style={{ opacity: 0.8, marginBottom: "1.5rem" }}>
        {notFound
          ? "This page does not exist. The link may be wrong or out of date."
          : "An unexpected error occurred. Please return to DevThrottle and try again."}
      </p>
      <button
        type="button"
        onClick={() => {
          clearRecovered();
          window.location.assign("/mobile/");
        }}
        style={{ padding: "0.8rem 1.25rem", fontSize: "1rem", fontWeight: 600, borderRadius: 10, border: "none", cursor: "pointer" }}
      >
        Go to DevThrottle
      </button>
    </div>
  );
}

/**
 * The router's root layout. It exists so every real (matched) route renders through here, which lets
 * us clear the one-shot recovery guard on any successful load - so a later stale episode in a very
 * long-lived session can self-heal again.
 */
export function RootLayout() {
  useEffect(() => {
    clearRecovered();
  }, []);
  return <Outlet />;
}
