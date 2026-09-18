# The step-table consistency test - what it does, and the proof it can fail

Written by the Developer on 18 September 2026, on SOREN_NORTH, in the worktree
`D:\ReposFred\devthrottle.worktrees\wt04`, on branch `method/workflow-consistency-test` cut from
`origin/method/workflow-four-seats` at commit `610ed9dfe`.

This answers one finding from the independent review of pull request 3092: the mission workflow's
five steps are written twice and nothing held the two copies together.

- `src/CcDirector.Gateway/Workflows/BuiltInWorkflows.cs` - a `WorkflowStep` record per step. This is
  what the Gateway and the Cockpit show.
- `src/CcDirector.Gateway/Workflows/Content/mission.instructions.md` - a five-row markdown table.
  This is what an agent is served when it runs `cc-devthrottle workflow instructions mission`.

Replacing a doer or a reviewer in one of them left the whole suite green while the Cockpit
advertised a different mission from the one agents were told to run.

The test is `WorkflowStoreTests.Mission_step_table_in_the_conduct_matches_the_mission_definition`,
in `src/CcDirector.Gateway.UnitTests/WorkflowStoreTests.cs` - the file the deleted fidelity test
lived in, next to the other workflow tests. It reads the markdown out of the **embedded resource**,
through `BuiltInWorkflows.InstructionsFor("mission")`, the same way the neighbouring tests and the
product itself read it. It does not read a file path off disk and it does not hold its own copy of
the table.

## 1. The test passing

```
> dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~WorkflowStoreTests

Test run for D:\ReposFred\devthrottle.worktrees\wt04\src\CcDirector.Gateway.UnitTests\bin\Debug\net10.0\CcDirector.Gateway.UnitTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 4 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

Thirteen: the twelve that were already in the class, plus this one. Run by itself so the name is on
the record rather than only a count:

```
> dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~Mission_step_table_in_the_conduct -v n

  Passed CcDirector.Gateway.Tests.WorkflowStoreTests.Mission_step_table_in_the_conduct_matches_the_mission_definition [6 ms]
```

## 2. The test failing on purpose

This is the part that matters. A test nobody has watched fail is not known to be able to fail.

The test was watched failing **twice**, on two different columns, because one mutation only proves
the column it touched. Both mutations were made in `BuiltInWorkflows.cs` - the markdown was never
edited - and both were reverted.

### 2a. A doer

Changed the `Build` step's `Doer` from `"Developer"` to `"Tech Lead"`, and nothing else:

```
> dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~WorkflowStoreTests

[xUnit.net 00:00:07.44]     CcDirector.Gateway.Tests.WorkflowStoreTests.Mission_step_table_in_the_conduct_matches_the_mission_definition [FAIL]
  Failed CcDirector.Gateway.Tests.WorkflowStoreTests.Mission_step_table_in_the_conduct_matches_the_mission_definition [8 ms]
  Error Message:
   The mission workflow's step table and its C# definition disagree.
  Row 3 ("Build"), column "Doer".
  BuiltInWorkflows.cs:      "Tech Lead"
  mission.instructions.md:  "Developer"
Both are served - the Cockpit shows the record, an agent is served the table - so a change to either one has to be made in the other.
  Stack Trace:
     at CcDirector.Gateway.Tests.WorkflowStoreTests.AssertCell(Int32 rowNumber, String stepName, String column, String definition, String table) in D:\ReposFred\devthrottle.worktrees\wt04\src\CcDirector.Gateway.UnitTests\WorkflowStoreTests.cs:line 312
   at CcDirector.Gateway.Tests.WorkflowStoreTests.Mission_step_table_in_the_conduct_matches_the_mission_definition() in D:\ReposFred\devthrottle.worktrees\wt04\src\CcDirector.Gateway.UnitTests\WorkflowStoreTests.cs:line 301

Failed!  - Failed:     1, Passed:    12, Skipped:     0, Total:    13, Duration: 6 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

The twelve other tests in the class stayed green, so the mutation was caught by this test and not by
a bystander.

### 2b. A word in a "done when" line

Restored the doer, then changed one word in the `Land the record` step's `Done` - `merged` to
`pushed` - and nothing else:

```
> dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~WorkflowStoreTests

[xUnit.net 00:00:06.34]     CcDirector.Gateway.Tests.WorkflowStoreTests.Mission_step_table_in_the_conduct_matches_the_mission_definition [FAIL]
  Failed CcDirector.Gateway.Tests.WorkflowStoreTests.Mission_step_table_in_the_conduct_matches_the_mission_definition [5 ms]
  Error Message:
   The mission workflow's step table and its C# definition disagree.
  Row 4 ("Land the record"), column "Done when".
  BuiltInWorkflows.cs:      "The mission's record is pushed to the main branch."
  mission.instructions.md:  "The mission's record is merged to the main branch."
Both are served - the Cockpit shows the record, an agent is served the table - so a change to either one has to be made in the other.
  Stack Trace:
     at CcDirector.Gateway.Tests.WorkflowStoreTests.AssertCell(Int32 rowNumber, String stepName, String column, String definition, String table) in D:\ReposFred\devthrottle.worktrees\wt04\src\CcDirector.Gateway.UnitTests\WorkflowStoreTests.cs:line 312
   at CcDirector.Gateway.Tests.WorkflowStoreTests.Mission_step_table_in_the_conduct_matches_the_mission_definition() in D:\ReposFred\devthrottle.worktrees\wt04\src\CcDirector.Gateway.UnitTests\WorkflowStoreTests.cs:line 303

Failed!  - Failed:     1, Passed:    12, Skipped:     0, Total:    13, Duration: 6 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

Each message names the row by number **and** by step name, names the column, and prints both sides.
That is the whole point of writing the assertion by hand: `Assert.Equal() Failure` on its own would
cost the next reader exactly the time this test exists to save.

### 2c. Both mutations were reverted

Both were undone with `git checkout -- src/CcDirector.Gateway/Workflows/BuiltInWorkflows.cs`. The
file is clean, against both the working tree and the commit:

```
> git status --short
?? MANDATE-DEVELOPER-CONSISTENCY-TEST.md

> git diff --stat
(no output)

> git diff --stat HEAD
(no output)
```

`BuiltInWorkflows.cs` appears in neither, so it carries no change of mine. The one untracked file is
this Developer's own mandate, left where the session that opened it put it; it is not part of this
change.

The restore run below was a **full build**, not `--no-build`. A `git checkout` restores the source
and not the compiled assembly, so a `--no-build` run after a revert certifies the mutated binary and
can report a false green:

```
> dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~WorkflowStoreTests

Test run for D:\ReposFred\devthrottle.worktrees\wt04\src\CcDirector.Gateway.UnitTests\bin\Debug\net10.0\CcDirector.Gateway.UnitTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 4 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

## 3. The whole suite

```
> dotnet test src/CcDirector.Gateway.UnitTests

Failed!  - Failed:     9, Passed:  6325, Skipped:     2, Total:  6336, Duration: 3 m 28 s - CcDirector.Gateway.UnitTests.dll (net10.0)
```

The nine that failed, every one of them named:

| # | Test | On `BASELINE.md`? |
|---|---|---|
| 1 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_that_omits_the_tenant_is_refused` | yes, number 3 |
| 2 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_whose_tenant_is_only_whitespace_is_refused` | yes, number 4 |
| 3 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "system")` | yes, number 5 |
| 4 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "local")` | yes, number 6 |
| 5 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "9f2c1b7e-4d3a-4c5e-8b6f-0a1d2e3f4a5b")` | yes, number 7 |
| 6 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.A_tenant_with_whitespace_inside_an_otherwise_legal_value_is_refused` | yes, number 8 |
| 7 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_whose_tenant_is_empty_is_refused` | yes, number 9 |
| 8 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.Every_character_dotnet_calls_whitespace_is_refused_as_a_tenant` | yes, number 10 |
| 9 | `CcDirector.Gateway.Tests.TurnsVerbUnresolvedTranscriptTests.Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk(agent: Grok)` | yes, number 2 |

**No test failed that is not on the baseline list.** The tenth baseline failure,
`FleetOutcomeStoreTests.Answer_ConcurrentCallersOnTwoInstances_ExactlyOneWins`, passed in this run -
which is what `DELIVERY-LEAD-CHECK.md` and `DEVELOPER-CHECK.md` already record for this branch tip,
both of which failed nine of the ten.

The counts line up exactly, which is the check worth doing on a number that could otherwise hide a
test that never ran:

| Run | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Branch tip, from `DELIVERY-LEAD-CHECK.md` | 6335 | 6324 | 9 | 2 |
| This branch | 6336 | 6325 | 9 | 2 |

One test more, one pass more, the same nine failures. That one test is this one.

## What this test does NOT cover

Written down so the next reader spends their scepticism somewhere useful.

- **It is four columns of five rows, and nothing else.** It says nothing about the prose around the
  table, the step `Description` field (which the table has no column for), the workflow's summary,
  when-to-use or human-checkpoint text, or the other three built-in workflows. Each of those can
  still drift freely.
- **It is not the byte-for-byte fidelity test this change deleted**, and it is not meant to be. That
  one held two copies of a whole rules document equal. Recreating it was explicitly out of scope.
- **It holds the two copies EQUAL. It does not say either one is right.** Editing both in the same
  wrong way passes, exactly as it should - that is a review's job, not a test's.
- **The escape handling is exercised by construction, not by a case.** The parser splits on
  unescaped pipes and unescapes `\|`, because that is the one character a table cell must escape;
  today's table contains no escaped pipe, so that path is reasoned about rather than measured.
- **The run above is one suite on one machine.** It is not the release gate
  (`.\scripts\test-local.ps1 -Parked -Configuration Release`), it covers no web or Python tests, and
  `CcDirector.Gateway.UnitTests` is parked out of the default local gate - so a green default run
  says nothing about this test. It has to be asked for.
