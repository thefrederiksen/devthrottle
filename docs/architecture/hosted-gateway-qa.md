# Hosted Gateway - QA evidence

Dated proof records for the Hosted Gateway mission's data-layer steps.

## Step 4a - Postgres provider proof

Date: 2026-07-18

Goal for this step: give the Gateway's EF Core context (`GatewayDbContext`) a PostgreSQL provider
for the single-tenant hosted install, decoupled from multi-tenancy, while the local install stays on
SQLite. The model is provider-agnostic; this step proves it actually runs on real Postgres and that the
SQLite path is untouched.

### What changed

- `CcDirector.Gateway.csproj` - added `Npgsql.EntityFrameworkCore.PostgreSQL` version 9.0.2, pinned to
  the EF Core 9.0.2 already referenced (a 10.x Npgsql would drag EF Core to 10 and break the pin).
  Restore is clean under TreatWarningsAsErrors - no downgrade, no version conflict.
- `Data/GatewayDatabase.cs` - provider selection is now config-driven and reads `CC_GATEWAY_DB_CONNECTION`
  in three distinct cases: UNSET (the variable is absent) keeps exactly today's SQLite behavior; SET to a
  blank/whitespace value THROWS a fail-loud configuration error before any provider is built (a blank value
  is a misconfiguration, never a request for SQLite); SET to a real connection string uses Npgsql (migrations
  assembly `CcDirector.Gateway.Migrations.Postgres`, history table `__EFMigrationsHistory` in the `gateway`
  schema). There is no fallback between the two providers - a configured-but-broken Postgres throws with a
  message that names the provider and states it will NOT revert to SQLite. The SQLite-only
  `PRAGMA journal_mode=WAL` runs on the SQLite branch only. The Postgres path logs a credential-redacted
  target (host + database only, parsed with `NpgsqlConnectionStringBuilder`), never the connection string,
  and never interpolates the provider's exception message (only the exception type name) into a log or
  thrown message, so a password can never reach a log.
- `Data/GatewayDbContext.cs` - `OnModelCreating` adds a Postgres-only block guarded by
  `Database.IsNpgsql()` so SQLite is 100% unchanged: `HasDefaultSchema("gateway")`, and an explicit
  byte-ordinal collation `"C"` on the four natural-key string primary-key columns
  (`snoozes.SessionId`, `push_subscriptions.Endpoint`, `session_spend.SessionId`, `mission_notes.Key`)
  so their ordering and uniqueness match SQLite's default BINARY (memcmp) behavior. The UTC converter,
  decimal ban, GUID-key rule, and tenant scope are untouched (already provider-agnostic).
- `Data/GatewayDbContextDesignTimeFactory.cs` - one design-time factory, switched by
  `CC_GATEWAY_EF_PROVIDER` (`postgres` selects Npgsql with the Postgres migrations assembly and gateway
  history table; unset selects SQLite). Exactly one `IDesignTimeDbContextFactory<GatewayDbContext>` in
  the tree, so the tooling is never ambiguous.
- New project `src/CcDirector.Gateway.Migrations.Postgres` (net10.0, Nullable, TreatWarningsAsErrors) -
  holds only the generated Postgres migration and snapshot. It references `CcDirector.Gateway` for the
  model; `CcDirector.Gateway` does NOT reference it back (that would be a cycle). Added to the solution.
- `CcDirector.Gateway.Tests.csproj` - references the Postgres migrations project so the proof test can
  load it, plus the new gated proof test `Data/PostgresProviderProofTests.cs`.

### The migration command

The Postgres migration was scaffolded by switching the design-time provider to Postgres via
`CC_GATEWAY_EF_PROVIDER`:

```
CC_GATEWAY_EF_PROVIDER=postgres dotnet ef migrations add InitialPostgres \
  --project src/CcDirector.Gateway.Migrations.Postgres \
  --startup-project src/CcDirector.Gateway.Migrations.Postgres \
  --context GatewayDbContext \
  --output-dir Migrations
```

Note on regeneration: the Postgres migrations project is BOTH the target (`--project`) and the
`--startup-project`. It references `CcDirector.Gateway`, so `GatewayDbContext` and the single design-time
factory are discovered through that reference, and the migrations assembly is the startup project's own
output - so the migration DLL is always present and the command is reproducible from a clean checkout with
no "build the other project first" step. `CcDirector.Gateway` still deliberately does NOT reference the
migrations project (no cycle). To confirm the committed migration is in sync with the model,
`dotnet ef migrations has-pending-model-changes` with the same `--project`/`--startup-project` reports no
pending changes.

At runtime, whatever hosted startup/publish project composes the Gateway will need to reference
`CcDirector.Gateway.Migrations.Postgres` so the migration DLL ships beside the Gateway assembly and
`Database.Migrate()` can load it. That host/publish wiring - plus a publish smoke test that loads the
assembly and constructs `GatewayDatabase` against a real Postgres - is NOT done in this increment; it is
DEFERRED to the deploy increment. Today the ONLY project that references the migrations assembly is the
test project `CcDirector.Gateway.Tests` (added here so the proof test can load it); the Gateway host is
deliberately left untouched. So the runtime hosted path is proven at the data-layer level by the test
project here, but the end-to-end host packaging is still owed by the deploy increment.

The command wrote `Migrations/20260718120027_InitialPostgres.cs`, its `.Designer.cs`, and
`Migrations/GatewayDbContextModelSnapshot.cs` into the Postgres migrations project only. The SQLite
migration set and snapshot under `src/CcDirector.Gateway/Data/Migrations/` are byte-for-byte unchanged
(`git status` reports zero changes there).

### Generated Up() - the Postgres-specific shape

The generated migration puts every table under the `gateway` schema, uses `jsonb` for the owned JSON
columns, `timestamp with time zone` for every UTC `DateTime`, `uuid` for every GUID key, and
`collation: "C"` on the four natural-key primary-key columns. Excerpts:

```
migrationBuilder.EnsureSchema(
    name: "gateway");

migrationBuilder.CreateTable(
    name: "cron_jobs",
    schema: "gateway",
    columns: table => new
    {
        Id = table.Column<string>(type: "text", nullable: false),
        ...
        CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
        ...
        Action = table.Column<string>(type: "jsonb", nullable: false),
        Target = table.Column<string>(type: "jsonb", nullable: false)
    },
    ...);

migrationBuilder.CreateTable(
    name: "account_hosted_ai_spend",
    schema: "gateway",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false),
        ...
    },
    ...);

migrationBuilder.CreateTable(
    name: "push_subscriptions",
    schema: "gateway",
    columns: table => new
    {
        Endpoint = table.Column<string>(type: "text", nullable: false, collation: "C"),
        ...
    },
    constraints: table =>
    {
        table.PrimaryKey("PK_push_subscriptions", x => x.Endpoint);
    });
```

The other three `collation: "C"` columns are `snoozes.SessionId`, `session_spend.SessionId`, and
`mission_notes.Key`. Counts across the whole migration: 25 `timestamp with time zone` columns, 17
`uuid` columns, 8 `jsonb` columns (cron target/action, wingman versions, workflow_runs
criteria/proof/participants, workflow_versions steps/outcome_criteria), and exactly 4 `collation: "C"`
columns. The snapshot carries `HasDefaultSchema("gateway")`.

### Proof on real Postgres

A throwaway Postgres 16 container was started:

```
docker run -d --name cc-pg-proof -e POSTGRES_PASSWORD=proof -e POSTGRES_DB=ccpgproof \
  -p 55432:5432 postgres:16
```

The database name begins with the dedicated throwaway prefix `ccpg`; the from-nothing migrate test refuses
to `EnsureDeleted()` any database whose name does not start with that prefix, so the drop can never hit a
real database.

The gated proof tests (`PostgresProviderProofTests`, skipped unless `CC_GATEWAY_TEST_PG_CONNECTION` is
set) were run against it. Each builds a `GatewayDbContext` on Npgsql with the Postgres migrations
assembly and the gateway-schema history table, calls `Database.Migrate()`, and exercises the shapes that
could diverge between providers - a JSON-owned column (WorkflowVersion Steps + OutcomeCriteria), a
natural-key/collation column (PushSubscription endpoints, asserting byte-ordinal order and PK
uniqueness), an explicit-collation catalog check, and a UTC timestamp (SessionSpend). All writes set
`ActiveTenant` = "local".

```
CC_GATEWAY_TEST_PG_CONNECTION="Host=localhost;Port=55432;Database=ccpgproof;Username=postgres;Password=proof" \
  dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj \
  --filter "FullyQualifiedName~PostgresProviderProofTests"

Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 1 s
```

The five tests that passed:

- `Migrate_CreatesGatewaySchemaAndTables_OnRealPostgres` - drops and re-migrates from nothing (guarded
  so it only drops a database whose name carries a throwaway marker), then asserts the `gateway` schema,
  all 16 mapped tables, and the `__EFMigrationsHistory` table exist under the `gateway` schema and NONE
  of them under `public`.
- `Collation_ExplicitC_OnExactlyTheFourNaturalKeys_OnRealPostgres` - reads `pg_attribute.attcollation`
  (the column's DEFINED collation, which is the type-default pseudo-collation for a plain text column and
  the `C` collation only where the migration set `COLLATE "C"` explicitly, so it distinguishes our
  explicit collation from the database's default even if that default is already C) and asserts EXACTLY
  the four natural-key columns (`snoozes.SessionId`, `push_subscriptions.Endpoint`,
  `session_spend.SessionId`, `mission_notes.Key`) carry an explicit `C`, and no gateway column carries
  any explicit collation other than the default or `C`.
- `WorkflowVersion_JsonOwnedColumns_RoundTrip_OnRealPostgres` - Steps and OutcomeCriteria written and
  read back field-for-field through `jsonb`.
- `PushSubscription_NaturalKeyByteOrdinalCollation_OnRealPostgres` - endpoints `endpoint_a`,
  `endpoint_C`, `endpoint_B` come back ordered `endpoint_B`, `endpoint_C`, `endpoint_a` (byte order:
  'B' < 'C' < 'a'; a locale collation would have put `endpoint_a` first), and a duplicate endpoint is
  rejected with `DbUpdateException`.
- `SessionSpend_UtcTimestampRoundTrip_OnRealPostgres` - UTC timestamps come back equal with
  `Kind == Utc`.

### SQLite still green, Postgres tests skip by default

With the environment variable unset, the same test binary shows the SQLite Data tests green and the five
Postgres tests skipped - the default (SQLite) run is unaffected. The 35 passing include the existing
data-layer tests plus two additions that need no Postgres: the provider-selection tests
(`GatewayDatabaseProviderSelectionTests` - env var unset selects SQLite; env var set-but-blank fails
loud, isolated in a `DisableParallelization` collection with the env var saved and restored) and the
redaction tests (`GatewayDatabaseRedactionTests` - a password containing ';' and '=' never appears in
`RedactConnectionTarget`'s output, and an unparseable string returns the fixed literal without echoing
input).

```
dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~Data"

  Skipped CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.PushSubscription_NaturalKeyByteOrdinalCollation_OnRealPostgres
  Skipped CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.WorkflowVersion_JsonOwnedColumns_RoundTrip_OnRealPostgres
  Skipped CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.Collation_ExplicitC_OnExactlyTheFourNaturalKeys_OnRealPostgres
  Skipped CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.Migrate_CreatesGatewaySchemaAndTables_OnRealPostgres
  Skipped CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.SessionSpend_UtcTimestampRoundTrip_OnRealPostgres

Passed!  - Failed:     0, Passed:    35, Skipped:     5, Total:    40, Duration: 2 s
```

The whole solution builds clean (Debug): 0 warnings, 0 errors, TreatWarningsAsErrors on. The container
was removed after the run (`docker rm -f cc-pg-proof`).

## Step 4a - increment 2: proof on the real Supabase Postgres

Date: 2026-07-18

Increment 1 proved the provider on a throwaway Docker Postgres. Increment 2 proves the SAME provider
against the REAL hosted target - the Supabase Postgres project - through the actual Gateway startup path,
in an isolated `gateway` schema, before any Azure deploy.

### Target and constraints

The connection points at the hosted Supabase project's SESSION POOLER
(`Host=<...>.pooler.supabase.com;Port=5432;...;SSL Mode=Require`, password REDACTED - never committed or
logged), authenticating as a dedicated role scoped to the `gateway` schema ONLY: it has no access to the
website's accounts/auth/public tables, and it cannot create or drop databases. So the increment-1 harness
that drops a throwaway database (the `ccpg`-prefixed `EnsureDeleted` path) does NOT apply here. This is a
real integration run instead: it applies the migration into the existing `gateway` schema and leaves it in
place (deploy-ready), and it cleans up its own test rows so the tables are left empty.

The connection string lives ONLY in the machine-local secret store: the cc-secrets entry
`devthrottle-gateway-db-connection`, which `cc-secrets run devthrottle-gateway-db-connection -- <command>`
supplies at the point of use as `DEVTHROTTLE_GATEWAY_DB_CONNECTION`; the command exports it into
`CC_GATEWAY_DB_CONNECTION`. It is never echoed, committed, or written to a
log - the Gateway's Postgres path logs only a redacted host+database target.

### The proof (a real integration run)

`GatewayDatabaseLivePostgresProofTests` (new) drives the REAL runtime path: each fact constructs a
`GatewayDatabase` (the same class the running Gateway uses), whose constructor reads
`CC_GATEWAY_DB_CONNECTION`, selects Npgsql, and runs `Database.Migrate()` - applying the Postgres migration
set into the `gateway` schema on Supabase. It never calls `EnsureDeleted` and never drops anything.

CI SAFETY: every fact is gated behind the PRESENCE of `CC_GATEWAY_DB_CONNECTION` (skips cleanly when
unset, the same pattern as the increment-1 Postgres tests). Automated CI never sets it, so CI never
connects to the hosted project and never needs the secret; the proof runs only locally/manually with the
connection string exported.

```
CC_GATEWAY_DB_CONNECTION="<Supabase session-pooler string; password redacted>" \
  dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj \
  --filter "FullyQualifiedName~GatewayDatabaseLivePostgresProofTests"

Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 6 s
```

The four facts that passed on real Supabase:

- `Startup_AppliesGatewaySchemaAndTables_OnConfiguredPostgres` - constructing `GatewayDatabase` migrates
  into the `gateway` schema; asserts the schema, all 16 mapped tables, and `__EFMigrationsHistory` exist
  under `gateway` and NONE of the mapped tables exist under `public` (schema isolation holds on the real
  project).
- `JsonOwnedColumns_RoundTrip_OnConfiguredPostgres` - a WorkflowVersion's Steps + OutcomeCriteria written
  and read back field-for-field through `jsonb`, then the row deleted.
- `NaturalKeyByteOrdinalCollation_RoundTrip_OnConfiguredPostgres` - endpoints come back in byte order
  (`..._B`, `..._C`, `..._a`; a locale collation would have put `..._a` first), a duplicate endpoint is
  rejected, then the rows deleted.
- `UtcTimestampRoundTrip_OnConfiguredPostgres` - a session_spend row's UTC timestamps come back equal with
  `Kind == Utc`, then the row deleted.

### Schema left clean and deploy-ready

Every round-trip deletes its rows in a `finally`, so a failed assertion could not leave data behind.
Verified independently after the run by counting rows directly on Supabase (via `psql` over the pooler),
summing across ALL 16 application tables rather than only the three written:

```
app_table_rows_total=0        (sum of row counts across all 16 mapped tables)
migrations_history_rows=1     (the one applied InitialPostgres record - expected)
app_tables_in_gateway=16
```

So the `gateway` schema holds the 16 application tables, ALL empty (0 rows in total), plus the
`__EFMigrationsHistory` table carrying exactly one row: the applied `InitialPostgres` migration. That one
history row is not residue - it is the record that the migration ran, and it is precisely what makes the
schema deploy-ready: the eventual hosted deploy's `Migrate()` reads it, sees the migration already applied,
and is a no-op. This closes increment 2. The runtime host packaging that ships the migrations assembly (so
the deployed Gateway can load it) remains the deploy increment's work.

## Step 4b - increment 3 (code): ship the Postgres migrations in the published image

Date: 2026-07-18

The provider and the runtime MigrationsAssembly wiring were proven against real Supabase in increment 2,
but that ran inside the solution where every assembly is referenced. The deployed container publishes the
Gateway alone; if it does not carry the SEPARATE Npgsql migrations assembly, a container boot against
Supabase would find no migration set. This increment closes that gap. Single-tenant, NO behavior change to
the SQLite/local path.

### The circular-dependency finding (why a thin host)

The work order's first-cut step - "CcDirector.Gateway ProjectReferences CcDirector.Gateway.Migrations.Postgres"
- cannot build: the migrations assembly already references CcDirector.Gateway (for GatewayDbContext), so the
reverse reference is a build cycle (confirmed: MSB4006, "circular dependency in the target dependency graph").
And the Dockerfile publishes CcDirector.Gateway itself as the container entrypoint, so the migrations DLL has
to be a real dependency of the PUBLISHED project to load at runtime (a loose-copied DLL does not resolve
through EF's Assembly.Load in a framework-dependent publish). The fix is the standard EF pattern for a
separate migrations assembly: a thin startup/host project references BOTH the context assembly and the
migrations assembly.

### What changed

- New project `src/CcDirector.Gateway.Host` (net10.0, framework-dependent, ASP.NET Core): the container
  entrypoint. It references CcDirector.Gateway AND CcDirector.Gateway.Migrations.Postgres, and its Program.cs
  is a one-liner that calls the shared startup and forwards args verbatim. CcDirector.Gateway does NOT
  reference the migrations assembly (that is the cycle above); the host does, so the migrations DLL ships in
  the host's publish output.
- `src/CcDirector.Gateway/GatewayEntryPoint.cs` (new) + `Program.cs` (now a one-liner): the entire Gateway
  startup logic - the --port/--help parsing, the CC_GATEWAY_HOSTED platform-port resolution, and the worker
  wiring - was extracted verbatim into a shared `GatewayEntryPoint.Run(args)` that BOTH executables call. The
  local console host and the container host therefore run byte-identical startup with zero drift; the only
  difference between the two executables is the migrations-assembly reference. Verified: both exes print
  identical --help output and exit 0.
- `Dockerfile`: changed ONLY the publish target (to src/CcDirector.Gateway.Host) and the entrypoint
  (to CcDirector.Gateway.Host.dll --port 7878). EXPOSE 7878, USER gateway, ENV HOME, and ENV CC_DIRECTOR_ROOT are
  unchanged. The host csproj sets ErrorOnDuplicatePublishOutputFiles=false because CcDirector.Gateway is
  itself an executable, so a RID publish emits its (inert) deps.json/runtimeconfig into two paths - the
  documented "referencing executable projects" case; the container entrypoint uses the host's own runtime
  config, which is unaffected.
- Confirmed nothing ELSE launches CcDirector.Gateway.dll as a hosted entrypoint (only the Dockerfile did);
  the local/desktop path launches CcDirector.Gateway directly on SQLite and is untouched.

### Evidence (a RUN, not a build)

Publish output of the host (the same command the Dockerfile runs), proving the container carries and can
resolve the migrations assembly:

```
dotnet publish src/CcDirector.Gateway.Host/CcDirector.Gateway.Host.csproj \
  -c Release -r linux-x64 --no-self-contained \
  -p:RunMobileBuild=false -p:RunCockpitBuild=false -p:RunWorkspaceTypecheck=false -o <out>

  CcDirector.Gateway.Host.dll                    PRESENT
  CcDirector.Gateway.Migrations.Postgres.dll     PRESENT
  Npgsql.dll                                     PRESENT
  CcDirector.Gateway.Host.deps.json references CcDirector.Gateway.Migrations.Postgres   (2 entries)
  CcDirector.Gateway.Host.runtimeconfig.json targets Microsoft.AspNetCore.App
```

Note (issue #1892): the publish command above is the one that was run for THIS piece of work, and it is
no longer the command in the Dockerfile. The image now builds the cockpit and the mobile app into
`wwwroot/c` and `wwwroot/m`, so the three `Run*` properties are `true` and the runtime-identifier flags
are gone. Read the Dockerfile for the current command.

Boot smoke test (`GatewayHostBootSmokeTests`):
- `PostgresMigrationSet_ResolvesByAssemblyName_WithoutDatabase` (always runs, no database): loads the
  migration set by the assembly name the host wires (`GetMigrations()`, which does not connect) and asserts
  it contains `20260718120027_InitialPostgres`. This is the CI-safe half - it proves the separately-assembled
  set is resolvable by name, connecting to nothing.
- `HostStartupPath_ResolvesAndAppliesPostgresMigrations_OnConfiguredPostgres` (env-gated by
  CC_GATEWAY_DB_CONNECTION, skips when unset so CI touches nothing): constructs the real GatewayDatabase (the
  hosted startup path), which runs Migrate(), and asserts `GetAppliedMigrations()` contains
  `20260718120027_InitialPostgres`. Run against real Supabase: 2 passed (both facts), password never logged.

Regression: the Gateway Data-namespace suite with no env var
(`dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter FullyQualifiedName~CcDirector.Gateway.Tests.Data`)
- 9 passed, 10 skipped. Every database/Supabase fact is among the 10 skips, so nothing connects to a server,
and the SQLite/local path stays green and unchanged.

## Live end-to-end validation - the hosted Gateway on Azure App Service + Supabase

Date: 2026-07-18

The hosted Gateway is deployed and LIVE: Azure App Service B1 (Linux container, East US), the image built
from merged HEAD (the CcDirector.Gateway.Host entrypoint with the Postgres migrations shipped), pointed at
the real Supabase Postgres in the isolated `gateway` schema via CC_GATEWAY_DB_CONNECTION as an App Service
application setting (the connection string is never baked into the image, never logged - the Postgres path
logs only host+database). Public URL https://devthrottle-gw.azurewebsites.net, /healthz returns 200. Boot
log: `[GatewayDatabase] Open: ready, provider=Postgres host=aws-1-us-east-1.pooler.supabase.com
database=postgres`; there is NO gateway.db on the App Service file share (the operational stores are on
Supabase).

Validation was run by pointing a DEDICATED, ISOLATED test Director at the cloud gateway - never the live
fleet, never the live token. The test Director is a slot-5 `cc-director` built from origin/main, given its
OWN CC_DIRECTOR_ROOT and a config.json with the gateway block (gateway.url = the public URL, gateway.token =
the hosted director-auth token, read at point of use from the machine-local credential file, never echoed or
committed). Three steps, each confirmed on BOTH the client side (this run) and the gateway/Supabase side
(container logs + psql over the pooler by the deploy overseer).

### Step 1 - authenticate + establish the tunnel

The test Director authenticated with the token ALONE (no account/JWT/device-enrollment) and its
GatewayStreamClient established the SignalR tunnel over HTTPS/WSS to the public URL on 443 (App Service
terminates TLS and maps 443 to the container's WEBSITES_PORT 7878); no tailnet (the hosted-mode contract -
CC_GATEWAY_HOSTED skips tailnet/Serve).
- Client side: /healthz directors 0 -> 1; GET /directors (bearer token) returns directorId
  71eeeedd-9bfc-4d1c-bf3b-cbbcfc11a57e, pid 38432 (matches the launched process), machine SOREN_NORTH,
  version 1.5.0, source=stream (the SignalR tunnel, not an endpoint probe), tailnetEndpoint=null.
  Unauthenticated GET /directors -> HTTP 401 (token gating).
- Gateway side (container logs): `[DirectorRegistry] RegisterFromStream: id=71eeeedd..., machine=SOREN_NORTH,
  version=1.5.0`; `[DirectorHub] Hello: director=71eeeedd... bound to conn=Pn44B_Sc`; tenant=local; and both
  `GET /directors -> 200` (with the bearer) and `-> 401` (without) observed.

### Step 2 - a real operation writes through to Supabase

A token-gated write through the cloud gateway: `PUT /gateway/missions/notes` with
{mission: HGW-VALIDATE-STEP2-38432, why: hosted-validation-71eeeedd-step2} -> HTTP 200; `GET
/gateway/missions/notes` reads it straight back (round-trip through the gateway). (The gateway token
authenticates the /gateway/... route as well - auth is unified.)
- Physically verified in Supabase (psql over the pooler, gateway_app -> gateway schema): the
  gateway.mission_notes row is Key=hgw-validate-step2-38432, Why=hosted-validation-71eeeedd-step2,
  UpdatedAtUtc=2026-07-18 18:49:09.448459+00, tenant_id=local; total_rows=1 (clean isolated schema). The
  write went all the way to the database, not ephemeral container state.

### Step 3 - restart: re-register + persist

`az webapp restart` on the App Service (the overseer ran it - the resource group is in the deploy
subscription, a different tenant than the client's az login).
- Genuine fresh boot: container logs show a new startup at 18:52:41-45
  (`[GatewayHost] listening on 0.0.0.0:7878, version 1.5.0`); the old container disposed its Postgres
  connection at 18:53:07 (clean handoff).
- Client side observed the outage: /healthz 200 -> HTTP 000 (~t+19s) -> 200 (~t+23s), a ~4s real outage
  during the container handoff.
- The tunnel auto-re-established with no manual action:
  `[DirectorRegistry] RegisterFromStream: id=71eeeedd...` at 18:53:16; /healthz directors:1 again.
- Persistence proven: after the fresh boot the mission_notes row is BYTE-IDENTICAL - Key,
  Why=hosted-validation-71eeeedd-step2, and UpdatedAtUtc=2026-07-18 18:49:09.448459+00 (the ORIGINAL write
  time, untouched, not rewritten) - re-read via both the gateway GET and psql. State is Supabase-backed and
  survived a fresh container = not ephemeral.

### Idle-hold observation (App Service ~230s idle cut)

The Director-Gateway tunnel heartbeats about every 10s (gateway logs), well under App Service's ~230s idle
cap. Over a deliberate 276s idle window (no operations sent through the tunnel), client-side /healthz polling
showed directors=1 continuously with 0 drops (poll interval 15s). The gateway-side continuous log watch
(authoritative) confirmed the tunnel HELD - no idle drop: only two RegisterFromStream events across the
ENTIRE run (18:45:07 initial, 18:53:16 post-restart re-register) and ZERO during the idle window; 84 unbroken
~10s heartbeat lines from 18:55:05 to 19:01:55 all on the SAME connection (conn=TTpM8mjD - the connection id
never changed); zero disconnect/closed events. A drop-and-reconnect would have logged a fresh
RegisterFromStream and a new connection id; neither happened. So the ~10s SignalR heartbeat keeps the tunnel
active and App Service never idle-cuts it - it holds, no reconnect needed.

### Result

The hosted Gateway is validated end to end: a real Director authenticates and tunnels to it over the public
URL, a real operation persists through it into the isolated Supabase gateway schema, state survives an App
Service restart, and the tunnel holds past the idle cap. The isolated test Director (pid 38432) and its
scratch CC_DIRECTOR_ROOT were torn down after the run.

## Tenant scoping - the PRIMARY KEY half (snoozes, session_spend, push_subscriptions)

Date: 2026-07-19

Issue #1840 made `cron_jobs`, `workflows`, `workflow_versions` and `mission_notes` tenant-composite. A
persistence census afterwards found three tenant-scoped tables still keyed on CALLER-SUPPLIED input with
`tenant_id` present only as a data column:

- `snoozes` - primary key `SessionId` (the session id a Director presents on a snooze request)
- `session_spend` - primary key `SessionId` (the session id on a record-spend request)
- `push_subscriptions` - primary key `Endpoint` (the browser's push URL)

### Why that is a defect, not a style point

Every store upserts the same way: read THROUGH the tenant query filter, and add when the read finds nothing.
So a second tenant presenting an identifier the first tenant already holds reads nothing (the filter hides the
other tenant's row), takes the insert branch, and the database rejects it on the primary key. That is a
cross-tenant SQUAT (the second tenant cannot use that identifier at all, because another tenant has it) and an
EXISTENCE ORACLE (the failure discloses that another tenant holds it). No row content crosses the boundary,
but the key space does.

Reproduced before the fix by driving the REAL stores (`SnoozeRegistry`, `SessionSpendStore`,
`PushSubscriptionStore`) with two tenants over one database - three tests, three failures, each
`SQLite Error 19: 'UNIQUE constraint failed: <table>.<key>'`.

### The fix

`GatewayDbContext` now gives all three a composite primary key, following #1840 exactly:
`(tenant_id, SessionId)`, `(tenant_id, SessionId)`, `(tenant_id, Endpoint)`. No store code changed - every
lookup already goes through a filtered LINQ query, never `Find(key)`.

### The guard that should have caught it

`TenantScopeGuardTests` asserted the tenant COLUMN and the query FILTER, and is structurally blind to the KEY -
which is exactly how these three passed it. A third assertion,
`EveryTenantScopedTable_HasTenantIdInItsPrimaryKey_UnlessTheGatewayMintsTheKeyItself`, now requires every
tenant-scoped table's primary key to include `tenant_id`, unless the key is one the Gateway itself mints.

That exemption is a TYPE, not a written list. The first attempt was an allowlist of ten table names, each with a
sentence asserting "this store mints the key with `Guid.NewGuid()`", re-checked by asserting the key was still a
single `Guid`. That check could not fail for the change it existed to catch: a store switching from minting its
key to accepting one from the caller keeps the key a single `Guid`, so the guard stayed green in precisely the
dangerous case - which is worse than no guard, because it reads as protection and stops anyone downstream from
looking.

The exemption is now `GatewayMintedKeyEntity`, whose `Id` has a PRIVATE setter and is minted by the base class
itself. The ten entities derive from it and no longer declare their own `Id`. Assigning a caller-supplied value
to one of these keys is not a test failure found later - it does not compile. The three ways to escape that are
each closed by construction:

| Escape | What stops it |
|---|---|
| Assign a caller-supplied value to the key | Compile error `CS0272` - the setter is inaccessible |
| Widen the setter so that compiles again | `TheGatewayMintedKey_CanOnlyBeWrittenByTheBaseClassThatMintsIt` turns red |
| Re-declare a settable `Id` on the derived entity, or drop the base class | The primary-key test requires the mapped key property to be DECLARED ON `GatewayMintedKeyEntity`, so it turns red |

Each of those three was verified by making the change and watching the failure, and both guards were confirmed
green again once it was reverted.

### The migration, and what it does to existing rows

One migration per provider, generated from the same model:
`Data/Migrations/20260719201700_CallerSuppliedKeysScopedByTenant` (SQLite) and
`Migrations/20260719201740_CallerSuppliedKeysScopedByTenant` (Postgres). Each drops the three single-column
primary keys and adds the composite ones. `dotnet ef migrations has-pending-model-changes` reports no pending
changes on BOTH providers.

Existing rows are preserved, and that is established by RUNNING the upgrade, not by reading the operations.
`CallerSuppliedKeyUpgradePreservesRowsTests` migrates a database to the migration immediately before this one,
inserts rows through raw SQL the way a database in the field holds them, migrates the rest of the way, and
asserts every row and every column value is unchanged. It also asserts the migration actually ran - by name out
of `__EFMigrationsHistory`, and by the key really being composite afterwards - so a migration that silently did
nothing cannot pass by leaving the rows undisturbed. A second test shows the upgrade delivers what it is for:
before it, a second tenant reusing a session id is refused on the primary key; after it, both rows coexist.

This matters because the reasoning below is NOT sufficient on its own. On SQLite a primary key cannot be altered
in place, so EF turns each operation pair into a table rebuild, and whether the copy carries every row and every
column value is a property of that generated rebuild rather than of the three lines anyone reads in the
migration file. The supporting reasons:

- No row is deleted, inserted, or edited. On SQLite a primary-key change is a table REBUILD (EF creates the new
  table, copies every row, drops the old, renames); on Postgres it is `ALTER TABLE ... DROP CONSTRAINT` plus
  `ADD PRIMARY KEY`, with no table rewrite.
- Uniqueness cannot be violated by existing data: the OLD key already made the second column unique on its own,
  so `(tenant_id, <that column>)` is unique a fortiori. There is no possible duplicate to trip on.
- `tenant_id` is already NOT NULL on all three tables (`ApplyTenantScope` marks it required), which a primary-key
  column must be, so no backfill and no nullability change is needed.
- On a single-tenant install every row is `tenant_id = 'local'`, so the composite key behaves identically to the
  old one and the upgrade is invisible.
