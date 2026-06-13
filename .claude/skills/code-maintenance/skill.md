---
name: code-maintenance
description: Audit the cc-director repo for hygiene problems (junk files, root clutter, stale docs, dead/unreferenced code, archived dirs) and produce an incremental cleanup plan, optionally filing one-at-a-time GitHub cleanup issues. Triggers on "/code-maintenance", "clean up the repo", "repo hygiene", "code maintenance", "tidy the codebase".
---

# Code Maintenance

Keeps the cc-director codebase clean and easy to maintain over time. This skill AUDITS
the repository for accumulated cruft, classifies what it finds, and produces an
INCREMENTAL cleanup plan so that nothing is removed in one risky sweep. The guiding
rule: every removal is small, reversible, and verified by a build + test run before the
next removal happens.

This is the broad, whole-repo hygiene skill. For the narrow "which C# projects/classes
are unused" question it reuses the existing `dead-code-finder` skill rather than
duplicating it.

## CRITICAL: Propose, do not destroy

**This skill never deletes, moves, or rewrites code on its own.** It produces a plan and,
with explicit approval, files GitHub issues. Actual removal is done by a developer
(or the developer-agent) working ONE issue at a time, building and testing after each.

- Default mode is read-only audit -> categorized report.
- Filing GitHub issues requires explicit user approval (it is outward-facing).
- Anything uncertain goes to a human as UNSURE - never silently deleted.
- An "archive" is acceptable when unsure, but git history is the real safety net, so
  prefer deletion-via-issue over hoarding once something is confirmed dead.

## Quick Reference

| Step | Action |
|------|--------|
| 1 | Run the audit checklist across root, code, docs, tooling |
| 2 | Classify each finding: DELETE / ARCHIVE / MOVE / KEEP / UNSURE + risk |
| 3 | Group findings into incremental, independently-testable work units |
| 4 | Present the plan; get approval before filing issues |
| 5 | File one epic + one child issue per work unit (label `maintenance`) |
| 6 | Report what was filed; the cleanup itself is done one issue at a time |

## When to run (cadence)

Run on a regular cadence (e.g. monthly, or after a large feature lands) and whenever the
root directory or `docs/` starts feeling cluttered. Healthy targets:

- No junk or garbage-named files anywhere (see Audit checklist #1).
- Root holds only project-level essentials (sln, props, README, LICENSE, gitignore,
  CLAUDE.md, and intentional top-level dirs). No stray scripts, temp, or backup files.
- `docs/` separates LIVE reference from ARCHIVED history; stale handovers/plans/trackers
  are not mixed in with current docs.
- Every project in `src/` is either in `cc-director.sln` and referenced, or removed.
- No `*-archived` / `*-old` / `*-backup` directories lingering without a decision.

## Audit checklist

Work top-down. Use `git ls-files` (tracked files only - ignore bin/obj/node_modules).

### 1. Junk / garbage-named files (highest confidence, lowest risk)
- `git ls-files | grep '"'` - filenames with quotes/escapes are almost always corruption
  from a botched shell `cp`/`mv` (e.g. mangled names embedding a command). DELETE.
- Search for `*.bak`, `*.old`, `*.orig`, `*.tmp`, `*~`, `nul` (Windows reserved - always
  delete), accidental editor/scratch files.

### 2. Root clutter / misplaced files
- List the root: anything that is not a top-level essential or an intentional directory
  is a candidate to MOVE (into `scripts/`, `docs/`, `tools/`, etc.) or DELETE.
- One-time migration scripts (e.g. `step1-backup.bat`, `step2-migrate.bat`) once the
  migration is done: ARCHIVE or DELETE.
- Duplicate skill trees: a root `skills/` directory is suspect when the canonical one is
  `.claude/skills/`. Confirm with grep before recommending removal.

### 3. Stale docs (docs/ is the biggest cruft magnet)
- HANDOVER docs (`*Handover*`, `handover-*`): stale once the work merged -> ARCHIVE.
- PLAN docs (`plan-*.md`, `docs/plans/**`): if the work shipped -> ARCHIVE. Newer
  `docs/plans/` generally supersedes root-level `plan-*.md`.
- TRACKER docs (`*-tracker.md`): stale progress trackers -> ARCHIVE.
- DATED / AUDIT snapshots (`*AUDIT*`, names with a date) -> ARCHIVE.
- External-doc copies (e.g. a vendor SDK reference pasted in) -> DELETE, link instead.
- Committed DATA/backup dumps (`*-backup.json`, large one-off PNGs) -> DELETE or move to
  test fixtures; they should not live in docs/.
- KEEP: README-referenced core (CodingStyle.md, VisualStyle.md, cli-reference.md,
  CC_TOOLS.md, PHILOSOPHY.md), `docs/architecture/**`, `docs/public/**`,
  `docs/cencon/**` (authoritative per the CenCon method), active `docs/plans/**`.

### 4. Dead / unreferenced code
- Invoke the `dead-code-finder` skill for the C# pass, OR run the checks directly:
  - `src/*/` directories NOT present in `cc-director.sln` (grep the sln for each dir).
  - Empty project dirs (only bin/obj on disk, no tracked source / no .csproj).
  - Test projects that exist but are NOT in the sln (e.g. a `*.Tests` dir missing) -
    usually ADD-to-sln, not delete.
  - Projects in the sln but referenced by NO other project's `.csproj` AND with no
    `using` of their namespace -> dead-code candidate (verify no reflection/DI/runtime
    load before recommending DELETE).
- For tools/: compare `tools/*` against `tools/registry.json` and grep docs/scripts.
  An unregistered tool may be (a) infra library (cc_shared, cc_storage) = KEEP,
  (b) test fixture generator = KEEP, or (c) genuinely orphaned = verify then decide.
  Beware false positives: a tool referenced only in `build-tools.bat` or
  `docs/public/` is still in use.

### 5. Archived / superseded directories
- `*-archived`, `*-old`, `*-deprecated`, `*-backup` dirs: each needs an explicit
  decision (DELETE - git history has it / MOVE to a dedicated archive area). Do not
  leave them undecided.
- Multiple variants of the same tool (e.g. several setup-wizard variants): identify the
  current/shipped one, then ARCHIVE or DELETE the rest. Watch for `.csproj` NAME
  COLLISIONS between variants.

### 6. .gitignore cruft (signal, not action)
- A `.gitignore` full of one-off entries (specific PNGs, specific stderr txt files,
  specific backup json) is a SYMPTOM that junk was committed then patched over. Use it as
  a map of past messes; verify whether the referenced files are still tracked.

## Classification rules

Tag every finding with an action and a risk level. Be honest about uncertainty.

| Action | Meaning | When |
|--------|---------|------|
| DELETE | Remove outright | Junk, corruption, confirmed-dead, external-doc copies |
| ARCHIVE | Move to an archive area, keep history | Completed handovers/plans/trackers, superseded specs |
| MOVE | Relocate to its proper home | Misplaced-but-useful files |
| KEEP | Leave it | Core reference, active code, infra libs, test fixtures |
| UNSURE | Ask a human | Anything where references are ambiguous or intent unknown |

Risk: LOW (no build/runtime impact, e.g. a stray file), MEDIUM (removes a project/dir -
needs a build + test), HIGH (touches shipped/setup/release paths - needs coordination).

## The incremental-removal rule (the whole point)

Removing a lot at once makes it impossible to know what broke. So:

- **One logically-independent change per GitHub issue.** A developer removes that one
  thing, runs the build + tests (`dotnet build cc-director.sln`, then the test suites),
  confirms green, then moves to the next issue.
- Order issues easiest/safest first: junk files -> empty dirs -> single dead projects ->
  multi-variant consolidations / shipped-path changes last.
- Each child issue must state: exactly what to remove, how to verify nothing broke
  (build + which tests + any manual smoke), and the rollback (git revert).
- Group only things that truly stand or fall together (e.g. "delete 3 garbage files" is
  one issue even though it's 3 files).

## Archive convention

When ARCHIVE is the call:
- Docs: move under `docs/archive/<category>/` (e.g. `docs/archive/handovers/`,
  `docs/archive/plans/`) with a short `README.md` noting provenance. Keep the live
  `docs/` tree lean.
- Code: prefer DELETE once confirmed dead - git history is the archive. Only keep a
  `*-archived` directory when there is a concrete near-term reason to read it.

## Filing GitHub issues (approval-gated)

After the plan is approved:

1. Ensure a `maintenance` label exists:
   `gh label create maintenance --color "5319e7" --description "Code base maintenance / cleanup" 2>/dev/null || true`
2. Create the EPIC (tracking) issue: title `[Maintenance] Code base cleanup - <date>`,
   body explains the incremental method and lists each child as a checkbox (fill in
   numbers after creating children, or create epic first and edit).
3. Create one CHILD issue per work unit. Each child:
   - Title: `[Maintenance] <concise action>`
   - Body sections: What to remove + paths; Why it's safe; How to verify (build + tests +
     smoke); Rollback; Risk level.
   - Labels: `maintenance` plus `flow:ready-dev` if it meets the Definition of Ready, and
     `documentation` for docs-only issues.
   - Milestone: `Docs / Ops / Verify-Close` for docs, `1. Infrastructure & Framework`
     for code/tooling (adjust to current milestones).
4. Link children back to the epic; report the full list of created issue numbers.

Use `gh issue create --title ... --body-file <tmpfile> --label ... --milestone ...`.
Write bodies to a temp file (heredoc) to keep formatting; ASCII only.

## Examples

**User:** clean up the repo

**Agent:**
1. Runs the audit checklist (git ls-files surveys, grep for orphan src projects, docs
   staleness scan, junk-name scan).
2. Produces a categorized report: e.g. "3 garbage-named files in tools/cc-computer
   (DELETE, LOW); root skills/ duplicate (DELETE, LOW); src/CcDirector.TestHarness empty
   (DELETE, LOW); src/CcDirector.VoskStt unreferenced (DELETE, MEDIUM - verify);
   ~20 stale handover/plan/tracker docs (ARCHIVE, LOW)."
3. Proposes an epic + N child issues, ordered safest-first.
4. On approval, creates the `maintenance` label, the epic, and the children; reports the
   issue numbers.

**User:** /code-maintenance --report-only

**Agent:** Runs the audit and returns the categorized report only; files nothing.

## Reference

- Dead C# code pass: `.claude/skills/dead-code-finder/skill.md`
- CenCon flow + labels: `docs/cencon/DEVELOPMENT_METHOD.md`
- Coding/visual standards (do not "clean up" these away): `docs/CodingStyle.md`,
  `docs/VisualStyle.md`
- Windows note: delete any `nul` file immediately - it breaks git on Windows.
- ASCII only in all output and file content.

---

**Skill Version:** 1.0
**Last Updated:** 2026-06-13
