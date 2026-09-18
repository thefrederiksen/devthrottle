# Developer mandate - cut the `mission` workflow to steps plus a pointer

Mission: **Implement the DevThrottle Method v1**, phase 3. Issue:
<https://github.com/thefrederiksen/devthrottle/issues/3080> - read it in full with
`gh issue view 3080 --repo thefrederiksen/devthrottle`.

You report to the Tech Lead, session `ceaa45db` ("Method v1 - Tech Lead - mission workflow to four
seats"). Do not message the owner, and do not arrange your own review - the Tech Lead sends your
work to a Reviewer when it accepts it.

## Where you work

`D:\ReposFred\devthrottle.worktrees\wt01`, branch `method/workflow-four-seats`, already cut from
`origin/main`. It is yours. Do not work in `D:\ReposFred\devthrottle`, do not create another
worktree, and do not switch branches.

## Why this exists

The `mission` workflow that every session fetches today teaches the **old** method: the seats
Architect, Manager and Worker, a house analogy that is retired, and the rule that only the Architect
talks to the owner. All three were replaced on 18 September 2026. The seats are now **Architect,
Delivery Lead, Tech Lead, Developer**.

The second half is bigger than a rename. A Skill is knowledge - how to do a kind of thing. A
Workflow is the shape of a run - its steps, who sits in each, who reviews it, what proves each one
done. The `mission` workflow currently carries about 4,500 words of RULES, which is skill material,
and those rules are kept byte-for-byte equal to a second copy in the repository by a fidelity test.
Rules written twice drift, and this repository spent a day this week repairing exactly that drift.

The owner decided on 18 September: **cut the workflow back to its steps and their proof, plus one
line telling the seat to fetch the method skill for the rules.**

## What to change

### 1. `src/CcDirector.Gateway/Workflows/Content/mission.instructions.md`

Rewrite it. It holds only:

- **The five steps below**, with the doer, the reviewer, and what proves each one done.
- **Where the human is bothered**: once, at the report, from the Delivery Lead.
- **The one-worktree rule**: one worktree per concurrent workstream, cut from `origin/main`, never
  the shared checkout.
- **The messaging limits**: a session may message only the session that started it and the sessions
  it started, at most six an hour; messages queue and are read from
  `cc-devthrottle message inbox`; nobody waits for an answer (`message send --reply-wanted`,
  answered with `message reply`); never broadcast to the whole fleet.
- **One line pointing at the method for how to work**, carrying the exact command
  `cc-devthrottle skill get devthrottle-method`.

The five steps, from the mission document section 5. These are the authority - do not reword the
"Done when" column into something weaker:

| Step | Doer | Reviewer | Done when |
|---|---|---|---|
| Settle the design | Architect | none | The mission document exists with its required sections, the why and the goal are stated, and the owner has said go. |
| Drive | Delivery Lead | none | Every phase is merged and the mission's own check passes. |
| Build | Developer | Tech Lead, or the Delivery Lead when there is no Tech Lead | A merged pull request with its proof. Committed and pushed is still in progress. |
| Land the record | Delivery Lead | none | The mission's record is merged to the main branch. |
| Report | Delivery Lead | none | The owner has one page to read. |

**What must NOT be in it:** seat definitions, laws, recorded incidents, the house analogy, the
order-of-authority block, the "who may interrupt the owner" section, the brief-writing checklist,
the reset-the-Manager section, the landing-the-work section. Every one of those is skill material
and is being published in `devthrottle-method` by another seat. Do not write that skill, and do not
restate any of it here.

**Hard gate: under 1,500 words** (`wc -w`). The Tech Lead counts it, and so does the Delivery Lead.
Aim well under - the two sibling workflows are 139 and 180 words. Shorter is the point of the
change, not a side effect of it.

Keep the document's opening honest about what it is and is not: this file is the shape of one run,
and the rules live in the method.

### 2. `src/CcDirector.Gateway/Workflows/BuiltInWorkflows.cs`

The `mission` entry only. Bring `Summary`, `WhenToUse`, `HumanCheckpoint` and the five
`WorkflowStep` records to the four seats and the table above - step names, descriptions, `Doer`,
`Reviewer` and `Done`.

The words **"Manager", "Worker" and "Inspector" must not survive anywhere in this workflow's text
or metadata.** Note that today's step 2 is "Drive the phase" with doer Manager; it becomes "Drive"
with doer Delivery Lead, and its reviewer becomes `null`. The Build step's reviewer becomes the Tech
Lead, expressed so the fallback in the table is not lost.

**Do not touch the `standalone`, `standalone-with-review` or `fleet-manager` entries.** They have
their own seats and are out of scope, including the word "Worker" where it appears in them.

**Do not sweep "Manager" across the repository.** Only this workflow's text and metadata. Historical
mission records under `docs/missions/` and `docs/reviews/` are dated history and are not to be
edited.

### 3. The twin file and the fidelity test - the Tech Lead's decision, already made

**Delete `.claude/skills/mission/SKILL.md` and the fidelity test.** The reasoning goes in the pull
request body; it is written out in `TECH-LEAD-DECISION.md` beside this file, and you should read it
so your pull request body carries it accurately.

Enumerate what you are deleting. The list, which is also your blast radius - check each one
actually behaves as described before you touch it, and tell the Tech Lead if any does not:

1. `.claude/skills/mission/SKILL.md` - 4,624 words of the retired method. Disposable because its
   content is superseded by `devthrottle-method`, and because a repository-local skill file shadows
   an installed fleet skill of the same subject, so leaving it in place would keep teaching the old
   seats to every session working inside this repository. Delete the now-empty
   `.claude/skills/mission/` directory with it.
2. `Mission_instructions_are_a_faithful_extraction_of_the_skill_file` in
   `src/CcDirector.Gateway.UnitTests/WorkflowStoreTests.cs`, with its two private helpers
   `ApplyListedEdits` and `ReplaceExactlyOnce`, which nothing else calls. Disposable because its
   entire subject - two copies of the same rules - is gone. Check whether `Normalize` and
   `RepoRoot()` in that file still have another caller before removing either; if they do, keep
   them.
3. The fidelity paragraph in that file's class doc-comment (around line 15), and the sentence in
   the `BuiltInWorkflows.cs` doc-comment (around line 185) that calls the mission body "the faithful
   extraction of `.claude/skills/mission/SKILL.md`". Disposable because both describe a mechanism
   that will no longer exist; a comment that outlives its mechanism is a false claim in the source.
4. The `".claude/skills/mission/SKILL.md"` entry in `RequiredFiles` in
   `src/CcDirector.Core.UnitTests/Skills/RetiredMessagingWordsTests.cs` (around line 335), and
   `"mission"` from the id list in `TaughtFiles()` (around line 381). Disposable because the file it
   names will not exist. The retired-words scan still reads the whole
   `src/CcDirector.Gateway/Workflows/Content/` directory, so the workflow itself stays covered -
   verify that is still true rather than taking it from me.
5. The pointer in the `SessionOrdering.cs` comment (around line 921) that cites
   `.claude/skills/mission/SKILL.md`. Repoint it at the workflow; do not change any resolver
   behaviour, and do not restructure that comment beyond the dangling reference.

Nothing else is deleted. If you find another live reference to the skill file that is not dated
history, stop and tell the Tech Lead rather than deciding it yourself.

### 4. How it ships - it does not, today

The built-in seeder republishes the workflow on Gateway startup when the shipped bundle hash
changes, so this reaches the fleet with the next Gateway deploy. **Do not deploy. Do not run
`cc-devthrottle workflow push` or `publish`. Do not hand-edit a published workflow.** This mission
deploys nothing.

## The check - run it and paste the output

Run all five and keep the output; it goes in the pull request body.

1. The Gateway unit tests pass. `dotnet test src/CcDirector.Gateway.UnitTests` - print the result
   line. Run `src/CcDirector.Core.UnitTests` too, because you are editing a test in it.
2. `wc -w src/CcDirector.Gateway/Workflows/Content/mission.instructions.md` - print the number, and
   it is under 1,500.
3. `grep -niE "(manager|worker|inspector|house)" src/CcDirector.Gateway/Workflows/Content/mission.instructions.md`
   returns nothing that is a seat name, and the same grep over the `mission` entry in
   `BuiltInWorkflows.cs` returns nothing that is a seat name. Show what it returned, including
   nothing.
4. The five steps in `BuiltInWorkflows.cs` carry the doers and reviewers in the table above - show
   them.
5. The instructions name `devthrottle-method` and carry the command to fetch it - show the line.

A grep returning nothing is only evidence if you show the command and its empty output; run the
same grep against the old file first so you can see it finds hits when hits exist.

## When you are done

Commit and push to `method/workflow-four-seats`, then report to the Tech Lead with
`cc-devthrottle session report`. Do not open the pull request and do not merge - the Tech Lead runs
the check itself and arranges the review. A Developer saying "done" is a claim, not a proof, and it
is the Tech Lead's job to disbelieve it.

Answer every review finding you are sent, accepted or declined with the reason. A finding is never
closed by the seat that raised it.

## What you never do, however sure you are

- Deploy anything. Publish or push a workflow or a skill to the Gateway.
- Delete data, tear down infrastructure, or rewrite history.
- Send anything outward - no email, no post, no message to anyone but the Tech Lead.
- Run anything in the background, or use a sub-agent inside your session.
- Name any assistant, vendor or model in a commit, a pull request, an issue, a comment or a
  document. No "Co-authored-by" trailer, no "Generated with" line. Write as the owner, and check
  your text before every commit.

If something is genuinely unclear, ask the Tech Lead with
`cc-devthrottle message send ceaa45db "<question>"` and keep working on everything else. Do not
write a status and stop.
