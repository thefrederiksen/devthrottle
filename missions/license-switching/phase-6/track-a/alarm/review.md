# Review of A1 - the license alarm (License Switching, Phase 6, Track A)

Reviewer: session 35d2b3c9, "License Switching - Reviewer - A1 license alarm", a separate review session on a
different agent from the Developer. Branch `license-alarm-a1` at commit 33d00d817 (origin/main at 08dec1140,
v2.23.0). Pull request #4110.

## Scope

What I read, in full, from `git diff origin/main...origin/license-alarm-a1`:

- Gateway: `Licenses/LicenseAlarmService.cs`, `Licenses/LicenseAlarmFold.cs`, `Push/LicenseAlarmPushNotifier.cs`,
  `Api/LicenseEndpoints.cs` (the new route), `Util/SessionKeyGuard.cs`, `GatewayHost.cs` (construction, route
  mapping, the minute tick, disposal), `Data/Entities/LicenseEntities.cs`, `Data/GatewayDbContext.cs`,
  `Licenses/LicenseStore.cs`, `Licenses/LicenseDisplayFold.cs`, the contracts in `LicenseCockpitDtos.cs`, and both
  migrations (`AddLicenseAlarms` for SQLite and for Postgres, the `.cs` files; the Designer and snapshot files I did
  not read line by line).
- Gateway tests: `LicenseAlarmServiceTests`, `LicenseAlarmFoldTests`, `LicenseAlarmsEndpointTests`, the added rows
  of `LicenseGuardTests` and `PostgresProviderProofTests`.
- Clients: `packages/client-core/src/licenses/LicenseAlarmBanner.tsx` and `licensesClient.ts`, the Cockpit
  `AppShell.tsx` and its test, `styles.css`; the phone `LicenseAlarmBar.tsx` and its test, `routes.tsx`,
  `styles.css`, `public/push-sw.js` and `pushSw.test.ts`.
- Mission record: `contract.md` section 9, `cockpit-api.md` section 8, the track README, `layout_check.py`, and the
  five screenshots (viewed as images).
- The commit message and the pull request body.

What I checked against origin/main to confirm the new code's assumptions: `LicenseOwnerEmail` and its first field
(a label, never an address - the alarm email's joined account list is the documented use), the
`LicenseOwnerEmailSender` delegate, `GatewayMintedKeyEntity` (the id is minted at construction, so the push can carry it
before `SaveChanges`), `GatewayDatabase.CreateContext(tenant)`, `LicenseStore.NormalizeEmail` and `MaxMessageLength`,
the reason codes and log states, `LicenseLogStates.IsOpen` (Recorded is final), `LicenseRenewalEmailService
.TickEveryTenantAsync` (the tick is a line-for-line copy of it), `ITenantPass`, the Cockpit's Vite base (`/`, so
`/licenses` is a real address from the phone and from a push), the phone's base (`/mobile/`), the client-core helpers
`useVisiblePolling`, `describeAndReport`, `backgroundRecovered`, and how the Licenses log already renders a
non-switch row's account in the "to" column (the renewal-failed row does the same, so an alarm row is consistent).

What I ran, in the Developer's worktree at the branch tip (no file changed):

- `dotnet test src/CcDirector.Gateway.UnitTests` filtered to `LicenseAlarm*` and `LicenseGuardTests`: 72 passed,
  0 failed, 31 seconds.
- The same project filtered to `TenantScopeGuardTests`, `TenantGateArchitectureTests`, `LicenseDisplayFoldTests`
  against the built binary: 143 passed, 0 failed. The tenancy guards and the route census are reflective (no
  explicit list names the license routes or tables), so the new entity and route are covered by them.

What I could not reach:

- The full `-Parked` run. Another session holds the machine-wide release-gate lock; the pull request says the same
  and still owes that result. My 215 tests are a filtered slice of `Gateway.UnitTests`, not the suite.
- Postgres. `scripts/test-database.ps1` was not run (Docker, and not mine to start here). Every alarm test runs on
  SQLite. See finding 1.
- The web suites. I read the Cockpit, phone and client-core tests but did not run them; the pull request reports
  them green.
- A live Gateway, a real phone, a real push subscription, a real owner email.

Real names and attribution: swept the whole diff and the commit message for machine names, account addresses,
addresses on the home network, vendor names and attribution trailers. Only made-up names (machine-a, machine-b,
alice@example.com, bob@example.com) and the existing `support@devthrottle.com` sender appear. The screenshots show
made-up names only. The pull request body and the commit carry no attribution.

Rule 7 (the client is dumb): the banner renders `summary`, every `text` and `linkText` as sent, shows nothing on
`count` 0, and decides nothing else. The shells supply only where the link goes. The service worker shows the
Gateway's title, body and tag. Clean.

Cross-tenant: every read and write of `license_alarms` goes through `CreateContext(tenant)`; the push notifier enters
the tenant's scope before reading or pruning subscriptions; the route resolves the caller's tenant with
`ResolveReadTenant`. The two-tenant test and the endpoint test hold it down. Clean.

Fallbacks: a push that fails is logged and does not fail the alarm (the brief asks for exactly this; the banner and
the email are the lasting voice). A sender that throws becomes a failed send with the growing wait, never a silent
"sent". A mark that fails after the mail service confirmed a send is remembered in-process so no second email goes
out. No fallback hides a failure.

## Findings

### 1. The Postgres migration has no test of its own, so the service's writes are proven on SQLite only - should-fix

`src/CcDirector.Gateway.Migrations.Postgres/Migrations/20261017082512_AddLicenseAlarms.cs:14` (the table) and
`src/CcDirector.Gateway/Licenses/LicenseAlarmService.cs:727-732` (`ExecuteUpdate`).

Every earlier License Switching migration has a Postgres test beside it in `Gateway.DatabaseTests`
(`AddLicenseRenewalPostgresTests`, `AddLicenseSwitchHoldPostgresTests`): it proves the migration applies right after
its predecessor without touching rows stored before it, that the real store then writes and reads through
`GatewayDatabase` on PostgreSQL, and that `Down` removes it. `AddLicenseAlarms` has none. The Developer added the two
collation columns to `PostgresProviderProofTests`, which proves the "C" collation and nothing else.

The harm: the hosted Gateway, the one the owner's machines report to, runs Postgres, and the alarm's first run on it
is the production deploy. Three things in this service run on Postgres for the first time there: `ExecuteUpdate`
setting a nullable `DateTime` into a `timestamp with time zone` column, the identity compare on `Kind` and
`AccountEmail` under the byte collation, and the `(tenant_id, ClosedAtUtc)` index behind every page's poll. If any of
them fails, `Reconcile` throws, no alarm opens, the owner email never goes out - the quiet failure this mission exists
to end, and it would surface first as a line in the hosted log. The fix is the sibling test: apply the migration after
the one before it, open, re-raise, close and mark one alarm through `LicenseAlarmService` on PostgreSQL, run `Down`.

### 2. The Cockpit banner is switched off while the shell shows a team's pages - should-fix

`apps/cockpit/src/AppShell.tsx:369`: `<LicenseAlarmBanner enabled={wholeApp} ... />`, where `wholeApp` is
`!team.resolving && !team.choosing && teamPages === null` (line 204).

The brief says the banner is on every Cockpit page, and the owner email the Developer wrote
(`LicenseAlarmFold.cs:314`) promises it: "Each problem is shown in red at the top of every page of the Cockpit until
it is fixed." On a team's pages the banner is not read and not drawn, so that sentence is false there. The read is
made with the person's own key and the alarm is about the person's own machines; which team's pages the shell is
showing changes nothing about it. A person who spends the afternoon in a team view has no banner for the afternoon.
The push and the email still arrive, so the harm is bounded, but the words the owner reads should be true. Either
enable the banner whenever the shell is signed in (my recommendation; the own-fleet hooks `useKeepWarm` and
`useSuggestionCount` are gated by `wholeApp` for a different reason - they are about the fleet on screen), or change
the email sentence. The Cockpit test "shows one alarm's line ..." renders at `/history` with no team, so it does not
see this.

### 3. The kind's title is checked before it is trimmed - note

`src/CcDirector.Gateway/Licenses/LicenseAlarmService.cs:353-357`. `title.EndsWith('.')` runs on the untrimmed
string and `Title = title.Trim()` after it, so `"Not ready. "` passes and the push reads "machine-a: Not ready.. Open
Licenses to see what to do." A kind is a constant in code, so this is cosmetic; trimming first closes it.

### 4. The minute-tick retry is proven at the service, not through the tick - note

`src/CcDirector.Gateway/Licenses/LicenseAlarmService.cs:501-532` and `GatewayHost.cs:7045`. The brief's "a failed
email is retried on a later tick" is held down by `SendDueEmailAsync_FailedEmail_IsRetriedOnALaterTick_AndSentOnce`,
which calls `SendDueEmailAsync` directly. `TickEveryTenantAsync` and its place in the Gateway's once-a-minute check
have no test. The method is a line-for-line copy of `LicenseRenewalEmailService.TickEveryTenantAsync`, so the risk
is low; recorded so the gap is known rather than assumed covered.

## Verdict

Approve for merge once the Developer has answered findings 1 and 2 (accept and fix, or decline with the reason in
the README). Nothing I found is a must-fix: the rule matches the brief and section 5 of MISSION.md, the identity and
the idempotent raise are right, the words are pinned and true (with the one exception in finding 2), no route raises
or clears an alarm, tenants are separated, the public repository holds only made-up names, and there is no
attribution. The `-Parked` result the pull request still owes is the Tech Lead's to collect; my filtered runs
(215 tests, all green on the branch binary) do not replace it.
