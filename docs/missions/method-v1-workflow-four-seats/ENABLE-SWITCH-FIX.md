# The enable-switch assertions - the proof

Mission: "Implement the DevThrottle Method v1", phase 3. Written by the Developer on
18 September 2026, on SOREN_NORTH, in the mission worktree
`D:\ReposFred\devthrottle.worktrees\wt01`, on branch `fix/workflow-enable-switch-asserts`.

Branch cut from `origin/main` at `70a618f6a`, and rebased onto `3893edda2` when `origin/main` moved
under it mid-run (`3893edda2` is documentation only - the report for issue 3029 - and touches no
code this change can reach). Every run below was made at `70a618f6a` or on top of it; the rebase
changed nothing the runs looked at.

## The change

Two assertions in `src/CcDirector.Gateway.Tests/WorkflowEnableSwitchTests.cs`, and nothing else.
`git diff origin/main..HEAD --stat` reports one file, six insertions, three deletions - three of the
insertions are a comment.

```diff
-        // Pinned history is untouchable: a seated run's conduct never disappears under it.
-        Assert.Contains("THE FOUR LAWS", workflows.GetInstructions("mission", version: 1));
+        // Pinned history is untouchable: a seated run's conduct never disappears under it. The
+        // property is "this read resolved to the shipped mission conduct", so it is asserted
+        // against the shipped source itself - never a phrase copied out of it, which rots the
+        // moment the conduct is reworded.
+        Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"), workflows.GetInstructions("mission", version: 1));
```

```diff
-        Assert.Contains("THE FOUR LAWS", workflows.GetInstructions("mission", version: null));
+        Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"), workflows.GetInstructions("mission", version: null));
```

`BuiltInWorkflows.InstructionsFor` is the accessor the product and the store already read the
shipped body from - `BuiltInWorkflowSeeder` calls it at both of its own call sites, and the
neighbouring project asserts against it in seven places already (`SharedWorkflowLibraryTests`,
`WorkflowAuthoringTests`, `WorkflowCloneTests`, `WorkflowStoreTests`, `WorkflowTenantOverrideTests`).
It reads the embedded `Workflows/Content/mission.instructions.md` resource out of the Gateway
assembly and throws if it is missing. No text is copied into the test.

The test file already had `using CcDirector.Gateway.Workflows;`, so nothing else moved.

---

## 1. The failure, reproduced before the fix

On `origin/main` at `70a618f6a`, with nothing applied:

```
$ git rev-parse HEAD
70a618f6a3730294fb58021108ed1515cdb7fe8e
$ git status --short
?? DEVELOPER-TASK.md

$ dotnet test src/CcDirector.Gateway.Tests --filter FullyQualifiedName~WorkflowEnableSwitchTests
```

```
[xUnit.net 00:00:08.62]     CcDirector.Gateway.Tests.WorkflowEnableSwitchTests.Off_refuses_new_runs_and_the_flip_back_restores_everything [FAIL]
  Failed CcDirector.Gateway.Tests.WorkflowEnableSwitchTests.Off_refuses_new_runs_and_the_flip_back_restores_everything [5 s]
  Error Message:
   Assert.Contains() Failure: Sub-string not found
String:    "# How a mission runs\n\nA mission is a body"...
Not found: "THE FOUR LAWS"
  Stack Trace:
     at CcDirector.Gateway.Tests.WorkflowEnableSwitchTests.Off_refuses_new_runs_and_the_flip_back_restores_everything() in D:\ReposFred\devthrottle.worktrees\wt01\src\CcDirector.Gateway.Tests\WorkflowEnableSwitchTests.cs:line 78

[xUnit.net 00:00:09.14]     CcDirector.Gateway.Tests.WorkflowEnableSwitchTests.Off_refuses_the_default_conduct_read_with_a_clear_message_but_pinned_reads_resolve [FAIL]
  Failed CcDirector.Gateway.Tests.WorkflowEnableSwitchTests.Off_refuses_the_default_conduct_read_with_a_clear_message_but_pinned_reads_resolve [200 ms]
  Error Message:
   Assert.Contains() Failure: Sub-string not found
String:    "# How a mission runs\n\nA mission is a body"...
Not found: "THE FOUR LAWS"
  Stack Trace:
     at CcDirector.Gateway.Tests.WorkflowEnableSwitchTests.Off_refuses_the_default_conduct_read_with_a_clear_message_but_pinned_reads_resolve() in D:\ReposFred\devthrottle.worktrees\wt01\src\CcDirector.Gateway.Tests\WorkflowEnableSwitchTests.cs:line 60

Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 6 s - CcDirector.Gateway.Tests.dll (net10.0)
```

Both failures are exactly the ones the task named, at the two lines it named, with the message it
quoted. The diagnosis holds.

The filter matches four tests, not seven: `FullyQualifiedName~WorkflowEnableSwitchTests` does not
match `WorkflowEnableSwitchEndpointTests`, which is a different class in the same file. That matters
for section 4 below, where those three endpoint tests do run and do fail, for an unrelated reason.

## 2. The same command after the fix

```
$ dotnet test src/CcDirector.Gateway.Tests --filter FullyQualifiedName~WorkflowEnableSwitchTests

Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 4 s - CcDirector.Gateway.Tests.dll (net10.0)
```

## 3. The fix watched failing on purpose

### 3a. Changing a word in the conduct - and why it PASSES, which is the point

The task asked for one word changed in
`src/CcDirector.Gateway/Workflows/Content/mission.instructions.md`. That mutation does **not** make
the new assertion fail, and it must not.

```
$ head -1 src/CcDirector.Gateway/Workflows/Content/mission.instructions.md
# How a mission trundles

$ dotnet test src/CcDirector.Gateway.Tests --filter FullyQualifiedName~WorkflowEnableSwitchTests

Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 4 s - CcDirector.Gateway.Tests.dll (net10.0)
```

This is not a hole and it is not a stale build. Both sides of the equality read the same shipped
resource: `InstructionsFor` reads it out of the rebuilt assembly, and the store is seeded from it.
When the conduct is reworded they move together, which is **precisely the rot-immunity the fix is
for** - a reword is what broke the old `Assert.Contains`, and that is why `main` is red.

The build was real and the mutation reached the binary rather than sitting only in the source:

```
$ grep -c "How a mission trundles" src/CcDirector.Gateway/bin/Debug/net10.0/CcDirector.Gateway.dll
1
```

So the green run above is a genuine run of a genuinely mutated assembly. An assertion that a reword
cannot break is only worth anything if a **wrong read** can break it, which is 3b.

### 3b. Breaking what the assertion actually guards

The property under test is "this read resolved to the shipped mission conduct". The mutation that
falsifies it is one that makes the read resolve to something else. Reverting 3a first, then a
one-line change in the product, in `src/CcDirector.Gateway/Workflows/WorkflowStore.cs`:

```diff
@@ -621,7 +621,7 @@ public sealed class WorkflowStore
                         "history remains readable by explicit version, and it can be re-enabled in " +
                         "the cockpit or with: cc-devthrottle workflow enable " + key);
             }
-            var row = ResolveVersionRow(ctx, key, version);
+            var row = ResolveVersionRow(ctx, "standalone", version);
             return row?.InstructionsMarkdown;
         }
     }
```

The read now returns another workflow's conduct - the kind of defect the enable switch could hide,
since a wrong body is still a body and the refusal path is untouched. Both new assertions catch it,
and they name the document that came back:

```
[xUnit.net 00:00:10.52]     CcDirector.Gateway.Tests.WorkflowEnableSwitchTests.Off_refuses_new_runs_and_the_flip_back_restores_everything [FAIL]
  Failed CcDirector.Gateway.Tests.WorkflowEnableSwitchTests.Off_refuses_new_runs_and_the_flip_back_restores_everything [8 s]
  Error Message:
   Assert.Equal() Failure: Strings differ
                 v (pos 6)
Expected: "# How a mission runs\r\n\r\nA mission is a bo"...
Actual:   "# How standalone work runs\r\n\r\nOne agent p"...
                 ^ (pos 6)
  Stack Trace:
     at CcDirector.Gateway.Tests.WorkflowEnableSwitchTests.Off_refuses_new_runs_and_the_flip_back_restores_everything() in ...\WorkflowEnableSwitchTests.cs:line 81

[xUnit.net 00:00:10.99]     CcDirector.Gateway.Tests.WorkflowEnableSwitchTests.Off_refuses_the_default_conduct_read_with_a_clear_message_but_pinned_reads_resolve [FAIL]
  Failed CcDirector.Gateway.Tests.WorkflowEnableSwitchTests.Off_refuses_the_default_conduct_read_with_a_clear_message_but_pinned_reads_resolve [154 ms]
  Error Message:
   Assert.Equal() Failure: Strings differ
                 v (pos 6)
Expected: "# How a mission runs\r\n\r\nA mission is a bo"...
Actual:   "# How standalone work runs\r\n\r\nOne agent p"...
                 ^ (pos 6)
  Stack Trace:
     at CcDirector.Gateway.Tests.WorkflowEnableSwitchTests.Off_refuses_the_default_conduct_read_with_a_clear_message_but_pinned_reads_resolve() in ...\WorkflowEnableSwitchTests.cs:line 63

Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 8 s - CcDirector.Gateway.Tests.dll (net10.0)
```

Both assertions have now been watched failing, at both call sites, on the property they are for.
The other two tests in the class stayed green, so the mutation was caught by the assertions that
were changed rather than by the whole class collapsing.

### 3c. Revert, clean, and a FULL rebuild

```
$ git checkout -- src/CcDirector.Gateway/Workflows/WorkflowStore.cs
$ git status --short
?? DEVELOPER-TASK.md
$ git diff HEAD --stat
(no output - no tracked change against HEAD)
```

The only thing `git status` reports is the untracked `DEVELOPER-TASK.md` this Developer was opened
with. It is not part of the change and is not committed.

Re-run as a full build, not `--no-build` - a `git checkout` restores the source and not the compiled
assembly, and a `--no-build` run would have certified the mutated one:

```
$ dotnet test src/CcDirector.Gateway.Tests --filter FullyQualifiedName~WorkflowEnableSwitchTests
  CcDirector.Gateway -> ...\CcDirector.Gateway\bin\Debug\net10.0\CcDirector.Gateway.dll
  CcDirector.Gateway.Tests -> ...\CcDirector.Gateway.Tests\bin\Debug\net10.0\CcDirector.Gateway.Tests.dll

Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 5 s - CcDirector.Gateway.Tests.dll (net10.0)
```

The two assembly lines in that output are the rebuild. The fix was committed **before** either
mutation, so no `git checkout` in this section could have eaten it.

## 4. The whole `CcDirector.Gateway.Tests` project - NOT COMPLETED, and why

**This is the one thing asked of me that I did not deliver. It is not a result I am reporting as a
pass or a fail; it is a run that never finished.**

```
$ dotnet test src/CcDirector.Gateway.Tests
```

It was **killed by this session's own harness at 45 minutes 56 seconds of test execution because this
machine ran critically low on memory.** It never printed a summary line, so there is no
`Failed!`/`Passed!` total to quote and no complete list of failing tests. The harness's own notice
says not to restart it unprompted, because memory may still be short, so I have not. At the time of
the kill there were 52 `dotnet.exe` processes on the machine from other sessions, and 11 percent of
physical memory was free.

What the partial log shows, and what it is worth:

- 399 failures had been reported by 45:56, in a run that was not finished.
- The dominant error signatures are infrastructure, not product assertions: 201
  `System.Net.Http.HttpRequestException : Response status code does not indicate success`, 27
  `Npgsql.PostgresException : 28P01: password authentication failed for user "postgres"`, 21
  `System.TimeoutException : Timed out waiting for the Director stream`.
- `FleetManagerRoutesHostTests.The_session_list_pins_the_Fleet_Manager_and_offers_each_row_its_change_of_owner`,
  the hand-over failure I was told to expect and leave alone, does not appear in the partial log at
  all - neither passing nor failing. **I therefore cannot confirm or deny it, and I have not touched
  it.**

**The deeper point, which matters more than the kill.** A raw `dotnet test` is not the supported way
to run this project, and its number would not have meant much even had it finished.
`scripts/test-local.ps1` lists `src\CcDirector.Gateway.Tests` in its `$postgresProjects`: the
supported run **builds a throwaway PostgreSQL, hands the test processes its connection strings, and
destroys it afterwards**, deliberately overriding whatever the machine has inherited. A bare
`dotnet test` gives the suite no database at all, which is what the 27 Npgsql authentication
failures are. The suite is also host-bound and is parked out of the default gate for that reason; it
takes a machine-wide lock under the script, and a raw invocation takes none, so it was racing
whatever the other 52 processes were doing.

The three `WorkflowEnableSwitchEndpointTests` cases in the same file are a clean example of what
that does to a reading. They failed like this:

```
  Failed CcDirector.Gateway.Tests.WorkflowEnableSwitchEndpointTests.Unknown_workflow_switch_is_a_404 [2 ms]
  Error Message:
   Assert.Equal() Failure: Values differ
Expected: NotFound
Actual:   Unauthorized
```

`Unauthorized` on an unknown-workflow 404 check is the test host refusing the bearer token - the
Gateway host did not come up properly under the contention. It is not a product verdict, and it is
not reachable from the two assertions I changed, which are in the other class in that file and touch
no HTTP.

**What it would take to get a real answer:** Docker running, a quiet machine, and
`.\scripts\test-local.ps1 -Parked`. I checked Docker: `com.docker.backend.exe` is alive but
`docker version` did not answer within 120 seconds, so the engine was not usable either. **This is
the piece of the check I am handing back rather than dressing up.**

What I can say about my own change's blast radius, and it is not a substitute for the run: the
committed diff against `origin/main` is one file, and within it two assertions inside
`WorkflowEnableSwitchTests`. No product code and no other test file is touched, and the four tests
in that class pass.

## 5. `CcDirector.Gateway.UnitTests`, test by test against `BASELINE.md`

```
$ dotnet test src/CcDirector.Gateway.UnitTests

Failed!  - Failed:     9, Passed:  6375, Skipped:     2, Total:  6386, Duration: 3 m 59 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

The nine, named in full and checked one at a time against `BASELINE.md` beside this file:

| # | Test | On the baseline? |
|---|---|---|
| 1 | `Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_that_omits_the_tenant_is_refused` | yes, number 3 |
| 2 | `Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_whose_tenant_is_only_whitespace_is_refused` | yes, number 4 |
| 3 | `Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "system")` | yes, number 5 |
| 4 | `Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "local")` | yes, number 6 |
| 5 | `Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "9f2c1b7e-4d3a-4c5e-8b6f-0a1d2e3f4a5b")` | yes, number 7 |
| 6 | `Stats.HostedSchemaRefusesAnUnownedRowTests.A_tenant_with_whitespace_inside_an_otherwise_legal_value_is_refused` | yes, number 8 |
| 7 | `Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_whose_tenant_is_empty_is_refused` | yes, number 9 |
| 8 | `Stats.HostedSchemaRefusesAnUnownedRowTests.Every_character_dotnet_calls_whitespace_is_refused_as_a_tenant` | yes, number 10 |
| 9 | `WorkspaceCapturedSeatsAreImmutableTests.An_update_that_keeps_the_captured_origin_but_omits_the_Director_is_accepted` | **no** - see below |

**The bar is "no failure outside the named list", and number 9 is outside it, so it does not get
waved through.** It is a database-backed fixture flake under the parallel run, and I ran it alone
before calling it anything:

```
$ dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~WorkspaceCapturedSeatsAreImmutableTests"

Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15, Duration: 4 s
```

Its failure message is the fixture, not an assertion - the shared SQLite harness handed it a
disposed handle:

```
System.InvalidOperationException : The Gateway SQLite database at
'C:\Users\soren\AppData\Local\Temp\cc-gateway-db-tests-5993ab05994046d48c1fd3d258acca04\gateway.db'
could not be opened or migrated: Cannot access a disposed object.
Object name: 'SQLitePCL.sqlite3'.
---- System.ObjectDisposedException : Cannot access a disposed object.
   at CcDirector.Gateway.Data.GatewayDatabase.Open()
   at CcDirector.Gateway.Tests.Data.GatewayDbTestHarness.Open(ITenantContext tenant)
   at CcDirector.Gateway.Tests.WorkspaceCapturedSeatsAreImmutableTests.NewStore()
```

The stack never reaches the test body, so nothing was asserted, and the test is in a file this
change does not touch. It passes alone, fifteen tests in four seconds.

**Two baseline entries did not fail, and both are accounted for rather than assumed:**

- Baseline number 1, `Fleet.FleetOutcomeStoreTests.Answer_ConcurrentCallersOnTwoInstances_ExactlyOneWins`,
  passed. `BASELINE.md` records it as a concurrency test across two store instances, and phase 3's
  `DEVELOPER-CHECK.md` records it going both ways on this machine.
- Baseline number 2, `TurnsVerbUnresolvedTranscriptTests.Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk(agent: Grok)`,
  passed. It was **fixed on main** in `561452cf4` (#3106) after the baseline was measured. That is
  also why the totals have moved: the baseline was taken at `c135d44b2` with 6336 tests, and this
  run sees 6386.

**A correction to what I was told.** The task said `BASELINE.md` "records that one database-backed
test can flake per full parallel run and pass alone". It does not say that in those words. What it
says is that eight of its ten are a PostgreSQL fixture and one is a concurrency test across two
store instances, which "points at a database-backed fixture rather than at the product"; the
run-it-alone practice comes from `DEVELOPER-CHECK.md`. The substance was right and I followed it - I
am recording the difference so the next seat does not go looking for a sentence that is not there.

## What this proof does NOT cover

- **The whole `CcDirector.Gateway.Tests` project.** Section 4. The run was killed and there is no
  total. This is the gap, and it is the reason this proof is not complete.
- **One machine, one configuration.** SOREN_NORTH, Windows, Debug. Nothing was run on Linux or
  macOS, and nothing was run in Release.
- **Anything outside those two projects.** No web tests, no Python tests, no `Core.UnitTests`, and
  no `scripts/test-local.ps1` gate run of any kind.
- **The hand-over failure I was told to leave alone.** I left it alone, and I also never saw it,
  passing or failing. I am not claiming it is still there; I am saying I did not reach it.
