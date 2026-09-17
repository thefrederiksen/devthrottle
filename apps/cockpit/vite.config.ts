import { execSync } from "node:child_process";
import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";

// Stamp the cockpit's OWN build identity at build time so the About page can show exactly which
// cockpit is deployed, independently of the Gateway's version. The two can diverge when only the
// static cockpit files are refreshed (a cockpit-only redeploy), so the Gateway version alone cannot
// tell you which cockpit you are looking at. The commit is the repo HEAD at build time; the time is
// the build time. git is present in a normal build path (dev and the release MSBuild), so that is
// the default source and it is not wrapped in a fallback - if it cannot read the commit the build
// fails loudly.
//
// One build path genuinely has no git repository to ask: the container image. Its build context
// excludes .git, and a build run from a git worktree has only a pointer file there anyway, so
// `git rev-parse` cannot answer inside the image. That path supplies the commit directly through
// COCKPIT_COMMIT (the repo-root Dockerfile requires it as a build argument). This is a second
// SOURCE for the same fact, not a fallback to a made-up value: if neither the environment nor git
// yields a commit, the build still fails and no build is ever stamped "unknown".
const commitFromEnvironment = process.env.COCKPIT_COMMIT?.trim();
const cockpitCommit =
  commitFromEnvironment && commitFromEnvironment.length > 0
    ? commitFromEnvironment
    : execSync("git rev-parse --short HEAD").toString().trim();
const cockpitBuildTime = new Date().toISOString();

// The SAME two facts, written to a file the SERVER can read: dist/build.json, which the Gateway's
// MSBuild target copies to wwwroot/c/build.json. The defines above are compiled into the minified
// bundle, so they are readable only by the browser running that bundle - the Gateway process has no
// way to learn them, and before this file existed the About page could only report the cockpit build
// of the bundle it was itself served from, never the mobile app's, and never from a server-side call.
// One derivation, two destinations: both come from the two constants above, so the file and the
// bundle can never disagree about which build this is.
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

// The desktop Cockpit is the Gateway's canonical front door: it is served at the site root "/"
// (epic #967 cutover, issue #979 - the Blazor Server Cockpit is retired). Every asset URL is
// root-relative. Build output goes to dist/, which the Gateway's release-gated MSBuild target
// (BuildCockpitApp on CcDirector.Gateway.csproj) copies into wwwroot/c/; the Gateway serves those
// files at "/" and falls unknown page paths back to index.html for the React router.
//
// No service worker / PWA here: the desktop Cockpit is not an offline-installable app the way the
// phone shell is. It is a plain static single-page app the Gateway serves same-origin.
// Dev-only: when COCKPIT_PROXY_TARGET is set (e.g. http://127.0.0.1:7878 for a live Gateway, or a
// test Director's Control API port), the dev server fronts that origin for the Gateway routes the
// client calls root-relative - so `npm run dev` on this app drives a real fleet without a production
// build. The terminal WebSocket (/sessions/{sid}/stream) needs ws:true. This affects ONLY the dev
// server; the production build (served by the Gateway at /c) never uses it. The target is read from
// the environment, never a hard-coded address, so the Gateway-only-ingress rule is not weakened.
const proxyTarget = process.env.COCKPIT_PROXY_TARGET;
// Every root-relative prefix any Cockpit page calls must be fronted, or that page 404s under
// `npm run dev`. The list below is the union of the prefixes the pages fetch (client-core + the
// views): the session/fleet core plus every later-ported page -
// Account (/account), the settings + about pages (/gateway), Dictionary + recordings
// (/ingest, /dictation), Feedback (/turnbriefs), and the settings AI-key panel (/vault).
const devProxy = proxyTarget
  ? {
      "/sessions": { target: proxyTarget, changeOrigin: true, ws: true },
      "/directors": { target: proxyTarget, changeOrigin: true },
      "/interrupted": { target: proxyTarget, changeOrigin: true },
      "/fanout": { target: proxyTarget, changeOrigin: true },
      "/cron": { target: proxyTarget, changeOrigin: true },
      "/wingman": { target: proxyTarget, changeOrigin: true },
      // The Assistant screen (fleet assistant build): its turn calls POST /assistant/turn, and its
      // keep-warm ping POST /brain/warmup.
      "/assistant": { target: proxyTarget, changeOrigin: true },
      "/brain": { target: proxyTarget, changeOrigin: true },
      // The client error channel: on-screen and uncaught errors report to the Gateway log.
      "/client-errors": { target: proxyTarget, changeOrigin: true },
      "/lists": { target: proxyTarget, changeOrigin: true },
      "/account": { target: proxyTarget, changeOrigin: true },
      "/gateway": { target: proxyTarget, changeOrigin: true },
      "/ingest": { target: proxyTarget, changeOrigin: true },
      "/dictation": { target: proxyTarget, changeOrigin: true },
      "/turnbriefs": { target: proxyTarget, changeOrigin: true },
      "/vault": { target: proxyTarget, changeOrigin: true },
      // Dev reports (dev reports mission, phase 3): the four owner routes the Reports view calls.
      "/dev-reports": { target: proxyTarget, changeOrigin: true },
      // The Transcription Health page and the Transcription tab of Settings. Neither was proxied
      // before, so both could only ever be exercised against a Gateway that served the bundle itself.
      //
      // The three /transcription API paths are listed EXACTLY, never the "/transcription" prefix: the
      // Cockpit's own Transcription Health page is routed at /transcription, so proxying the prefix
      // hands the page's own URL to the Gateway and the app answers its own route with JSON. (The
      // mobile config can proxy the prefix safely - its app is mounted under /mobile.)
      // Work history (issue #2194): the API paths are listed EXACTLY, never the "/history" prefix,
      // for the same reason as /transcription below - the History page itself is routed at
      // /history, so proxying the prefix would hand the page's own URL to the Gateway.
      "/history/report": { target: proxyTarget, changeOrigin: true },
      "/history/sessions": { target: proxyTarget, changeOrigin: true },
      "/transcription/stats": { target: proxyTarget, changeOrigin: true },
      "/transcription/terms": { target: proxyTarget, changeOrigin: true },
      "/transcription/history": { target: proxyTarget, changeOrigin: true },
      "/voice-quality": { target: proxyTarget, changeOrigin: true },
      // Web Push (issue #1257): the notifications toggle fetches the VAPID public key and registers /
      // unregisters this browser's subscription at the Gateway's /push/* endpoints, so `npm run dev`
      // reaches them against a live Gateway too.
      "/push": { target: proxyTarget, changeOrigin: true },
      // Device enrollment (issue #1088): the shared client-core callback exchanges the cloud device
      // key at the Gateway's POST /mobile/enroll, so the enrollment flow works under `npm run dev` too.
      // /m is kept alongside /mobile because the Gateway still answers the old /m/enroll (back-compat).
      "/mobile": { target: proxyTarget, changeOrigin: true },
      "/m": { target: proxyTarget, changeOrigin: true },
    }
  : undefined;

export default defineConfig({
  base: "/",
  define: {
    __COCKPIT_COMMIT__: JSON.stringify(cockpitCommit),
    __COCKPIT_BUILD_TIME__: JSON.stringify(cockpitBuildTime),
  },
  plugins: [react(), buildStampPlugin(cockpitCommit, cockpitBuildTime)],
  server: {
    proxy: devProxy,
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: false,
  },
});
