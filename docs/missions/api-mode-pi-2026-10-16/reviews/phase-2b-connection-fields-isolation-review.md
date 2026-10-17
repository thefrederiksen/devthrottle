# Phase 2 follow-on review: `SessionConnectionFieldsTests` gets its own registry database (pull request #4136)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Phase 2 follow-on, item 1. Pull request
#4136, branch `api-mode-connection-fields-isolation`, reviewed at its head commit `1f841602d` (one commit on
base `6546d5fc9`). Reviewer seat, opened by the Gateway Tech Lead, running a different agent family from the
Developer. Written 17 October 2026 on devlinux.

**Verdict: holds. The diagnosis is the most likely cause, the fix is right, and it is complete for this class.
Nothing in the `GET /sessions` route or the pushed store can serve `ConnectionMode` or `WheelHolder` wrongly
under concurrency, so there is no product defect for the 13 October rule to keep the test alive for; the test
stays because its failure came from a machine-shared file the test wiring opened, and the change removes that
file from the class. Three findings, none of which blocks the merge. Finding 1 is about the precision of the
pull request's own account of the failure, not about the fix.**

## 1. Scope: what I read, what I did not run

**Read, in full, on the branch at `1f841602d`:** the pull request body; the diff (one file, three lines);
`Api/SessionConnectionFieldsTests.cs` as it now stands, every line. In the product: `Pairing/DeviceRegistry.cs`
(all three constructors, `ResolveStorePath`, `InitializeAuthority`, `Dispose`); `Pairing/DeviceRegistryImporter.cs`
(`Import`, the marker check and the marker insert); `Data/GatewayDatabase.cs` (the constructor, the SQLite `Open`
path with its `Migrate` and write-ahead-logging pragma, and `Dispose`, which clears only this database's own
connection pool); `Tenancy/HostedTenantBoundary.cs` (`ResolveRequestTenant`); the whole `GET /sessions` handler in
`Api/GatewayEndpoints.cs` from `ResolveReadTenant` to the roster fold, and a search of the Gateway for every read
or write of the two fields; `Streaming/PushedSessionStore.cs` (`ApplySnapshot`, `ApplyDelta`, `GetLastKnown`,
`RecomputeClocks`); `Api/NetDiagResultStore.cs` (constructor, `Load`, `Quarantine`). In the test infrastructure:
`GatewayDbTestHarness.cs`, `GatewayDbEnvironmentGate.cs`, `GatewayDbTestHarnessDevices.cs`,
`TestStorageRootRedirect.cs`, `ProcessEnvironmentCollection.cs`, `AssemblyParallelism.cs` (four threads). For
comparison: the same fix in `ApiModeVerbEndpointTests` (pull request #4082) and the comment in
`PushApiModeEventsHubTests`; the list of every other class in the Gateway unit suite that still builds the
parameterless `DeviceRegistry` (fifteen files, fourteen beside this one), and the four of them the Developer ran
beside this class.

**Ran: nothing.** The Tech Lead's brief forbids test runs on this machine (the machine crashed from parallel test
runs; the full gate runs on Windows). Every statement below is from reading. In particular I did not repeat the
Developer's 24 combined runs, the 8-thread probe, or the 16 runs after the change; those numbers are taken from
the pull request body, not checked.

**Could not reach:** the text of the one original failure, which nobody recorded. The pull request says so
itself. Finding 1 is what reading can say about it.

## 2. Question (a): is the diagnosis the most likely cause, or is there another shared piece of state or a real race in the route

### The shared file, and what the parameterless constructor does with it

`new DeviceRegistry()` resolves its store path to `<CcStorage.Config()>/director/devices.json` and opens its own
`GatewayDatabase` at that path plus `.gateway.db`. In the unit suite `CcStorage.Root()` is the one temporary
directory `TestStorageRootRedirect` sets for the whole process, so every class in the process that builds the
parameterless registry opens the SAME SQLite file. The constructor then does three things on that file, in this
order: `Migrate()` (the whole migration chain, about 1.5 seconds on an empty file by the harness's own
measurement), the write-ahead-logging pragma, and `DeviceRegistryImporter.Import()`, which checks for a marker row
keyed on the source path and, finding none, inserts one inside a transaction. The marker column is UNIQUE. Two
constructions that both pass the marker check before either commits end with one of them throwing
`UNIQUE constraint failed: device_import_markers.SourcePath`. That is the text the Developer's probe produced and
the text `PushApiModeEventsHubTests` and the phase 2 receipts review recorded for the same shape.

So the diagnosis names a real mechanism, on a path this class did open, with the only text ever observed for
this class of failure. I agree it is the most likely cause.

### Why the window is wider than "the same instant"

The pull request says the collision "needs two classes to build the parameterless registry in the same instant".
That understates it. The first construction in a process holds the file through a 1.5 second migration before it
reaches the marker; a second class whose construction starts anywhere in that time is serialised behind the
migration (the Developer's probe shows this, because eight concurrent constructions failed on the marker and not
on the migration) and then races the first one to the marker insert with Entity Framework's first-query
compilation cost in front of both. The window is the first construction's whole run, not an instant, and with four threads and
fifteen classes that build the registry this way, being inside it is a matter of which classes the runner picks
first. That is consistent with "failed once, passed alone, did not reproduce in 24 runs".

### Is there another shared piece of state?

I looked for any. What this class touches, before and after the change:

- `_root` under the temporary directory with a fresh identifier per instance: the Director registry lives there.
  Per instance, not shared.
- `PushedSessionStore`: one instance per test, no static state (the only statics are pure helpers).
- `HostedTenantBoundary` over `SingleTenantContext`: on self-host `ResolveRequestTenant` returns `Local` without
  touching the device registry at all. So the registry database is read only during construction, never during a
  request. The two `GET /sessions` tests cannot be disturbed by another class's database activity once
  `StartAsync` has returned.
- `GatewayEndpoints.Map` itself: it constructs one store at a process-wide path unconditionally,
  `new NetDiagResultStore(Path.Combine(CcStorage.Root(), "diagnostics-results.json"))`, whose constructor reads
  the file. See finding 3. In this suite no test writes that file at the default root (`NetDiagResultStoreTests`
  uses its own path, and no unit test posts to `/diag/result`), so today the read finds no file and returns. It is
  not the cause of the failure, but it is a process-wide path the class still touches after this change.
- `GatewayDatabase.Dispose` clears the pool for its own connection string, which for the parameterless registry
  is the shared file's string. A class that disposed a parameterless registry while this class was constructing
  one would hit this class's connection. No class in the suite disposes a parameterless registry (none of the
  fifteen wraps it in `using`), so this hazard is unarmed; the change removes this class from it anyway, because
  its registry now has its own connection string.
- The environment gate: the parameterless constructor opens its database outside
  `GatewayDbEnvironmentGate`; the harness's `Open` goes through it. The one test that blanks the provider variable
  now runs alone in the process environment collection, so this was not the cause either, but the change moves
  this class onto the gated path, which is the right side of it.

I found no other shared state.

### Is there a real race in how the route serves the two fields?

No. The route never writes either field. `ApplySnapshot` stores the pushed row itself under the Director entry's
lock, and `DiscardInboundRole` nulls seven Gateway-owned fields that are not these two. `GetLastKnown` hands out
`Clone()` copies under the same lock, and `RecomputeClocks` touches only `IdleSeconds`. `Clone` is
`MemberwiseClone`, so both strings come across (the class's own `Clone_` test pins that). The only Gateway code
that reads the fields in the roster path is `ApiModeRowStamp`, which stamps `ApiMode` from them and does not
assign them. The handler's own branches (team cut, hosted pushed-only filter, reachability) remove rows or add
envelope rows; none rewrites a session's `ConnectionMode` or `WheelHolder`. So a request that reaches the handler
serves exactly what was pushed, and the only way these two tests fail is before the request, in `StartAsync`.
There is no product defect here, and the 13 October rule's exception does not apply: the product is correct.

### What the 13 October rule says about keeping the test

The rule's letter is "a test that fails on correct code is deleted, first time, no repair; what it covered, if it
matters, is new work: a test that cannot depend on the machine". These two tests are the only proof in the suite
that the real `GET /sessions` route serves the two fields unchanged to a client; the other four tests in the class
prove the wire decoding and the clone, not the route. So the coverage matters, and the new test the rule asks for
is this class with a database of its own, which is what the pull request is. The fleet has already applied the
rule this way twice (`ApiModeVerbEndpointTests` in #4082, `PushApiModeEventsHubTests`). I read the pull request as
the rewrite the rule calls for, not a repair of a sensor that lights at random: the test no longer depends on the
machine. Holds.

## 3. Question (b): is the fix right and complete for this class

**Right.** The class gains a `GatewayDbTestHarness` field, builds the registry over `_harness.Open()` with the
legacy import path in the harness directory, and disposes the harness last, after the application and the
Director registry. That is the same shape as `ApiModeVerbEndpointTests`. The registry built this way does not own
its database (`_ownsDatabase` is false on that constructor), the harness does, and the harness disposes every
database it opened and deletes its directory. Dispose order is right: the test server stops first, then the
Director registry, then the database. The four tests that never call `StartAsync` pay nothing for the field: the
harness creates its directory only on `Open`, and `Dispose` on an unopened harness finds nothing to close and no
directory to delete.

**Complete for this class.** After the change the class has no use of the parameterless `DeviceRegistry`, no
default `CcStorage` path of its own, and no static state. The one process-wide path left is the diagnostics
store `Map` builds (finding 3), which is the route map's doing, not the class's, and is a read of an absent file
in this suite.

The fully qualified type name `CcDirector.Gateway.Tests.Data.GatewayDbTestHarness` saves a `using` line for
the harness namespace (the test sits in `CcDirector.Gateway.UnitTests.Api`; `SessionKeyErrorReportTests` imports
the namespace and uses the short name). Either is fine, and it mirrors `ApiModeVerbEndpointTests`.

## 4. Findings

Numbered, with a severity and a concrete failure case each. None blocks the merge.

### Finding 1 (low): the stated mechanism explains one failing test per class per run, and the brief says two failed

**What.** The pull request names the marker collision as the cause and says the two `GET /sessions` tests "once
failed when run beside other classes". Within one class xunit runs tests one after another, each on a fresh
instance. In the marker race the loser throws only after the winner's marker is committed (SQLite cannot report a
UNIQUE violation against an uncommitted row; it waits, the winner commits, then the loser's insert fails). So the
second `GET /sessions` test, starting after the first one threw, runs `Import` against a file that already holds
the marker, and `Import` is a no-op. Under this mechanism the second test passes.

**Concrete case.** Thread 1 runs `SessionStateEventEmitterTests`, thread 2 runs this class. Both construct the
parameterless registry inside the first second of the process; thread 1 commits the marker first; thread 2's
`GetSessions_APushedApiModeSession_...` throws in `StartAsync`. Then thread 2 starts
`GetSessions_APushFromAnOlderDirector_...`: `Migrate` finds nothing pending, `Import` finds the marker, the test
passes. One failure in the class, not two.

**What it means.** Either the record of "two failed" is loose (one failed and the other was counted with it), or
the failure was a sibling shape on the same shared file whose window covers both tests, for example a failure
inside `Migrate` while another class's first construction held the file, or a `database is locked` from the
write-ahead-logging pragma. Every one of those shapes lives on the same file the class no longer opens, so the fix
covers them equally and the verdict does not depend on which it was. What changes is the claim: the pull request
body should say the exact exception for this class's failure is not known and that the file, not one specific
insert, is the thing removed. The Tech Lead may be able to settle it by looking at the run that failed: if both
tests failed, the text was not the marker text.

**Developer's answer.** Accepted, by the Delivery Lead's ruling. The pull request body now says plainly that the
exact exception behind the original failure is unknown, because it was never recorded, and that the fix removes
the shared database file from this class, so it covers the marker collision and every sibling shape on that file
alike. The body no longer claims the marker text as the failure's own text.

### Finding 2 (low): the house helper for this exact line exists and is not used

**What.** `GatewayDbTestHarnessDevices.OpenDevices(this GatewayDbTestHarness harness, string legacyFile = "devices.json")`
is `new(harness.Open(), harness.LegacyPath(legacyFile))`, in the harness's own namespace
`CcDirector.Gateway.Tests.Data`, written for the fifteen classes that build a registry this way. The change spells the expression out instead, as
`ApiModeVerbEndpointTests` did in #4082.

**Concrete case.** None that fails: the two are the same call. The cost is that a later change to how a harness
registry is built (say the legacy file name, or a tenant argument) reaches `OpenDevices` and not these two
inlined copies. `_harness.OpenDevices()` is one call and reads as what it is. The Developer decides; I would not
hold the merge for it.

**Developer's answer.** Accepted, by the Delivery Lead's ruling. The registry line is now
`_harness.OpenDevices()`; the class imports `CcDirector.Gateway.Tests.Data` and names the harness by its short
name, and the `CcDirector.Gateway.Pairing` import it no longer uses is gone.

### Finding 3 (low): the route map still opens one process-wide file, outside this class's control

**What.** `GatewayEndpoints.Map` builds `new NetDiagResultStore(Path.Combine(CcStorage.Root(), "diagnostics-results.json"))`
unconditionally, so every test class that maps the Gateway's routes reads that one file at the process's test
root, and `Load` catches only `JsonException`. In this suite nobody writes the file at that path, so the read
finds nothing and returns; the class is safe today.

**Concrete case.** If a future unit test posts to `/diag/result` on a server mapped over the default root, its
`Save` writes that file while another class's `Map` is reading it. A read that lands on a partial write throws
inside `Map` (an `IOException` is not caught, and a `JsonException` is caught by moving the file aside, which is
a mutation of another test's state). Both `GET /sessions` tests in this class would then fail in `StartAsync`
with a text that has nothing to do with sessions. This is not this pull request's to fix, and it is not the cause
of the failure under review (no such writer exists). It is recorded so that the next "failed beside other classes,
passed alone" in a class that maps the routes is not re-diagnosed from scratch. The fix, when it is wanted, is a
`netDiagResults` parameter on `Map`, as `pushedSessions` already is.

**Developer's answer.** Noted, no change, by the Delivery Lead's ruling: it is outside this pull request. This
review is the record of it, including the fix when it is wanted (a `netDiagResults` parameter on `Map`).

## 5. Observations (not findings)

- The three partner classes the Developer ran beside this one, `SessionKeyErrorReportTests`,
  `SessionStateEventEmitterTests` and `TenantScopedSweepTests`, each already own a `GatewayDbTestHarness` and
  still build the parameterless `DeviceRegistry` beside it. They remain the collision partners for the other
  eleven classes. The phase 2 receipts review already lists all fifteen; that list is now fourteen. Out of this
  pull request's scope, as the Tech Lead instructed ("changing nothing else").
- The pull request body's reproduction section is the right shape: it says what it ran, what it saw, and what the
  proof does not cover. Finding 1 is the one place it claims a little more than reading supports.
- No assistant attribution in the commit message or the pull request body.
