# Review - the step-table consistency test

Written 18 September 2026 by a second independent Reviewer, running a different agent from the
Developer that wrote the test and from the Reviewer that raised the finding it answers. Opened by the
Delivery Lead. It was told not to trust the Developer's proof file, and that it did not have to find
anything.

It did its own mutation on BOTH sides - the C# definition and the markdown table - where the
Developer's proof mutated only the C# side. Mutating the markdown is the more interesting of the two,
because it exercises the parser rather than the comparison.

The Delivery Lead's answer is at the bottom. The Reviewer did not write it.

---

Scope

I reviewed the branch tip detached at origin/method/workflow-consistency-test and read the two relevant code paths:
- src/CcDirector.Gateway.UnitTests/WorkflowStoreTests.cs
- src/CcDirector.Gateway/Workflows/BuiltInWorkflows.cs
- src/CcDirector.Gateway/Workflows/Content/mission.instructions.md
- docs/missions/method-v1-workflow-four-seats/CONSISTENCY-TEST.md

I also checked the branch diff and recent history with the commands described in the task, then ran the focused verification the review calls for:
- dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~WorkflowStoreTests
- a direct mutation of BuiltInWorkflows.cs to change the Build step doer, followed by git checkout -- to restore it
- a direct mutation of src/CcDirector.Gateway/Workflows/Content/mission.instructions.md to change the Build row's doer, followed by git checkout -- to restore it

I did not run the whole Gateway.UnitTests suite because the task's review bar is targeted failure and the baseline file already names the current machine-level failures outside this test. I did not need a wider run to answer the consistency question.

Verdict

No blocking findings.

Findings

Blocking findings: none.

Other findings worth recording but not blocking: none within the scope reviewed.

Why I reached that verdict:
- The new test can fail. I reproduced the failure by mutating the mission definition and by mutating the markdown table; each mutation failed in the expected row and column, with the message naming the row, step, column, and both values.
- The test reads the shipped embedded markdown, not a file path on disk. It calls BuiltInWorkflows.InstructionsFor("mission"), which is the same path the product reads from the embedded resource.
- The parser does not silently pass on a missing table. It asserts that a header row matching "Step | Doer | Reviewer | Done when" exists and it asserts that the table is followed by a delimiter row. It also asserts that the table has at least one step row. A no-table or no-rows parse would fail loudly instead of passing.
- The table walk is ordered and positional. It first asserts the step count match, then compares row 1 to step 1, row 2 to step 2, and so on. A swapped step, added step, or missing step fails.
- The normalization is narrow. It strips the markdown-table padding, unescapes an escaped pipe, and trims the visible cell content. It does not fold case, collapse inner whitespace, drop punctuation, or ignore trailing words. A real difference in wording is still a failure.
- The literal reviewer guard is correct and reachable: a WorkflowStep.Reviewer equal to "none" would be a different meaning from the markdown's spelling for "no separate review seat" and is explicitly rejected.

Tree clean

I restored every mutation and left the tracked tree clean for this review scope. The status output from the final check is:

git status --short --untracked-files=no

(no output)

---

## What was done about it

Nothing, and that is the correct answer to a review with no findings. Recorded here so that this
review and a review that never ran cannot be mistaken for each other: its scope is stated above, it
names the two mutations it made and the two it did not, and it says plainly that it did not run the
whole suite. The whole suite was run twice by the Delivery Lead at this tip instead, and both runs
are in `DELIVERY-LEAD-CHECK.md`.
