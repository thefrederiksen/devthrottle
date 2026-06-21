---
name: implementation-loop
description: Supervisor of the CenCon Implementation session for cc-director (issue #259). Drives one GitHub issue through Developer -> QA -> Developer -> QA until QA passes, then QA squash-merges to main on a clean build; escalates flow:needs-human on a weak spec or 3 QA bounces. --all drains the flow:ready-dev queue. Triggers on "/implementation-loop", "implementation loop", "drive this issue to done", "dev-qa loop".
---

# Implementation Loop (CenCon Development Method - cc-director)

You are the **Implementation session supervisor**. You loop the Developer and QA roles against a
single issue until QA passes and the change is **merged to main**. This is the runtime of issue
#259: Developer + QA collapsed into one looping session.

**You stay thin. The work happens in sub-agents.** You do NOT read code, build, or drive the app in
your own context. You spawn a fresh sub-agent (the `Agent` tool) for each DEV phase and each QA
phase; that sub-agent does the heavy work in its own throwaway context and hands you back only a
short structured result. This is what keeps your context small enough to drive many issues in a row
(`--all`). See "Execution model" below - it is the heart of this skill, not an optimization.

**Read the contract first:** `docs/cencon/DEVELOPMENT_METHOD.md`. The sub-agents you spawn perform
the Developer Agent (`/developer-agent`) and QA Agent (`/qa-agent`) roles - their laws and proof
bars still govern; you only sequence them and own the loop guards. That document wins on any
disagreement.

Tracker: **GitHub Issues** in `thefrederiksen/cc-director` (via `gh`). State is carried by `flow:*`
labels.

## What this skill is for

- `/implementation-loop <issue#>` - drive that one issue to merged-`flow:done`.
- `/implementation-loop` (no arg) - take the oldest `flow:ready-dev` issue and drive it.
- `/implementation-loop --all` - keep draining the `flow:ready-dev` queue, one issue at a time,
  until none remain.
- `/implementation-loop --source devops <workItemId>` - drive ONE Azure DevOps work item (issue
  #300). The tracker is the work item (claim and write-back via `az boards`, see "devops mode"
  below); the engineering mechanics (branch, PR, squash-merge, proof) stay GitHub PR-based in the
  code repo the work item's description names (default `thefrederiksen/cc-director`).

## The terminal signal you MUST emit (machine-readable, every path)

This loop emits a single **machine-readable terminal signal** as the LAST thing you print for an
issue, on EVERY terminal path. It is the keystone of the autonomous queue runner (epic #270): an
external watcher reads this block to know the run finished without parsing prose. The full contract
lives in `docs/cencon/DEVELOPMENT_METHOD.md` Section 7a - this skill emits against it.

There are exactly three signal values, mapped from the loop's outcomes:

| Signal | Emit it when | merged |
|--------|--------------|--------|
| `done` | MERGED - QA passed and squash-merged the PR to main (`flow:done`) | `yes` |
| `needs-human` | PARKED (3-strike `flow:needs-human`), ESCALATED (weak-spec `flow:needs-human`), or merge conflict / dirty post-merge build (`flow:needs-human`) | `no` |
| `failed` | The loop could NOT complete - pre-flight dirty-tree / leftover-stash stop (Step 0a), wrong-base stop, a build-tool failure, a crash, or any abnormal exit that reaches no clean outcome | `no` |

The sentinel block has this exact shape (ASCII only):

```
IMPL-LOOP-TERMINAL
issue: <N>
signal: done | needs-human | failed
pr: <pr number or none>
merged: yes | no
reason: <one line - why this terminal state>
```

**Emission rules (mandatory):**

- Emit the block **in addition to** the existing human-readable one-line report (Step 4) - never
  instead of it. Print the one-line report first, then the sentinel block as the final output.
- Emit **exactly one** sentinel block per issue per run, as your final action for that issue, so a
  watcher reading the last block gets an unambiguous answer. In `--all` mode that is one block per
  issue, printed as each issue finishes.
- The sentinel is the LAST transcript output for the issue. Nothing about the issue follows it.
- This is non-negotiable on EVERY terminal path below: PASS, PARK, ESCALATE, and abnormal stop. If
  the loop stops for ANY reason (including a Step 0a pre-flight stop before any issue work), emit the
  matching sentinel - a `failed` signal when no clean outcome was reached.

## devops mode (issue #300): CLAIM and WRITE-BACK against an Azure DevOps work item

Invoked as `/implementation-loop --source devops <workItemId>` (the Gateway queue runner's
`DevopsSourceAdapter` seeds exactly this for a `source = devops` work-list ref). devops mode changes
ONLY the tracker operations - who carries the claim and where the terminal status is written back.
Everything else is identical to github mode: the DEV/QA sub-agents, the proof bar, the loop guards,
the leave-clean invariant, and the engineering mechanics (branch, PR, squash-merge, proof committed
under `docs/cencon/proof/`) all stay GitHub PR-based in the CODE repo. The work item's description
names the target code repo; when it names none, the default is `thefrederiksen/cc-director` (v1).
The proof directory for a devops item is `docs/cencon/proof/devops-<workItemId>/`.

**Host prerequisites (documented, not auto-installed):** the Azure CLI with the `azure-devops`
extension, already authenticated (`az login`), with the default organization configured
(`az devops configure --defaults organization=https://dev.azure.com/<org>`). No PAT/secret handling
in v1 - the az CLI session is the transport. Work item ids are organization-wide, so
`az boards work-item show --id <id>` locates the item without a project argument; read the item's
`System.TeamProject` field when a project-scoped call (comments REST) needs it.

**State mapping (the `flow:*` equivalent, pinned static - decision D-3):** v1 pins the common
process templates in a static table. Probe the work item TYPE's allowed state names (one lookup,
still no dynamic state-graph discovery):

```bash
az devops invoke --area wit --resource workitemtypestates \
  --route-parameters project="<System.TeamProject>" type="<System.WorkItemType>" \
  --api-version 7.1 --query "value[].name"
```

and pick the template row whose in-progress/done names appear in that list. (The item's CURRENT
state name alone is NOT enough: `To Do` is the initial state of both Basic and Scrum, but their
in-progress states differ - `Doing` vs `In Progress`. Hit live on the issue #300 proof project,
which is Scrum.) An unrecognized state set is an escalation (`needs-human` - never guess a
transition).

| Template (recognized by its state names) | initial / proposed | in-progress | done |
|------------------------------------------|--------------------|-------------|------|
| Basic (`To Do` / `Doing` / `Done`)       | `To Do`            | `Doing`     | `Done` |
| Scrum (`To Do` / `In Progress` / `Done`) | `To Do`            | `In Progress` | `Done` |
| Agile (`New` / `Active` / `Resolved` / `Closed`) | `New`      | `Active`    | `Closed` |

**CLAIM (replaces the Step 0.2 label claim; same verify-after-claim shape as #298):**

```bash
# (a) best-effort claim: State -> the template's in-progress state, plus a CLAIM discussion comment
az boards work-item update --id <N> --state "<in-progress state>" \
  --discussion "CLAIM by <director-id>/<session-id> at $(date -u +%Y-%m-%dT%H:%M:%SZ)"
# (b) verify-after-claim: list the work item's comments; the OLDEST "CLAIM by " comment wins
az devops invoke --area wit --resource comments \
  --route-parameters project="<System.TeamProject>" workItemId=<N> \
  --api-version 7.1-preview --query "comments[?starts_with(text, 'CLAIM by ')]"
```

If the oldest CLAIM comment is another run's, you LOST the race: back off and leave the winner's
state intact (do not transition it back - the winner owns it).

**WRITE-BACK (the terminal mapping, decision D-3):**

| Loop terminal signal | Work item State | Plus |
|----------------------|-----------------|------|
| `done` | the template's done state | discussion comment with the merged PR URL |
| `needs-human` | STAYS in-progress | tag `needs-human` (append to `System.Tags`) + discussion comment saying what a human must do |
| `failed` | back to the initial/proposed state | discussion comment with the failure reason |

```bash
# done:        az boards work-item update --id <N> --state "Done" --discussion "done: merged <PR URL>"
# needs-human: az boards work-item update --id <N> --fields "System.Tags=<existing>; needs-human" \
#                --discussion "needs-human: <reason>"
# failed:      az boards work-item update --id <N> --state "To Do" --discussion "failed: <reason>"
```

**Sentinel:** unchanged (Section 7a is source-agnostic). `issue: <workItemId>` is the correlation
key - the Gateway runner watches for the work item id in the `IMPL-LOOP-TERMINAL` block exactly as
it watches for a GitHub issue number.

**What the DEV/QA sub-agents see:** the spawn prompt tells them the tracker is an Azure DevOps work
item (id + org/project + the item's description as the spec) instead of a GitHub issue. The
`flow:*` label steps in their skills are carried by the work item State + comments per the tables
above; defect comments and reports go on the work item discussion. Everything code-side (branch
naming, PR, proof commit, clean-tree gate) is unchanged.

## The authority this loop carries (and its scope)

Inside this loop, the QA role is authorized to **commit and squash-merge to main on a clean pass**.
This overrides the standing "never commit/merge without explicit ask" rule (CLAUDE.md global +
project) and the method's old "merge is a human step" (DECIDED D5). The authorization is **scoped to
this loop only** - it does not extend to any other context, and a standalone `/qa-agent` session
still does not merge. The merge is autonomous only on a clean pass; a conflict or a dirty build
escalates to the human, never a force.

## The leave-clean invariant (no orphaned branches, no uncommitted WIP, no dangling PRs)

This loop must NEVER leave junk behind. A prior run abandoned a half-built feature as loose
working-tree edits on a feature branch with no PR, so the next run tripped over a dirty tree at
pre-flight - that must never happen again. The rule, in full:

- **A PR may be left OPEN at a stopping point in exactly ONE case:** the loop escalates
  `flow:needs-human` (weak spec, 3-strike, merge conflict, or dirty post-merge build). The QA role
  PARKS it (qa-agent Step 3c) - all work committed to the PR branch, the PR up to date, and the
  issue updated to say it is parked and why. The parked PR is the human's entry point.
- **In EVERY other terminal outcome the repo is left clean:** on a PASS the PR is squash-merged and
  the branch deleted (the branch dies). The developer never hands off, and QA never finishes, with a
  dirty working tree. No dangling PRs, no orphaned branches, no uncommitted files - period.
- **You (the supervisor) enforce this between phases and between issues** with the clean-tree gate in
  Step 4. `git status --porcelain` is a one-line cleanliness check - it does not pollute your context
  the way reading source would, so it is allowed and required. A sub-agent that returns leaving a
  dirty tree has violated its own skill: you do NOT advance to the next issue on a dirty tree - you
  stop and surface it to the human.
- **A clean working tree is NOT enough - also check `git stash list`.** `git stash` empties the
  working tree while hiding WIP in the stash list, so `git status --porcelain` passes while a mess
  lingers. A prior run "parked by cleanup" exactly this way, and the human had to clean it up. NO
  sub-agent may stash to fake a clean tree, and your Step 4 gate asserts `git stash list` carries no
  stash the loop created. WIP is committed to the PR branch (FAIL/PARK) or merged (PASS) - never
  stashed, never discarded.

## Execution model: thin supervisor, sub-agent per phase

This is the design that makes the loop work over many issues without choking on context.

- **The supervisor (you, the main context)** holds only: the current issue number, its current
  `flow:*` label, the bounce counter, and a one-line ledger of results. Nothing else. You never
  read source files, never run `dotnet build`, never look at screenshots in your own context.
- **Each phase runs in a fresh sub-agent** spawned with the `Agent` tool. The sub-agent reads the
  role skill, does all the noisy work (file reads, edits, build logs, slot-5 launch, screenshots,
  Control API calls) **in its own context, which is discarded when it returns**, and hands you back
  only a compact structured result. A build log or a screenshot must never cross back into your
  context - only the summary does.

**Why this is correct, not just cheap:** the CenCon method already keeps all durable state OUTSIDE
agent memory - status lives in the `flow:*` label, the Developer's report and the QA defect live in
issue comments, and the code + proof live on the PR branch. So a fresh sub-agent reconstructs
everything it needs by reading the issue and checking out the branch; it never needs the previous
sub-agent's context. A QA sub-agent that never saw the Developer's reasoning is also a more honestly
independent verifier (separation of duties). The throwaway context is backed by permanent proof
committed to the branch - that, not the supervisor's memory, is the audit trail for the autonomous
merge.

### How you spawn a phase sub-agent

Spawn with the `Agent` tool. The prompt names the role, the skill file to follow, and the issue, and
demands the structured handback. You pass NO code and NO file contents - the sub-agent reads what it
needs itself. Example DEV spawn prompt:

```
You are the Developer Agent. Read .claude/skills/developer-agent/skill.md and follow it exactly to
implement issue #<N> in thefrederiksen/cc-director. Do all work in your own context. When done,
your final message must be ONLY this block (nothing else):

RESULT
outcome: ready-qa | needs-human
issue: <N>
pr: <pr number or none>
branch: <branch name>
proof: <repo-relative path to your committed proof, or none>
summary: <one line - what you did, or which DoR item was missing>
```

The QA spawn prompt is the same shape, pointing at `.claude/skills/qa-agent/skill.md`, with
`outcome: pass | fail | needs-human`, plus `merged: yes | no` and `defect: <one line>` on fail.

You **parse only that RESULT block** to decide the next step and to write your one-line ledger
entry. You do not absorb the sub-agent's working detail.

### Concurrency rule (why this is safe on one machine)

Phases within ONE loop run strictly one at a time - DEV completes and returns before QA is spawned -
and in `--all` mode issues are processed one at a time. Never spawn the DEV and QA sub-agents in
parallel.

Safety against OTHER loops on the same machine is **per-session isolation** (issue #299), not a
single reserved slot: every DEV/QA sub-agent works in its own git worktree (own bin/obj + own
`local_builds`), allocates its own test slot >= 6 via `scripts/agent-session-isolation.ps1` (the
slot is reserved atomically by registering the per-slot `cc-director<N>-launch` scheduled task -
a registration race has exactly one winner), and the test Director self-allocates its Control API
port (discovered from the Director's own log; launches are serialized through a machine-wide mutex
inside the script so one Director has BOUND its port before the next one probes - the Director's
PortAllocator picks a port before Kestrel binds it, so unserialized simultaneous startups can pick
the same port). Two concurrent implementation sessions therefore get different slots, different
build outputs, and different ports by construction. The issue-level
claim (Step 0, #298) keeps two loops off the same ISSUE; the session isolation keeps them off the
same MACHINE resources.

## Quick Reference

| Phase | Runs as | Outcome it can produce |
|-------|---------|------------------------|
| DEV | fresh sub-agent following `developer-agent` | `flow:ready-qa` (implemented + proof) OR `flow:needs-human` (weak spec) |
| QA | fresh sub-agent following `qa-agent` | `flow:done` + **squash-merge to main** OR `flow:qa-failed` (bounce) OR `flow:needs-human` (conflict/dirty build) |
| GUARD | this supervisor (your context) | `flow:needs-human` after 3 bounces; tracks bounce count + ledger only |

## Priority-queue file mode (PRIORITY_LOOP.md)

In addition to selecting from the `flow:ready-dev` label queue, the loop can be driven from a curated
**priority file**, `PRIORITY_LOOP.md` at the repo root. This is how a human (or a `priority-loop`
launcher skill) pins an explicit, ordered batch of issues. When the loop is given this file (invoked
with it, told to "drive PRIORITY_LOOP.md", or handed an explicit ordered list that you record into
it), it drives the listed issues ONE AT A TIME in EXACT file order - never by age, never reordered.
File order overrides the oldest-first selection in Step 0.1; the Step 0 CLAIM and every loop guard
still apply unchanged to each listed issue.

The file is a **supervisor working artifact, not product code.** It MUST stay out of git so that
writing to it never trips the clean-tree gate: ensure `PRIORITY_LOOP.md` is in `.gitignore` or the
repo-local `.git/info/exclude` before the first write. The Step 0a pre-flight and the Step 4
clean-tree gate both read `git status --porcelain`, which then ignores it. NEVER commit it to a PR
branch.

It carries an ordered table - one row per issue, highest priority first - with a `Status` column the
loop owns (`pending` | `claimed` | `done` | `needs-human` | `failed`):

```
| Order | Issue | Status  | Outcome notes |
|-------|-------|---------|---------------|
| 1     | 583   | pending | |
```

**Marking (as each issue moves).** Set the row to `claimed` the moment you win the Step 0.2 claim, so
a crash leaves a readable trail. Then in Step 4, immediately after you emit the issue's
`IMPL-LOOP-TERMINAL` sentinel, set `Status` to the terminal signal and put a one-line result (PR
number + what was verified, or why it parked/failed) in `Outcome notes`. The file is the
human-readable mirror of the per-issue sentinels.

**Reset at queue exhaustion (the "clean up" - file kept, NEVER deleted).** When the whole loop is
done - every listed issue reached a terminal state:
- If ALL rows are `done`: reset `PRIORITY_LOOP.md` to its empty template - the title, the status
  legend, and an EMPTY queue table (header row + separator only, no data rows) - so it is ready to be
  refilled next time. We keep the file to use again; do not delete it.
- If any row ended `needs-human` or `failed`: do NOT reset. Leave the file intact so those rows stay
  visible as the human's follow-up list, and say so in your final summary. Reset only once those rows
  are resolved and re-run to `done`.

## The loop (per issue)

### Step 0a: Pre-flight (clean tree + correct base) - BEFORE any issue work

The loop must start from a clean, known base so its work never mixes with unrelated uncommitted
changes or branches off the wrong point. Check this FIRST, every run:

```bash
git status --porcelain   # must be EMPTY
git stash list           # must show NO stash (a leftover stash is dangling WIP - treat as not-clean)
git rev-parse --abbrev-ref HEAD   # expected: main (the base the Developer role branches from)
```

- **Dirty working tree** (any output from `git status --porcelain`): STOP. Do not start. Report the
  uncommitted files and ask the human to commit or discard them. The loop never auto-stashes or
  discards - that could silently swallow someone's work, and stashing is never how this loop cleans
  up. **Emit the terminal signal `failed`** (the run could not complete - it never reached a clean
  outcome) with `issue: <N or none>`, `pr: none`, `merged: no`, `reason: pre-flight stop - dirty
  working tree`.
- **A non-empty `git stash list`:** STOP and surface it. A leftover stash is parked WIP someone hid;
  do not start a run on top of it and never silently drop it - ask the human what to do with it.
  **Emit the terminal signal `failed`** with `reason: pre-flight stop - leftover stash`.
- **Not on the base branch** (`main`, unless the issue/PR says otherwise): STOP and confirm with the
  human which base to use before continuing. **Emit the terminal signal `failed`** with `reason:
  pre-flight stop - wrong base branch`.
- Only when the tree is clean and the base is correct does the loop proceed to Step 0.

On any of these pre-flight stops the sentinel is the final output. (`issue` is the selected issue
number if one was given on the command line, else `none` - no issue work has begun.)

(This guard is independent of the issue-selection guards below; both must pass.)

### Step 0: Select AND CLAIM the issue (duplicate-prevention - issue #298)

Two concurrent loops must never implement the same issue (it happened on #199: duplicate PRs #294 /
#296). So selection is not just "read the oldest `flow:ready-dev`" - it is **select-then-claim**, and
the claim removes the issue from every other loop's selection set the instant work starts. The full
mechanism is DEVELOPMENT_METHOD.md Section 4a (DECIDED D6); this is how the loop performs it.

**Step 0.0 - Stale-claim sweep (recover crashed claims FIRST).** A loop that crashed after claiming
leaves an issue `flow:in-progress` with no live owner - invisible to selection forever unless
reclaimed. Before selecting, sweep stale claims back into the pool:
```bash
# any flow:in-progress issue whose newest CLAIM comment is older than 60 min is stale
gh issue list --repo thefrederiksen/cc-director --label flow:in-progress --state open \
  --json number --jq '.[].number' | while read N; do
  NEWEST=$(gh issue view "$N" --repo thefrederiksen/cc-director --json comments \
    --jq '[.comments[] | select(.body|startswith("CLAIM flow:in-progress by "))] | sort_by(.createdAt) | last | .createdAt')
  # if NEWEST is empty or older than 60 minutes, reclaim it:
  #   gh issue edit "$N" --repo thefrederiksen/cc-director --add-label flow:ready-dev --remove-label flow:in-progress
  #   gh issue comment "$N" --repo thefrederiksen/cc-director --body "STALE-CLAIM SWEEP: reclaimed flow:in-progress -> flow:ready-dev (claim older than 60 min)."
done
```
Do NOT sweep an issue this run just claimed (a fresh claim is protected - that is what stops the
sweep stealing an issue out from under a healthy run).

**Step 0.1 - Select.**
- Arg given: use that issue number. Confirm it carries `flow:ready-dev` (or `flow:qa-failed` /
  `flow:in-progress` from a prior pass of THIS run - treat as resume; do not re-claim what you
  already hold).
- No arg: pick the oldest open `flow:ready-dev` (the selection query reads `flow:ready-dev` ONLY, so
  an in-progress/claimed issue is already invisible):
  ```bash
  gh issue list --repo thefrederiksen/cc-director --label flow:ready-dev --state open \
    --json number,title,updatedAt --jq 'sort_by(.updatedAt) | .[0]'
  ```
  If none, report "No flow:ready-dev issues" and stop.

**Step 0.2 - Claim it (best-effort) and verify-after-claim.** GitHub labels are NOT an atomic
compare-and-swap, so close the race honestly:
```bash
# (a) best-effort claim: ready-dev -> in-progress in ONE edit, then record WHO claimed it
gh issue edit <N> --repo thefrederiksen/cc-director --add-label flow:in-progress --remove-label flow:ready-dev
gh issue comment <N> --repo thefrederiksen/cc-director --body "CLAIM flow:in-progress by <director-id>/<session-id> at $(date -u +%Y-%m-%dT%H:%M:%SZ)"
# (b) verify-after-claim: the WINNER is whoever's CLAIM comment is OLDEST
gh issue view <N> --repo thefrederiksen/cc-director --json comments \
  --jq '[.comments[]|select(.body|startswith("CLAIM flow:in-progress by "))]|sort_by(.createdAt)|.[0].body'
```
- If the oldest `CLAIM ...` comment is **yours**: you won. Proceed.
- If it is **another loop's**: you LOST the race. Back off - leave the winner's `flow:in-progress`
  intact (do NOT relabel it back; the winner owns it), then in `--all` mode select the next oldest
  `flow:ready-dev` and re-claim, or in single-issue mode report that the issue was claimed by another
  run and stop (emit no sentinel for an issue you never owned).

This is best-effort with a deterministic loser-back-off (claim-comment order is the arbiter), not
lock-free atomicity - see Section 4a for the honest residual-window note. (`gh issue list --label`
is eventually consistent; allow a few seconds for the index to reflect a relabel.)

Initialize a **bounce counter = 0** for this issue (the 3-strike guard).

### Step 1: DEV phase (spawn a fresh sub-agent)

Spawn a **Developer sub-agent** (`Agent` tool) with the DEV prompt from "Execution model" above,
pointing at `.claude/skills/developer-agent/skill.md` and this issue. The issue now carries the
loop's `flow:in-progress` claim (Step 0.2); tell the sub-agent it is the claimed issue (no
`flow:ready-dev` required). It plans, implements against every acceptance criterion, builds clean
(`dotnet build cc-director.sln`), proves it on a slot-5 test Director launched via the
`cc-director-launch` scheduled task, commits proof to the PR branch, and on hand-off swaps the claim
to `flow:ready-qa` (removing `flow:in-progress`) - all in its own context. It returns a `RESULT`
block; you read only that.

- `outcome: ready-qa` -> record the pr/branch from the RESULT, go to Step 2.
- `outcome: needs-human` -> the spec failed the Definition of Ready and there is no Product session
  here to re-sharpen it (the sub-agent already escalated `flow:needs-human`). **You stop this issue**
  - report the missing DoR items from `summary`, then go to Step 4 (which emits the `needs-human`
    terminal signal) before moving on (next issue if `--all`, else end).

### Step 2: QA phase (spawn a fresh, independent sub-agent)

Spawn a **QA sub-agent** (`Agent` tool) with the QA prompt from "Execution model", pointing at
`.claude/skills/qa-agent/skill.md` and the now-`flow:ready-qa` issue. It is a brand-new context that
never saw the Developer sub-agent's work - that is the point, and it is what makes the verification
genuinely independent. It reads the issue (including the Developer's report and any prior defect
comment), checks out the PR branch, verifies every acceptance criterion in the running app with its
own proof, runs the regression + CenCon checks, and produces a QA proof report. It returns a
`RESULT` block; you read only that.

- **PASS** (`outcome: pass`, `merged: yes`): the sub-agent labeled `flow:done`, squash-merged the PR
  to main with the clean-build gate (QA Step 3a), and closed the issue. Go to Step 4 (which emits the
  `done` terminal signal).
- **FAIL** (`outcome: fail`): the sub-agent labeled `flow:qa-failed` with a specific, reproducible
  defect (in its issue comment; the one-liner is in `defect`). **bounce counter += 1.**
  - If **bounce counter >= 3**: stop the autonomous loop. Label `flow:needs-human`, comment a
    summary of the unresolved defect(s), and stop this issue (mirrors the 3-strike ping-pong guard
    in DEVELOPMENT_METHOD.md Section 5a). Go to Step 4 (which emits the `needs-human` terminal
    signal).
    ```bash
    gh issue edit <ID> --repo thefrederiksen/cc-director --add-label flow:needs-human --remove-label flow:qa-failed
    ```
  - Else: go back to **Step 1** and spawn a **fresh Developer sub-agent**. It reads the issue
    (including the QA defect comment - that is how the defect crosses contexts, not via shared
    memory), fixes via its Step 6, and re-labels `flow:ready-qa`. (A FAIL bounce is NOT a terminal
    outcome - no sentinel is emitted on a bounce; the sentinel is emitted only once the issue reaches
    a terminal state.)
- **NEEDS-HUMAN** (`outcome: needs-human`): the QA sub-agent hit a merge conflict or a dirty
  post-merge build and escalated `flow:needs-human` without forcing it. **You stop this issue** -
  record the `defect`/`summary` line, go to Step 4 (which emits the `needs-human` terminal signal),
  then move on (next issue if `--all`, else end).

### Step 3: Merge guard (inside QA's pass path, enforced here)

The squash-merge happens in QA Step 3a, but the loop owns the guard:

- Merge command: `gh pr merge <PR> --repo thefrederiksen/cc-director --squash --delete-branch`.
- **Build gate:** the merged result must build clean (`dotnet build cc-director.sln`) before the
  merge is treated as final.
- **Conflict or dirty build -> never force.** Re-label `flow:needs-human`, comment the exact
  failure, and stop this issue. Go to Step 4 (which emits the `needs-human` terminal signal).

### Step 4: Clean-tree gate, then report, emit the terminal signal, and (optionally) loop

**Clean-tree gate (mandatory, before you report or advance).** Whatever the outcome, assert the repo
was left clean - and "clean" means BOTH the tree AND the stash list:
```bash
git status --porcelain   # MUST be empty
git stash list           # MUST be empty of any stash the loop created (stashing is NOT cleanup)
```
- If both are empty: good - the phase left no orphan and hid nothing in a stash. Proceed to report.
- If `git status --porcelain` is NOT empty, OR `git stash list` shows a stash this run created: a
  sub-agent violated its skill - it left WIP behind, or it tried to fake a clean tree by stashing
  (the exact mess that prompted this gate). Do **not** advance to the next issue and do **not**
  auto-stash, auto-discard, or auto-drop a stash (that could swallow real work). STOP, report exactly
  what is dirty or stashed, and ask the human - this is the failure mode this whole invariant exists
  to catch. This is an abnormal stop: the terminal signal for this issue is **`failed`** (the run
  could not leave a clean outcome). On a PASS outcome also confirm the PR is gone: `gh pr list --repo
  thefrederiksen/cc-director --state open` must not show it; on a needs-human outcome the parked PR
  may remain (that is the one allowed open PR).

**Claim-release gate (issue #298).** `flow:in-progress` is a transient working state and must NEVER
be the label an issue is left in at a terminal stop. Confirm the claim was released:
```bash
gh issue view <N> --repo thefrederiksen/cc-director --json labels --jq '[.labels[].name]'
```
The result must NOT contain `flow:in-progress` (it should be `flow:done` on PASS, or
`flow:needs-human` on a park/escalate). If `flow:in-progress` lingers, a sub-agent failed to swap it
- this is an abnormal stop: do not advance, surface it, and the terminal signal is `failed`. (A
crashed claim left this way would be recovered by the next run's Step 0.0 stale-claim sweep, but at a
clean terminal stop you release it here, you do not rely on the sweep.)

Then report a one-line result with the link:
```
Issue #NNN: MERGED (flow:done) - N/N criteria verified, squash-merged to main, branch deleted | link
Issue #NNN: PARKED (flow:needs-human) - 3 QA bounces, PR #NN parked, defect: <one line> | link
Issue #NNN: ESCALATED (flow:needs-human) - spec not ready: <DoR item> | link
```

**Then emit the terminal signal as the FINAL output for this issue** (see "The terminal signal you
MUST emit" above - this is in addition to the one-line report, never instead of it). Map the outcome
to exactly one signal:

| Outcome reached | signal | pr | merged | reason example |
|-----------------|--------|----|--------|----------------|
| MERGED (PASS, `flow:done`) | `done` | the merged PR number | `yes` | `squash-merged to main; N/N criteria verified` |
| PARKED (3-strike `flow:needs-human`) | `needs-human` | the parked PR number | `no` | `3 QA bounces; parked for human - <defect>` |
| ESCALATED (weak-spec `flow:needs-human`) | `needs-human` | `none` (or PR number if one exists) | `no` | `spec not ready - <DoR item>` |
| Merge conflict / dirty post-merge build (`flow:needs-human`) | `needs-human` | the parked PR number | `no` | `merge conflict; parked for human` |
| Abnormal stop (Step 0a pre-flight, dirty Step 4 gate, build-tool failure, crash) | `failed` | `none` (or PR number if one exists) | `no` | `pre-flight stop - dirty tree` / `clean-tree gate failed - WIP left behind` |

Emit it exactly like this (fill in the values for the outcome reached), as the very last lines:
```
IMPL-LOOP-TERMINAL
issue: NNN
signal: done
pr: 124
merged: yes
reason: squash-merged to main; 6/6 criteria verified
```

Exactly one such block per issue per run. In `--all` mode emit one block per issue as each finishes,
each as that issue's last output before the next issue begins.

**If driving from `PRIORITY_LOOP.md` (priority-queue file mode):** right after emitting the sentinel,
mark this issue's row per "Priority-queue file mode" above (Status + one-line Outcome notes), and when
the whole queue is drained perform the end-of-queue reset described there. Writing to the excluded
priority file does not affect the clean-tree gate.

- `--all` mode: clear context between issues (memory reset, DEVELOPMENT_METHOD.md Section 7), then
  return to Step 0 for the next `flow:ready-dev`. Stop when the queue is empty. The Step 0a pre-flight
  re-checks the clean tree at the start of each issue too - belt and suspenders.
- Single-issue mode (default): stop after this issue.

## Memory reset (Section 7) is automatic with sub-agents

The per-phase memory reset the method calls for (DEVELOPMENT_METHOD.md Section 7, DECIDED D2) is
achieved by the execution model itself: every DEV and QA phase is a fresh sub-agent with a throwaway
context, so no spec, fixture, build log, or assumption from one phase or one issue bleeds into the
next. The supervisor stays thin by construction. In `--all` mode you simply move to the next issue
after Step 4 - because you only ever held one-line results, no `/clear` is required (use it only if
your own ledger has grown large over a very long queue).

## Loop guards (so it can neither spin nor merge recklessly)

| Guard | Trigger | Action |
|-------|---------|--------|
| Pre-flight | dirty working tree or wrong base branch (Step 0a) | STOP before any work; ask the human - never auto-stash/discard |
| Issue-claim | another loop's CLAIM comment is older (Step 0.2 verify-after-claim) | LOST the race: back off, leave the winner's `flow:in-progress`, select the next issue (or stop) |
| Stale-claim | `flow:in-progress` with newest CLAIM comment older than 60 min (Step 0.0) | reclaim `flow:in-progress` -> `flow:ready-dev` so the issue is never stranded invisible |
| Claim-release | `flow:in-progress` still present at a terminal stop (Step 4) | abnormal stop: surface it; signal `failed`; next run's sweep recovers it |
| Weak spec | Developer role rejects on Definition of Ready | `flow:needs-human`, stop issue |
| 3-strike | 3rd `flow:qa-failed` on the same issue | `flow:needs-human`, stop issue |
| Build gate | post-merge `dotnet build` not clean | `flow:needs-human`, do NOT merge |
| Conflict | `gh pr merge` reports a conflict | `flow:needs-human`, do NOT force |
| Leave-clean | `git status --porcelain` not empty OR `git stash list` has a run-created stash after a phase (Step 4) | STOP, surface dirty files / stashes, ask human; never auto-stash/discard/drop, never advance |

## What you do NOT do

- You do not implement or verify in your own context - you spawn a fresh sub-agent per phase, which
  carries the role's own laws and proof bars. You own the sequencing and the guards.
- You do not let a build log, screenshot, or file dump from a sub-agent into your context - you read
  only its `RESULT` block.
- You do not spawn the DEV and QA sub-agents in parallel - phases are strictly sequential.
- You do not merge outside a clean QA pass, and you never force a merge through a conflict or a
  dirty build.
- You do not skip the Developer role's proof or the QA role's independent verification to "save a
  loop" - the adversarial verify is the point.
- You do not extend the merge authority beyond this loop.
- You do not advance to the next issue, or finish, leaving the working tree dirty, a branch orphaned,
  or a PR dangling. The only sanctioned open PR at a stopping point is a PARKED `flow:needs-human`
  (qa-agent Step 3c). Everything else ends clean.
- You do not stop an issue WITHOUT emitting its terminal signal. Every terminal path - PASS, PARK,
  ESCALATE, abnormal stop - ends with exactly one `IMPL-LOOP-TERMINAL` sentinel block as the final
  output for that issue. A run that stops without a sentinel is a contract violation (#270 depends on
  it).

## Reuses

- The `Agent` tool - spawns the per-phase sub-agents whose throwaway contexts keep the supervisor
  thin.
- `developer-agent` skill - the role a DEV sub-agent reads and follows (plan, implement, build,
  prove, hand to `flow:ready-qa`).
- `qa-agent` skill - the role a QA sub-agent reads and follows (independent verify,
  pass+squash-merge / fail-bounce).
- The Control API (loopback REST) + a per-session test Director via
  `scripts/agent-session-isolation.ps1` (allocate / launch / teardown; CLAUDE.md rule 0b) - each
  sub-agent allocates its own slot >= 6 in its own worktree, so concurrent loops on one machine
  never share a test Director, a build output, or a port (issue #299).

---

**Skill Version:** 0.9 (DRAFT - thin supervisor + fresh sub-agent per phase; realizes issue #259)
**Implements:** the Implementation session loop in docs/cencon/DEVELOPMENT_METHOD.md (D2, D5, terminal-signal contract Section 7a)
**Builds on:** the `Agent` tool (per-phase sub-agents), developer-agent (DEV role), qa-agent (QA role + merge), DEVELOPMENT_METHOD.md
**Created:** 2026-06-10
**Changes in 0.2:** DEV and QA phases now run as fresh sub-agents (throwaway context, structured RESULT handback) instead of inline skill invocations - keeps the supervisor thin so it can drive many issues without context pollution. Added Execution model, handback format, concurrency rule.
**Changes in 0.3:** Added the leave-clean invariant (no orphaned branches, no uncommitted WIP, no dangling PRs) + the Step 4 clean-tree gate + the Leave-clean guard. One sanctioned open PR at a stopping point: a PARKED flow:needs-human. Hardened after a prior run abandoned a half-built feature as loose working-tree edits.
**Changes in 0.4:** Closed the stash loophole. A prior run "parked by cleanup" via `git stash` - the tree looked clean (`git status --porcelain` empty) while WIP sat hidden in the stash list, and the human had to clean it up. Banned `git stash` as a cleanup/park mechanism, extended the Step 0a pre-flight and Step 4 gate to also assert `git stash list` is empty, and updated the Leave-clean guard accordingly.
**Changes in 0.5 (issue #272):** Added the machine-readable terminal signal. The loop now emits exactly one `IMPL-LOOP-TERMINAL` sentinel block (`signal: done | needs-human | failed`) as its final output on EVERY terminal path - in addition to the human one-line report - so the autonomous queue runner (#270) can detect a finished run without parsing prose. Mapping: MERGED -> `done`; PARKED/ESCALATED/conflict/dirty-build -> `needs-human`; pre-flight stop / abnormal exit -> `failed`. Wired into Step 0a, Step 1, Step 2, Step 3, and Step 4; contract recorded in DEVELOPMENT_METHOD.md Section 7a.
**Changes in 0.6 (issue #298):** Added the issue-level CLAIM (duplicate-prevention). Step 0 now select-THEN-claims: a stale-claim sweep (Step 0.0) reclaims crashed `flow:in-progress` claims older than 60 min, selection reads `flow:ready-dev` only, and the chosen issue is claimed `flow:ready-dev` -> `flow:in-progress` with a verify-after-claim re-read (oldest `CLAIM` comment wins; the loser backs off). Step 4 adds a claim-release gate (no `flow:in-progress` may linger at a terminal stop). New guards: Issue-claim, Stale-claim, Claim-release. Closes the #199 duplicate-PR race. Mechanism + honest residual-window note in DEVELOPMENT_METHOD.md Section 4a / D6.
**Changes in 0.7 (issue #299):** Rewrote the concurrency rule: same-machine safety now comes from per-session isolation (own worktree, own slot >= 6 reserved via the per-slot scheduled-task registration in `scripts/agent-session-isolation.ps1`, self-allocated Control API port) instead of the old "single slot-5 so nothing can collide" claim. Two loops on one machine are safe on resources (#299) and on issues (#298).
**Changes in 0.9:** Added priority-queue file mode (`PRIORITY_LOOP.md`): the loop can be driven from a
curated, ordered priority file (file order overrides oldest-first selection), it marks each issue's
row as the issue is claimed and again when it reaches a terminal state (the human-readable mirror of
the `IMPL-LOOP-TERMINAL` sentinels), and on full clean drain it resets the file to an empty template
(file kept, never deleted; left intact for human follow-up if any row parked needs-human/failed). The
priority file is a supervisor working artifact kept out of git (`.gitignore`/`.git/info/exclude`) so
writing to it never trips the Step 0a / Step 4 clean-tree gate. Pairs with the `priority-loop`
launcher skill.
**Changes in 0.8 (issue #300):** Added devops mode (`/implementation-loop --source devops <workItemId>`): CLAIM and WRITE-BACK move to the Azure DevOps work item via `az boards` (State transitions per the pinned Basic/Scrum/Agile mapping table + discussion comments, template chosen by the work item type's state list, verify-after-claim by oldest CLAIM comment as in #298); engineering mechanics stay GitHub PR-based in the code repo the work item description names. The sentinel contract is unchanged - `issue: <workItemId>` is the correlation key. Matches the Gateway's per-source adapter dispatch (`ISourceAdapter`, DEVELOPMENT_METHOD.md Section 7b).
