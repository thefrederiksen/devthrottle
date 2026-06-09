# CC Director - CenCon Development Method

**Schema:** CenCon Method v1.0 (Development Governance)
**Status:** DRAFT v0.1
**Last Updated:** 2026-06-09
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

The four agents are not hypothetical: they are the four running cc-director sessions
(product / developer / QA / support). cc-director is the runtime of its own development method.

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

Each agent is a single-purpose Claude Code session (one of the four running cc-director sessions).
Agents do not talk directly; they hand work down the line by changing the `flow:*` label on the
issue. Between work items, an agent's memory is cleared so no context bleeds from the last ticket
into the next (Section 7).

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
  Agent's report. (SOC 2 separation of duties; the QA session is a different identity from the
  developer session.)
- **Fail path:** Labels `flow:qa-failed`, writes WHY, returns to Developer.
- **Pass path:** Labels `flow:done`, closes the issue, links the QA proof report.
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

1. The Developer Agent works on a branch and opens a PR (the `implement-issue` skill already does
   this). Commits to the **PR branch** are authorized by adoption of this method.
2. The screenshot(s) and the HTML report are committed under `docs/cencon/proof/issue-<n>/`
   (e.g. `report.html`, `before.png`, `after.png`).
3. The Developer Agent links them **repo-relative** in an issue comment, alongside the PR link:

   ```
   Proof: docs/cencon/proof/issue-123/report.html  (PR #124)
   ```

4. **Merging the PR to `main` still requires the human's explicit approval** - branch commits are
   authorized, a merge is not. The handoff artifact to QA is the issue + the proof on the branch,
   not a merged change.

The QA Agent's proof report follows the same path: committed under `docs/cencon/proof/issue-<n>/`
(e.g. `qa-report.html`) and linked from the `flow:done` (or `flow:qa-failed`) comment.

---

## 7. Memory Reset Between Work Items

Each agent processes exactly one issue per fresh context. When an item leaves an agent (handed
down or bounced back), that agent's session memory is cleared before it picks up the next item.
This prevents spec, code, or assumptions from one ticket leaking into another.

OPEN DECISION (D2): mechanism for the reset and the loop. Candidate: cc-director itself (the
supervisor) restarts or re-seeds the agent session per item, or the agent uses `/clear` between
items. To be specified when we wire the QA Agent's loop. (cc-director's own session-restart and
handover machinery is the natural implementation surface.)

---

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
| D2 | Memory-reset + loop mechanism (who restarts agents) | OPEN: cc-director supervisor / `/clear` per item (specify when building QA loop) |
| D3 | Reject round-trip: human pause or fully autonomous | DECIDED: fully autonomous, 3-strike human escalation (Section 5a) |
| D4 | Proof transport on GitHub | DECIDED: committed to PR branch under docs/cencon/proof/issue-<n>/, linked repo-relative; branch commits authorized, merge to main needs explicit human OK (Section 6a) |
| D5 | Whether merged-to-main is part of `flow:done` or a separate human step | OPEN: currently a separate human step (merge is never autonomous) |

---

*Extends CenCon Method v1.0. Source of truth for how cc-director is changed.*
