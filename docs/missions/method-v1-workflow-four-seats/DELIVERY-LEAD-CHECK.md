# The check, re-run by the Delivery Lead at the branch tip

Run 18 September 2026 on SOREN_NORTH, in the mission worktree
`D:\ReposFred\devthrottle.worktrees\wt01`, at commit `550af84f5` on `method/workflow-four-seats` -
the tip that this pull request proposes, including the one comment edit the Developer's run at
`ac182448b` predates.

This is not a copy of `DEVELOPER-CHECK.md`. That file records the Developer's run and is its own
evidence; this one exists because a seat saying "done" is a claim, and the tip being merged is not
the commit that was measured.

Compared test by test against `BASELINE.md`, which the Tech Lead measured on clean `origin/main`
(`c135d44b2`) in a separate worktree.

## `src/CcDirector.Gateway.UnitTests`

```
Failed!  - Failed:     9, Passed:  6324, Skipped:     2, Total:  6335, Duration: 6 m 3 s
```

The nine, read out of the run's own `[FAIL]` lines:

| # | Test | On the baseline? |
|---|---|---|
| 1 | `Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "9f2c1b7e-4d3a-4c5e-8b6f-0a1d2e3f4a5b")` | yes, number 7 |
| 2 | `Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "local")` | yes, number 6 |
| 3 | `Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "system")` | yes, number 5 |
| 4 | `Stats.HostedSchemaRefusesAnUnownedRowTests.A_tenant_with_whitespace_inside_an_otherwise_legal_value_is_refused` | yes, number 8 |
| 5 | `Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_that_omits_the_tenant_is_refused` | yes, number 3 |
| 6 | `Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_whose_tenant_is_empty_is_refused` | yes, number 9 |
| 7 | `Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_whose_tenant_is_only_whitespace_is_refused` | yes, number 4 |
| 8 | `Stats.HostedSchemaRefusesAnUnownedRowTests.Every_character_dotnet_calls_whitespace_is_refused_as_a_tenant` | yes, number 10 |
| 9 | `TurnsVerbUnresolvedTranscriptTests.Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk(agent: Grok)` | yes, number 2 |

**No test fails that is not on the baseline.** Baseline number 1,
`Fleet.FleetOutcomeStoreTests.Answer_ConcurrentCallersOnTwoInstances_ExactlyOneWins`, passed here as
it did in the Developer's run; it is a concurrency test across two store instances and it goes both
ways on this machine.

The counts match the Developer's run exactly - 6335 total against the baseline's 6336, the one
missing test being `WorkflowStoreTests.Mission_instructions_are_a_faithful_extraction_of_the_skill_file`,
which this change deletes.

## `src/CcDirector.Core.UnitTests`

```
Passed!  - Failed:     0, Passed:   666, Skipped:     0, Total:   666, Duration: 56 s
```

Identical to the baseline. This suite holds the retired-words guard, whose entries this change edits.

## The one edit this tip adds

In `src/CcDirector.Gateway.Contracts/SessionOrdering.cs`, the parenthetical citation of the mission
workflow is deleted from the Architect notice comment. The cited workflow no longer contains the
word Manager, so the citation had stopped supporting the clause it was attached to. The word
`Manager` is deliberately left: `SessionRoles.Manager` is a live product constant and
"Manager-derivation" names the resolver's real derivation of that role string, so renaming it in the
comment would make the comment wrong about the code.

Also checked, by grep across the repository: no file outside `docs/` history now names
`.claude/skills/mission/SKILL.md`. The remaining hits are dated mission and review records, which
are history and are not edited.

## What this check does NOT cover

- The parked suites, the web tests and the Python tools. Nobody on this mission has run them.
- That a mission conducted under the new workflow text succeeds. This proves the shipped text and
  metadata only. That is phase 5 of this mission.
- The retired-words guard's continued coverage of the workflow body was established by reading its
  file list, not by watching the guard fail on purpose.

---

# The second check, after the consistency test was added

Run 18 September 2026 in the same worktree, at commit `04b4d2295` - the tip after the independent
review's second finding was answered with
`WorkflowStoreTests.Mission_step_table_in_the_conduct_matches_the_mission_definition` and its proof
file. The Developer's own run is at `CONSISTENCY-TEST.md`; this is the Delivery Lead running it
again, because a seat saying "done" is a claim.

**This run found a failure that is NOT on the baseline, and that is worth more than the green run
that followed it.** It is recorded first, in full, because the bar on this machine is "no failure
outside the named list" and the honest thing to do with a breach is to write it down rather than
re-run until it goes away.

## Run 1 at `04b4d2295`

```
Failed!  - Failed:    10, Passed:  6324, Skipped:     2, Total:  6336, Duration: 5 m 33 s
```

Nine were the baseline's numbers 2 to 10. The tenth was not on the baseline at all:

```
CcDirector.Gateway.Tests.Messaging.FleetDoorbellTests
  .A_deferred_ring_types_nothing_and_does_not_move_the_message_towards_stuck
```

## What was done about it

Three things, in this order, before any conclusion was drawn.

**1. The class was run alone.**

```
> dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~FleetDoorbellTests -v n

Test Run Successful.
Total tests: 72
     Passed: 72
```

All 72, including the one that failed, pass in isolation.

**2. The mechanism was checked in the source rather than guessed at.**
`src/CcDirector.Gateway.UnitTests/Messaging/FleetDoorbellTests.cs` line 17 holds
`private readonly GatewayDbTestHarness _harness = new();` - it is a database-backed test. Product
issue 3029 records this exact intermittent class in this exact suite, and names its cause:
`StatsConcurrencyTestDb.Dispose` calls `SqliteConnection.ClearAllPools()`, which is **process
global**, so one class's teardown pulls another class's pooled connections while that class is still
running. Its recorded symptom is "one database test fails per full run, a different one each time,
all passing in isolation", which is precisely what happened here.

**3. The whole suite was run again at the same commit.**

```
Failed!  - Failed:     9, Passed:  6325, Skipped:     2, Total:  6336, Duration: 4 m 50 s
```

Nine failures, every one on the baseline, and `FleetDoorbellTests` passed.

## What that means, stated carefully

The failure is a pre-existing intermittency in the suite's own database fixtures, not damage from
this change. Three independent things support that and they are not the same evidence three times:
the test passes alone, the class it belongs to is database-backed through the harness the known
defect attacks, and it did not recur at the same commit. Nothing in this change touches messaging,
the doorbell, or any database fixture.

**What it also means is that `BASELINE.md` is incomplete as a bar.** Ten named failures is the right
floor, but the real behaviour of this suite is "those ten, plus up to one database-backed test that
fails in a full parallel run and passes alone". A seat that reads the baseline as an exhaustive list
will, sooner or later, read a flake as damage - or, worse in the other direction, take a breach on
trust because "it is probably the known flake". Neither is acceptable, and the answer is the one
followed here: **run it alone, name the mechanism from the source, and re-run the suite** before
calling anything pre-existing.

That belongs on product issue 3029, which already records the mechanism and now has one more
measured instance of it, on a named test, with the isolation run beside it.

## The count reconciliation

| Run | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Baseline, clean `origin/main` `c135d44b2` | 6336 | 6324 | 10 | 2 |
| Branch tip `550af84f5` (before the consistency test) | 6335 | 6324 | 9 | 2 |
| Branch tip `04b4d2295`, run 1 | 6336 | 6324 | 10 | 2 |
| Branch tip `04b4d2295`, run 2 | 6336 | 6325 | 9 | 2 |

The total returns to 6336 because the consistency test replaces, one for one, the fidelity test this
change deletes. That is a coincidence of counts and not evidence on its own - the two tests are named
in the diff and that is what says which went and which arrived.
