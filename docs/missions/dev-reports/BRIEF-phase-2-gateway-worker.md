# Worker brief - phase 2, the Gateway report record and delivery

You are a Worker on the Dev Reports mission. Your Manager is session 2ba644bd. Report to it, once, when done
or genuinely blocked (`cc-devthrottle message send 2ba644bd "<one line>"`). Never contact the owner.

**Your worktree:** `D:\ReposFred\devthrottle-dev-reports-p2-gateway`, branch `dev-reports/p2-gateway` (cut from
`mission/dev-reports`). Work there only. Commit and push to that branch as you go (every coherent step - never
hold work only on disk). No attribution of any kind in commits. Never merge to main, never open a pull request.
Never touch another worktree. Never stop, kill or restart any Director or cc-director process.

## Read first (in your worktree)

1. `docs/missions/dev-reports/PLAN-phase-2.md` - THE SPEC. Routes, shapes, state machine, prompt fold, storage.
   Build exactly that. If something in it is wrong or impossible, message the Manager - do not quietly deviate.
2. `docs/missions/dev-reports/HANDOFF-phase-2.md` and `STATE.md` (rulings 1-9).
3. `packages/client-core/src/devreports/CONTRACT.md` section 3 - the item shapes you store and validate.
4. `src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs` - the publish check (already built, phase 1).
5. Repo `CLAUDE.md` (logging, no fallbacks, tests named Method_Scenario_Result).

## Where things are (already found for you)

- EF: `src/CcDirector.Gateway/Data/GatewayDbContext.cs`, entities in `Data/Entities` deriving from
  `TenantScopedEntity`, `ApplyTenantScope<T>`; a close small analog is `MissionNotes/MissionNoteStore.cs` +
  `Api/MissionNotesEndpoint.cs`. Snake_case columns, UTC DateTime, GUID keys ValueGeneratedNever, provider-agnostic
  model (see the Postgres collation block near line 1205).
- Migrations: TWO in this change - SQLite (`src/CcDirector.Gateway/Data/Migrations`) and Postgres
  (`src/CcDirector.Gateway.Migrations.Postgres/Migrations`). Generate LAST, after all other code:
  ```
  dotnet ef migrations add AddDevReports --project src/CcDirector.Gateway --startup-project src/CcDirector.Gateway --context GatewayDbContext -o Data/Migrations
  CC_GATEWAY_EF_PROVIDER=postgres dotnet ef migrations add AddDevReports --project src/CcDirector.Gateway.Migrations.Postgres --startup-project src/CcDirector.Gateway.Migrations.Postgres --context GatewayDbContext -o Migrations
  ```
  Then `dotnet ef migrations has-pending-model-changes` for both must say no changes. `PendingModelChangesWarning`
  throws, so the whole DB test suite is red until the migration exists. The mission/fleet-manager branch holds
  unlanded migrations; ignore it - generate on your branch's chain head.
- Auth: `Util/AuthMiddleware.cs` - `AuthMiddleware.CallingSession(ctx)` is the session identity (null when not a
  session key); `IdentityKind(ctx)`. Tenant for a request: `ResolveReadTenant` in `Api/GatewayEndpoints.cs`.
- **`Util/SessionKeyGuard.cs` is an explicit allow list.** Session routes must be added there (with
  `SessionKeyGuardTests` InlineData rows); owner routes must NOT be. A route not added answers 403 to every agent
  while every test stays green - so test with a real session key.
- Turn end: `GatewayHost.cs` around line 2940, the `TurnEndWatcher` `onTurnEnd` handler. Session Rules hang off
  it via `Rules/RuleTurnEndLauncher.cs` (tenant scope, fire and forget, never throws) - do the same with a
  `DevReportTurnEndLauncher` (or similar) and add the call there. After a restart the watcher's catch-up fires
  for a session first seen idle (`IsNewTurn=false`) - that is how held items survive a restart; prove it.
- Session liveness: `PushedSessions.TryLocate(tenant, sid, staleAfter)` gives the pushed row (`ActivityState`);
  `Registry.Get(tenant, directorId)`; ending is on `SessionHistoryEntity` (closed/finished/director-stopped/
  interrupted).
- Sending a prompt: `Api/SessionVerbClient.SendPromptAsync` and its three-way `PromptSendKind` - see
  `Rules/GatewayRuleEnvironment.TypeIntoSessionAsync` for how Accepted / NeverLeftTheGateway / unknown map. Set
  `PromptRequest.Provenance` so the delivery is the OWNER's turn (find what makes `LastOwnerTurnAtUtc` move in
  `CcDirector.Core/Sessions/Session.cs` and make a dev-report delivery count as the owner's turn; if that needs a
  new `SubmissionRoutes` value, add it and say so).
- Integration test hosts: `src/CcDirector.Gateway.Tests` (real `GatewayHost`; parked, host-bound) and
  `src/CcDirector.Gateway.UnitTests`. Look for an existing test that authenticates with a session key and one
  that fakes a Director accepting a prompt (grep `SendPromptAsync`, `FakeDirector`, `SessionKey`). Every Gateway
  in a test process shares ONE gateway.db - give each test its own session ids.

## What to build

Everything in PLAN-phase-2.md except the tool: entities + both migrations, a `DevReportStore`, the
`DevReportItemStates` fold, the `DevReportPromptFold`, the delivery service with the per-session lock, the session
and owner endpoints (in their own file, e.g. `Api/DevReportEndpoints.cs`, mapped from `GatewayHost` next to the
other feature endpoints), the guard entries, the turn-end wiring. Logging per repo CLAUDE.md.

## Proof required (all of it)

- Unit: prompt fold (byte-for-byte, every anchor type, answers with and without comment, several reports, owner
  words verbatim including leading spaces and newlines), state fold, idempotent resend (same id twice -> one
  item, one delivery), later answer replaces held earlier one, 10 MB limit (just under passes, just over 413),
  malformed item -> 400 and nothing stored.
- Integration on a real Gateway: publish -> owner sends while session Working -> `held`, no prompt reached the
  Director -> turn ends -> exactly ONE prompt containing the cell's row and column and the chosen option; another
  tenant's device key gets 404 on read, html and send; a session key calling an owner route is refused; a device
  key calling publish is refused; session A's key cannot publish or reply for session B; send to an ended session
  -> `refused` "This session has ended"; held items survive a Gateway restart (new host on the same database ->
  catch-up turn end -> delivered once).
- **Revert each guard and watch its test go red** (idempotency, owner check, session-key scoping, ended refusal,
  held-while-working, restart drain), then restore. Record each in your report as: guard, test, red message.
  Commit before each mutation (a checkout restores HEAD, not your fix), and never use `--no-build` on the restore run.
- `.\scripts\test-local.ps1` green, and `.\scripts\test-local.ps1 -Parked` (needs Docker; it starts its own
  PostgreSQL). If a parked failure is not yours, prove it against the parent commit.

## Done means

Everything committed and pushed on `dev-reports/p2-gateway`, and a file
`docs/missions/dev-reports/WORKER-phase-2-gateway.md` committed there: what you built (file list), every route
with its real answer, what is proven and how (including each revert proof), and what is NOT proven. Then ONE
line to the Manager.
