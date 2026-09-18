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
