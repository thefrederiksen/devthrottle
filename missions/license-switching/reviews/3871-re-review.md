# PR 3871 re-review - Gateway license storage and usage ingest

Verdict: changes needed

## Answers

1. **Accepted, with the findings below.** `Ingest`, `RegisterConnection`, and `UnregisterConnection` use the same per-owner monitor, so for an identical tenant and Director key an in-flight report and a reconnect/disconnect are serialized. `DirectorHub` has no locks of its own. `Hello` and disconnect call the session, repository, and license registries sequentially; their locks are released before the next registry is called. The registry's `_livenessGate` is taken later in `Hello`, after the license monitor has been released. I found no lock-order cycle. The owner monitor does, however, span the synchronous database write, and its key comparer permits case variants to have separate owners. The new test also does not deterministically establish that the reconnect task ran before the apply callback returns.

2. **Accepted.** `CapabilitiesFor` removes `PushLicenseUsage` when no ingest is configured, and `GatewayCapabilities_WithNoLicenseIngest_DoNotOfferPushLicenseUsage` checks that case while retaining another hub method.

3. **Accepted.** The four reads named in the finding log entry, result, and failure; `LicenseReadModel.Read` does likewise.

## New findings

1. **P2 - `src/CcDirector.Gateway/Licenses/LicenseUsageIngest.cs:56`.** `_owners` uses the default tuple comparer, which compares Director ID strings case-sensitively. `Hello` only trims the supplied ID, while `PushedRepositoryStore` deliberately indexes Directors with `StringComparer.OrdinalIgnoreCase` (`src/CcDirector.Gateway/Streaming/PushedRepositoryStore.cs:55`). A reconnect presenting the same Director ID with different casing therefore gets a separate owner here: the previous connection remains authorized to ingest reports even after the repository store has transferred ownership. Normalize the ID or use matching case-insensitive key semantics, and cover a differently-cased reconnect.

2. **P2 - `src/CcDirector.Gateway/Licenses/LicenseUsageIngest.cs:128`.** The per-Director monitor stays held through `_store.ApplyReport` at line 143. `ApplyReport` performs synchronous database queries and `SaveChanges` (`src/CcDirector.Gateway/Licenses/LicenseStore.cs:95,146`); the Gateway uses both SQLite and PostgreSQL, and this call has no cancellation or ingest deadline. A database wait therefore also blocks that Director's `Hello` registration and disconnect cleanup on the same monitor, potentially keeping the SignalR invocation/tunnel stuck for the provider's wait. Preserve the ownership/write ordering without holding this in-memory monitor over blocking database I/O.

3. **P2 - `src/CcDirector.Gateway.UnitTests/Licenses/LicenseUsageIngestTests.cs:71`.** The test schedules `RegisterConnection` with `Task.Run` and waits 500 ms for that task to finish, but has no signal proving the worker started before `insideApply` returns. If the worker is not scheduled during that window, it can register after the write and the test still passes without exercising the unlocked interleaving. Add a deterministic start/overlap rendezvous so the test proves the reconnect attempted registration while the report was inside the critical section.

## Verification

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` - passed, 44 passed, 0 failed, 0 skipped.

## Handoff

- The requested Tech Lead message was not sent: the supplied `cc-devthrottle.CMD` path was not recognized by this shell, and `cc-devthrottle` is not on PATH.
