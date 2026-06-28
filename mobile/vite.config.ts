import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { VitePWA } from "vite-plugin-pwa";

// The app is served by the Gateway under /m, so every asset URL must be /m-rooted.
// The PWA service worker caches the app shell (Issue 1, AC7) so the roster opens offline
// showing the last-known data. Build output goes to dist/, which the Gateway's release-gated
// MSBuild target copies into wwwroot/m/.
export default defineConfig({
  base: "/m/",
  plugins: [
    react(),
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
        start_url: "/m/",
        scope: "/m/",
        display: "standalone",
        background_color: "#0B1020",
        theme_color: "#0B1020",
        icons: [
          {
            src: "/m/icon-192.png",
            sizes: "192x192",
            type: "image/png",
          },
          {
            src: "/m/icon-512.png",
            sizes: "512x512",
            type: "image/png",
          },
        ],
      },
      workbox: {
        // App shell precache. index.html is served by the Gateway (token injection) and is
        // navigation-fallback cached so a cold offline open still renders the shell.
        navigateFallback: "/m/index.html",
        globPatterns: ["**/*.{js,css,html,png,svg,woff2}"],
        // Cache the last /sessions response so an offline open shows the last-known roster.
        runtimeCaching: [
          {
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
