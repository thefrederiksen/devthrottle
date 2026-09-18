# DevThrottle Cockpit (React)

A React + TypeScript single-page app, served by the Gateway at the site root `/`. It is the desktop
**shell** of the Cockpit rebuild (epic #967): the whole user interface moved off Blazor Server onto
React, thin over the shared library `@devthrottle/client-core` (`packages/client-core`). Every shared
concern - the typed Gateway client, history rendering, the terminal byte-stream engine, dictation,
device auth, and session ordering - lives in the package, and this app owns only its screens, routing,
and styles.

This app is one member of the npm **workspace** rooted at the repository root (`packages/*` and
`apps/*`), alongside the mobile shell (`apps/mobile`). Install dependencies once at the workspace
root, not here.

## The canonical Cockpit

This React app **is** the Cockpit. The Blazor Server Cockpit was retired in the epic #967 cutover
(issue #979): the Gateway now serves this app at the site root `/` and every Cockpit page path falls
back to `index.html` so the React router resolves it, exactly as the mobile shell is served at `/m`.
There is no Blazor Cockpit process any more.

## Build

```bash
# From the repository root (the workspace root):
npm ci
npm run build --workspace @devthrottle/cockpit   # type-checks, then builds the static app into dist/
```

`dist/` is static files only. There is no runtime Node dependency: the Gateway serves the built
files, and the release pipeline (the `BuildCockpitApp` MSBuild target on `CcDirector.Gateway.csproj`,
gated to a publish/release configuration) runs `npm ci` at the workspace root and
`npm run build --workspace @devthrottle/cockpit`, then copies this app's `dist/**` into the Gateway's
`wwwroot/c/`. A routine `dotnet build` does NOT run npm.

Vite resolves `@devthrottle/client-core` from its TypeScript source (the package's `exports` map
points at `src/`), so there is no separate library build step to sequence.

## Gateway-only ingress

The browser talks **only** to the Gateway, through root-relative paths. An ESLint rule
(`eslint.config.js` at the workspace root) bans absolute `http`, `https`, `ws`, and `wss` string
literals in `packages/**` and `apps/**` so a Director address can never leak to the client. Run it
with `npm run lint` at the root.

## Local dev

```bash
npm run dev --workspace @devthrottle/cockpit   # Vite dev server; proxy API paths to a running Gateway
```

## Layout

- `src/AppShell.tsx` - the three-region desktop layout frame (left rail / main pane / right rail).
- `src/panes/` - placeholder panes, each replaced by a real ported page in its own issue.
- `src/main.tsx` - the router (rooted at the site root `/`); the pages it routes to are listed in
  `src/routes.tsx`. `/` opens the Fleet Manager page (`/fleet-manager`).
- Everything shared with the mobile shell lives in `@devthrottle/client-core`.
