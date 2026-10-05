# Test runs - devthrottle_internal#2305, Gateway part

All runs on SOREN_NORTH, 4 October 2026, with `MSBUILDDISABLENODEREUSE=1`, one run at a time. They ran after the
review fixes, on the code of both pull requests stacked together (this one and the read route on top of it), rebased
on main at `738b6a8de`. The machine was busy with other sessions' gate runs throughout, and that shaped the runs below.

## The local gate

`.\scripts\test-local.ps1`. First run: seven suites Completed and three red, one test each, all in Director code this
change does not touch:

- `SendWaitNoticeTests.SendTextAsync_PasteTakenInSlowly_TheWaitSaysWhatItWaitsFor` (Core) - a wait-timing assertion
- `GitChangesViewHiddenTabTests.WhileTheHostPanelIsHidden_NeitherTickRuns_AndNothingIsFetched` (Avalonia) - a file
  held by another process
- `TerminalAttachReplayTests.Attach_WindowResizedAtTheReplayEndBeforeAnyTailByte_TheTailIsParsedAtTheNewSize`
  (Terminal) - a regular-expression timeout

Second run, unchanged code: Avalonia and Terminal Completed; Core was stopped by the two-minute ceiling. Core run on its
own: `Passed! - Failed: 0, Passed: 1221`.

```
CcDirector.Core.UnitTests                1221 passed (run on its own, see above)
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

`dotnet test src\CcDirector.Gateway.UnitTests` no longer finishes inside the ten-minute limit on this machine while
other gates run, so it ran in three parts by folder, which together cover every test:

```
Wingman, History, Factory, Data      Failed: 5, Passed: 1437, Skipped: 1, Total: 1443
Every other folder                   Failed: 0, Passed: 2347, Skipped: 9, Total: 2356
Every test outside those folders     Failed: 0, Passed: 4934, Skipped: 0, Total: 4934
```

The five red are `Wingman.OwnedSessionsAreNotReadTests`, open as devthrottle#3534: they fail whenever that class runs
without the rest of the suite. **The same five fail on main's own commit `738b6a8de`**, built in a separate worktree and
run with the same filter (`Failed: 5, Passed: 19, Total: 24`). This change does not cause them.

## The Gateway suite, this area

`.\scripts\test-local.ps1 -Gateway`, in three pieces for the same reason:

```
-Filter "FullyQualifiedName~Mentor"     outcome=Completed    total=7     executed=7
-Filter "FullyQualifiedName~Teams"      outcome=Completed    total=61    executed=61
-Filter "FullyQualifiedName~Prompt" (not Teams, not Mentor)
                                        outcome=Completed    total=95    executed=95
```

The Mentor piece includes `TeamMentorSwitchHostTests`: a real Gateway host with the Mentor switch unset has no
writer, and with Teams dark and the Mentor switch set it still has none. With both on, it has one.

`.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Postgres|FullyQualifiedName~Migration"` (the migration
pins move):

```
CcDirector.Gateway.Tests                 outcome=Completed    total=56     executed=52
```

The four not executed are `GatewayDatabaseLivePostgresProofTests.*_OnConfiguredPostgres`, which run only against a
configured live database and are skipped by design. `AddTeamMentorPostgresTests` (apply, one block per person and
week, old session rows untouched, Down removes it all) executed and passed.

## The Cockpit (the read route's pull request)

`npx vitest run` in `packages/client-core` on `src/teams/mentorClient.test.ts`: 36 passed. In `apps/cockpit` on
`src/mentor/MentorView.test.tsx`: 23 passed. `npm run typecheck` for both: clean.

## Red proofs - a test goes red when the rule it guards is broken

Each mutation was built (the build succeeded, so the run tested the mutated binary), run against every Mentor and
Prompt unit test, reverted with `git checkout`, rebuilt, and run green again.

**A prompt's words outside a quote** (review G1) - `MentorBrief.Check` no longer refusing a field that repeats eight
words of a prompt (`if (WordRuns(value).Any(promptRuns.Contains) && value.Length < 0)`):

```
Failed MentorBriefTests.Check_AFreeTextFieldCarryingAPromptsWords_IsRefused(... "workedOn repeated")
Failed MentorBriefTests.Check_AFreeTextFieldCarryingAPromptsWords_IsRefused(... "howItWent repeated")
Failed MentorBriefTests.Check_AFreeTextFieldCarryingAPromptsWords_IsRefused(... "wentBadlyAndWhy repeated")  x2
Failed MentorBriefTests.Check_AFreeTextFieldCarryingAPromptsWords_IsRefused(... "oneThingToTry repeated")
Failed!  - Failed:     5, Passed:   403, Skipped:     1, Total:   409
```

Restored: `Passed!  - Failed:     0, Passed:   408, Skipped:     1, Total:   409`.

**The quote rule** (first round) - `MentorBrief.Check` accepting a quote that is not one of this person's prompts of
this week (`if (!request.PromptsByLabel.ContainsKey(label) && label.Length < 0)`): 4 tests red, green when restored.

**The own-block rule** (the read route's pull request, first round) - the read route serving every block to a
Developer (`.Where(b => true)`): 2 tests red, green when restored.
