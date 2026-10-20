# Session Rules, part 2 - hook triggers, scopes, a Rules page, and rules agents can manage

> **STATUS: PAUSED by the owner on 2026-10-20, after phases 1 and 2 merged.** Phase 1 (#4268) and phase 2
> (#4267) are on main. Phase 3 is parked, built and in review, on branch `mission/session-rules-hooks-p3`
> (with `-p3-gateway` and `-p3-pages`). Phases 4 and 5 were not started. The resume point, every parked
> branch and what is left are in the GitHub issue linked from `state.md`. No session is seated. Resume from issue #4295.

Mission document. Written 2026-10-18 by the Architect seat ("Session Rules - Architect - hook triggers
and scopes"), from a design conversation with the owner and four versions of a dev report he answered.
Branch `mission/session-rules-hooks`, cut from `origin/main` at `7a365544b`. Gateway Mission:
"Session Rules - a rule reads the terminal and acts" (`a0dcab30-737e-4f01-a598-68b71759040e`).

This document describes the WORK. Conduct is the DevThrottle Method (`cc-devthrottle skill get
devthrottle-method`) and the fleet's `mission` workflow. It grants nothing.

The first Session Rules mission (2026-09-02, PR 2665, record in `docs/missions/session-rules-2026-09-02/`)
is the base this builds on. Read its `plan.md` and `how-to-write-a-rule.md` before anything else.

---

## 1. The mission

Extend Session Rules into the one place every rule lives: rules that fire on agent hook events (before a
command, after an edit, when a session stops, when it starts) as well as on an idle screen; every rule
scoped to everywhere, a machine, a repo, or a repo on a machine; a Rules page to see and manage them; and
any agent session able to create and change them.

## 2. The why

The owner's sentence, confirmed in the status report of 2026-10-20:

> "My most important rules exist only as text an agent can ignore, and I cannot see, set or scope them. I want
> rules the product enforces, that I and the agents can manage."

From the design conversation:

> "If we build in hooks, we need to figure out how do we get from the terminal back into our
> application ... so the gateway can control what happens."

> "We should be able to set up different rules for different machines."

> "We will need to expand this and set up a rules page that can use hooks, but it should also be able
> to maybe do other rules ... on startup, if certain things happen ... if there's a missing license or
> API error."

> "They should absolutely be configurable by an agent session. Any agent should be able to change and
> set up the rules."

The Architect's reading, for context only (the quotes above are the authority): today the rules that
matter most to the owner exist only as prose in instruction files, which an agent can ignore, and the
Session Rules engine that can act on a stuck session has no screen and refuses agents. The owner cannot
see, set or scope his rules, and the agents cannot help him do it. The why question in the dev report was
still open when the owner said go; see section 10.

## 3. The goal

A QA report, the flow AND the failure cases, that shows on Windows and Linux (Mac where a Mac Director is
available):

1. A **hook rule** scoped to one repo, and another scoped to one machine, each recording what it would
   have done in dry run, then denying a command in live - and the agent seeing the reason and carrying on.
   The same command in a different repo or on a different machine is NOT touched.
2. **Most specific scope wins**: a repo rule overriding a global rule, with the Rules page showing which
   rule decided and which it overrode.
3. **An agent**, using only `cc-devthrottle rules`, creating a rule, promoting it, changing it and
   deleting it, with every change recorded against that session.
4. **The Rules page** in the Cockpit listing screen, hook and session start rules together, opening one,
   and showing its firing record. The phone shows the list and the record.
5. The **September screen rule** (the usage-limit case) still firing exactly as before.

## 4. Decisions

Every answer the owner gave, in his words where he gave words. Dev report:
`https://gateway.devthrottle.com/r/3bed20c2-5936-4608-ad19-33b3c20cb39a` (versions 1-4).

| # | Question | Owner's answer |
|---|---|---|
| D1 | Scopes | "the rule belongs to a repo, or it belongs globally to all repos, or it belongs to a particular computer ... we should be able to set up different rules for different machines." Architect added a fourth, repo on machine; owner accepted. |
| D2 | When scopes disagree | "The most specific scope wins: repo on machine, then repo, then machine, then everywhere." He chose this over deny-wins, knowing any repo can undo a global rule. |
| D3 | How a rule recognises a repo | "By its git remote" - so every worktree of a repo gets its rules without being listed. |
| D4 | How to run it | "Start a full mission (Architect, Delivery Lead, Developers), with phase 1 limited to Match rules on the before-a-command event in Observe mode." |
| D5 | One rules system or two | "One rules system. Hook events become a new kind of trigger in Session Rules, with the same store, the same Rules page and the same record; scopes (machine, repo, both) are added for every rule. The work runs under the existing Session Rules" mission. |
| D6 | Kinds of rule | A Rules page "that can use hooks, but it should also be able to do other rules ... on startup ... a missing license or API error." So three kinds: screen, hook, session start. |
| D7 | Agents | "Any agent should be able to change and set up the rules." Agents may create, change, promote and delete. Every change is recorded with the session and agent that made it. |
| D8 | Slow work | "Do we only run Python, or do we sometimes start a new session that runs Python" - both, on different events: anything the agent waits on is fast and local; anything slow starts a visible session and never blocks. |
| D9 | An unanswered phone approval (Ask) | "It is denied. An unanswered request is never waved through; the agent is told why and carries on." (status report, 2026-10-20) |
| D10 | Going on after phases 1 and 2 | "Stop here. Keep what is merged; park phase 3 on its branch." With: "Create a github issue and lets get back to it all later" (status report, 2026-10-20) |

**Override (law 19).** D6 replaces the September ruling r11, "the terminal screen is the ONLY input":
hook events and session start are now inputs too. Ruling **r15 stands unchanged**: no generated code runs,
ever. A rule names a check we ship and gives its arguments; neither the owner, an agent nor a model ever
supplies code. The "Script" handler in dev report version 1 was withdrawn for this reason and is replaced
by **Check**.

**Seats.** This Mission was created on 2026-09-02 and still carries workflow conduct version 8 (where the
Architect keeps the mission and lands main). The owner chose the current method's seats in D4. Follow the
current method: the Delivery Lead drives and lands the record.

## 5. Design

### What exists (read it; do not rebuild it)

- **Session Rules engine and store**, Gateway: `src/CcDirector.Gateway/Rules/` - `SessionRuleStore`,
  `RuleEvaluator`, `RuleCandidateFilter`, `RulePrimitiveRegistry` (checks derived by reflection from
  `[RulePrimitive]` methods - ruling A2), `RuleTurnEndLauncher` (fires only on Working to idle),
  `SessionRuleFiring` record. Routes in `Api/SessionRuleEndpoints.cs`: list, show, create (always dry
  run), promote, delete, firings. **Session keys are refused today** (`session_key_out_of_scope`).
- **The hook path, file based, no port, no pipe.** `CcDirector.Core/Claude/ClaudeHookInstaller.cs` and
  `Codex/CodexHookInstaller.cs` install a SessionStart hook only. `Sessions/SessionHookFiles.cs`: each
  session has its own preamble file (`session-preambles/<id>.json`, Director writes it) and its own event
  drop (`session-pointers/<id>.<token>.json`, hook writes it, Director watches), both written atomically.
  POSIX sh on Linux and Mac, PowerShell on Windows. The removal of the network port (that mission's phase
  3) was deliberate: do not bring an HTTP call or a listening port back into the hook.
- **Keeping per-session files current from the Gateway:** `Sessions/SessionPreambleMaintainer.cs`
  re-renders every live session's file after the Director's interval refresh of Gateway stores.

### What is built

1. **Trigger kinds.** A rule has a `kind`: `screen` (today's), `hook`, `session-start`. A hook rule also
   names its `event` (before a command, after an edit, session stops, before compaction, session ends,
   prompt submitted) and optionally the `tool` (shell, edit, ...).
2. **Scopes on every rule.** `everywhere`, `machine` (one or more, or all except some), `repo` (git
   remote, normalised), `repo-on-machine`. The most specific matching scope decides (D2). Existing rules
   migrate to `everywhere`.
3. **Handlers for hook rules**, cheapest first: **Match** (a pattern, no code); **Check** (a shipped check
   from the same registry Session Rules already derives, e.g. `port_is_free`, `lint_file`,
   `file_keeps_line`, with a timeout and a per-rule timeout result); **Ask** (the owner approves on the
   phone - the hook makes an outgoing HTTPS call with the session's own key, which is not a port);
   **Session** (starts a visible, named, Mission-attached session; never blocks; only on events nobody is
   waiting on). Results: deny, ask, tell the agent, allow.
4. **Getting rules to the hook.** The Gateway serves the effective rules for a machine and repo. The
   Director keeps a per-session rules file current, like the preamble. The hook reads that file and
   decides locally - no network on the decision path. Decisions go to the session's event drop; the
   Director forwards them to the Gateway as firings.
5. **The evaluator is fast on every OS.** A hook runs before every policed tool call. PowerShell startup
   is too slow to pay per call (see the comment in `ClaudeHookInstaller`). Register hooks only for tools
   that have rules, and evaluate in one fast native command, not a script.
6. **Agent adapters.** Claude Code first; Codex next. Rules are agent-neutral; each agent gets a thin
   adapter for its hook format and event names. Verify each agent's real event list against the installed
   version before relying on it - the list in the dev report was from memory.
7. **Agents manage rules.** Open `/gateway/rules` to session keys. Add `cc-devthrottle rules list | show |
   create | change | promote | delete | history`. Record every change: session, agent, time, before and
   after.
8. **The Rules page.** Cockpit left rail after Workflows (ruling A7). Tabs: All, Screen, Hook, Session
   start, Fired today, Changed by agents. A rule's page shows its sentence, scope, mode, handler and its
   firing record. "Preview a machine + repo" shows the effective rules and which rule overrode which. Phone:
   list and record, read-only acceptable. Shared code in `packages/client-core`.

### Out of scope

- The plain-English authoring conversation (Session Rules phase 3, never built). Rules are written as
  sentence + structured fields for now; agents fill the fields.
- Any rule-supplied code (r15).
- Deploying the Gateway. Done is merged to main; the release is the owner's call through the
  `deploy-hosted-gateway` path.

### Style guides

The Architect looked for a coding style guide and a visual style guide in this repository and found none
under those names. Follow the surrounding code. The Delivery Lead should ask the owner once whether one
exists elsewhere before the Cockpit phase.

## 6. Phases

| Phase | What | Needs a Tech Lead | Runs in parallel with |
|---|---|---|---|
| 1 | Hook trigger on Claude Code: scopes and git-remote repo identity on every rule, the Director's per-session rules file, a PreToolUse hook for the shell tool, Match handler only, **dry run only** (records "would have denied"), firings forwarded to the Gateway. Linux and Windows. | Yes - Gateway, Director and the hook installer at once | Phase 2 |
| 2 | Agents manage rules: session keys allowed on `/gateway/rules`, `cc-devthrottle rules`, the change history. | No - one Developer | Phase 1 |
| 3 | The Rules page: Cockpit and phone, all three kinds, preview, fired today, changed by agents. | Yes | - (after 1 and 2) |
| 4 | Live hooks: deny / tell the agent, the Check handler, after-edit and session-stop events, the Codex adapter, Mac. | Yes | - |
| 5 | Session start rules (e.g. license or API key missing at start), the Ask handler (phone approval), the Session handler (start a reviewer). | Yes | - |

Phase 1 is deliberately harmless: in dry run nothing can be blocked, so it can merge early and gather
real "would have denied" records before anything enforces.

## 7. The check

Proven means: run by the seat that receives the work, on a real Director session, with the failure cases.

- **Unit and contract tests** (needs .NET SDK 10; devlinux has only 8 - use a machine that has 10):
  `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules"` and
  `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~Hook|FullyQualifiedName~SessionPointerDrop"`,
  plus the new tests each phase adds. The existing `HookScriptContractTests` and `RulesTypeNothingGuardTests`
  must stay green.
- **Live, per phase, on Linux and Windows:**
  1. Create a hook rule scoped to repo A that matches `echo RULE-PROBE`. In a session in repo A, run it:
     the firing record shows "would have denied" (phase 1) or the agent sees the deny and its reason
     (phase 4).
  2. Same command in repo B: no firing. Same rule scoped to machine X, run on machine Y: no firing.
     These are the negative controls and they are not optional - a check that only passes is not a check.
  3. A global rule and a repo rule that disagree: the repo rule decides, and the preview says so.
  4. From a session, with only `cc-devthrottle rules`: create, promote, change, delete - each in
     `rules history` with that session's id.
  5. The September usage-limit screen rule still fires on its demonstration rig
     (`docs/missions/session-rules-2026-09-02/demonstration-rig.md`).
- **Timing:** measure the hook's added time per shell command on Windows and Linux and put the numbers in
  the QA report. No target is set; the owner sees the measurement.

## 8. Merge plan

One pull request per phase, each to `main`, each reviewed by a Reviewer on a different agent before
merge (Codex first, Pi on GLM-5.3 if Codex cannot). Phases 1 and 2 can merge as soon as they pass:
phase 1 only records, and phase 2 only adds a command and opens routes that already validate every write.
Live enforcement arrives only in phase 4, behind rules that have each spent time in dry run first. CI on
main is sometimes red on unrelated tests: check which test failed before calling a phase broken.

## 9. Where it ends

Merged to `main`. Not in production: deploying the Gateway, the Cockpit and the mobile app is the
owner's decision, through the `deploy-hosted-gateway` path only. The Delivery Lead hands the owner one QA
report (a dev report) at the end.

## 10. Questions

Asked and answered: D1-D8 above.

Still open, for the Delivery Lead to carry - none of them blocks phase 1 or 2:

1. **The why in the owner's own sentence** - ANSWERED, section 2. Section 2 quotes him; the dev report question was unanswered
   when he said go. Ask once, at the first natural point, and replace section 2's reading with his words.
   Recommendation: offer the draft from dev report version 3 as the starting point.
2. **A style guide** - ANSWERED: the repository has `docs/CodingStyle.md`, `docs/VisualStyle.md` and
   `docs/CockpitVisualStyle.md`. Phase 3 follows them.
3. **Whether a deny on the phone (Ask) times out to allow or to deny** - ANSWERED: deny (D9). Recommendation: deny, so an
   unanswered request is never silently waved through. Needed before phase 5.

Open for phase 4, from the review of the whole of phase 1 (`reviews/phase-1-whole-review.md`, observations
2 and 3). Neither is a harm in phase 1, where every decision is dry run; both must be answered before
anything enforces:

4. **The session a rule polices can rewrite its own rules file.** The file's path is in the agent's own
   environment (`CC_SESSION_RULES_FILE`), carries no secret in its name, and the Director rewrites it only
   when the Gateway's answer changes. In dry run the worst outcome is a missing or forged "would have
   denied" record. Once a deny is live, one write by the agent removes the rule that stops it, and nothing
   today notices. Phase 4 has to decide how the evaluator tells a file the Director wrote from one the
   agent edited, or where the file lives so the agent cannot write it.
5. **Nothing bounds the rules file.** The Gateway sets no limit on a match text's length, the number of
   match texts in a rule, or a tenant's number of rules, and the evaluator reads and scans the whole file
   before every shell command. Only a tenant's own owner and agents can grow it, so the cost falls on that
   tenant alone, but it is paid on every command. Set a bound on the Gateway's write side before phase 4.
