# Tech Lead decision - the twin file and the fidelity test

Mission: Implement the DevThrottle Method v1, phase 3. Issue 3080, scope item 4.
Decided 18 September 2026 by the Tech Lead seated for this phase.

## The decision

**Delete `.claude/skills/mission/SKILL.md`, and delete the fidelity test that holds it equal to the
workflow.**

## What the two things are

`src/CcDirector.Gateway/Workflows/Content/mission.instructions.md` is the body of the `mission`
workflow, compiled into the Gateway and served to every session that runs
`cc-devthrottle workflow instructions mission`.

`.claude/skills/mission/SKILL.md` is a second copy of that same body, with a YAML frontmatter block
on top, living in this repository so that a session working inside this repository finds it as a
Claude Code skill.

`Mission_instructions_are_a_faithful_extraction_of_the_skill_file` in
`src/CcDirector.Gateway.UnitTests/WorkflowStoreTests.cs` asserts the two are byte-for-byte equal,
modulo two named self-reference edits. It was written when the workflow body was extracted out of
the skill file, and its own doc-comment says what it is for: *"If it has been stubbed (phase 6),
this fidelity test has done its job and should be retired."*

## Why delete rather than keep

**The test's subject stops existing.** The test exists to stop two copies of the same rules from
drifting. After this change the workflow holds no rules - it holds five steps, their proof, the
one-worktree rule, the messaging limits, and a pointer. The rules move to `devthrottle-method`,
where they are written once. A test that holds a second copy equal to the first is machinery for a
problem the change removes; keeping it would mean keeping the second copy alive purely so the test
had something to check.

**Keeping the file is not neutral - it is the defect.** The file currently teaches Architect,
Manager and Worker, the house analogy, and "only the Architect talks to the owner". All three were
replaced on 18 September, and issue 3080 opens by naming them as what is wrong. Claude Code reads a
project's own `.claude/skills` ahead of the fleet skills a Director installs, so inside the very
repository where DevThrottle is built, this file **wins** over the method skill that replaces it.
Leaving it in place would mean the seats building the method keep being taught the method it
replaces, and would be invisible in exactly the place it would otherwise be caught.

**Refreshing it instead of deleting it costs more than it buys.** The alternative is to rewrite the
twin to match the new short workflow and keep the fidelity test pointed at it. That preserves the
`/mission` slash command inside this repository, and it preserves the duplication - a second copy
of a document whose whole purpose is now to point somewhere else, with a test whose whole purpose is
to stop the pointer from drifting from itself. The steps are the same five in both places and the
file is small, so the drift risk it guards is small; the cost is that the repository keeps a skill
named `mission` that shadows the fleet skill named `devthrottle-method`, and the next person to
change the seats has two files to find instead of one. The duplication is what the issue asks us to
end.

**Nothing else reads it.** The remaining references are dated mission records under `docs/missions/`
and `docs/reviews/`, which are history and are not edited, plus two source comments and one test
guard entry that name the path and are fixed in this change.

## What is being deleted

Enumerated, with why each piece is disposable. Nothing outside this list is removed.

1. `.claude/skills/mission/SKILL.md`, and the directory `.claude/skills/mission/` with it - 4,624
   words of the retired method. Superseded by `devthrottle-method`; actively harmful while it
   shadows it.
2. `Mission_instructions_are_a_faithful_extraction_of_the_skill_file` and its two private helpers
   `ApplyListedEdits` and `ReplaceExactlyOnce` in `WorkflowStoreTests.cs` - nothing else calls
   either helper, and the test's subject is gone.
3. The fidelity paragraph in the `WorkflowStoreTests.cs` class doc-comment, and the sentence in the
   `BuiltInWorkflows.cs` doc-comment calling the mission body a faithful extraction of the skill
   file - both describe a mechanism that will no longer exist, and a comment that outlives its
   mechanism is a false claim sitting in the source.
4. The `.claude/skills/mission/SKILL.md` entry in `RequiredFiles`, and `"mission"` from the id list
   in `TaughtFiles()`, in `RetiredMessagingWordsTests.cs` - the file they name will not exist. The
   scan still reads the whole `Workflows/Content/` directory, so the workflow body remains covered
   by the retired-words guard.

5. The comment on the `Workflows\Content\*.instructions.md` `EmbeddedResource` item in
   `src/CcDirector.Gateway/CcDirector.Gateway.csproj` (around line 90), which says the bodies are
   kept as `.md` files "so the mission conduct stays diffable against its source
   (`.claude/skills/mission/SKILL.md`)". The whole clause is the twin-file claim, not only the
   parenthetical, and after the deletion there is no source to be diffable against - so it is
   rewritten to state the reason that survives, which is that the bodies are `.md` files rather than
   C# string literals so they stay readable in a diff and cannot drift through escaping. The
   neighbouring comment on `Skills\Content\*.skill.md` is untouched: shipped skills do still have
   repository twins, so its wording is still true.

One reference is repointed rather than deleted: the `SessionOrdering.cs` comment that cites the
skill file is made to cite the workflow, with no change to resolver behaviour.

**This list was incomplete when it was written.** Item 5 was not on it. The Developer found the
project-file comment while checking the blast radius it had been handed, stopped, and asked before
touching it rather than deciding on its own - which is the behaviour the mandate asked for and the
reason the enumeration is done by a second pair of eyes rather than taken on the Tech Lead's word.
The Tech Lead verified the reference in the source before answering, and widened the fix beyond what
was proposed, because striking only the parenthetical would have left a sentence that was still
false.

## What this does not cover

- It says nothing about the other repository copies of shipped skills - `fleet-comms`,
  `terminology`, `dev-throttle`, `move-session`. Those are twins of Gateway **skills**, governed by
  the one-source rule in `CLAUDE.md`, and they are untouched.
- It does not remove the word "Manager" from anywhere outside this workflow's own text and metadata.
  The repository still carries the old seat names in code comments, documents and historical
  records. That is a larger sweep and it is not this phase.
- The workflow does not change for any session until the next Gateway deploy, because a built-in
  workflow ships inside the Gateway image. This change deploys nothing.
