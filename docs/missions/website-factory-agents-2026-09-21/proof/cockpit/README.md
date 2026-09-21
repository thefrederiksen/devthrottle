# Proof - the Cockpit Factory Agents area

Website Business Factory, product track. Developer task: the Gateway fold for the Factory Agents area, its
contract, the Cockpit pages (Screens 1-5) and the "factory agent" chip on a session (Screen 6).

**State: the record and the trigger are both wired.** The record (pull request 3272) and the trigger (pull request
3273) are merged, and this change reads both: the trigger's own status (OK, RED "check failed", RED "no checks
ran", RED for a lock held past six hours), Pause and Resume on the pages, and empty checks collapsed by outcome and
trigger actor. Everything below - screenshots and gate - was made at head `0d608f9d3`.

## What was built

- **The fold** (`src/CcDirector.Gateway/Factory/FactoryAgentsFold.cs`): one place on the Gateway computes every
  word, number, colour and grouping the five screens show (critical rule 7). The Cockpit renders them verbatim.
- **The contract** (`src/CcDirector.Gateway.Contracts/FactoryAgentsViewDtos.cs`) and its client-core types
  (`packages/client-core/src/factory/factoryAgentsClient.ts`).
- **The routes** (`src/CcDirector.Gateway/Api/FactoryAgentsViewEndpoints.cs`) under `/gateway/factory-agents`.
  `GET /switch` is mapped whatever the switch says, so the Cockpit can hide the area; every other route is
  mapped only while `factoryAgents.enabled` is on. They are the owner's pages: a session key is refused.
- **Wired to the real record and the real triggers.** "I have handled it" appends a correcting row and never edits
  the escalation. Pause and Resume go through the trigger's own store, the same rows `cc-devthrottle trigger` reads.
- **Screen 6, the chip.** A wire change: a new `SessionDto.FactoryAgent` field, stamped by the roster fold from the
  record's "started" rows for the sessions on screen, only while the switch is on. A Director cannot set it.
- **Reports.** The per-factory-agent summary over a chosen window, Export CSV, and "Make a report from this" as a
  saved filter. "Ask the record" and scheduled reports are not built and do not appear.
- **The index migration** `IndexFactoryActivityReads` (SQLite and PostgreSQL), from the review: `(TenantId,
  SessionId, Outcome, OccurredUtc)` for the chip's read on every roster poll, and `(TenantId, OccurredUtc)` for the
  window read with no factory named. The outcome sweeps already had `(TenantId, Outcome, OccurredUtc)` from the
  record's own migration.

## The screenshot QA, at head `0d608f9d3`

An isolated Gateway (its own root folder, port 7899, no Tailscale, no authentication, launched through its own
scheduled task, torn down after) built from the head, seeded through real calls. The scripts are in `rig/`.

How the rows were made, honestly:

- **Triggers**: the real `cc-devthrottle trigger add`.
- **Trigger checks**: the Director's real check-report route, so the Gateway itself decided every outcome and wrote
  every trigger row ("nothing to do", "failed", "skipped").
- **A factory agent's own rows**: the record's own route, the one `cc-devthrottle factory record` calls. The command
  itself refuses on a rig with no authentication ("Who acted is not known": there is no session key for the Gateway
  to stamp), always stamps the time as now, and takes no session id, so it cannot write a night of rows.
- **Sessions in the roster**: `rig/fakedirector.py` opens the real Director stream (SignalR) and pushes three
  sessions, so the roster and the trigger's lock read real session rows.
- **The six-hour lock**: the rig has no real Director to start a session, so the start - the two columns a real
  start writes, `LastSessionId` and `LastStartedUtc` seven hours ago - was written into the rig's own trigger row.
  The check that then found the lock, and the pause and resume that released it, are real.

| Screenshot | What it shows |
|---|---|
| `01-factories-flow-and-failures.png` | Screen 1. Two factories, both FAULT, each with the trigger's own sentence: the six-hour lock and "no checks ran" (Site Care), "check failed" (Website Business). Numbers: asked, escalated, runs, failed, blocked, empty checks. |
| `02-all-factory-agents.png` | All six factory agents with what wakes them, their last run and status (FAULT, IDLE, WORKING). |
| `03-activity-empty-checks-collapsed-and-a-blocked-row.png` | Screen 3. Empty checks collapsed into grey lines ("Checked "Review requests" 24 times - nothing to do"), single checks their own lines, a BLOCKED row, and the three trigger faults above the list. |
| `04-factory-agent-front-desk.png` | Screen 2 for a working factory agent: "Definition not stored yet - #2177 mission 1", woken by its trigger (OK), last 7 days, and a night of rows with sessions. |
| `05-factory-agent-all-runs-nothing-to-do.png` | A trigger whose every run is "nothing to do": 0 runs, 41 empty checks, one collapsed line, status OK. |
| `06-factory-agent-broken-check-red.png` | A broken check, RED: "check failed: exit code 1: cc-invoices: cannot reach the bank feed". |
| `07-factory-agent-no-checks-ran-red.png` | A trigger that never checked, RED "no checks ran", never a quiet night. |
| `08-waiting-for-you-before-handled.png` | Screen 4: one escalation (with "I have handled it") and one asked item (without it). |
| `09-waiting-for-you-after-handled.png` | After "I have handled it": the escalation is gone, the asked item stays. |
| `10-activity-the-correcting-row.png` | The escalation row unchanged and marked "Corrected ...", and the new DONE row that "Corrects the row of ...". |
| `11-pause-asks-first.png` | Pause asks first, in the Gateway's words. |
| `12-factory-agent-paused.png` | A paused factory agent: PAUSED, "(paused)" on its trigger, Resume offered. |
| `13-stuck-lock-six-hours-red.png` | The stuck lock, RED: "session 137 has not ended after 7 hours; no new session starts until it does - pause and resume the trigger to release it", after a real check that counted 2 and was SKIPPED. |
| `14-stuck-lock-paused.png` | The same factory agent paused; still RED, because pausing alone does not release the lock. |
| `15-stuck-lock-released-by-resume.png` | After Resume: status OK, and the record's ALLOWED row saying the lock on session 137 was released. |
| `16-reports-summary-and-saved-report.png` | Screen 5: the per-factory-agent table over the last 7 days with a total, the open faults, Export CSV, and the saved report "Night shift, last 7 days". |
| `17-sessions-factory-agent-chip.png` | Screen 6: the "Factory agent" chip on the two sessions a trigger started, and none on the ordinary session. |
| `01b-factories-after-handled-pause-and-release.png` | Screen 1 after the QA: the escalation gone, Seo Writer PAUSED, the lock released, the two remaining faults still RED. |
| `18-switch-off-no-rail-item.png` | The switch off: no "Factory Agents" item in the rail. |
| `19-switch-off-route-shows-nothing.png` | The switch off: `/factory-agents` is the ordinary "Page not found". |

**Found by this pass and fixed in this change:**

- A factory agent's page printed "Last check" twice with one trigger (fold now sends the page-level line only for
  two or more triggers; tested in the fold and the Cockpit).
- The report table's number headers sat one column to the left of their numbers (a CSS specificity fix).

**Found and NOT fixed here, because it is older than this change:**

- On an empty roster the Sessions page crashes the browser tab. `origin/main`'s own Cockpit (`44c38f962`), swapped
  into the same rig, crashes the same way, so it predates this change. `18-...` therefore shows the rail from the
  Fleet Manager page.
- The earlier screenshot pass loaded every page under `/c/`, where the Cockpit's router has no routes, so its pages
  were "Page not found". Those screenshots are replaced; the Cockpit is served at the site root.

## What the tests prove

| Tests | What they prove |
|---|---|
| `FactoryAgentsFoldTests` (Gateway unit) | Empty trigger checks collapse by outcome and trigger actor, never by sentence; the trigger's own red; a blocked row; an escalation cleared by a correcting row; a paused trigger; "I have handled it" refused when the corrections read was cut; the page's last check said once. |
| `FactoryAgentsViewSupportTests` (Gateway unit) | Paging stops and says so at the ceiling; a cut corrections read raises the Waiting warning and not the window's; saved reports round-trip for their own account only. |
| `FactoryRecordWiringTests`, `FactoryTriggerSourceTests` (Gateway unit) | Tenant-explicit reads and writes; the chip read; Pause and Resume through the trigger's store. |
| Migration pins (Gateway unit and integration) | `IndexFactoryActivityReads` is the newest migration on both providers, carries the current model, and applies on real PostgreSQL with no pending model changes. |
| `FactoryAgentsViewRouteTests`, `FactoryActivityRouteTests`, `FactoryTriggerHostTests` (Gateway integration, booted host) | The owner's pages over the real routes; a session key refused; the switch off. |
| Cockpit vitest | Each page renders the fold's words verbatim; the rail item only when the Gateway says on; the chip. |

## The gate, as run on 2026-09-21 at head `0d608f9d3`

| Run | Result |
|---|---|
| `.\scripts\test-local.ps1` (default) | 8 of 9 suites Completed. `CcDirector.Launcher.Tests` 195 of 197: the two known restart-signal tests (issue 3242). `gate-default.txt`. |
| `dotnet test src\CcDirector.Gateway.UnitTests` | 7,149 passed, **1 failed**, 8 skipped. The failure is `TriggerServiceTests.TheHistory_KeepsAtLeastTheNewest500` (the merged trigger's own test, untouched here): `ObjectDisposedException` on a SQLite handle during the parallel run. Rerun alone three times: 31 of 31 each time (`gate-gateway-unit-trigger-rerun.txt`). The skips: 6 statistics-database proofs, `TenantGateArchitectureTests.DT_TEN_3` (skipped in every run), and the PostgreSQL boot smoke test, run in the row below. `gate-gateway-unit.txt`. |
| `GatewayHostBootSmokeTests` with PostgreSQL | 12 of 12, including the PostgreSQL boot, which pins `IndexFactoryActivityReads` as the newest applied migration. Private throwaway instance (`scripts\pg-stats-proof-rig.ps1 -Instance wbfcock`), removed after. `gate-gateway-unit-boot-smoke-postgres.txt`. |
| Gateway.Tests filtered to Factory and Trigger | 13 of 13. `gate-gateway-integration-factory-trigger.txt`. |
| `.\scripts\test-local.ps1 -Gateway -Filter FullyQualifiedName~Postgres` | 46 passed, 4 not executed (they need `CC_GATEWAY_DB_CONNECTION`). `gate-postgres-proofs.txt`. |
| `GatewayDatabaseLivePostgresProofTests`, against the private instance | 4 of 4. `gate-postgres-live-proofs.txt`. |
| `npm test` in `apps/cockpit` | 536 passed, 62 files. `gate-cockpit-npm-test.txt`. |
| `npm run build` in `apps/cockpit` | Built. `gate-cockpit-npm-build.txt`. |
| client-core `npm test` | First run: 1,492 of 1,492 passed, but vitest reported one unhandled error - a reconnect timer in `src/terminal/stream.test.ts` firing after teardown - and exited non-zero. Rerun: 1,492 of 1,492, no errors. `gate-client-core-npm-test.txt`, `gate-client-core-npm-test-rerun.txt`. |
| `tsc --noEmit` (client-core and Cockpit), eslint on the 18 changed web files | Clean. `gate-typecheck-lint.txt`. |

## Not covered

- The full Gateway.Tests integration suite (about an hour) was not run at this head; the Factory, Trigger and
  PostgreSQL filters were, as the mandate asks.
- Core.Tests was not run (this change adds contract types only; the run aborted on an unrelated host crash last
  time).
- The mobile app: it gets the chip field through client-core but does not render it.
- A real Director starting a trigger's session; the rig wrote the start of the six-hour lock into its own row.
