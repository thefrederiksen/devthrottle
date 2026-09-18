# Review - phase 3, pull request 3092

Written 18 September 2026 by an independent Reviewer running a different agent from the seats that
wrote the work, opened by the Delivery Lead. It was told not to trust the mission's own report and
that it did not have to find anything. The Delivery Lead's answers to both findings are at the
bottom, under "What was done about each finding" - the Reviewer did not write those and does not
decide them.

---

Scope

Reviewed pull request 3092 at commit 610ed9dfe461c7e6480acbbac7af09aa9ef11082 against origin/main. Read the shortened mission workflow body, the built-in workflow metadata, the retired-words guard, the session ordering comment and resolver, the workflow store tests, the project file, and repository references to the deleted twin file and fidelity helpers. Checked the complete pull request file list and whitespace. Did not run the test suites or build.

The retired-words guard still includes every markdown file under src/CcDirector.Gateway/Workflows/Content, so the shortened mission body remains covered. No live reference to the deleted twin file, fidelity test, or deleted helpers remains in the reviewed tree. The session ordering comment remains consistent with the code: an explicit Architect bypasses Manager derivation, while Manager remains a live derived role constant.

Verdict

Findings worth recording but not blocking. No blocking findings.

Finding 1 - external instruction dependency

File: src/CcDirector.Gateway/Workflows/Content/mission.instructions.md:11

The workflow instructs the reader to run `cc-devthrottle skill get devthrottle-method`, but that skill is not present in this repository or in the referenced shipped skill set at this revision. A mission started from this workflow therefore cannot obtain the rules it says are required and cannot be run as documented until the separate skill change is published. This is an ordering dependency outside this pull request rather than a defect in the workflow extraction itself; the dependent change must land before this workflow is presented as usable.

Finding 2 - metadata and body are not cross-checked

Files: src/CcDirector.Gateway/Workflows/BuiltInWorkflows.cs:44-94 and src/CcDirector.Gateway/Workflows/Content/mission.instructions.md:14-21

The tests verify that both sources are non-empty and that the store preserves each source, but no test compares the mission's step names, seats, reviewers, or completion text across the C sharp metadata and the markdown table. Replacing, for example, the metadata reviewer or doer with a different constant would leave the current suite green while the Gateway and Cockpit advertised a different mission than the instructions agents receive. A focused consistency test would make this contract explicit. This is a coverage gap, not evidence that the current values disagree.

---

## What was done about each finding

Answered by the Delivery Lead, which holds the builder's side of this pull request now that the
phase's Tech Lead and Developer have been stopped. A Reviewer advises; it does not command - and no
finding is left unanswered.

**Finding 1, the external instruction dependency - ACCEPTED, and it is an ordering constraint rather
than a change to this pull request.** The Reviewer is right that a mission started from this workflow
today cannot fetch the rules it is told to fetch. The skill is phase 2 of this mission, in the other
repository, and it is published to the Gateway separately. The two do not race in practice: a
built-in workflow only reaches a session when the Gateway is deployed, and the skill is published
directly and takes effect at once, so the skill is live before any session can read the pointer. That
was already stated in this pull request's body under "What is NOT proven" before the review ran, and
it is stated again here because the Reviewer found it independently, which is the better evidence of
the two.

**Finding 2, no test compares the step metadata with the instruction body - ACCEPTED and FIXED.** The
five step names, doers, reviewers and "done when" lines are written twice, once in
`BuiltInWorkflows.cs` and once in the markdown table, and nothing held them together: a constant
could be substituted in either one and the suite would stay green while the Cockpit advertised
different steps from the ones an agent is served. That is the same failure this whole change exists
to end - the same thing written twice, drifting - so leaving it as a note would have been the wrong
answer to it. A focused consistency test was added, and it is a different contract from the
byte-for-byte fidelity test this change deletes: it holds the five rows of one table equal to five
step records, not two copies of a rules document equal to each other. The test, what it asserts and
the proof that it fails when the two disagree are recorded in `CONSISTENCY-TEST.md` beside this file.
