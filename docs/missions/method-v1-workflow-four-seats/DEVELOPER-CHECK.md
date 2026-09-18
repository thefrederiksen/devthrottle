# The check - phase 3, the mission workflow cut to four seats

Run by the Developer on 18 September 2026, on SOREN_NORTH, in the mission worktree
`D:\ReposFred\devthrottle.worktrees\wt01`, at commit `ac182448b` on `method/workflow-four-seats`.

Compared test by test against `BASELINE.md` beside this file, which the Tech Lead measured on clean
`origin/main` (`c135d44b2`) in a separate worktree.

## 1. The two suites

### `src/CcDirector.Gateway.UnitTests`

```
Failed!  - Failed:     9, Passed:  6324, Skipped:     2, Total:  6335, Duration: 6 m 22 s
```

The nine, named in full:

| # | Test | On the baseline? |
|---|---|---|
| 1 | `TurnsVerbUnresolvedTranscriptTests.Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk(agent: Grok)` | yes, number 2 |
| 2 | `Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_that_omits_the_tenant_is_refused` | yes, number 3 |
| 3 | `Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_whose_tenant_is_only_whitespace_is_refused` | yes, number 4 |
| 4 | `Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "system")` | yes, number 5 |
| 5 | `Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "local")` | yes, number 6 |
| 6 | `Stats.HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(tenant: "9f2c1b7e-4d3a-4c5e-8b6f-0a1d2e3f4a5b")` | yes, number 7 |
| 7 | `Stats.HostedSchemaRefusesAnUnownedRowTests.A_tenant_with_whitespace_inside_an_otherwise_legal_value_is_refused` | yes, number 8 |
| 8 | `Stats.HostedSchemaRefusesAnUnownedRowTests.An_insert_whose_tenant_is_empty_is_refused` | yes, number 9 |
| 9 | `Stats.HostedSchemaRefusesAnUnownedRowTests.Every_character_dotnet_calls_whitespace_is_refused_as_a_tenant` | yes, number 10 |

**Every failure is on the baseline. No test fails that is not on it.** The baseline's number 1,
`Fleet.FleetOutcomeStoreTests.Answer_ConcurrentCallersOnTwoInstances_ExactlyOneWins`, passed in this
run - it is a concurrency test across two store instances and it goes both ways on this machine.

**The counts reconcile exactly, which is the part worth checking rather than the totals.** Baseline
6336 total, 6324 passed, 10 failed. This run: 6335 total, 6324 passed, 9 failed. One test left the
suite - `WorkflowStoreTests.Mission_instructions_are_a_faithful_extraction_of_the_skill_file`, which
this change deletes and which was passing on the baseline, so the passed count should have fallen to
6323. It did not, because the flaky concurrency test moved the other way in the same run. The two
movements are independent and each is accounted for; the identical passed count is a coincidence of
the two, not evidence on its own.

The eight `HostedSchemaRefusesAnUnownedRowTests` failures are a PostgreSQL connection failure
(`Npgsql.NpgsqlConnection.Open`) in the fixture's `Reset()`, not a product assertion. The Grok
failure asserts `no_transcript` and gets `ok` - product issue 3029, the test using the shared
temporary directory as a repository path. This change touches neither mechanism.

### `src/CcDirector.Core.UnitTests`

```
Passed!  - Failed:     0, Passed:   666, Skipped:     0, Total:   666, Duration: 59 s
```

Identical to the baseline. This suite holds the retired-words guard, whose entries this change edits.

## 2. The instruction body is under 1,500 words

```
$ wc -w src/CcDirector.Gateway/Workflows/Content/mission.instructions.md
359 src/CcDirector.Gateway/Workflows/Content/mission.instructions.md
```

Was 4,502. The two sibling workflows are 139 and 180.

## 3. No retired seat name survives

The same grep run against the file BEFORE the change, so that an empty result afterwards is evidence
rather than an absence that could mean the grep never worked:

```
$ grep -niE "(manager|worker|inspector|house)" src/CcDirector.Gateway/Workflows/Content/mission.instructions.md | wc -l
48
$ sed -n '42,92p' src/CcDirector.Gateway/Workflows/BuiltInWorkflows.cs | grep -niE "(manager|worker|inspector|house)" | wc -l
8
```

After the change, both return nothing:

```
$ grep -niE "(manager|worker|inspector|house)" src/CcDirector.Gateway/Workflows/Content/mission.instructions.md
$ echo $?
1
$ awk '/Id: "mission"/,/Id: "standalone"/' src/CcDirector.Gateway/Workflows/BuiltInWorkflows.cs | grep -niE "(manager|worker|inspector|house)"
$ echo $?
1
```

Exit status 1 is grep saying it ran and matched nothing. The `standalone`,
`standalone-with-review` and `fleet-manager` entries are untouched, including the word "Worker"
where it appears in them.

## 4. The five steps carry the doers and reviewers from the mission document

```
$ awk '/Id: "mission"/,/Id: "standalone"/' src/CcDirector.Gateway/Workflows/BuiltInWorkflows.cs | grep -E "Name: |Doer: |Reviewer: |Done: "
Name: "Settle the design",
Doer: "Architect",
Reviewer: null,
Done: "The mission document exists with its required sections, the why and the goal are stated, and the owner has said go."
Name: "Drive",
Doer: "Delivery Lead",
Reviewer: null,
Done: "Every phase is merged and the mission's own check passes."
Name: "Build",
Doer: "Developer",
Reviewer: "Tech Lead, or the Delivery Lead when there is no Tech Lead",
Done: "A merged pull request with its proof. Committed and pushed is still in progress."
Name: "Land the record",
Doer: "Delivery Lead",
Reviewer: null,
Done: "The mission's record is merged to the main branch."
Name: "Report",
Doer: "Delivery Lead",
Reviewer: null,
Done: "The owner has one page to read."
```

The `Done` strings are concatenated across source lines above for readability. All five were
extracted from the source and compared against the mission document's column programmatically, and
all five match byte-for-byte. Step 2's doer moved from Manager to Delivery Lead and its reviewer
from Architect to `null`; step 3's reviewer carries the fallback rather than losing it.

## 5. The instructions name the method and carry the command to fetch it

```
$ grep -n "devthrottle-method" src/CcDirector.Gateway/Workflows/Content/mission.instructions.md
11:    cc-devthrottle skill get devthrottle-method
```

## What this check does NOT cover

- **The method skill does not exist yet.** The workflow now points at `devthrottle-method`, and
  `cc-devthrottle skill list` does not have it - checked on 18 September 2026, zero matching rows
  against the five built-ins it does list. Until the seat writing it publishes it, a session
  that follows the pointer gets nothing. That is scope item 3 of issue 3080 and another seat's work,
  but it is a real gap between this landing and that one, and it should not be discovered by a
  session mid-mission.
- **Nothing was deployed and nothing was published.** The served workflow is still version 26 with
  the old text; this reaches the fleet only with the next Gateway deploy.
- **No run of the workflow was performed.** This proves the shipped text and metadata, not that a
  mission conducted under it succeeds.
- **The three parked suites, the web tests and the Python tools were not run.** This change touches
  none of them, and `Gateway.UnitTests` is itself parked out of the default local gate.
- **The retired-words guard's coverage of the workflow body was checked by reading the code, not by
  watching it fail.** `TaughtFiles()` still adds every `*.md` under
  `src/CcDirector.Gateway/Workflows/Content/`, so removing the skill file's entries did not remove
  the workflow from the scan. The guard was not made to fail on purpose to prove it still sees the
  file.
