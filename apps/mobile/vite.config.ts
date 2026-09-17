import { execSync } from "node:child_process";
import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import { VitePWA } from "vite-plugin-pwa";

// Stamp the mobile app's OWN build identity, exactly as apps/cockpit/vite.config.ts stamps the
// cockpit's. Until this existed the mobile bundle had NO version identity anywhere: its About page
// showed the content-hashed script filename it scraped out of the live DOM (an opaque hash, not a
// build), and the Gateway had no way at all to say which mobile app it was serving.
//
// Two SOURCES for the one fact, never a fallback to an invented value (the same rule the cockpit
// config documents at length): git HEAD in every normal build path, or COCKPIT_COMMIT when the build
// runs inside the container image, whose build context excludes .git so git cannot answer. If neither
// yields a commit the build fails loudly rather than stamping "unknown".
const commitFromEnvironment = process.env.COCKPIT_COMMIT?.trim();
const mobileCommit =
  commitFromEnvironment && commitFromEnvironment.length > 0
    ? commitFromEnvironment
    : execSync("git rev-parse --short HEAD").toString().trim();
const mobileBuildTime = new Date().toISOString();

// The same two facts as a file the SERVER can read: dist/build.json, copied by the Gateway's MSBuild
// target to wwwroot/mobile/build.json. The defines feed the phone's own About page; the file lets the
// Gateway report the served mobile build to the Cockpit About page, which cannot run this bundle.
// Not precached by the service worker (workbox globPatterns covers only icons/fonts), so a reload
// always reads the deployed file rather than a cached copy of an older one.
function buildStampPlugin(commit: string, buildTime: string): Plugin {
  return {
    name: "devthrottle-build-stamp",
    apply: "build",
    generateBundle() {
      this.emitFile({
        type: "asset",
        fileName: "build.json",
        source: `${JSON.stringify({ commit, buildTime }, null, 2)}\n`,
      });
    },
  };
}

// Dev-only: the mobile shell calls the Gateway through root-relative URLs, so a local Vite run
// needs an explicit Gateway front door for enrollment and the real app APIs. Without this proxy,
// POST /mobile/enroll falls through to Vite's SPA shell and returns <!doctype html>; the enrollment
// callback then tries to parse that as JSON and strands a fresh visitor on "Something went wrong".
//
// Opt in with MOBILE_PROXY_TARGET (for example https://gateway.devthrottle.com). Production is
// unchanged: the Gateway serves the built app and all of these routes same-origin. /mobile/enroll is
// intentionally exact; proxying all of /mobile would steal the local app, its assets, and its routes.
const proxyTarget = process.env.MOBILE_PROXY_TARGET;
const devProxy = proxyTarget
  ? {
      "/sessions": { target: proxyTarget, changeOrigin: true, ws: true },
      "/directors": { target: proxyTarget, changeOrigin: true },
      "/interrupted": { target: proxyTarget, changeOrigin: true },
      "/fanout": { target: proxyTarget, changeOrigin: true },
      "/cron": { target: proxyTarget, changeOrigin: true },
      "/wingman": { target: proxyTarget, changeOrigin: true },
      "/lists": { target: proxyTarget, changeOrigin: true },
      "/account": { target: proxyTarget, changeOrigin: true },
      "/gateway": { target: proxyTarget, changeOrigin: true },
      "/ingest": { target: proxyTarget, changeOrigin: true },
      "/dictation": { target: proxyTarget, changeOrigin: true },
      "/transcription": { target: proxyTarget, changeOrigin: true },
      // The Transcription tab of Settings shows the folded microphone-quality verdict, which the phone
      // never read before that tab existed.
      "/voice-quality": { target: proxyTarget, changeOrigin: true },
      "/turnbriefs": { target: proxyTarget, changeOrigin: true },
      "/vault": { target: proxyTarget, changeOrigin: true },
      // Dev reports (dev reports mission, phase 3): the four owner routes the Reports view calls.
      "/dev-reports": { target: proxyTarget, changeOrigin: true },
      "/push": { target: proxyTarget, changeOrigin: true },
      // The Assistant screen: its turn calls POST /assistant/turn and its keep-warm ping POST /brain/warmup.
      "/assistant": { target: proxyTarget, changeOrigin: true },
      "/brain": { target: proxyTarget, changeOrigin: true },
      // The client error channel: on-screen and uncaught errors report to the Gateway log.
      "/client-errors": { target: proxyTarget, changeOrigin: true },
      "/stats": { target: proxyTarget, changeOrigin: true },
      "/healthz": { target: proxyTarget, changeOrigin: true },
      "/diag": { target: proxyTarget, changeOrigin: true },
      // The enrollment mint shares the app's prefix, so proxy only the API leaf.
      "/mobile/enroll": { target: proxyTarget, changeOrigin: true },
      // The public site still returns mobile enrollment through the legacy callback path. The
      // Gateway redirects it to /mobile/device-callback, preserving the URL fragment.
      "/m/device-callback": { target: proxyTarget, changeOrigin: true },
    }
  : undefined;

// The app is served by the Gateway under /mobile, so every asset URL must be /mobile-rooted.
// (The Gateway 301-redirects the old /m mount to /mobile so installed PWAs and bookmarks keep working.)
// The PWA service worker caches the app shell (Issue 1, AC7) so the roster opens offline
// showing the last-known data. Build output goes to dist/, which the Gateway's release-gated
// MSBuild target copies into wwwroot/mobile/.
export default defineConfig({
  base: "/mobile/",
  define: {
    __MOBILE_COMMIT__: JSON.stringify(mobileCommit),
    __MOBILE_BUILD_TIME__: JSON.stringify(mobileBuildTime),
  },
  server: {
    proxy: devProxy,
  },
  plugins: [
    react(),
    buildStampPlugin(mobileCommit, mobileBuildTime),
    VitePWA({
      registerType: "autoUpdate",
      // The injected token script in index.html must survive into the served shell, and the
      // service worker must NOT cache index.html (it carries the per-machine token); we serve
      // index.html through the Gateway so it can inject the token every load.
      injectRegister: "auto",
      manifest: {
        name: "DevThrottle Mobile",
        short_name: "DevThrottle",
        description: "Mission Control for Claude Code, on your phone.",
        start_url: "/mobile/",
        scope: "/mobile/",
        display: "standalone",
        background_color: "#0B1020",
        theme_color: "#0B1020",
        icons: [
          {
            src: "/mobile/icon-192.png",
            sizes: "192x192",
            type: "image/png",
          },
          {
            src: "/mobile/icon-512.png",
            sizes: "512x512",
            type: "image/png",
          },
        ],
      },
      workbox: {
        // Update promptly on reload (Car Mode diagnostic): a new service worker takes control of the
        // page as soon as it installs (skipWaiting) and claims the already-open clients (clientsClaim),
        // and the stale precache from the previous build is deleted (cleanupOutdatedCaches). Combined
        // with registerType "autoUpdate" above, this means a single refresh actually replaces the cached
        // bundle instead of serving the old one until every tab is closed. These are the autoUpdate
        // defaults; they are set explicitly here so the update behavior is visible and cannot silently
        // regress.
        skipWaiting: true,
        clientsClaim: true,
        cleanupOutdatedCaches: true,
        // Web Push (app-icon "needs you" dot): import the hand-written push handler into the
        // generated service worker. It only adds push/notificationclick/message listeners and does
        // not touch Workbox's precache/offline behavior. The file ships verbatim from public/ and is
        // served by the Gateway at /mobile/push-sw.js (importScripts URLs are relative to the SW scope).
        // The ?v= is a cache-buster AND an update trigger: bump it whenever push-sw.js changes so the
        // generated sw.js content changes too, which is what makes the browser re-install the service
        // worker and re-import the new push handler (an unchanged sw.js is never re-fetched). The
        // Gateway serves push-sw.js ignoring the query, with no-cache.
        importScripts: ["push-sw.js?v=2"],
        // NETWORK-FIRST APP SHELL (root cause fix): the app shell and the JS/CSS bundle are NO LONGER
        // precached and served cache-first. A precached, cache-first index.html was serving the phone a
        // STALE bundle for a whole load even though the Gateway serves a fresh index.html (no-cache) - so
        // deploys never reached Soren's installed PWA, and skipWaiting/clientsClaim lost the race because
        // the old worker served the stale shell before the update could win. Car Mode is under active
        // iteration, so correctness beats offline caching here: only rarely-changing icons/fonts are
        // precached, and the shell + bundle go through network-first runtime rules below (fresh when
        // online, last-known copy only as an offline fallback). No navigateFallback: navigations are the
        // network-first "m-shell" route below, not a cache-first precached page.
        globPatterns: ["**/*.{png,svg,ico,woff2}"],
        runtimeCaching: [
          {
            // The app shell: EVERY navigation under /mobile fetches the current index.html from the Gateway
            // first (served no-cache), so the phone always loads the latest bundle with no manual cache
            // clear. Falls back to the last-served shell ONLY when the network is unavailable (offline
            // open), after a short timeout so a slow network does not stall the open.
            urlPattern: ({ request, url }) => request.mode === "navigate" && url.pathname.startsWith("/mobile"),
            handler: "NetworkFirst",
            options: {
              cacheName: "m-shell",
              networkTimeoutSeconds: 4,
              expiration: { maxEntries: 8, maxAgeSeconds: 60 * 60 * 24 },
              cacheableResponse: { statuses: [0, 200] },
            },
          },
          {
            // The hashed JS/CSS bundle: network-first so an iterating build is picked up immediately. The
            // filenames are content-hashed, so the cached copy is always a correct offline fallback.
            urlPattern: ({ url }) => url.pathname.startsWith("/mobile/assets/"),
            handler: "NetworkFirst",
            options: {
              cacheName: "m-assets",
              networkTimeoutSeconds: 4,
              expiration: { maxEntries: 40, maxAgeSeconds: 60 * 60 * 24 * 7 },
              cacheableResponse: { statuses: [0, 200] },
            },
          },
          {
            // Cache the last /sessions response so an offline open shows the last-known roster.
            urlPattern: ({ url }) => url.pathname === "/sessions",
            handler: "NetworkFirst",
            options: {
              cacheName: "sessions-cache",
              expiration: { maxEntries: 1, maxAgeSeconds: 60 * 60 * 24 },
              cacheableResponse: { statuses: [0, 200] },
            },
          },
        ],
      },
    }),
  ],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: false,
  },
});
