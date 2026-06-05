# One URL: the Cockpit becomes THE web UI, behind the Gateway front door

**Status:** IMPLEMENTED + DEPLOYED LIVE 2026-06-05 (same day). One URL verified over
the tailnet (/, /fleet, /exes, /transcripts, /healthz all 200 via
https://&lt;tailnet-host&gt;/); the :7470 mapping is gone; interstitial verified.
QA: docs/features/gateway-tray/QA_REPORT.html (one-URL section + screenshots).
Implementation notes: the Cards dashboard became the Blazor /fleet page;
exes/transcripts/dictionary moved as Cockpit-served static pages (full Blazor
conversions can follow page by page). D-ONEURL-1 resolved itself: nothing in the
desktop app references /legacy-manager anymore.
**Builds on:** docs/plans/gateway-tray-app.md (v2, implemented 2026-06-05) - the
Gateway is a per-user tray app that already supervises the Cockpit in `--managed`
mode (launch at logon, restart on crash, silent update on relaunch).
**Decided:** keep TWO executables (Gateway = stateful nervous system + future
Agent Brain; Cockpit = fast-churning UI that must be swappable without bouncing
the brain). This plan removes the visible seams between them.

---

## 1. End state

- **One URL.** `https://<tailnet-host>/` is everything. The Gateway front door
  reverse-proxies all UI traffic to the Cockpit; the Gateway's REST API keeps its
  existing routes (explicit endpoints win, unmatched requests fall through to the
  Cockpit). The `:7470` Tailscale mapping disappears; the Cockpit binds loopback
  only and is reachable solely through the Gateway.
- **The Cockpit is anything and everything (web).** The Gateway serves NO HTML
  pages anymore. Every embedded page - the Cards dashboard, exes, transcripts,
  dictionary - moves into the Cockpit as a Blazor page (or merges with an
  existing Cockpit page that already covers it). The dashboard stays a web page;
  it is simply served by the Blazor app now.

## 2. Phase 1 - Reverse proxy (one URL)

1. **YARP in the Gateway** (`Microsoft.ReverseProxy` package): one catch-all
   fallback route -> `http://127.0.0.1:<cockpitPort>`, ordered AFTER every
   explicit Gateway endpoint so the REST API keeps precedence. WebSocket
   passthrough on (Blazor Server's SignalR circuit rides it). Forwarded headers
   (X-Forwarded-Proto/Host/For) flow through so the Cockpit generates correct
   public URLs behind Tailscale TLS.
2. **`GET /` falls through to the Cockpit.** Delete the directory-page mapping
   (the JSON identity answer moves to `/healthz`-adjacent or a `/about` endpoint
   for tools that probed `/` with Accept: application/json - check callers first).
3. **Cockpit trusts the loopback proxy** (ForwardedHeaders config mirroring the
   Gateway's own).
4. **Proxy-down behavior:** when the Cockpit child is not up yet (boot, mid-swap),
   the proxy answers 503 with a tiny static "Cockpit starting..." page that
   auto-refreshes - never a raw connection error on the phone.
5. **Tailscale:** front door stays 443 -> 7878; the 7470 serve mapping is
   removed (TailscaleServeProvisioner drops the cockpit mapping). Phone bookmark
   becomes just the host.
6. Tray menu: "Open Dashboard"/"Open Cockpit" collapse into one "Open Cockpit"
   -> front-door base URL. `GET /cockpit` info endpoint returns the front-door
   URL.
7. Sweep for hardcoded `:7470` links in Cockpit/phone JS and Director code.

## 3. Phase 2 - UI consolidation into the Cockpit

Inventory of the Gateway's embedded Web/ pages and their fates:

| Page | Fate |
|---|---|
| `directory.html` (Cards dashboard) | PORT to a Cockpit Blazor page - either merged into the Cockpit home or a dedicated /fleet page, preserving parity: per-machine grouping, state dots, age, Open session, unreachable-Director banner |
| `manager.html` (legacy aggregator) | DELETE - see desktop note below |
| `exes.html` + page route | Port to a Cockpit page (the ExesEndpoints REST stays on the Gateway) |
| `transcripts.html` | Port to a Cockpit page |
| `dictionary.html` | Port to a Cockpit page |
| `api.html` | DELETE - repo docs are the source of truth |
| `login.html` | KEEP - the token-auth mechanism stays as is |

Desktop note: the Avalonia Director's embedded Gateway view loads
`/legacy-manager` today. With it deleted, that WebView points at the front door
(the Cockpit) instead - one-line change - or the tab is removed entirely.
**(D-ONEURL-1, Soren's call when we get there.)**

## 4. Phase 3 - Verify

1. Phone E2E over the tailnet: one URL, Cockpit loads, Blazor circuit (live
   updates) works through the proxy, terminal stream works.
2. Kill the Cockpit child -> supervisor relaunches -> "Cockpit starting..."
   interstitial during the gap, then recovers without touching the Gateway.
3. Cockpit deploy via `scripts/deploy-cockpit.ps1` -> Gateway uptime unaffected
   (uptime visible in the tray Settings window).
4. The ported dashboard page matches the old Cards page against the live fleet.
5. QA report with screenshots (boardroom HTML).

## 6. Out of scope

- Agent Brain / turn briefs (next plans; unaffected - this is why the two-exe
  split exists).
- Auth changes: none. Single-user tailnet; the token mechanism stays exactly
  as is.
- Gateway REST route renaming (e.g. moving under /api/*): NOT done - existing
  callers (phone, Directors, tools, Cockpit's DirectorClient) keep their paths;
  the fallback-proxy precedence makes renaming unnecessary.

## 7. Decisions log

- Two executables CONFIRMED (2026-06-05): not a framework limitation - one
  process could host tray + REST + Blazor - but a lifecycle decision: the
  Cockpit must be deployable/restartable without bouncing the Gateway (which
  will hold the warm Agent Brain).
- One URL via fallback reverse-proxy in the Gateway; Cockpit goes
  loopback-only.
- The Gateway serves no HTML UI; the Cockpit is the ONLY web UI. The Cards
  dashboard stays a web page, served by the Cockpit as a Blazor page
  (clarified by Soren 2026-06-05 - NOT a native Avalonia view; the tray
  window stays just Settings/status).
- OPEN: D-ONEURL-1 (desktop Director's embedded gateway view: re-point at the
  Cockpit vs remove the tab).
