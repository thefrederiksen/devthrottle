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

## The authority this loop carries (and its scope)

Inside this loop, the QA role is authorized to **commit and squash-merge to main on a clean pass**.
This overrides the standing "never commit/merge without explicit ask" rule (CLAUDE.md global +
project) and the method's old "merge is a human step" (DECIDED D5). The authorization is **scoped to
this loop only** - it does not extend to any other context, and a standalone `/qa-agent` session
still does not merge. The merge is autonomous only on a clean pass; a conflict or a dirty build
escalates to the human, never a force.

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

Phases run strictly one at a time - DEV completes and returns before QA is spawned - and in `--all`
mode issues are processed one at a time. So two sub-agents never run at once and never collide on
the slot-5 test Director, the build output, or a Control API port. Never spawn the DEV and QA
sub-agents in parallel. (Isolated git worktrees would only be needed if we ever parallelized across
issues, which this loop deliberately does not.)

## Quick Reference

| Phase | Runs as | Outcome it can produce |
|-------|---------|------------------------|
| DEV | fresh sub-agent following `developer-agent` | `flow:ready-qa` (implemented + proof) OR `flow:needs-human` (weak spec) |
| QA | fresh sub-agent following `qa-agent` | `flow:done` + **squash-merge to main** OR `flow:qa-failed` (bounce) OR `flow:needs-human` (conflict/dirty build) |
| GUARD | this supervisor (your context) | `flow:needs-human` after 3 bounces; tracks bounce count + ledger only |

## The loop (per issue)

### Step 0a: Pre-flight (clean tree + correct base) - BEFORE any issue work

The loop must start from a clean, known base so its work never mixes with unrelated uncommitted
changes or branches off the wrong point. Check this FIRST, every run:

```bash
git status --porcelain   # must be EMPTY
git rev-parse --abbrev-ref HEAD   # expected: main (the base the Developer role branches from)
```

- **Dirty working tree** (any output from `git status --porcelain`): STOP. Do not start. Report the
  uncommitted files and ask the human to commit, stash, or discard them. The loop never auto-stashes
  or discards - that could silently swallow someone's work.
- **Not on the base branch** (`main`, unless the issue/PR says otherwise): STOP and confirm with the
  human which base to use before continuing.
- Only when the tree is clean and the base is correct does the loop proceed to Step 0.

(This guard is independent of the issue-selection guards below; both must pass.)

### Step 0: Select the issue

- Arg given: use that issue number. Confirm it carries `flow:ready-dev` (or `flow:qa-failed` from a
  prior pass - treat as resume-at-DEV).
- No arg: pick the oldest open `flow:ready-dev`:
  ```bash
  gh issue list --repo thefrederiksen/cc-director --label flow:ready-dev --state open \
    --json number,title,updatedAt --jq 'sort_by(.updatedAt) | .[0]'
  ```
  If none, report "No flow:ready-dev issues" and stop.

Initialize a **bounce counter = 0** for this issue (the 3-strike guard).

### Step 1: DEV phase (spawn a fresh sub-agent)

Spawn a **Developer sub-agent** (`Agent` tool) with the DEV prompt from "Execution model" above,
pointing at `.claude/skills/developer-agent/skill.md` and this issue. It plans, implements against
every acceptance criterion, builds clean (`dotnet build cc-director.sln`), proves it on a slot-5
test Director launched via the `cc-director-launch` scheduled task, commits proof to the PR branch,
and labels `flow:ready-qa` - all in its own context. It returns a `RESULT` block; you read only that.

- `outcome: ready-qa` -> record the pr/branch from the RESULT, go to Step 2.
- `outcome: needs-human` -> the spec failed the Definition of Ready and there is no Product session
  here to re-sharpen it (the sub-agent already escalated `flow:needs-human`). **You stop this issue**
  - report the missing DoR items from `summary` and move on (next issue if `--all`, else end).

### Step 2: QA phase (spawn a fresh, independent sub-agent)

Spawn a **QA sub-agent** (`Agent` tool) with the QA prompt from "Execution model", pointing at
`.claude/skills/qa-agent/skill.md` and the now-`flow:ready-qa` issue. It is a brand-new context that
never saw the Developer sub-agent's work - that is the point, and it is what makes the verification
genuinely independent. It reads the issue (including the Developer's report and any prior defect
comment), checks out the PR branch, verifies every acceptance criterion in the running app with its
own proof, runs the regression + CenCon checks, and produces a QA proof report. It returns a
`RESULT` block; you read only that.

- **PASS** (`outcome: pass`, `merged: yes`): the sub-agent labeled `flow:done`, squash-merged the PR
  to main with the clean-build gate (QA Step 3a), and closed the issue. Go to Step 4.
- **FAIL** (`outcome: fail`): the sub-agent labeled `flow:qa-failed` with a specific, reproducible
  defect (in its issue comment; the one-liner is in `defect`). **bounce counter += 1.**
  - If **bounce counter >= 3**: stop the autonomous loop. Label `flow:needs-human`, comment a
    summary of the unresolved defect(s), and stop this issue (mirrors the 3-strike ping-pong guard
    in DEVELOPMENT_METHOD.md Section 5a).
    ```bash
    gh issue edit <ID> --repo thefrederiksen/cc-director --add-label flow:needs-human --remove-label flow:qa-failed
    ```
  - Else: go back to **Step 1** and spawn a **fresh Developer sub-agent**. It reads the issue
    (including the QA defect comment - that is how the defect crosses contexts, not via shared
    memory), fixes via its Step 6, and re-labels `flow:ready-qa`.
- **NEEDS-HUMAN** (`outcome: needs-human`): the QA sub-agent hit a merge conflict or a dirty
  post-merge build and escalated `flow:needs-human` without forcing it. **You stop this issue** -
  record the `defect`/`summary` line and move on (next issue if `--all`, else end).

### Step 3: Merge guard (inside QA's pass path, enforced here)

The squash-merge happens in QA Step 3a, but the loop owns the guard:

- Merge command: `gh pr merge <PR> --repo thefrederiksen/cc-director --squash --delete-branch`.
- **Build gate:** the merged result must build clean (`dotnet build cc-director.sln`) before the
  merge is treated as final.
- **Conflict or dirty build -> never force.** Re-label `flow:needs-human`, comment the exact
  failure, and stop this issue.

### Step 4: Report and (optionally) loop

Report a one-line result with the link:
```
Issue #NNN: MERGED (flow:done) - N/N criteria verified, squash-merged to main | link
Issue #NNN: ESCALATED (flow:needs-human) - 3 QA bounces, defect: <one line> | link
Issue #NNN: ESCALATED (flow:needs-human) - spec not ready: <DoR item> | link
```

- `--all` mode: clear context between issues (memory reset, DEVELOPMENT_METHOD.md Section 7), then
  return to Step 0 for the next `flow:ready-dev`. Stop when the queue is empty.
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
| Weak spec | Developer role rejects on Definition of Ready | `flow:needs-human`, stop issue |
| 3-strike | 3rd `flow:qa-failed` on the same issue | `flow:needs-human`, stop issue |
| Build gate | post-merge `dotnet build` not clean | `flow:needs-human`, do NOT merge |
| Conflict | `gh pr merge` reports a conflict | `flow:needs-human`, do NOT force |

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

## Reuses

- The `Agent` tool - spawns the per-phase sub-agents whose throwaway contexts keep the supervisor
  thin.
- `developer-agent` skill - the role a DEV sub-agent reads and follows (plan, implement, build,
  prove, hand to `flow:ready-qa`).
- `qa-agent` skill - the role a QA sub-agent reads and follows (independent verify,
  pass+squash-merge / fail-bounce).
- The Control API (loopback REST) + slot-5 test Director via `cc-director-launch` - the shared
  runtime the sub-agents drive for proof (CLAUDE.md rule 0b). Because phases are sequential, only one
  sub-agent uses slot 5 at a time.

---

**Skill Version:** 0.2 (DRAFT - thin supervisor + fresh sub-agent per phase; realizes issue #259)
**Implements:** the Implementation session loop in docs/cencon/DEVELOPMENT_METHOD.md (D2, D5)
**Builds on:** the `Agent` tool (per-phase sub-agents), developer-agent (DEV role), qa-agent (QA role + merge), DEVELOPMENT_METHOD.md
**Created:** 2026-06-10
**Changes in 0.2:** DEV and QA phases now run as fresh sub-agents (throwaway context, structured RESULT handback) instead of inline skill invocations - keeps the supervisor thin so it can drive many issues without context pollution. Added Execution model, handback format, concurrency rule.
