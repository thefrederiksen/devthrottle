# Test runs - devthrottle_internal#2305, Gateway part

All runs on SOREN_NORTH, 4 October 2026, with `MSBUILDDISABLENODEREUSE=1`, one run at a time, on the code of this pull
request together with its stacked read route (pull request 2); main has moved since only by an unrelated tool.

## The local gate

`.\scripts\test-local.ps1` - green, every suite `outcome=Completed`, executed equals total:

```
CcDirector.Core.UnitTests                outcome=Completed    total=1221   executed=1221
CcDirector.Avalonia.Tests                outcome=Completed    total=892    executed=892
CcDirector.Engine.Tests                  outcome=Completed    total=68     executed=68
CcDirector.HostedAgent.Tests             outcome=Completed    total=88     executed=88
CcDirector.Launcher.Tests                outcome=Completed    total=197    executed=197
CcDirector.Terminal.Avalonia.Tests       outcome=Completed    total=82     executed=82
CcDirector.Reclaim.Tests                 outcome=Completed    total=310    executed=310
cc-director-setup.Tests                  outcome=Completed    total=25     executed=25
cc-director-setup-engine.Tests           outcome=Completed    total=676    executed=676
cc-director-setup-cli.Tests              outcome=Completed    total=35     executed=35
```

## The Gateway unit suite (parked from the default run)

`dotnet test src\CcDirector.Gateway.UnitTests`:

```
Passed!  - Failed:     0, Passed:  8655, Skipped:    10, Total:  8665
```

## The Gateway suite, this area

`.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Teams|FullyQualifiedName~Mentor|FullyQualifiedName~Prompt"`:

```
CcDirector.Gateway.Tests                 outcome=Completed    total=133    executed=133
```

`.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Postgres|FullyQualifiedName~Migration"` (the migration
pins move):

```
CcDirector.Gateway.Tests                 outcome=Completed    total=55     executed=51
```

The four not executed are `GatewayDatabaseLivePostgresProofTests.*_OnConfiguredPostgres`, which run only against a
configured live database and are skipped by design. The moved PostgreSQL pins (`TurnVerdictAnswerChoicePostgresTests`,
`FleetOutcomeStopIdentityPostgresTests`) executed and passed.

`AddTeamMentorPostgresTests` (apply, one block per person and week, old session rows untouched, Down removes it all):

```
CcDirector.Gateway.Tests                 outcome=Completed    total=2      executed=2
```

## Red proofs - a test goes red when the rule it guards is broken

Each mutation was built (the build succeeded, so the run tested the mutated binary), run, reverted with
`git checkout`, rebuilt, and run green again.

**The quote rule** - `MentorBrief.Check` accepting a quote that is not one of this person's prompts of this week
(`if (!request.PromptsByLabel.ContainsKey(label) && label.Length < 0)`):

```
Failed MentorBriefTests.Check_AnAnswerNotTheShapeAskedFor_IsRefused_WithAReason(... "not one of this person's prompts")  x2
Failed TeamMentorWriterTests.WriteWeekAsync_AnswerNamingAnotherPersonsPromptId_IsRefused_AndWritesNoBlock
Failed TeamMentorWriterTests.WriteWeekAsync_APromptFromAnotherWeek_IsNeverShown_AndCannotBeQuoted
Failed!  - Failed:     4, Passed:    96, Skipped:     1, Total:   101
```

Restored: `Passed!  - Failed:     0, Passed:   100, Skipped:     1, Total:   101`.

**The own-block rule** (pull request 2) - the read route serving every block to a Developer (`.Where(b => true)`):

```
Failed TeamMentorEndpointsTests.Read_ADeveloperWithNoBlockThatWeek_GetsNone_NotSomeoneElses
Failed TeamMentorEndpointsTests.Issue2305Test1_ADevelopersPage_HoldsOnlyTheirOwnBlock_WordForWordTheManagersCopy
Failed!  - Failed:     2, Passed:    98, Skipped:     1, Total:   101
```

Restored: `Passed!  - Failed:     0, Passed:   100, Skipped:     1, Total:   101`.
