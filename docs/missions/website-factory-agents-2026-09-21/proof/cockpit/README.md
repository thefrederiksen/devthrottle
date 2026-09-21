# Proof - the Cockpit Factory Agents area

Website Business Factory, product track. Developer task: the Gateway fold for the Factory Agents area, its
contract, the Cockpit pages (Screens 1-5) and the "factory agent" chip on a session (Screen 6).

**State: the record is wired; the trigger is not.** The trigger pull request has not merged. Until it does, the
account has no triggers, so no page offers Pause, no "woken by" line appears, and the trigger status
(OK / RED "check failed" / RED "no checks ran") is not on Screens 1 and 2 yet. The screenshot QA of the flow and
the failure cases is done after that merge, against an isolated local Gateway, and lands in this folder.

## What was built

- **The fold** (`src/CcDirector.Gateway/Factory/FactoryAgentsFold.cs`): one place on the Gateway computes every
  word, number, colour and grouping the five screens show (critical rule 7). The Cockpit renders them verbatim.
- **The contract** (`src/CcDirector.Gateway.Contracts/FactoryAgentsViewDtos.cs`) and its client-core types
  (`packages/client-core/src/factory/factoryAgentsClient.ts`).
- **The routes** (`src/CcDirector.Gateway/Api/FactoryAgentsViewEndpoints.cs`) under `/gateway/factory-agents`.
  `GET /switch` is mapped whatever the switch says, so the Cockpit can hide the area; every other route is
  mapped only while `factoryAgents.enabled` is on. They are the owner's pages: a session key is refused.
- **Wired to the real record.** The pages read and write the activity record in the account the ROUTE resolved,
  through tenant-explicit `Append` and `Query` overloads on `FactoryActivityRecord` (still only those two
  method names, so the append-only surface test holds). "I have handled it" appends a correcting row; it never
  edits the escalation.
- **Screen 6, the chip.** A wire change: a new `SessionDto.FactoryAgent` field. The roster fold stamps it from
  the record's "started" rows for exactly the sessions it is showing (`FactorySessionStarts`), and only while the
  switch is on. A Director's copy of the field is stripped when its push arrives, so only the Gateway sets it.
- **Reports.** The per-factory-agent summary over a chosen window, Export CSV, and "Make a report from this" as a
  saved filter kept in the account's settings. "Ask the record" and scheduled reports are not built and do not
  appear.
- **The Cockpit pages** (`apps/cockpit/src/factory/`): Factories, All factory agents, Activity, Reports, a
  factory agent's page, and Waiting for you, plus the rail item after Fleet Map, shown only while the Gateway says
  the switch is on.

## What the tests prove

| Tests | What they prove |
|---|---|
| `FactoryAgentsFoldTests` (Gateway unit) | Empty trigger checks collapse into one line; a factory with triggers and no rows is red "no checks ran"; a blocked row; an escalation cleared by a correcting row; a paused trigger shown as paused; the chip text. |
| `FactoryAgentsViewSupportTests` (Gateway unit) | Paging through the record stops and says so at the ceiling; a record that claims more rows and returns none fails loud; saved reports round-trip for their own account only. |
| `FactoryRecordWiringTests` (Gateway unit, 7) | A row appended to the account the route resolved is read in that account only, whatever the ambient account; the session filter returns only the named sessions' rows; the first "started" row per session wins and other outcomes are ignored; the roster fold stamps the chip on the started session and null on the rest, and asks the record only for the sessions on screen; with the switch off an echoed chip is cleared; the push store strips a chip a Director sent. |
| `FactoryAgentsViewRouteTests` (Gateway integration, 4, booted host) | Rows a session writes through the record's route appear on the owner's Factories and Activity pages; "I have handled it" writes a correcting row and the escalation leaves Waiting for you; a session key is refused the owner's pages; with the switch off the switch route says off and the pages are 404. |
| Cockpit vitest (`apps/cockpit/src/factory/*.test.tsx`, `rosterFactoryAgentChip.test.tsx`, `AppShell.test.tsx`) | Each page renders the fold's words verbatim; the rail item exists only when the Gateway says the switch is on; the chip renders on a roster row. |

## The gate, as run on 2026-09-21 at commit 3ad70a289

| Run | Result |
|---|---|
| `.\scripts\test-local.ps1` (default) | 9 of 10 suites pass. `CcDirector.Launcher.Tests` fails 2 of 197 - the two known restart-signal tests (issue 3242). `gate-default.txt`. |
| Gateway unit suite (parked), `dotnet test` | 7,035 passed, 0 failed, 8 skipped. `gate-parked-gateway-unit.txt`. Seven of the eight skips are PostgreSQL proofs, run in the row below. |
| `.\scripts\test-local.ps1 -Gateway` (parked integration suite, with its own throwaway PostgreSQL) | 2,738 passed, **6 failed**, 8 not executed, 1 hour 4 minutes. `gate-parked-gateway-integration.txt`. All six failures are in `AWorktreeIsNotARepositoryTunnelProofTests`, and all throw inside the test's own `RealPath` helper (`Could not find a part of the path 'C:\'`) before any Gateway code runs. That file is untouched by this change. The eight not executed are four `GatewayDatabaseLivePostgresProofTests` and four `DoorbellEndToEndProof` tests. |
| `.\scripts\test-local.ps1 -Parked -Filter "FullyQualifiedName~Postgres\|FullyQualifiedName~HostedSchemaRefusesAnUnownedRow"` | Gateway unit 26 of 27 executed and passed (the skip is `TenantGateArchitectureTests.DT_TEN_3`, skipped in every run). Gateway integration 46 of 50 executed and passed; the four `GatewayDatabaseLivePostgresProofTests` still did not execute with the run's database up. `gate-parked-postgres-proofs.txt`. |
| Core.Tests (parked), `dotnet test` | **No verdict.** The run aborts: `RepositoryRegistryConcurrencyTests.A_reader_of_the_file_never_sees_a_half_written_list` crashes the test host (a thread opens a temporary file after its folder is deleted). The passed count differs between runs (766, then 383), so neither count is evidence. This change touches no Core code; Core.Tests reaches it only through the additive contract types. |
| `npm test` in `apps/cockpit` | 535 passed, 62 files. |
| `npm run build` in `apps/cockpit` | Built. |
| client-core vitest | 1,492 passed, 127 files. |
| `tsc --noEmit` (Cockpit and client-core), eslint on the changed web files | Clean. |

## Not covered yet

- The trigger: its status on Screens 1 and 2, Pause and Resume on a real trigger, and "woken by". Waiting on the
  trigger pull request.
- The screenshot QA of the flow and of the failure cases, against an isolated local Gateway. After the trigger
  merges.
- The four live-PostgreSQL proofs and four doorbell proofs that did not execute, and Core.Tests, which aborted.
