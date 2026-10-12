# PR 3871 review - Gateway license storage and usage ingest

Verdict: changes needed

1. **`must fix` - `src/CcDirector.Gateway/Streaming/DirectorHub.cs:192`.** `IsActiveConnection` is sampled as a boolean before `LicenseUsageIngest.Ingest` takes its own sequence lock. A reconnect can register a new active connection after this check, while the old invocation still proceeds through `ApplyReport`; the ingest gate does not revalidate connection ownership. That violates contract 2.1 and lets an already superseded connection overwrite the machine snapshot. `PushRepoSnapshot` validates ownership and applies the snapshot together inside `PushedRepositoryStore.ApplySnapshot`. The current hub test covers an old connection that is already superseded before invocation, not a reconnect interleaving between validation and write.

2. **`should fix` - `src/CcDirector.Gateway/Streaming/DirectorHub.cs:184`.** The ingest dependency is optional and the null branch logs and returns normally, but `BuildCapabilities` reflects the public hub methods and advertises `PushLicenseUsage` regardless of whether the ingest dependency was supplied. A missing registration therefore looks like a successful report invocation while dropping every snapshot. `GatewayHost` currently registers `LicenseUsage`, but the fallback hides a future wiring error; make the dependency required or make the advertised capability depend on a working ingest path.

3. **`should fix` - `src/CcDirector.Gateway/Licenses/LicenseStore.cs:267`.** `FindSwitch`, `ListOpenSwitches`, `ListLog`, and `ListActiveBars` perform public store reads without entry, result, or failure logs; `LicenseReadModel.Read` at `src/CcDirector.Gateway/Licenses/LicenseReadModel.cs:112` also has no operation log. This misses `CLAUDE.md:158`, which requires entry, exit, and error logging for every public method, and leaves a failed or never-run query without the required store-level trace.

## Verification

- `dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj --filter "FullyQualifiedName~Licenses"` - passed 41, failed 0, skipped 0.
- `dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj --no-restore` - succeeded, 0 warnings and 0 errors.
- `dotnet ef migrations has-pending-model-changes` against the SQLite model - no pending changes.
- The same model check with `CC_GATEWAY_EF_PROVIDER=postgres` and the PostgreSQL migrations project - no pending changes.
