# Review - phase E live fixes (pull request 3597)

Reviewed by a separate review session, 6 October 2026. Read only; nothing was edited.

## Scope

- Worktree `D:\ReposFred\_wt\factories-screen-review-e`, HEAD `65c584f51` on branch `factories-screen/live-fixes`,
  against `origin/main` at `00a7ca402` (fetched at review time). Two commits, eight files, 231 insertions and 18 deletions.
- Both defects in `BRIEF-developer-live-fixes.md` were checked: the refusal of a factory create to a Director too old
  to carry one, and one time zone per seat row.
- Checked by reading: the version comparison (pre-release, build metadata, missing version, the string the Director
  actually sends), whether a create naming no factory can be refused, whether the refusal can cross a tenant, every
  door a create can take, the time-zone conversion and whether any row still mixes zones, and the tests.
- Ran the three touched unit test classes in this worktree:
  `FactoriesScreenFoldTests`, `FactoryTalkEndpointTests`, `FactoryMembershipThroughTheCallersTests` - 93 passed,
  0 failed, 17 seconds. The two fixture bumps in `CcDirector.Gateway.Tests` (parked suite) were read, not run.
- Not done: no live Director was spoken to and nothing was deployed. The one finding below is proven by reading the
  code path end to end, not by a live reproduction.

## Verdict

**Mergeable, with one gap the Lead should decide on.** The two fixes are correct for what they set out to do, the
threshold version is right, the comparison handles every shape the Director can send, nothing crosses a tenant,
nothing refuses a create that names no factory, and the time-zone rule is applied consistently with tests that pin the
real conversions. One door that carries a factory create to a Director is still unchecked, and a person can reach it
from the command line. It is outside the brief's two defects but inside this review's scope, and the hook to close it
already exists, so it is one small change. Fix it in this pull request or open the issue the moment this merges.

## Finding 1 - the `--machine` spawn door still sends a factory create to a Director that cannot carry it

**Where.** `src/CcDirector.Gateway/Api/MachineEndpoints.cs` lines 469 to 478. `POST /machines/{machine}/sessions`
calls `SpawnFactory.TryEstablish` (which, for a session key, copies the calling session's own factory onto the
create - `SpawnFactory.cs` line 169, `req.Factory = lookup.Factory`) and then hands the create to
`spawner.SpawnOnMachineAsync(machine, req, ct)` with no `refuseDirector`. `MachineSessionSpawner.cs` lines 118 to
122 only refuse when a `refuseDirector` is passed; the Fleet Manager placement door passes one
(`FleetManagerPlacementEndpoints.cs` line 279), this door passes none. The new `SpawnFactory.DirectorCannotCarry`
is called from `GatewayEndpoints.DirectorSpawn.cs` lines 78 to 81 only, which is the `/directors/{id}/sessions` door.

**Who reaches it.** `cc-devthrottle session spawn <repo> --machine <name>` (`tools/cc-devthrottle/src/session_ops.py`
lines 2024 to 2044: `--machine` goes to `machines/{machine}/sessions`, line 2227). A person typing that in a factory
seat, or the seat's agent doing so, reaches this door with a session key, and the session key arm of
`TryEstablish` stamps the seat's factory onto the create.

**The harm.** The same defect the live QA found, on a second door. A seat of `warmforward` on SOREN_NORTH running
`cc-devthrottle session spawn D:\some\repo --machine SOREN_NORTH` sends a create with `factory: warmforward` to the
Director on that machine. The installed Director there reports version `2.12.0` today
(`%LOCALAPPDATA%\cc-director\instances\default\config\director\instances\6d4523e2-....json`), and a `2.12.0`
Director has no factory field on a create (`git show v2.12.0:src/CcDirector.ControlApi/SessionWriteExecutor.cs`
contains no "factory"), so it drops the field without a word. The child is born in no factory, can neither read nor
write the factory's memory, and nothing says why - exactly the sentence the agent said on 6 October. The Gateway-side
history row records what the Director pushed back, so the child cannot be put into its factory afterwards either
(the "birth fact" rule in `SpawnFactory`'s header).

**Why it is in scope.** The review instruction excluded the scheduled-run, trigger, continuation and handover creates.
This door is none of those. It is the command-line spawn onto a named computer, which a person reaches directly.

**The fix is small.** `MachineSessionSpawner.SpawnOnMachineAsync` already takes a `Func<string, string?> refuseDirector`
keyed by Director id. The machine door can pass a lambda that looks the id up in the Director registry (the route
already has `boundary` and the tenant in scope) and returns `SpawnFactory.DirectorCannotCarry(req, director)`. The
refusal arrives before the create is sent, as on the other door. One regression test through
`POST /machines/{machine}/sessions` with a session key in a factory and a `2.12.0` Director, asserting no create was
sent and the same sentence, would pin it. Note the machine door returns 502 for a failed spawn today; a refusal before
the create deserves the same 409 the Director door gives, so the command line can tell "refused" from "could not
reach".

## Checked and found correct

**The threshold version.** `FirstDirectorThatCarriesAFactory = 2.13.0` is right. The Director side of the factory
field (the create reads `Factory`, `Session.StampFactory`, the restore and Smart Restart paths) landed in commit
`a85e59533`, and `git tag --contains a85e59533 | sort -V | head -1` is `v2.13.0`. The `v2.12.0` Director has no
such field anywhere (grep count 0 in `SessionWriteExecutor.cs` and `NewSessionRequest.cs` at that tag).

**The version comparison against the real wire.** The Director sends `AppVersion.Semver`
(`src/CcDirector.Avalonia/App.axaml.cs` line 824 -> `ControlApiHost` -> `GatewayStreamClient` line 769 ->
`DirectorHub.Hello` -> `DirectorRegistry.RegisterFromStream` -> `DirectorDto.Version`). That is the `<Version>` from
`Directory.Build.props` with no commit suffix, so today it is exactly `2.12.0` or `2.16.0`. `ReleaseOf` also accepts
a `v` prefix, a `-pre` suffix and `+sha` build metadata, and two-part strings (`Version(2,16)` has `Build = -1`,
clamped to 0). An empty or unparseable version is refused with "a version it does not report", which is the right
answer: a Director whose version cannot be read cannot be trusted to carry the field. The parser is unit tested
through the Talk route with `2.12.0`, `2.12.9+68fd9d75b`, `0.0.0-test`, `""` (all refused) and `2.13.0`, `v2.16.0`,
`3.0.0-rc1` (all sent). A locally built development slot reports `2.16.0`, so development Directors are not
refused.

One observation, no harm shown: a pre-release of the threshold itself (`2.13.0-rc1`) would be treated as carrying.
The repository has never cut a pre-release tag and `Directory.Build.props` has never carried a suffix, so nothing can
send that string today.

**A create naming no factory is never refused.** `DirectorCannotCarry` returns null first thing when
`req?.Factory is null` (`SpawnFactory.cs` line 191), and `TryEstablish` has already folded a blank factory to null.
Every existing fixture that registers a `0.0.0-test` Director and spawns without a factory
(`DirectorSpawnMissionAndSeatTests.cs` line 100, the `OTHER-PC` Director in the Talk tests) depends on this and is
unchanged. There is no test that states it in so many words; see "missing tests" below.

**No cross-tenant leak.** `d` is the result of `TryResolveOwnedDirector(ctx, door.TenantBoundary, door.Registry, id, ...)`,
so a caller only ever gets a verdict, a machine name and a version for a Director of its own account. The sentence
names the machine (or the Director id when the machine name is blank), which the caller can already list.

**The refusal sits before anything is started.** It runs after origin and factory are settled and before the restore
claim check, the mission and seat resolution, and the create itself (`GatewayEndpoints.DirectorSpawn.cs` lines 75
to 108). The Talk tests assert `_sent` is empty on a refusal. A Director restoring its own seats sends the create to
itself, so a `2.13.0` or newer Director's restore is never refused by this check.

**One clock per seat row.** `SeatClock` picks the schedule's zone when the seat's schedules agree on exactly one, and
the account's zone otherwise. It names the zone only when it is not the account's, using the same equality as
`FactoryScheduleText.SameZone` (same id, or `HasSameRules`), so the "When it runs" column and the "Last run" column
on one row name a zone together or not at all - verified by reading both. A schedule's `TimeZoneId` cannot be blank
(`CronSchedule.cs` lines 33 to 36 require it and resolve it on save), so the empty-id filter in `SeatClock` never
changes the answer for a saved schedule. The conversions are pinned with real numbers: 05:00 Toronto on 6 October is
09:00 UTC (`Seats_AScheduleInAnotherZone_...`), and 23:30 Toronto on 5 October is 03:30 UTC on 6 October and reads
"Yesterday" in the seat's clock (`Seats_TheDayIsTheSchedulesDay_NotUtcs`). The status sentence for a failed run and
for a schedule that could not start, and the CEO card (zone in the heading once, lines unnamed), are all in the
seat's clock and tested. When a seat's schedules disagree on a zone, the run time falls back to the account's zone
unnamed next to two named schedule zones; that is what the brief's rule says (the account's zone needs no name) and
it is tested, so it is noted and not counted.

**Page-level items left in the account's zone.** "Waiting on you" items, "Last talk with you" and the goal number
card stay in the account's zone. None of them share a row with a schedule, so no row mixes zones; the owner acts on
them in their own time. Not a defect.

## Missing tests (minor)

- No test says in words that a create naming NO factory still goes to a `2.12.0` Director through the covered door.
  The behaviour is right and is relied on by fixtures in the parked `CcDirector.Gateway.Tests`, but the default gate
  does not run that suite, so the one test guarding against a too-wide refusal is not in the two-minute run. One
  `[Fact]` in `FactoryTalkEndpointTests` or the Cockpit spawn tests would close it.
- No test through `POST /machines/{machine}/sessions` for Finding 1 - it would fail today, which is the point.

## Fixture changes read

`CockpitSpawnIntoAFactoryTests.cs` and `FactoryMemoryEndToEndProof.cs` (parked suite) and
`FactoryMembershipThroughTheCallersTests.cs` register their Director as `2.16.0-test` instead of `0.0.0-test`, which
is necessary because they spawn WITH a factory. `FactoryTriggerHostTests` still uses `9.9.9-test`, which parses as
9.9.9 and is accepted. Correct and minimal.

## Round 2

Reviewed by the same separate review session, 6 October 2026, after the Developer answered round 1.

### Scope

- Worktree HEAD `40332606c` on `factories-screen/live-fixes`, which is now the head of pull request 3597, against
  `origin/main` at `a8dfc6cb3` (fetched at review time). The branch was rebased onto the new main; the round 1 files
  are byte-for-byte what round 1 reviewed (`git diff 65c584f51 HEAD` over them is empty), so only the new commit was
  new reading. Nine files in the pull request, 302 insertions and 21 deletions.
- Read: the new commit in full (`MachineEndpoints.cs`, the `SpawnFactory.DirectorCannotCarry` overload by id, the two
  new tests), the production wiring of the machine door, the Director registry lookup it uses, the machine spawner's
  refusal hook, and the resolver that supplies the Director id.
- Ran the three touched unit test classes at the new head: 97 passed, 0 failed, 16 seconds (four more than round 1,
  the two new theories times two doors).
- Not done: no live Director was spoken to, nothing deployed.

### Verdict

**Approved. Merge.** Finding 1 is closed on the right door with the right status, the two missing tests exist and
cover both doors, and nothing new was introduced.

### Finding 1 - closed

`MachineEndpoints.cs` lines 478 to 489. When the create names a factory, the door hands the spawner a `refuseDirector`
hook that calls the new `SpawnFactory.DirectorCannotCarry(req, directorId, directorOf)` with
`directors.Get(tenant, id)`. The hook runs inside `MachineSessionSpawner.SpawnOnMachineWithOutcomeAsync` after the
machine is resolved and before `_create` is called (lines 118 to 122), so nothing leaves. The door then answers 409
with the sentence and the machine name, ahead of the 502 branch, so "update that Director" and "could not reach that
computer" are different answers to the command line - the point raised in round 1.

Checked around it:

- **Production passes the registry.** `GatewayHost.cs` line 5406 onward passes `directors: Registry`, so the lookup
  is live, not null. A Gateway wired without it refuses a factory create with "cannot read which version", which is
  the fail-closed answer and is only reachable in a harness.
- **The lookup is tenant-scoped.** `DirectorRegistry.Get(TenantId, string)` (line 628) is keyed on the caller's
  tenant, so a Director of another account reads as absent and is refused, never described.
- **The id it is asked about is always registered.** `RegistryDirectorTargetResolver.ResolveAsync` returns a
  Director id only from the registry, including after an auto-launch, where it waits for the launched Director to
  register before returning its id. So the "cannot read" branch is not hit by a machine that just started its Director.
- **A create naming no factory passes `null` as the hook**, so the old path is untouched for it; the resolver,
  the create and the 502 branch are exactly as before.
- **The schedule starter is still deliberately uncovered.** `DirectorCronSessionStarter` calls
  `SpawnOnMachineAsync` with no hook, as the brief and round 1 scoped.

### The two tests - present and real

Both in `FactoryMembershipThroughTheCallersTests`, each a theory over `machine` and `director`, through the real
routes with a real session key and a real history row, against a Director registered as `2.12.0`:

- `A_CHILD_IN_A_FACTORY_IS_REFUSED_409_WHEN_THE_DIRECTOR_IS_TOO_OLD_TO_CARRY_IT_AND_NOTHING_LEAVES` asserts 409, the
  three parts of the sentence, and that the door's capture of the sent create is null. Take the hook off the machine
  door and the `machine` row answers 201 with the factory dropped downstream - red.
- `A_CREATE_NAMING_NO_FACTORY_STILL_GOES_TO_A_DIRECTOR_TOO_OLD_TO_CARRY_ONE` asserts success and a create with a
  null factory reaching the Director. This is the "too-wide refusal" guard round 1 asked for, now in the default
  gate's suite rather than only in the parked one.

The fixture gained a `directorVersion` parameter and now passes `directors: _registry` into `MachineEndpoints.Map`,
matching production.

### Nothing else new

The rebase brought in the Teams work from main underneath the branch, not into the pull request; the pull request's
own diff is the nine files listed. No new door, no change to the time-zone fold, no change to the version parser.
