# Review - the enable-switch assertions

Written 18 September 2026 by an independent Reviewer running a different agent from the Developer
that made the change, opened by the Delivery Lead. It was told not to trust the Developer's proof
file, that it did not have to find anything, and - because that proof file declares a gap in its own
check - it was asked for a specific ruling on whether the change may merge anyway.

It did the wrong-row mutation itself rather than taking the Developer's word for it.

The Delivery Lead's answer is at the bottom.

---

# Review: enable-switch assertions

## Scope

I reviewed the branch `origin/fix/workflow-enable-switch-asserts` at commit
`4920627067820305d383b0cd01ba25cc5c680ce9`, including:

- `src/CcDirector.Gateway.Tests/WorkflowEnableSwitchTests.cs`
- `docs/missions/method-v1-workflow-four-seats/ENABLE-SWITCH-FIX.md`
- `BuiltInWorkflows.InstructionsFor`, both seeder call sites, and the neighbouring unit tests
  that use the accessor
- The complete branch diff against `origin/main`

I ran the filtered test command from the task. It rebuilt the projects and passed all four tests.
I then changed `WorkflowStore.GetInstructions` to resolve the `standalone` row instead of the
requested workflow. The filtered run failed both changed assertions and reported the expected
mission body versus the actual standalone body. I reverted that mutation with `git checkout --`
and ran the filtered command again as a full rebuild; all four tests passed.

I did not run the whole `CcDirector.Gateway.Tests` project, any whole-solution test run, or the
supported parked test gate. The proof file honestly records that its own raw whole-project attempt
was killed before a total and that it was not a supported database-backed run. I did not repeat
that prohibited run.

## Verdict

No blocking findings.

The equality is stronger for the intended property than the removed phrase check. It detects a
wrong resolved body, while remaining intentionally insensitive to a legitimate rewording of the
shipped conduct. It is not circular for this test class: the store result is compared with the
canonical embedded body, and the deliberate wrong-row mutation made both assertions fail. The
accessor is the correct one: the seeder uses it at both call sites, and the neighbouring unit
tests use it for the same shipped-content contract.

The remaining failure modes are appropriate to the scope. A missing embedded resource makes the
canonical accessor throw; a missing resolved row makes the equality fail; a wrong workflow row,
wrong version, or wrong body makes the equality fail. A defect that changes the embedded source
and the seeded store content together will not be found by these assertions, but that is a content
test concern, not an enable-switch resolution concern.

## Declared test gap

This is enough to merge a two-assertion test repair. The filtered four-test run covers the changed
class, and the wrong-row mutation demonstrates that the new assertions fail on the defect they
are meant to guard. The uncompleted whole-project run is not evidence against this change: it was
an unsupported, resource-contended run with no final result, and the change adds no product code
or tests outside the filtered class.

## Findings

None within the reviewed scope.

## Tree state

The deliberate product mutation was reverted, and no tracked source or test changes remain beyond
the reviewed branch. The final `git status --short` output was:

```text
?? REVIEW-TASK.md
```


---

## What was done about it

**Nothing to fix, and one thing accepted.** No findings, and the Reviewer's ruling on the declared
gap is accepted as written: the filtered four-test run plus a mutation that makes both assertions
fail on the defect they guard is enough for a two-assertion test repair, and an unsupported,
resource-contended whole-project run with no final total is not evidence against a change that adds
no product code.

**The one limitation both seats named, recorded so it is not lost.** These assertions do not catch a
defect that changes the embedded conduct and the seeded store content together, because they compare
one against the other. That is correct for this test class - it is about the enable switch resolving
the right row, not about the conduct's content - and the content side is held by a different test,
`WorkflowStoreTests.Mission_step_table_in_the_conduct_matches_the_mission_definition`, added earlier
on this same branch in answer to the phase 3 review. The two together are what the old single
substring was pretending to be.
