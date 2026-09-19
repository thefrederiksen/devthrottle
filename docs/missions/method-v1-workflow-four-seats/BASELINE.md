# Measured baseline - clean `origin/main`, before any of this phase's changes

> **READ THE CORRECTION AT THE BOTTOM OF THIS FILE BEFORE YOU TRUST THIS BASELINE.** Its scope was
> wrong: it covers two test projects and the CI gate runs three. A change measured green against it
> broke `main` on tests that assert on the very file that changed. What the baseline omits is at the
> bottom under "Correction - this baseline's SCOPE was the hole", and the rule that replaces it is one
> line: run what CI runs.

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

**That last sentence is the defect, not a caveat, and it is the reason `main` went red.** The CI
gate runs the whole solution, and `src/CcDirector.Gateway.Tests` - not measured here - holds two
tests that assert on the very file this phase rewrote. See the correction at the bottom of this
file. The rule that replaces this sentence is: run what CI runs, and name any suite you could not
run together with the reason it cannot hide a regression in what you changed.

**Correction to what this Tech Lead was told.** The Delivery Lead passed on one known pre-existing
failure, number 2 above, and said the check was that the suite was "otherwise green". On this machine
it is not: there are ten. Taking that on trust would have meant reading nine real pre-existing
failures as damage from this change, or - worse in the other direction - accepting a run with nine
failures in it as expected. The number came from measuring, not from being told.

---

# Correction - this baseline's SCOPE was the hole, and it cost a red main

Added 18 September 2026 by the Delivery Lead, after the fact, because the sentence below was written
as a caveat and read by five seats as a formality.

This baseline says, at the end: *"It does not cover the suites this phase does not touch, the web
tests, or the Python tools."* That sentence was true and it was the defect.

**What happened.** Phase 3 rewrote
`src/CcDirector.Gateway/Workflows/Content/mission.instructions.md`, cutting it from 4,502 words to
359. Two tests in **`src/CcDirector.Gateway.Tests`** - a THIRD project, not one of the two measured
here - assert that the shipped mission conduct contains the string `THE FOUR LAWS`, which the cut
removed. They are
`WorkflowEnableSwitchTests.Off_refuses_the_default_conduct_read_with_a_clear_message_but_pinned_reads_resolve`
and `WorkflowEnableSwitchTests.Off_refuses_new_runs_and_the_flip_back_restores_everything`.

**CI runs `dotnet test cc-director.sln -c Release` - the whole solution.** This baseline covers two
projects out of it. So a Developer, a Tech Lead, two independent Reviewers on different agents, and
the Delivery Lead's own two full runs all passed a change to a file, past tests that assert on that
exact file, because every one of them ran the wrong suites. No amount of reviewing catches that.
`main` went red and the fix is `REVIEW-ENABLE-SWITCH.md` and `ENABLE-SWITCH-FIX.md` beside this file.

**What a later seat should take from this, in order of usefulness:**

1. **A baseline is only as good as its scope, and the scope has to be checked against what CI runs.**
   Two projects is not the solution. Before trusting a baseline, read the CI workflow's test step.
2. **`src/CcDirector.Gateway.Tests` cannot be run with a bare `dotnet test`.** It is listed in
   `$postgresProjects` in `scripts/test-local.ps1`, which builds a throwaway PostgreSQL, hands the
   test processes its connection strings and destroys it afterwards. A raw invocation gives it no
   database at all - a Developer's attempt here produced 27 `Npgsql` authentication failures and 201
   HTTP failures out of a partial 399 before the run was killed on low memory at 46 minutes. Those
   numbers mean nothing about the product. The supported run is
   `.\scripts\test-local.ps1 -Parked` with Docker up and a quiet machine.
3. **Number 2 on the list above is now FIXED on main.**
   `TurnsVerbUnresolvedTranscriptTests...(agent: Grok)` was repaired in `561452cf4` (#3106) after this
   baseline was measured, so the expected failure count on this machine is now **nine**, not ten.
   The totals have also moved - 6336 tests at `c135d44b2`, 6386 by the evening of the same day - so
   compare test by test and never by count.
4. **A database-backed test can fail once in a full parallel run and pass alone**, and this has now
   been seen on two different tests outside the named ten: `FleetDoorbellTests` (see
   `DELIVERY-LEAD-CHECK.md`) and `WorkspaceCapturedSeatsAreImmutableTests` (see
   `ENABLE-SWITCH-FIX.md`). Both passed alone. Product issue 3029 records the mechanism -
   `SqliteConnection.ClearAllPools()` is process-global. **Run it alone before you call it anything**,
   and never wave a breach through because it is "probably the known flake".
