# DevThrottle Mobile App - Implementation Document (Version 1)

Status: ready to implement (2026-06-28)
Architecture: see `mobile-app-architecture.html` in this folder (the diagrams and rationale).
Tracker repo: `thefrederiksen/devthrottle` (the GitHub repo; the old name `thefrederiksen/cc-director`
still redirects to it, so CenCon skills that name `cc-director` resolve correctly).

This document turns the architecture decision into a concrete, phased build plan. It is written so an
autonomous implementation-loop run can execute it one issue at a time, each issue small and fully
testable on a real phone before the next begins.

---

## 1. What we are building

One real mobile experience, built once: a **React + TypeScript Progressive Web App** served by the
**Gateway** (the always-on front door), driving the **existing** Gateway/Control REST API. The
desktop Blazor Cockpit is unchanged. When a phone opens the Cockpit URL, the Gateway redirects it to
the mobile app automatically; flipping the browser to "Desktop site" is the deliberate escape hatch
back to the full Cockpit.

Nothing in the backend changes. This is a new front-end plus a small front-door redirect.

### Version 1 pages (this document)
1. **Home / roster** - who needs my attention, then the full session list; tap to open a session.
2. **Session - Terminal mode** - the full terminal with Speak / Send / type; the on-the-road
   fallback that can do almost anything. The hero of V1; re-creates the MAUI Terminal tab.
3. **Session - Chat mode** - the same powers as Terminal, but the nicer cleaned history instead of
   the raw terminal, plus "listen to the assistant's latest reply" and Speak / type.
4. **Session management** - add a session (choose agent + repo), put on hold, remove, see status.

### Deferred to Version 2
- **Hands-free mode** (eyes-free / driving). Pieces exist; not defined well enough yet. Out of scope.

---

## 2. Foundations (shared by every page)

These are built once, in the first issue, and every page depends on them.

### 2.1 The app project
- New Vite + React + TypeScript project at the repo root: `mobile/`.
- Kept OUTSIDE the C# `src/` tree so it does not enter the .NET solution.
- PWA via `vite-plugin-pwa` (web app manifest + service worker + offline shell).

### 2.2 Build pipeline - build-time only, release pipeline only
- Node.js + npm are needed ONLY to compile the source to static files, and ONLY on the machine that
  builds the release.
- Machines that RUN the Gateway need nothing extra: the built static files ship in the release
  bundle, and the Gateway already serves static files.
- An MSBuild target on `CcDirector.Gateway.csproj` runs `npm ci && npm run build` and copies
  `mobile/dist/**` into the Gateway's served `wwwroot/m/`. The target is **gated to a
  publish/release configuration** (a `Condition`), so a routine local `dotnet build` does NOT run npm
  and ordinary C# developers do NOT need Node.

### 2.3 Served by the Gateway at `/m`, with token injection
- The Gateway serves the mobile build as static files at `/m` (mapped BEFORE the fallback proxy so it
  wins over the Cockpit proxy).
- The Gateway serves the mobile `index.html` with the per-machine Gateway token injected, exactly as
  the existing `/voice` page does today (`ReadGatewayToken()` pattern). The app sends that token as a
  `Bearer` header on API calls. Same Tailscale + TLS trust boundary the phone already relies on.

### 2.4 Typed API client - C# stays the source of truth
- The Gateway emits an OpenAPI document (built into .NET 10 via `Microsoft.AspNetCore.OpenApi` /
  `MapOpenApi`).
- A codegen step (`openapi-typescript`) generates TypeScript types + a typed fetch client into
  `mobile/src/api/`. A C# DTO change that is not reflected in the front-end fails the TypeScript
  build - the mismatch is caught at compile time.

### 2.5 The mobile redirect (front door)
- A small middleware at the Gateway front door, before the fallback proxy (the same place
  `CockpitProxy.UseBrowserPageRoutes` runs).
- On a browser navigation (`Accept: text/html`) whose path is NOT already under `/m`: if the
  `User-Agent` looks like a phone (Android / iPhone / "Mobile"), respond `302 Found` to `/m/`.
  Otherwise fall through unchanged to the desktop Cockpit.
- Escape hatch is free: Android "Desktop site" rewrites the User-Agent to a desktop signature, so
  that request no longer matches and falls through to the full Cockpit.
- It MUST be User-Agent based (decided server-side at navigation time, before any app loads); width
  detection still governs layout INSIDE the app.

---

## 3. Endpoints each page uses (all already exist)

Confirmed against `docs/plans/three-tabs-desktop-phone.md` (endpoint audit) and
`src/CcDirector.ControlApi/ControlEndpoints.cs`. The mobile app calls the **Gateway**, which resolves
the owning Director by session id and reverse-proxies per call.

| Page | Reads | Writes |
|------|-------|--------|
| Home / roster | Gateway `/sessions` aggregator (same shape the Cockpit Mobile page uses: `SessionDto`, "needs you" triage) | - |
| Terminal mode | `GET /sessions/{sid}/buffer?raw=true&since=` (live mirror) | `POST /sessions/{sid}/prompt` (type/Send, raw keys with AppendEnter=false), `/escape`, `/interrupt`, TTS for Speak |
| Chat mode | `GET /sessions/{sid}/turns`, `/summary`, `/wingman/explain` (clean history + latest reply) | `POST /sessions/{sid}/prompt` (type/Send), TTS for listen/Speak |
| Session management | `/sessions` (status), agent + repo lists for the add flow | session create, `POST /sessions/{sid}/hold`, session kill/remove |

TTS path: reuse the proven approach - audio fetched over plain HTTP (NOT through a SignalR circuit;
that constraint was the desktop Cockpit's, and does not apply to the static React app, but the same
`/api/tts`-style proxy of the Gateway's OpenAI "nova" voice is the model to follow).

Note for the Developer Agent: confirm the exact session-create and session-kill endpoints from the
Gateway/Control API and the MAUI client (`phone/CcDirectorClient`) when implementing Session
management (Issue 4); the MAUI app already performs add/remove/hold/status and is the behavioral
reference.

---

## 4. Phased issue breakdown

Each phase is its own GitHub issue, implemented and QA-verified before the next. Issue 1 stands up the
whole pipe and the first usable screen; later issues add one page each on top of that foundation.

### Issue 1 - Foundation + Home roster + mobile redirect (the first deployable version)
Stand up the entire pipe end to end and prove it on a phone:
- Scaffold `mobile/` (Vite + React + TypeScript + vite-plugin-pwa).
- Release-gated MSBuild build/copy into the Gateway `wwwroot/m/`; routine `dotnet build` stays
  Node-free.
- Gateway serves the build at `/m` with per-machine token injection (the `/voice` pattern).
- Gateway emits an OpenAPI document; generate the typed TS client into `mobile/src/api/`.
- Home / roster page: "who needs my attention" + the full session list, using the Gateway `/sessions`
  aggregator and the existing triage ordering. Tapping a session opens a minimal session-detail
  placeholder (full Terminal/Chat are later issues).
- Gateway mobile redirect middleware (mobile UA -> `/m`; Desktop-site falls through).
- PWA: installable, offline shell loads last-known roster.

OUT of Issue 1: Terminal mode, Chat mode, Session management (their own issues).

### Issue 2 - Session: Terminal mode (the hero)
- Re-create the MAUI Terminal tab on the React surface: live read-only terminal mirror
  (`buffer?raw&since=`), a Speak button (TTS of latest output), a Send button, and a text box that
  types into the session (`/prompt`), plus control keys (Enter / Esc / Stop / arrows) via
  `/prompt` (AppendEnter=false) + `/escape` + `/interrupt`.

### Issue 3 - Session: Chat mode
- The same controls as Terminal, but render the cleaned history (`/turns` + `/wingman/explain`)
  instead of the raw terminal, plus "listen to the assistant's latest reply" (TTS of the latest
  assistant turn). Type / Send / Speak identical to Terminal.

### Issue 4 - Session management
- Add a session (pick agent + repo), put a session on hold (`/hold`), remove a session (kill), and
  surface status. Mirror the MAUI add/remove/hold flow.

---

## 5. Definition of Ready / proof bar (per issue)

Every issue is written to the CenCon Definition of Ready (title with area prefix, problem/value,
scope in/out, MEASURABLE acceptance criteria, affected containers, proof target, assumptions flagged).
Affected containers for these issues are `CcDirector.Gateway` (serving, OpenAPI, redirect) and the new
`mobile/` project; later issues may touch `CcDirector.Gateway.Contracts` only via the generated
client.

Proof target for every issue: screenshots of the page working **on a phone-sized viewport** against a
running Director, each acceptance criterion shown Expected vs Actual, plus the HTML QA report.

---

## 6. Deploy and QA report (autonomous run)

After the implementation loop merges an issue to main on a clean QA pass, this machine
(`SOREN_NORTH`) deploys and reports:
- **Deploy:** rebuild and deploy the affected surface with the repo's own scripts -
  `scripts/redeploy-gateway.ps1` (Gateway serves the mobile app) and/or
  `scripts/deploy-cockpit.ps1`. Verify with `scripts/verify-gateway.ps1`. (A robocopy exit code 1 is
  a known false-alarm, not a failure.)
- **QA report email:** the QA Agent already produces an HTML QA report under
  `docs/cencon/proof/issue-<n>/`. The user has explicitly authorized emailing the final QA report
  directly to themselves (self-email only) at the end of the autonomous run, so the run is fully
  hands-off. This is a one-time, self-addressed exception to the standing "route all email through
  approval" rule, scoped to the QA report to the owner.

---

## 7. Cleanup (after V1 reaches parity - the LAST step, not the first)

Once the new app covers the V1 pages, remove the prototypes so there is one mobile app:
- `src/CcDirector.Cockpit/Components/Pages/Mobile.razor` (+ `.razor.css`) - the Blazor mobile page.
- `src/CcDirector.Cockpit/wwwroot/m/` (the vanilla Wingman Voice app) - replaced by the React build
  output at the Gateway's `/m`.
- `src/CcDirector.Cockpit/wwwroot/pages/voice/` - the static Voice page.
- The Cockpit nav "Mobile" item.

The vanilla app stays as the behavioral spec until its behavior is ported.

---

## 8. Caveats captured for the autonomous run

- **Repo name:** issues live in `thefrederiksen/devthrottle`; the CenCon skills say
  `thefrederiksen/cc-director`, which GitHub redirects to `devthrottle` (verified), so `gh` resolves
  correctly either way.
- **New toolchain:** this introduces Node/npm + a React/TS build to a C#-centric repo. The DEV phase
  installs Node on the build machine if absent and sets up the Vite project; the QA proof bar is
  satisfied with phone-viewport screenshots of the running page.
- **`cc-spawn` CLI is currently broken** on this machine (shim points to a missing exe). Launching the
  autonomous session uses the Control API `POST /sessions` on the loopback Director instead; the
  broken CLI is reported separately so it gets fixed.
