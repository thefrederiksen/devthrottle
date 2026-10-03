# Review: pull request thefrederiksen/devthrottle#3522 - teams, their members and the four roles (devthrottle_internal#2300)

Written by a separate review session, 3 October 2026. Head reviewed: `aec6a14b4`. Base: `origin/main` at
`5ec4eaa33` (fetched at the start of the review; it is also the merge base, so the diff is the whole change).

## Scope

**Read in full**

- `src/CcDirector.Gateway/Teams/TeamRegistry.cs`, `Teams/TeamRole.cs`, `Api/TeamEndpoints.cs`
- `Data/Entities/TeamEntity.cs`, `TeamMemberEntity.cs`, the `GatewayDbContext.cs` change, the `GatewayHost.cs` change
- `Tenancy/TenantRegistry.cs` (whole file, not only the changed lines)
- Both `AddTeams` migrations (SQLite and PostgreSQL), Up and Down
- Every new test file: `TeamRegistryTests`, `TeamEndpointsTests`, `TeamRolesTests` (by its run), `AddTeamsMigrationTests`,
  `HostedTeamEndpointsTests`, `AddTeamsPostgresTests`; and the changed lines of the existing migration-chain,
  boot-smoke, tenant-scope-guard and tenant-gate-architecture tests
- `docs/proof/teams-2300/README.md` and `api-transcript.txt`
- Issue #2300, issue #2098, and `seam-team-billing.md` sections 1 to 4
- Existing code the change now reaches, to see what a team's tenant id does there: `TenantScopedSweep`,
  `ITenantPass` (`TenantPass`), `HostedAccessLeaseService`, `EntitlementLeaseMonitor`, `PreFreeTierKeyReinstatement`,
  `GatewayHost.ResolveNarrationPlan`, `HostedTenantBoundary.ResolveRequestTenant`, `SessionKeyGuard.Evaluate` and its
  allow list, `DictionarySuggestionDailySweep`, `CronTenantSweep`, `DictionarySuggestionService.RunScanAsync`

**Ran**

- `dotnet test src/CcDirector.Gateway.UnitTests` filtered to Teams, TenantRegistry, TenantScopeGuard,
  TenantGateArchitecture and SessionKeyGuard: 458 passed, 0 failed, 1 skipped.

**Could not reach**

- `HostedTeamEndpointsTests` and `AddTeamsPostgresTests` (the `CcDirector.Gateway.Tests` suite). One attempt
  printed the test-run banner and then produced nothing for ten minutes (consistent with waiting on the
  machine-wide Gateway suite lock; the cause was not observed). It was stopped. **I have no result of my own for
  the hosted route tests or the PostgreSQL migration proof**; for those this review rests on reading the test code
  and on the builder's stated run in the proof README.
- The two generated `AddTeams.Designer.cs` files and the model snapshots were not read line by line; they are
  covered by the `HasPendingModelChanges` assertions in the chain tests, which I did not run in full.
- No revert (mutation) check was made, because the worktree may not be edited.
- The per-tenant cost of each background sweep was read, not measured.

## Verdict

**No blocker found. Mergeable once F1 is answered** (it is a decision about exposure, not a code defect in what is
written). The points looked at hardest, and what was found:

- *A person who never creates a team sees no change:* holds. `gateway.tenants` is not written or reshaped;
  `MintOrLookupBySubject`, `LookupBySubject`, `SubjectForTenant`, `EmailForTenant`, `ListAll` and
  `LookupByAccount` are untouched. The one changed existing read is `AllTenantIds`, which adds one read of an empty
  `teams` table per census refresh (at most once a minute, hosted only).
- *Tenant isolation:* a member list is served only after the caller's own membership row is found, the caller is
  taken from the device key's bound tenant and never from the request, and the not-a-member answer is identical to
  the no-such-team answer. Nothing in this change lets any request ENTER a team's tenant, so there is no new way
  to read a team's rows at all yet.
- *Exactly one Owner:* no code path writes a second Owner or removes or changes the Owner, and the filtered unique
  index backs "at most one" on both databases. No race found that yields zero or two Owners.
- *Migration:* additive only (two tables, two indexes), Down drops exactly those, the filter SQL is valid on both
  providers.
- *Subject or email in a log:* none found. The two `ex.Message` log lines in `TeamEndpoints` can carry only
  argument-validation text or the generic save-failure text, neither of which includes a subject or an email.
- *A session key on the team routes:* refused by the guard's default deny (the allow list has no `teams` entry) -
  confirmed by reading; see F4.

## Findings

### F1 - should-fix - team creation is live, unbounded and unbilled, and every team joins every background sweep for good

**Location:** `src/CcDirector.Gateway/GatewayHost.cs:4608` (the routes are mapped unconditionally),
`src/CcDirector.Gateway/Api/TeamEndpoints.cs:61` (`POST /teams`),
`src/CcDirector.Gateway/Tenancy/TenantRegistry.cs:164-170` (the census now includes every team),
`src/CcDirector.Gateway/Tenancy/TenantScopedSweep.cs:73`.

**The harm:** the moment this is deployed, any hosted account holding a device key - including a free account -
can call `POST /teams` as often as it likes. There is no release switch, no limit per account, no rate limit, and
no way to delete a team (not in this change, and none exists). Each call adds a permanent tenant id to
`AllTenantIds`, and every `TenantScopedSweep` (cron every minute, the retention sweeps, the dev report settle
sweep, the push sweep, the daily dictionary scan, and the rest - eleven of them) then enters that tenant's scope
and runs its queries on every cycle, forever, for a tenant that cannot hold a single row until #2311. A person
who creates teams in a loop, or a client defect that retries the create, grows the fan-out of every sweep for
every account on the shared database, and nothing can undo it short of deleting rows by hand. The billing seam
(section 6) says the Teams surfaces merge dark until the owner releases Teams, and section 3 expects an abandoned
checkout to leave a team behind; this route is neither dark nor bounded. I did not measure the per-tenant sweep
cost; for an empty tenant each sweep is a few reads, so the harm is proportional to how many teams get created,
and nothing here caps that number.

**What would settle it** (the builder's and Tech Lead's choice): hold the routes dark until Teams is released, as
the website side does; or cap teams created per account; or keep a team out of the census until it can own rows.
If it is accepted as is, that should be a stated decision rather than a default.

Developer answer: ACCEPTED. Teams now merges dark. New `Teams/TeamsReleaseSwitch.cs`: the team routes are mapped only when the environment variable `CC_GATEWAY_TEAMS` is exactly `1` (a hosted app setting, like `CC_GATEWAY_PUBLIC_URL`); unset or anything else is off, read once at construction (`GatewayHost.TeamsReleased`, with a `teamsReleased` constructor override for tests). Off, no team route exists, so a deploy of main exposes nothing and no team can be created, which is what keeps unbilled teams out of the sweep census. Tests: `TeamsReleaseSwitchTests` (only `1` releases), `HostedTeamsDarkTests` (a real hosted Gateway with the variable unset answers not-found on all three routes and creates no team), `HostedTeamEndpointsTests` now runs with the switch on (the routes answer). Capping teams per account and deleting a team are left to the release decision and to the role table (rename and delete are Owner-only, a later piece); the pull request body says how the owner turns Teams on.

### F2 - note - issue test 2's "data" proof passes without anything this change wrote

**Location:** `src/CcDirector.Gateway.UnitTests/Teams/TeamRegistryTests.cs:85-95`.

**The harm:** the second half of `Issue2300Test2_...` opens two `CronJobStore`s under `FixedTenantContext` for
two ids and shows one cannot read the other's row. That is the existing tenant filter, and it would pass
unchanged for any two strings; the team registry is not in its path, so no defect in this change could make it
fail. It is honest evidence that the filter still works, and the README says so ("the tenant filter proof covers
them"), but it is not evidence that a member of team A cannot reach team B's data, because the real question -
who is allowed to ENTER team B's tenant scope - has no code yet (#2311, #2302). The claim "can never read team
B's ... sessions or data" is therefore proven only for the member list in this change, and the test that will
actually carry it is owed by the issue that first binds a request to a team tenant. No change needed here beyond
not counting this as that proof.

Developer answer: AGREED, no change. The data half of test 2 proves only that the existing tenant filter separates two team tenant ids; nothing in #2300 lets a request enter a team's tenant, so the real proof (a member of team A cannot ENTER team B's scope) is owed by #2311/#2302, the first pieces that bind a request to a team tenant. The proof README and the pull request body already say sessions cannot exist in a team tenant yet; I will not count this as that proof.

### F3 - note - a team's tenant has no entitlement path, so the first Director bound to a team will be denied

**Location:** `src/CcDirector.Gateway/Tenancy/HostedAccessLeaseService.cs:131-133` (existing code, unchanged),
reached by the design in `TeamEntity.cs` (a team id is not in `gateway.tenants`).

**The harm:** none today. For the record of the seam: because a team's id is deliberately absent from
`gateway.tenants`, `SubjectForTenant(team)` is null, and the hosted access gate answers "not entitled" for it
before it reads anything (without revoking keys - it returns before the revoke). `ResolveNarrationPlan` and
`PreFreeTierKeyReinstatement` take the same no-subject branch. That is correct while no request can be in a team
tenant, and it means the Gateway half of #2299 and #2311 MUST add the team branch to that gate before a Director
is bound to a team, or every request from it is refused with the personal-plan answer. Worth one line in the seam
document so Track A does not discover it at runtime.

Developer answer: ACCEPTED. Added one line to `seam-team-billing.md`, section 2, "Track B - the membership table (#2300)": the hosted access gate (`HostedAccessLeaseService`, and the same no-subject branch in `ResolveNarrationPlan` and `PreFreeTierKeyReinstatement`) has no team branch, so #2299's Gateway half / #2311 must add it before a Director binds to a team. No code change here.

### F4 - note - the session-key refusal claimed in the route header has no test

**Location:** `src/CcDirector.Gateway/Api/TeamEndpoints.cs:28-29` (the claim);
`src/CcDirector.Gateway/Util/SessionKeyGuard.cs:295` onward (the allow list that makes it true today).

**The harm:** the header says an agent's session key cannot create teams or read a member list. That is true by
reading: the guard's allow list has no `teams` entry, so the default deny applies. But nothing fails if a later
widening of that list opens it, and `HostedTenantBoundary.ResolveRequestTenant` (lines 93-98) WOULD resolve a
session key to its owner's personal tenant, so an opened route would let any agent session create teams as the
owner. `HostedTeamEndpointsTests` covers an unbound device key but no session key. One assertion against
`SessionKeyGuard.Evaluate` for the three routes would pin the claim.

Developer answer: ACCEPTED. `TeamEndpointsTests.SessionKeyGuard_EveryTeamRoute_IsRefusedToAnAgentSessionKey` asserts `SessionKeyGuard.Check` refuses `GET /teams`, `POST /teams` and `GET /teams/{id}/members` to a session key, raised or not, so widening the allow list to a team route now fails a test.
