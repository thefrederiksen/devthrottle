# Phase C review - Talk

**Verdict: CHANGES REQUESTED**

Reviewed commit `8ea887d58686c43929d83f73e931a4029cae8f7e` (pull request 3590) with `git diff origin/main...HEAD`, against the merge base `c4f8a481c45a8a017382e365aa77d386f8e90514`.

## Findings

### 1. [P2] Prove an owner device is calling before creating an owner-authorized talk

Location: `src/CcDirector.Gateway/Api/FactoryTalkEndpoints.cs:46`

The handler checks only that the request resolves to a tenant. `SessionKeyGuard` correctly refuses ordinary and raised session keys, but that guard says nothing about the other authenticated credential classes. In particular, a workstation/Director device key resolves to its hosted tenant, and the shared machine token resolves to `Local` on self-host. Neither is the owner's signed-in browser or phone, yet both can pass this handler's tenant check and reach `startOnDirector`.

That distinction matters here, rather than being merely an origin-label issue. The generated first prompt says that the owner is present and explicitly permits changing anything about the factory, including `GOAL.md`, on the strength of the owner's approval. A Director or machine-token request can therefore start a session that receives a false assertion of live owner approval. `SpawnOrigin.TryEstablish` does not repair this: it recognizes only browser/phone device types as a person; other non-session credentials pass through with an unknown origin while the create still has no parent or controller and is dispatched.

Require the authenticated caller to be the owner's signed-in browser or phone before resolving the factory or sending the create. The existing `FleetManagerOwnerDevice.Require` helper expresses exactly that boundary. Add route tests proving a workstation/Director credential and a shared machine credential start nothing, alongside the existing session-key denial.

### 2. [P2] Return the required refusal sentence when the factory switch is off

Location: `src/CcDirector.Gateway/GatewayHost.cs:4864`

The Talk endpoint is mapped into `FactoryAgentsGate`, whose filter returns `Results.NotFound()` with no body when `factoryAgents.enabled` is off. The new test at `FactoryTalkEndpointTests.cs:251` asserts only the status and therefore blesses the missing body. This conflicts with the phase C brief's explicit contract that the switch-off refusal be a 400/404/409 **with a sentence**.

The harm is observable at the caller: if the switch changes after the Cockpit rendered the button, or a client calls the endpoint directly, it receives an empty 404 indistinguishable from a missing/old route. It cannot tell the owner that factory agents are disabled or offer the corrective action, unlike the unknown-factory, unknown-seat, and offline-Director refusals. Preserve the per-account gate while giving this mutating route a sentence-bearing 404, and assert the response body in the endpoint test.

## Scope and evidence

I reviewed all 15 files changed by `origin/main...HEAD`, with emphasis on:

- top-level ownership, parent/controller stamping, target computer, folder, factory, name, and Director selection;
- tenant derivation for registry, schedule, and Director reads, plus the second tenant-bound Director resolution before dispatch;
- `factoryAgents.enabled` and `SessionKeyGuard`, including the raised-session path;
- unknown factory/seat, offline Director, and switch-off refusals;
- the scheduled-run seed, unattended-run exclusions, opening instructions, `talked` activity line, memory write, and dated/session-identified goal change;
- acceptance and folding of the `talked` outcome; and
- the shared `/directors/{id}/sessions` extraction for behavior drift.

The ordinary and raised session-key checks both fall through the guard's default refusal, which is the correct policy: a session key must not start an owner's Talk session. The registry, Director list, schedule lookup, and final Director lookup are all derived from the authenticated tenant; I found no cross-tenant lookup or wrong-computer fallback in the reviewed path.

Verification executed:

- Gateway targeted tests: **72 passed, 0 failed, 0 skipped** (`FactoryTalk`, `FactoryActivityRecordTests`, and `FactoryAgentsFoldTests`).
- CLI activity tests: **13 passed** under the project's declared Click 8.2.1 / Typer 0.16.1 dependency floors. (The machine-wide Python environment has Click 8.1.8 and cannot separately capture `stderr`; its 8 harness failures were an invalid instrument, so I reran with the declared floors.)
- `git diff --check origin/main...HEAD`: completed with no whitespace errors.

Not proved here: a live hosted two-account HTTP run, a real Director/session launch, the phase D Cockpit caller, the phase B page fold after rebase, or the full repository test suite. Per the review request, the phase B duplicate definition of `talked` is not a finding.

## Round 2

**Verdict: APPROVED - no findings.**

Reviewed commit `dd6caed8d76c742dcccb24809295db28f871998a` (pull request 3590, two commits) with `git diff origin/main...HEAD` against the merge base `517d3ec0e`; origin/main at review time was `8669823a7`, one Teams commit past the base that touches nothing in this path.

### The two round 1 findings are fixed

1. **Only the owner's own browser or phone may start a talk.** `FactoryTalkEndpoints.cs:58` calls `FleetManagerOwnerDevice.Require` before anything else: a session key, a Director's device key (it enrols as "workstation", `DeviceRegistry.DefaultDeviceType`) and the shared machine token (no device identity on the request) are all refused with 403 `owner_only` and a sentence, before the factory is read and before any create is sent. Route tests cover the Director device key and the machine token (both 403, nothing dispatched), the owner's phone (201) and the owner's browser (201). The stand-in middleware in the tests stamps the request the way the real `AuthMiddleware` does (`DeviceTypeItemKey` and `AuthenticatedDeviceItemKey` from the verified identity, `AuthMiddleware.cs:654-656`); I checked that mapping by reading, not by running the real middleware.
2. **The switch-off refusal carries a sentence.** The route is now mapped outside the gate's group (`GatewayHost.cs:4870`) and asks `FactoryAgentsGate.IsOnFor` itself, answering 404 with "Factory agents are switched off for this account, so no talk was started..." The gate's group adds nothing but that one filter, so nothing is lost by mapping outside it. The owner check runs first, and a test proves a non-owner with the switch off learns nothing about the switch. The endpoint test asserts the body.

### The whole diff against the brief, mission item 5 and plan decision 8

- **Top-level, owned by the person.** The create goes through `GatewayEndpoints.StartSessionOnDirectorAsync`, the extracted body of `POST /directors/{id}/sessions`. I compared the extracted body with the removed one line by line: same checks, same order, same refusals. For a browser or phone key `SpawnOrigin.TryEstablish` stamps origin human and nulls both parent and controller, and `SpawnFactory.TryEstablish` keeps the stated factory after folding it to the one spelling. The test reads the create that leaves the Gateway: no controller, no parent, origin human.
- **On the seat's computer only.** The Director pick (`StoppedAtUtc is null` and machine name equal, first match) is the same predicate the scheduled-run resolver uses (`IDirectorTargetResolver.PickReachable`). Where the resolver would ask the launcher to start a Director, Talk answers 409 with a sentence and starts nothing elsewhere; tests cover a computer with no Director and a Director that said goodbye. The registry fills a seat's computer from the factory's when the manifest omits it, so the match is never against an empty name.
- **Factory folder and factory set.** `RepoPath` is the registry folder, `Factory` the registry id. On the Director, `StampFactory` runs and the factory memory is put in place whenever the session has a factory, whatever its origin. The explicit name passes the Director's weak-name rule (only a blank or the bare folder name is weak).
- **Tenant isolation.** Registry, Director list and the new `CronJobStore.Get(tenant, id)` all take the tenant the route resolved from the credential; `TryResolveOwnedDirector` re-checks the chosen Director in that tenant before the create. The new tenancy test proves the same schedule id in two accounts answers with the named account's seed.
- **Route security.** The route is authenticated (not in the middleware's public list); `SessionKeyGuard` is an allow list that does not name it, and a test asserts the refusal by path.
- **The seed's instructions.** Every command it names exists with the flags it uses: `factory memory list`, `factory memory get <name>`, `factory memory set <name> "<text>"`, `factory activity --factory <id> -n 30`, `factory record --factory --agent --outcome talked --what`, `factory register --manifest`, `factory list --json`, `session whoami`. The `talked` outcome is in the command line tool's outcome list and in `FactoryActivityOutcome.All` on main. `PUT /gateway/factory/registry` is allowed to a session key, so the re-register after a goal change can succeed from inside the talk. The reference section it points the agent to ("Factory registry and goal number", with `goalFile` and `goalApprovedOn`) is in `docs/cli-reference.md`. The unattended-run steps it switches off (dated rename, morning email, `session done`) match the brief, and the schedule seed is quoted verbatim between markers. The seed is plain ASCII, with a test for it.
- **"Last talk with you" end to end.** The end-to-end test appends the exact `talked` row the seed asks for through the real activity record and reads the page through the page route's own input reader and fold.

### Observation, not a finding

A Director that crashed without saying goodbye keeps `StoppedAtUtc` null until the eviction sweep, so Talk would send it the create and the owner would get the spawn door's 502 problem-details answer ("director not connected to the tunnel") rather than the 409 sentence. This is exactly what a person's New Session and a scheduled run do today, so it is not introduced here; phase D's Cockpit should render the problem body's `detail` as well as `error`.

### Scope: what I read, what I ran, what I could not reach

Read: all 11 files of the diff, and on origin/main `FleetManagerOwnerDevice`, `FactoryAgentsGate`, `SessionKeyGuard`, `AuthMiddleware` (stamping and public list), `SpawnOrigin`, `SpawnFactory`, `ResolveReadTenant`, `TryResolveOwnedDirector`, `DirectorRegistry.ListDirectors`, `DirectorHub` stop and disconnect, `IDirectorTargetResolver.PickReachable`, `MachineSessionSpawner`, `FactoryRegistryStore` (seat computer default, `Find`), `FactoryRegistryEndpoints`, the Director's `SessionCommandExecutor` create path and `SessionManager` factory memory placement, `SessionName.IsWeakExplicitName`, the command line tool's `cli.py`, `factory_ops.py`, `factory_memory_ops.py`, `factory_registry_ops.py`, and `docs/cli-reference.md`.

Ran: `git diff --check origin/main...HEAD` (clean); `dotnet test` on `CcDirector.Gateway.UnitTests` filtered to the FactoryTalk category plus `CronEngineTenancyTests`, `FactoriesScreenFoldTests`, `FactoryActivityRecordTests` and `FactoryAgentsFoldTests`: **125 passed, 0 failed, 0 skipped** (16 seconds).

Not reached: a live run over HTTP through the real `AuthMiddleware` (the route tests stamp the credential with a stand-in); a real Director creating the session and delivering the first prompt; the phase D Cockpit caller; the self-host Cockpit's credential kind (if a self-host Cockpit ever authenticates with the machine token rather than a browser device key, the owner would be refused there - the same exposure the existing Fleet Manager owner-only routes already carry); the full local gate (`.\scripts\test-local.ps1`) and the parked `Gateway.Tests` suite.
