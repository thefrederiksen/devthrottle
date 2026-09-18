# Measured baseline - clean `origin/main`, before any of this phase's changes

Run by the Tech Lead on 18 September 2026, on SOREN_NORTH, in a separate worktree
(`D:\ReposFred\devthrottle.worktrees\wt02`) at commit `c135d44b2`, with nothing from this phase
applied. The gate run gets its own worktree so nobody can move the files underneath it.

This exists so that "the suite is green" and "that failure was already there" are both checkable
claims rather than assertions. Every failure below is a **pre-existing** failure on this machine and
is not caused by this phase's change.

## `src/CcDirector.Gateway.UnitTests`

```
Failed!  - Failed:    10, Passed:  6324, Skipped:     2, Total:  6336, Duration: 5 m 31 s
```

The ten, named in full so a later run can be compared test by test rather than by count:

| # | Test |
|---|---|
| 1 | `CcDirector.Gateway.Tests.Fleet.FleetOutcomeStoreTests.Answer_ConcurrentCallersOnTwoInstances_ExactlyOneWins` |
| 2 | `CcDirector.Gateway.Tests.TurnsVerbUnresolvedTranscriptTests.Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk(agent: Grok)` |
| 3 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_that_omits_the_tenant_is_refused` |
| 4 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_whose_tenant_is_only_whitespace_is_refused` |
| 5 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "system")` |
| 6 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "local")` |
| 7 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "9f2c1b7e-4d3a-4c5e-8b6f-0a1d2e3f4a5b")` |
| 8 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.A_tenant_with_whitespace_inside_an_otherwise_legal_value_is_refused` |
| 9 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_whose_tenant_is_empty_is_refused` |
| 10 | `CcDirector.Gateway.Tests.Stats.HostedSchemaRefusesAnUnownedRowTests.Every_character_dotnet_calls_whitespace_is_refused_as_a_tenant` |

## `src/CcDirector.Core.UnitTests`

```
Passed!  - Failed:     0, Passed:   666, Skipped:     0, Total:   666, Duration: 1 m 25 s
```

## What this baseline does and does not tell you

**It tells you** the exact set of failures that a run of these two suites on this machine produces
with no change applied, so a later run can be compared test by test. The bar for this phase is that
**no test fails that is not on this list**, and that the two tests this phase deliberately removes
disappear rather than fail.

**It does not tell you** why these ten fail. Eight are `HostedSchemaRefusesAnUnownedRowTests` and one
is a concurrency test across two store instances, which points at a database-backed fixture rather
than at the product, and one is the known Grok transcript test filed as product issue 3029, whose
cause is recorded there as the test using the shared temporary directory as a repository path. This
phase changes none of that and is not fixing any of it.

**It does not cover** the suites this phase does not touch, the web tests, or the Python tools.

**Correction to what this Tech Lead was told.** The Delivery Lead passed on one known pre-existing
failure, number 2 above, and said the check was that the suite was "otherwise green". On this machine
it is not: there are ten. Taking that on trust would have meant reading nine real pre-existing
failures as damage from this change, or - worse in the other direction - accepting a run with nine
failures in it as expected. The number came from measuring, not from being told.
