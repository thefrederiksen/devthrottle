# CC Director - CenCon Development Method

**Schema:** CenCon Method v1.0 (Development Governance)
**Status:** DRAFT v0.1
**Last Updated:** 2026-06-10
**Owner:** Support Agent (maintains this document)
**Adapted from:** mindzieWeb `docs/cencon/DEVELOPMENT_METHOD.md` (same method, GitHub-Issues tracker)

---

## 1. Purpose

CenCon already governs how this repository is **documented** (architecture_manifest.yaml,
security_profile.yaml, INDEX.md). This document extends CenCon to govern how this repository is
**changed**.

It defines a four-agent development process and one hard rule:

> **No code is written without a clearly-defined work item.**

When you sit down at cc-director, you do not start editing files. You start a work item, you make
it clearly defined, and then you let the Developer Agent and QA Agent carry it the rest of the way.
This is not optional guidance - it is the method the repository enforces.

The four agents are not hypothetical, but they no longer map one-to-one onto running sessions
(issue #259): the **Developer and QA roles run inside a single Implementation session** that loops
build&lt;-&gt;verify, alongside a **Product** session and a **Support** session. The four roles and
the `flow:*` labels are unchanged; what changed is the session topology. cc-director is the runtime
of its own development method.

---

## 2. The Hard Gate

Every code change traces back to exactly one **GitHub issue** (in `thefrederiksen/cc-director`)
that has passed the **Definition of Ready** (Section 5). There are no exceptions for "small"
changes.

Rationale (consistent with CLAUDE.md proof-based verification):

- The issue is the durable, auditable handoff artifact between agents.
- A change with no issue has no spec, no acceptance criteria, and no proof target.
- Labels on the issue are the bus: they overlap cleanly with a normal GitHub flow and let any
  agent (or human) pick up where another left off.

The only work that may happen without an issue:

- Drafting/refining an issue (Product Agent's own job).
- Answering questions (Support Agent - read-only, never edits code).

---

## 3. The Four Agents

Each agent is a single-purpose role. Product and Support are their own Claude Code sessions; the
Developer and QA roles run together inside one **Implementation** session (issue #259) that loops
build&lt;-&gt;verify in place rather than handing off between two sessions. Roles hand work down the
line by changing the `flow:*` label on the issue - between separate sessions explicitly, and within
the Implementation session as an internal `flow:ready-dev` -&gt; `flow:ready-qa` loop. Between work
items, an agent's memory is cleared so no context bleeds from the last ticket into the next
(Section 7).

### 3.1 Product Agent

- **Job:** Own issues. Create them and sharpen them until they meet the Definition of Ready. The
  only way work enters the system.
- **Never:** Writes implementation code.
- **Input:** A raw request, idea, bug report, or an issue bounced back with `flow:rejected`.
- **Output:** An issue labeled `flow:ready-dev` that satisfies the Definition of Ready.
- **Tracker surface:** the `enter-issue` skill (create) and `gh` CLI; issue kinds map to the
  existing `bug` / `enhancement` labels (plus area labels such as `cockpit`, `installation`).

### 3.2 Developer Agent

- **Job:** Implement one ready issue end to end.
- **Reject path:** If the item does not meet the Definition of Ready, the Developer Agent labels
  it `flow:rejected`, writes WHY in a comment, and returns it to Product. It does not "do its best"
  with a weak spec.
- **Definition of Done it must satisfy before handing off:** Section 6.
- **Proof:** On completion, commits a screenshot and an HTML report to the PR branch under
  `docs/cencon/proof/issue-<n>/` and links them repo-relative in an issue comment (Section 6a).
- **Input:** An issue labeled `flow:ready-dev`.
- **Output:** Either `flow:rejected` (back to Product) or `flow:ready-qa` with proof linked.

### 3.3 QA Agent

- **Job:** Loop over `flow:ready-qa` issues, verify each independently with proof, pass or fail.
- **Independence:** Verifies actual behavior in the running app - it does not trust the Developer
  Agent's report. (SOC 2 separation of duties is preserved as ROLE separation: when Developer and QA
  run inside one Implementation session (#259), the QA pass is still performed deliberately and
  independently against what was asked - verify, fix, re-verify - never a rubber-stamp of the code
  just written.)
- **Fail path:** Labels `flow:qa-failed`, writes WHY, returns to Developer.
- **Pass path:** Labels `flow:done`, links the QA proof report, and - when running inside the
  Implementation session (the `implementation-loop` skill) - **squash-merges the PR to main** on a
  clean build, then closes the issue (DECIDED D5). Standalone QA does not merge.
- **Never stops on its own** - it takes the next QA item until the queue is empty.
- **Input:** An issue labeled `flow:ready-qa`.
- **Output:** `flow:qa-failed` (back to Developer) or `flow:done`.

### 3.4 Support Agent

- **Job:** The idle seat. Answers questions about the codebase and about what the other three
  agents are doing. Owns and maintains the CenCon documents (this file and `docs/cencon/`).
- **Never:** Touches issues or implementation code. Read-only with respect to product code.
- **Input:** Questions.
- **Output:** Answers, and CenCon doc updates when the architecture/method drifts.

---

## 4. The Label State Machine

State lives as a `flow:*` **label** on the GitHub issue. One agent watches for one trigger label.

```
  (raw request / idea / bug)
            |
            v
      [ Product Agent ]
            |
            |  meets Definition of Ready
            v
      flow:ready-dev ----------------------------+
            |                                     |
            v                                     |
      [ Developer Agent ]                         |
            |                                     |
     +------+-------------------------+           |
     |                                |           |
 weak spec                      implemented       |
     |                          + proof linked     |
     v                                |           |
 flow:rejected --> [ Product Agent ]--+ (re-sharpen, re-label ready-dev)
                                      ^
            +-------------------------+
            |
            v
      flow:ready-qa
            |
            v
      [ QA Agent ]
            |
     +------+----------------+
     |                       |
  defect found          verified
     |                       |
     v                       v
 flow:qa-failed         flow:done
     |                  (issue closed)
     v
 [ Developer Agent ] (fix, re-label ready-qa)
```

Label vocabulary (single source of truth - these labels exist in the repo):

| Label | Meaning | Owner who sets it | Next agent |
|-------|---------|-------------------|------------|
| `flow:ready-dev` | Spec is ready to implement | Product Agent | Developer Agent |
| `flow:rejected` | Spec too weak; see comment | Developer Agent | Product Agent |
| `flow:ready-qa` | Implemented + proof linked | Developer Agent | QA Agent |
| `flow:qa-failed` | Defect found; see comment | QA Agent | Developer Agent |
| `flow:done` | Verified with proof | QA Agent | (closed) |
| `flow:needs-human` | 3-strike escalation | Product Agent | the human |

Only one `flow:*` label is present at a time. Changing the label IS the handoff:

```bash
gh issue edit <N> --repo thefrederiksen/cc-director --add-label flow:ready-qa --remove-label flow:ready-dev
```

DECIDED (D1): the `flow:*` labels are authoritative. GitHub's open/closed state is cosmetic and is
not required to track these states; an issue is only closed when it reaches `flow:done`.

DECIDED (D3): the reject round-trip is fully autonomous. When the Developer Agent labels
`flow:rejected`, the Product Agent re-sharpens and re-submits with no human in the loop. (Guard
against ping-pong: see Section 5a.)

---

## 5. Definition of Ready (Product Agent's bar)

An issue is `flow:ready-dev` only when ALL of the following are true. The Developer Agent rejects
anything that fails this list.

1. **Title** is a single, specific outcome, with an area prefix (see Area Prefixes below).
2. **Problem / value:** one paragraph - what is wrong or wanted, and why it matters.
3. **Scope:** explicitly states what is IN and what is OUT.
4. **Acceptance criteria:** a checklist of observable, testable conditions. Each must be verifiable
   by the QA Agent with a screenshot, a Control API response, or a log/command - no "should feel
   faster".
5. **Affected area:** which projects/containers (per `architecture_manifest.yaml`) are expected to
   change - e.g. `CcDirector.Core`, `CcDirector.Avalonia`, `CcDirector.ControlApi`,
   `CcDirector.Gateway`, `CcDirector.GatewayApp`, `CcDirector.Engine`, a `cc-*` tool.
6. **Proof target:** what the success screenshot/report must show.
7. **No invented design intent:** any assumption about intended behavior is flagged as an
   assumption inside the issue, not stated as fact.

If a request cannot be made to satisfy this list, it is not ready - the Product Agent keeps working
it (or asks the human), it does not pass it down.

### 5a. Ping-pong guard (autonomous reject loop)

Because the reject round-trip is fully autonomous (D3), a spec must not bounce forever between
Product and Developer:

- Each `flow:rejected` -> re-sharpen -> `flow:ready-dev` cycle increments a reject count recorded in
  an issue comment.
- On the **third** rejection of the same issue, the agents stop the autonomous loop, label the
  issue `flow:needs-human`, and leave a comment summarizing the disagreement for the human to
  resolve.
- The Developer Agent's rejection comment must be specific (which DoR item failed and why) so
  Product can act on it rather than re-submitting the same spec.

---

## 6. Definition of Done (Developer Agent's bar)

Before labeling `flow:ready-qa`, the Developer Agent must have ALL of:

1. Code implemented against every acceptance criterion in the issue.
2. `review-code` skill invoked and `docs/CodingStyle.md` + `docs/VisualStyle.md` honored before/while
   writing code (per CLAUDE.md). UI changes must comply with `docs/VisualStyle.md`.
3. Solution builds clean: `dotnet build cc-director.sln`. For a runnable test binary, build a
   dev slot with `scripts\local-build-avalonia.ps1 -Slot 5` (slot 5+ is reserved for agent test
   Directors; never use the main build or slots 1-4 - CLAUDE.md rule 0b).
4. **Proof-based verification** performed (per CLAUDE.md): the change exercised in the running app.
   Launch the test Director via the `cc-director-launch` scheduled task (NEVER spawn cc-director.exe
   from inside the agent's own process tree - CLAUDE.md rule 0b), drive it via the Control API
   (loopback REST) and/or screenshots, and capture a screenshot showing the expected result. State
   Expected vs Actual for each acceptance criterion.
5. **CenCon not drifted:** if the change altered architecture or security posture, the relevant
   `docs/cencon/` files are updated in the same change, and no blocking security rule
   (DT-01..DT-NN in `security_profile.yaml`) is violated.
6. An **HTML report** committed and linked (Section 6a): what was implemented, which acceptance
   criteria are met, screenshots, and an explicit "I believe this is finished" statement.

A missing proof report is itself a Definition-of-Done failure - the issue does not advance.

### 6a. Proof on GitHub (the adaptation that differs from mindzieWeb)

GitHub's `gh` CLI cannot attach arbitrary files/images to an issue the way Azure DevOps work items
can. Therefore proof travels on the **pull request branch**:

1. The Developer Agent works on a branch and opens a PR. Commits to the **PR branch** are authorized
   by adoption of this method.
2. The screenshot(s) and the HTML report are committed under `docs/cencon/proof/issue-<n>/`
   (e.g. `report.html`, `before.png`, `after.png`).
3. The Developer Agent links them **repo-relative** in an issue comment, alongside the PR link:

   ```
   Proof: docs/cencon/proof/issue-123/report.html  (PR #124)
   ```

4. **Merging the PR to `main`:** the Developer Agent never merges - branch commits are authorized, a
   merge is not. Inside the `implementation-loop`, the **QA role** squash-merges to main on a clean
   pass as part of `flow:done` (DECIDED D5; user-granted authority scoped to the loop; clean-build
   gate, never forced through a conflict). A **standalone** QA session does not merge - it stops at
   `flow:done` and leaves the merge to the human. Either way, the Developer Agent's handoff artifact
   to QA is the issue + the proof on the branch, not a merged change.

The QA Agent's proof report follows the same path: committed under `docs/cencon/proof/issue-<n>/`
(e.g. `qa-report.html`) and linked from the `flow:done` (or `flow:qa-failed`) comment.

---

## 7. Memory Reset Between Work Items

Each agent processes exactly one issue per fresh context. When an item leaves an agent (handed
down or bounced back), that agent's session memory is cleared before it picks up the next item.
This prevents spec, code, or assumptions from one ticket leaking into another.

DECIDED (D2): the `implementation-loop` skill is the supervisor of the Implementation session, and
the memory reset is achieved by **running each phase in a fresh sub-agent**. The supervisor stays
thin - it holds only the issue number, the current `flow:*` label, the bounce counter, and a
one-line result ledger. Each DEV phase (following `developer-agent`) and each QA phase (following
`qa-agent`) is spawned as a separate sub-agent with a throwaway context; the sub-agent does all the
file reads, builds, and proof work in isolation and returns only a compact structured result. This
is why no spec, build log, or fixture bleeds between phases or between issues, and why `--all` can
drive many items without the supervisor's context filling up. The handoff between fresh sub-agents
relies on the method's existing durable state (the `flow:*` label, issue comments, and the PR
branch), not on shared memory - so a fresh QA sub-agent is also a more honestly independent verifier.
The 3-strike `flow:qa-failed` guard and the weak-spec `flow:needs-human` escalation live in the
supervisor; one phase runs at a time (DEV then QA), so sub-agents never collide on the slot-5 test
Director.

---

## 7a. Terminal Signal Contract (machine-readable loop outcome)

The `implementation-loop` skill (the supervisor of the Implementation session, Section 7 / D2)
reaches exactly one terminal outcome per issue per run. So that an external watcher - specifically
the autonomous work-item queue runner (epic #270) - can know when one loop run has finished without
parsing prose, the loop emits a single **machine-readable terminal signal** as the last thing it
prints to the session transcript on EVERY terminal path. This is a contract, not a feature: child 3
of #270 (the Gateway queue runner) is built against the spec recorded here.

### The three signal values

There are exactly three terminal-signal values:

| Signal | Meaning | Loop outcomes that map to it |
|--------|---------|------------------------------|
| `done` | The issue was verified and squash-merged to main; the run is fully complete. | MERGED (`flow:done`) |
| `needs-human` | The run stopped cleanly and a human must act; work is committed/parked, nothing is lost. | PARKED (3-strike `flow:needs-human`), ESCALATED (weak-spec `flow:needs-human`), merge conflict / dirty post-merge build (`flow:needs-human`) |
| `failed` | The run could not complete - it stopped abnormally and produced no usable result. | Pre-flight dirty-tree / leftover-stash stop (Step 0a), wrong-base stop, build-tool failure, crash, or any abnormal exit |

`done` and `needs-human` correspond to the loop's existing clean terminal outcomes (the work is
preserved either as a merge or as a parked PR). `failed` is the catch-all for any path on which the
loop cannot reach one of those two - the repository state is whatever it was, and a human should
look at why the run aborted.

### The sentinel block (transport = session transcript)

The signal travels on the **session transcript** (the surface the Gateway/Wingman already capture -
no new transport is introduced). The loop prints, as its final transcript output for the issue, a
single fenced sentinel block in this exact shape:

```
IMPL-LOOP-TERMINAL
issue: <N>
signal: done | needs-human | failed
pr: <pr number or none>
merged: yes | no
reason: <one line - why this terminal state>
```

- `issue` identifies the work item (so a watcher can correlate the signal with the item it started).
- `signal` carries exactly one of the three values above.
- `pr` is the PR number on `done`/`needs-human` paths that have one, or `none`.
- `merged` is `yes` only on the `done` signal; `no` on `needs-human` and `failed`.
- `reason` is a single human-readable line explaining the terminal state.

### Rules

- The sentinel block is emitted **in addition to** the loop's existing human-readable one-line
  report (Step 4 of the `implementation-loop` skill), never instead of it. The two coexist; the
  human report is unchanged.
- **Exactly one** sentinel block is emitted per issue per run, as the loop's final action on that
  issue, so a watcher reading the last sentinel block gets an unambiguous answer.
- The block is the loop's last transcript output for the issue (in single-issue mode) or for that
  issue's slot (in `--all` mode, one block per issue as each finishes).

This contract is the keystone of #270: every other child of that epic depends on the three values
and the block format defined here.

## 8. Relationship to the Rest of CenCon

- **architecture_manifest.yaml** tells the Product Agent which containers an item will touch
  (DoR item 5) and tells the Developer Agent where to update docs (DoD item 5).
- **security_profile.yaml** (OWASP Desktop DT-01..DT-NN + SOC 2 control mappings) is a checklist
  the Developer Agent must not violate and the QA Agent re-checks. A drift here can bounce an item.
- **INDEX.md** is the human entry point; this file is linked from it under Related Documentation.
- **Drift rule:** the 30-day documentation drift rule (enforced by `/review-code`) applies to this
  method document too - the Support Agent keeps it current as the process evolves.

---

## 9. Open Decisions (to resolve as we build)

| ID | Decision | Status |
|----|----------|--------|
| D1 | Labels vs GitHub open/closed state as authoritative | DECIDED: labels authoritative |
| D2 | Memory-reset + loop mechanism (who restarts agents) | DECIDED: the `implementation-loop` skill is the supervisor; it sequences DEV->QA->DEV until pass and `/clear`s (or re-seeds) between issues in `--all` mode (Section 7) |
| D3 | Reject round-trip: human pause or fully autonomous | DECIDED: fully autonomous, 3-strike human escalation (Section 5a) |
| D4 | Proof transport on GitHub | DECIDED: committed to PR branch under docs/cencon/proof/issue-<n>/, linked repo-relative; branch commits authorized (Section 6a) |
| D5 | Whether merged-to-main is part of `flow:done` or a separate human step | DECIDED: inside the `implementation-loop`, QA squash-merges to main on a clean pass as part of `flow:done` (user-granted authority, scoped to the loop); a standalone QA session still leaves the merge to the human |

---

*Extends CenCon Method v1.0. Source of truth for how cc-director is changed.*
