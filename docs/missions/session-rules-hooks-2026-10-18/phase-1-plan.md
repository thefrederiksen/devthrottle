# Session Rules part 2 - phase 1 plan and wire contract

Written 2026-10-18 by the phase 1 Tech Lead ("Session Rules - Tech Lead - phase 1 hook trigger and
scopes"). Branch `mission/session-rules-hooks-p1`, cut from `origin/main` at `7a365544b`. The WORK is
`mission.md` section 6, phase 1. This file is how phase 1 is split, the rulings the Tech Lead made so
three Developers build one thing and not three, and the exact shapes that cross between their pieces.

Phase 1, exactly: scopes and a `kind` on every rule (existing rules migrate to `everywhere` and
`screen`); git-remote repo identity; the Director's per-session rules file kept current like the
preamble; a Claude Code PreToolUse hook for the shell tool only; Match handler only; DRY RUN ONLY -
it records "would have denied" and never blocks; decisions written to the session's event drop and
forwarded by the Director to the Gateway as firings; Linux and Windows. No network call and no
listening port in the hook. The hook's added time per shell command is measured.

Phase 2 (agents manage rules) runs at the same time in another worktree. Every store change here is
ADDITIVE: new columns, new routes, nothing renamed, nothing removed. Whichever phase lands second
regenerates the migration model snapshot - that is mechanical, not a dispute.

---

## Tech Lead rulings (phase 1 only)

**P1. Match is literal text.** A Match handler holds one or more literal strings. A rule matches a
command when the command text CONTAINS any of them, compared ordinally (case-sensitive). No regular
expressions, no globs: a pattern language is an expression language, which r15 keeps out, and a
regular expression run before every shell command is a performance and denial-of-service hazard in
the one place we cannot afford one. Widening this later is a product change.

**P2. Hook rule results in phase 1 are `deny` and `allow`.** `allow` exists only so a more specific
rule can override a broader `deny` (D2). Everything is dry run, so the recorded decision is
`would_deny` or `would_allow`. Nothing is ever blocked.

**P3. Most specific wins (D2), with a stated tie rule.** Rank: `repo_on_machine` 4, `repo` 3,
`machine` 2, `everywhere` 1. Among the rules that MATCH a command, the highest rank decides. Two
matching rules at the same rank that disagree: `deny` wins. The decision names the deciding rule and
every matching rule it overrode, so the phase 3 page can say "this rule decided, it overrode that one".

**P4. The D1 scope is a NEW field, `appliesTo`, beside the September `scope`.** The September `scope`
(agent, repository path, machine, mission) is the screen evaluator's session filter and is left exactly
as it is. Every rule gets `appliesTo`: `{ "kind": "everywhere" | "machine" | "repo" |
"repo_on_machine", "machines": [..], "exceptMachines": [..], "repo": ".." }`.
- `everywhere`: no machines, no repo.
- `machine`: either `machines` (one or more) or `exceptMachines` (all except some), never both, no repo.
- `repo`: `repo` set, no machines.
- `repo_on_machine`: `repo` and one or more `machines`, no `exceptMachines`.
Anything else is refused at write time with a stated reason. Existing rows migrate to `everywhere`.
- A SCREEN rule may only have `appliesTo` `everywhere` in phase 1 (refused otherwise, with a reason):
  the roster does not push a session's git remote yet, so a repo-scoped screen rule would silently
  never fire, and a silent never-fire is the worst shape a defect takes.
- A HOOK rule must leave the September `scope` filter empty (refused otherwise): one rule, one scope
  system.

**P5. Machine identity is the machine name the Director reports** (`Environment.MachineName`),
compared case-insensitively.

**P6. Repo identity is the normalised URL of the `origin` remote.** One implementation, in
`CcDirector.Gateway.Contracts` (referenced by the Gateway and by Core), `GitRemoteIdentity.Normalise`:
strip the scheme (`https://`, `ssh://`, `git://`), any `user@` or `user:password@`, any port; turn the
scp form `git@host:owner/repo` into `host/owner/repo`; drop a trailing `.git` and trailing slashes;
lower-case all of it. `https://github.com/TheFrederiksen/devthrottle.git`,
`git@github.com:thefrederiksen/devthrottle.git` and `ssh://git@github.com:22/thefrederiksen/devthrottle`
all become `github.com/thefrederiksen/devthrottle`. A checkout with no `origin` has no repo identity:
only `everywhere` and `machine` rules reach it, and the rules file says `"repo": ""`. A rule's `repo`
is normalised at write time, so the account may paste either form.

**P7. Kinds, events, tools, handlers accepted in phase 1.** `kind`: `screen`, `hook`,
`session_start` exist as values; creating a `session_start` rule is refused ("not built yet - phase
5"). A hook rule must say `event` `before_command`, `tool` `shell`, handler `match`; any other value is
refused with a reason naming the phase that brings it. Promoting a hook rule to live is refused ("live
hook rules arrive in phase 4"). A hook rule needs no screen description, trigger words, calls,
cooldown or daily cap - the write gate becomes kind-aware rather than demanding fields that mean
nothing for it.

**P8. Hook rules never reach the screen evaluator.** `RuleCandidateFilter` and anything that feeds the
model call consider `screen` rules only. A test proves a hook rule whose text appears on a screen is not
a candidate.

**P9. The PreToolUse hook is registered for the shell tool for every Director Claude Code session.**
Claude Code reads its hooks at launch and rules arrive mid-session, so registering only when a rule
exists would miss every rule created after launch. "Register only for tools that have rules" is met at
the tool level: the shell tool is the only one any rule kind can name in phase 1. The cost of a session
with no rules is measured and reported.

**P10. The hook never blocks, by construction.** The evaluator always exits 0 and prints nothing to
standard output, whatever it decides and whatever goes wrong. It catches at its entry point only and
writes what went wrong to its evaluation log.

**P11. The evaluator is one small native command**, a new project `src/CcDirector.RuleHook`
(executable `cc-rule-hook`), with NO project references - it must start fast - and source-generated
JSON. It contains the P3 resolver: in phase 1 it is the only place a decision is made. It ships beside
the Director's own executable, and the installer writes the hook command with its absolute path.

---

## The wire contract

These shapes cross between the three Developers' pieces. Build to them exactly. A change to one is a
change to this file first, agreed with the Tech Lead.

### W1. Effective rules, Gateway to Director

`GET /gateway/rules/effective?kind=hook&machine=<machine>&repo=<normalised repo, may be empty>`

Answers the hook rules whose `appliesTo` reaches that machine and repo, each with its rank, in this
shape (the `rules` items are W2 items):

```json
{ "machine": "devlinux", "repo": "github.com/thefrederiksen/devthrottle", "rules": [ ... ] }
```

Callable with the Director's own credential (and the user's). Not by a session key - that is phase 2's
decision for the whole `/gateway/rules` surface.

### W2. The per-session rules file, Director to hook

Path: `<CcStorage root>/session-rules/<sessionId>.json`, named to the hook by the environment variable
`CC_SESSION_RULES_FILE`, written atomically (`SessionHookFiles.WriteAtomic`), rewritten after every
Director store refresh and when a session is created, compare-before-write, deleted when the session is
removed - the same life as the preamble file.

```json
{
  "version": 1,
  "sessionId": "8a1e...",
  "machine": "devlinux",
  "repo": "github.com/thefrederiksen/devthrottle",
  "generatedUtc": "2026-10-18T12:00:00.0000000Z",
  "rules": [
    {
      "id": "3f2c...",
      "rank": 3,
      "appliesTo": "repo",
      "event": "before_command",
      "tool": "shell",
      "match": ["echo RULE-PROBE"],
      "result": "deny",
      "reason": "the instruction sentence, verbatim",
      "state": "dry_run"
    }
  ]
}
```

A session with no hook rules gets a file with `"rules": []`, not no file - an absent file means "not a
Director session or not written yet", and the evaluation log says which.

### W3. The decision drop, hook to Director

Directory: `<CcStorage.SessionPointers()>/decisions/<sessionId>.<dropToken>/`, named to the hook by
`CC_SESSION_DECISION_DIR`, created by the Director at launch. The token is the session's existing
pointer-drop token, so the directory name is the same capability the pointer file already is. One FILE
PER DECISION, so back-to-back commands never overwrite each other: `<yyyyMMddTHHmmssfffffffZ>-<8 lower
hex>.json`, written to a `.tmp` sibling and renamed. Written ONLY when at least one rule matched.

```json
{
  "version": 1,
  "decisionId": "20261018T1200001234567Z-1a2b3c4d",
  "sessionId": "8a1e...",
  "occurredUtc": "2026-10-18T12:00:00.1234567Z",
  "agent": "claude_code",
  "event": "before_command",
  "agentEvent": "PreToolUse",
  "tool": "shell",
  "agentTool": "Bash",
  "subject": "echo RULE-PROBE",
  "machine": "devlinux",
  "repo": "github.com/thefrederiksen/devthrottle",
  "decision": "would_deny",
  "decidingRuleId": "3f2c...",
  "overriddenRuleIds": [],
  "matchedRuleIds": ["3f2c..."],
  "evaluationMicroseconds": 812
}
```

`subject` is the command text, cut to 4000 characters. It is recorded only for commands a rule matched.

### W4. The evaluation log, hook only, local

`<CcStorage root>/session-rules/<sessionId>.eval.log`, beside the rules file. ONE LINE PER EVALUATION,
match or not: UTC time, tool, number of rules read, number matched, the decision or `no_match` or
`no_rules_file` or `error: <message>`, and microseconds. Cut back to its last half when it passes 1 MB.
This is what makes a negative control a PRESENCE: "no firing" is proven by a `no_match` line for that
command, never by the absence of a firing.

### W5. Firings, Director to Gateway

`POST /gateway/rules/hook-firings` with the Director's credential, body `{ "decisions": [ W3, ... ] }`.
Answers `{ "accepted": n, "duplicates": n, "refused": [ { "decisionId": "..", "reason": ".." } ] }`.
Each accepted decision becomes a firing on its deciding rule, carrying the event, tool, subject,
machine, repo, decision and the overridden rule ids (new, additive columns on the firings table, plus a
`kind`). `decisionId` is unique per tenant, so a retried post is a duplicate, not a second firing. A
decision naming a rule the tenant does not have, or a rule that is not a hook rule, is refused with a
reason. The Director deletes a drop only after the Gateway accepted it or called it a duplicate; a
refused one is logged and deleted; a failed post leaves it for the next sweep.

---

## The work, one Developer each

| # | Developer | Worktree and branch | Builds |
|---|---|---|---|
| A | Gateway | `../devthrottle-session-rules-p1-gateway`, `mission/session-rules-hooks-p1-gateway` | `kind`, `appliesTo`, hook fields on the rule; firing columns; the migration for Sqlite AND Postgres; the kind-aware write gate (P4, P7); P8; W1; W5; `GitRemoteIdentity` (P6) in Contracts. FIRST COMMIT, pushed before anything else: `GitRemoteIdentity` and its tests, so C can rebase onto it. |
| B | Hook | `../devthrottle-session-rules-p1-hook`, `mission/session-rules-hooks-p1-hook` | `src/CcDirector.RuleHook` (P10, P11, P3, W2 reader, W3 writer, W4); the PreToolUse shell registration in `ClaudeHookInstaller` (P9), verified against the installed Claude Code's real event and tool names; shipping the executable beside the Director on Linux and Windows; a timing script that measures the hook's added time per shell command. |
| C | Director | `../devthrottle-session-rules-p1-director`, `mission/session-rules-hooks-p1-director` | The session's repo identity (P6, from its checkout); the rules-file maintainer (W1 fetch, W2 write, same life as the preamble); stamping `CC_SESSION_RULES_FILE` and `CC_SESSION_DECISION_DIR`; the decision-drop pickup and W5 forwarding with retry. |
| D | Live rig | after A, B and C merge into the mission branch | A local Gateway and an isolated test Director on Linux built from the mission branch, and the steps the Tech Lead runs for the live check. |

Each Developer: build to this file, write the tests first and watch them fail, run the check below on
their own branch, commit and push their branch, and report one line to the Tech Lead. The Tech Lead
runs the check again, sends the branch to a Reviewer on a different agent, returns findings to the
Developer, and merges the branch into `mission/session-rules-hooks-p1`.

## The check for phase 1

- `~/.dotnet/dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules"`
- `~/.dotnet/dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~Hook|FullyQualifiedName~SessionPointerDrop"`
- The new tests each Developer adds, named in their report. `HookScriptContractTests` and
  `RulesTypeNothingGuardTests` stay green.
- `.\scripts\test-local.ps1` (pwsh on Linux) on the merged mission branch before the pull request.
- Live, rows 1 and 2 of mission section 7, on a real Director session on Linux, and on Windows where a
  Windows Director is reachable: the positive, and BOTH negative controls (other repo; other machine),
  each negative proven by a `no_match` evaluation-log line, not by silence.
- Timing: median and 95th percentile added time per shell command, Linux and Windows, with no rules,
  one rule and fifty rules.
- The September usage-limit screen rule still firing.

Results go into `phase-1-proof.md` beside this file, each with its commit and exit code, or PENDING.
