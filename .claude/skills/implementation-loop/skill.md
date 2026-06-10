---
name: implementation-loop
description: Supervisor of the CenCon Implementation session for cc-director (issue #259). Drives one GitHub issue through Developer -> QA -> Developer -> QA until QA passes, then QA squash-merges to main on a clean build; escalates flow:needs-human on a weak spec or 3 QA bounces. --all drains the flow:ready-dev queue. Triggers on "/implementation-loop", "implementation loop", "drive this issue to done", "dev-qa loop".
---

# Implementation Loop (CenCon Development Method - cc-director)

You are the **Implementation session supervisor**. You run the Developer and QA roles in one place
and loop them against a single issue until QA passes and the change is **merged to main**. This is
the runtime of issue #259: Developer + QA collapsed into one looping session.

**Read the contract first:** `docs/cencon/DEVELOPMENT_METHOD.md`. You orchestrate the Developer
Agent (`/developer-agent`) and QA Agent (`/qa-agent`) skills - their laws and proof bars still
govern; you only sequence them and own the loop guards. That document wins on any disagreement.

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

## Quick Reference

| Phase | Role | Outcome it can produce |
|-------|------|------------------------|
| DEV | `/developer-agent` | `flow:ready-qa` (implemented + proof) OR `flow:needs-human` (weak spec) |
| QA | `/qa-agent` | `flow:done` + **squash-merge to main** OR `flow:qa-failed` (bounce) |
| GUARD | this skill | `flow:needs-human` after 3 bounces, or on merge conflict / dirty build |

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

### Step 1: DEV phase

Invoke the **Developer Agent** role (`/developer-agent`) on the issue. It plans, implements against
every acceptance criterion, builds clean (`dotnet build cc-director.sln`), proves it on a slot-5
test Director launched via the `cc-director-launch` scheduled task, commits proof to the PR branch,
and labels `flow:ready-qa`.

- If the Developer role finds the spec **not ready** (fails the Definition of Ready), there is no
  Product session here to re-sharpen it. The Developer role escalates `flow:needs-human` and stops.
  **You stop this issue too** - report the missing DoR items and move on (next issue if `--all`,
  else end).

### Step 2: QA phase

Invoke the **QA Agent** role (`/qa-agent`) on the now-`flow:ready-qa` issue. It verifies every
acceptance criterion **independently** in the running app with its own proof (it does NOT trust the
Developer role's screenshots), runs the regression + CenCon checks, and produces a QA proof report.

- **PASS:** every criterion verified. QA labels `flow:done`, then **squash-merges the PR to main**
  with a clean-build gate (QA Step 3a), and closes the issue. Go to Step 4.
- **FAIL:** QA labels `flow:qa-failed` with a specific, reproducible defect. **bounce counter += 1.**
  - If **bounce counter >= 3**: stop the autonomous loop. Label `flow:needs-human`, comment a
    summary of the unresolved defect(s), and stop this issue (mirrors the 3-strike ping-pong guard
    in DEVELOPMENT_METHOD.md Section 5a).
    ```bash
    gh issue edit <ID> --repo thefrederiksen/cc-director --add-label flow:needs-human --remove-label flow:qa-failed
    ```
  - Else: go back to **Step 1** (Developer role reads the defect, fixes via its Step 6, re-labels
    `flow:ready-qa`).

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

## Memory reset between issues (--all mode)

Each issue is driven in a fresh context so no spec, fixture, or assumption bleeds into the next
(DEVELOPMENT_METHOD.md Section 7, DECIDED D2 - this skill IS the supervisor that implements the
reset). Use `/clear` between issues, or re-seed the session per item.

## Loop guards (so it can neither spin nor merge recklessly)

| Guard | Trigger | Action |
|-------|---------|--------|
| Pre-flight | dirty working tree or wrong base branch (Step 0a) | STOP before any work; ask the human - never auto-stash/discard |
| Weak spec | Developer role rejects on Definition of Ready | `flow:needs-human`, stop issue |
| 3-strike | 3rd `flow:qa-failed` on the same issue | `flow:needs-human`, stop issue |
| Build gate | post-merge `dotnet build` not clean | `flow:needs-human`, do NOT merge |
| Conflict | `gh pr merge` reports a conflict | `flow:needs-human`, do NOT force |

## What you do NOT do

- You do not implement or verify directly - you invoke the Developer and QA roles, which carry
  their own laws and proof bars. You own the sequencing and the guards.
- You do not merge outside a clean QA pass, and you never force a merge through a conflict or a
  dirty build.
- You do not skip the Developer role's proof or the QA role's independent verification to "save a
  loop" - the adversarial verify is the point.
- You do not extend the merge authority beyond this loop.

## Reuses

- `developer-agent` - the DEV phase (plan, implement, build, prove, hand to `flow:ready-qa`).
- `qa-agent` - the QA phase (independent verify, pass+squash-merge / fail-bounce).
- The Control API (loopback REST) + slot-5 test Director via `cc-director-launch` - the shared
  runtime both roles drive for proof (CLAUDE.md rule 0b).

---

**Skill Version:** 0.1 (DRAFT - the Implementation session supervisor; realizes issue #259)
**Implements:** the Implementation session loop in docs/cencon/DEVELOPMENT_METHOD.md (D2, D5)
**Builds on:** developer-agent (DEV phase), qa-agent (QA phase + merge), DEVELOPMENT_METHOD.md
**Created:** 2026-06-10
