# Handover - Implement the DevThrottle Method v1

Written 18 September 2026 by the phase 3 Tech Lead, on the owner's instruction, as the single
record a fresh Delivery Lead needs. Everything below is checked, not remembered. Where something is
a claim rather than a measurement, it says so.

Mission id `dcb3b2de-52b8-446c-9959-b9261fde5b77`. Mission document:
`D:\ReposFred\devthrottle_internal\docs\method\MISSION-2026-09-18-method-v1.html`. Its phase table
and its section 7 check are still correct; its status wording is stale.

## Why you are being seated

The previous Delivery Lead died and was re-seated, and a replacement cannot message the seats its
predecessor started - the fleet rule is that a session may message only the session that started it
and the sessions it started. So all three phase seats finished into a void, none could report
upward, and each surfaced to the owner separately. The owner saw one mission scattered across four
sessions and stopped it.

You are the fix: one seat, finishing the remaining work, reporting to the owner **once**. The three
phase seats have been stopped. Their work is on branches and pull requests and nothing is lost.

## The state of the five phases

| Phase | What exists | What is left |
|---|---|---|
| 1 - Freeze the method as version 1 | Merged, pull request 2104 | Nothing |
| 2 - Publish the `devthrottle-method` skill | Pull request **2114** open and mergeable in `devthrottle_internal`, 259 lines, `docs/method/skill/devthrottle-method/SKILL.md` + `skill.json` | Review, merge, then actually publish to the Gateway and verify |
| 3 - Cut the `mission` workflow to steps plus a pointer | Branch `method/workflow-four-seats` in `thefrederiksen/devthrottle`, tip `e72ffbff2`, pushed | One comment edit, then pull request, review, merge |
| 4 - The heartbeat | Pull request **2115** open in `devthrottle_internal`, 873 lines, `docs/method/research/heartbeat.md` | It is RESEARCH, not a decision. The owner must choose |
| 5 - Run one real mission under the method | Nothing | Needs 2 and 3 live |

## Phase 3 - what was done, and the one thing outstanding

Issue <https://github.com/thefrederiksen/devthrottle/issues/3080>, scope items 1, 2, 4 and 5.

Done on the branch:

- `mission.instructions.md` cut from 4,502 words to **359** - five steps, where the human is
  bothered, the one-worktree rule, the messaging limits, and the line
  `cc-devthrottle skill get devthrottle-method`.
- `BuiltInWorkflows.cs` mission entry moved to the four seats, with the doers and reviewers from the
  mission document's table.
- `.claude/skills/mission/SKILL.md` deleted, and the byte-for-byte fidelity test with it. The
  reasoning is in `TECH-LEAD-DECISION.md` beside this file and belongs in the pull request body.
- Five follow-on references fixed or removed, enumerated in that decision file.

**The one outstanding edit.** In `src/CcDirector.Gateway.Contracts/SessionOrdering.cs` around line
921, the comment reads `...writes the brief and hires the Manager (the mission workflow:
cc-devthrottle workflow instructions mission), and the resolver enforces the direction...`. The
cited workflow no longer contains the word Manager, so the citation no longer supports the clause.
**Delete the parenthetical citation only.** Do NOT rename Manager to Delivery Lead there:
`SessionRoles.Manager` is a live product constant (`public const string Manager = "Manager"`) and
"Manager-derivation" names the resolver's real derivation of that role string, so a rename would
make the comment wrong about the code. The sentence already names the resolver, which is better
evidence than any prose document.

Then: open the pull request against `main` linked to issue 3080, carry the check output and the
twin-file reasoning in the body, get it reviewed by a different agent family, merge.

## The test bar on this machine - do not use "green"

Measured by the Tech Lead on clean `origin/main` at `c135d44b2`, in its own worktree, nothing
applied. Named test by test in `BASELINE.md` beside this file.

- `CcDirector.Gateway.UnitTests`: **Failed 10**, Passed 6324, Skipped 2, Total 6336.
- `CcDirector.Core.UnitTests`: Failed 0, Passed 666.

The Tech Lead's own run at `e72ffbff2` gave Failed 9, Passed 6324, Skipped 2, Total 6335 - all nine
on the baseline, and the total down by exactly the one deleted test. The bar is **no failure outside
the named list**, never "the suite is green". The previous Delivery Lead passed on one known failure
and said the suite was otherwise green; on this machine it is not.

## What the two open internal pull requests still need

**2114 (phase 2).** Its author recorded plainly that nothing has touched the Gateway, so
`skill push`, `skill list` and `skill get` are all unexercised, and that its token figure is a
scaled estimate rather than a direct measurement. `cc-devthrottle skill list` today has five
built-ins and no `devthrottle-method`. Merging the pull request does not publish the skill -
publishing is a separate act and it is yours.

**2115 (phase 4).** It is options with their evidence, including two measurements written up as
proving nothing and one figure the author could not reproduce at all. Section 7 of the mission
document says phase 4 is done when there is a written decision in the owner's words plus the issues
it creates. Bring him one recommendation, not the options.

## The ordering constraint that matters

The workflow now points at `devthrottle-method`, which does not exist. The new text reaches no
session until the next Gateway deploy - until then the fleet is served version 26 - so the window is
narrow, but **phase 2 must publish before the next Gateway deploy** or a session that follows the
pointer gets nothing. This mission deploys nothing itself.

## What is NOT proven

- No mission has been run under the new workflow. Phase 3 proves the shipped text and metadata, not
  that a mission conducted under it succeeds. That is phase 5 and it is the actual goal.
- The parked suites, the web tests and the Python tools were not run by anyone on this mission.
- The retired-words guard's continued coverage of the workflow body was established by reading its
  file list, not by watching the guard fail on purpose.
