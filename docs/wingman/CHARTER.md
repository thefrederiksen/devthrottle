# The Wingman Charter

This is the single source of truth for what the Wingman is, what it must achieve, and
the invariants it must hold **everywhere it is used**. The Wingman is a cross-cutting
component: it rides alongside every Claude Code / AI session card in CC Director, and it
is used in more places over time. A one-time fix is not enough; the invariants below are
enforced continuously by an automated audit gate (see section 4) so that new usages
cannot silently drift from them.

If you are adding or changing Wingman behavior, read this first. If a change would break
an invariant, the change is wrong - not the invariant.

## 1. Purpose

The Wingman exists to **genuinely help the user understand and act on what their AI
sessions are doing**, without making them read the raw terminal. It is the user's
second set of eyes on every session: it watches state, summarizes turns for the screen,
speaks for the voice/in-car experience, answers questions about a session, reads content
back faithfully, judges goal progress, and flags rule violations.

The bar for every Wingman feature is simple: **does it actually help, or does it send the
user back to the raw tab?** If a Wingman output is so lossy, so generic, or so wrong that
the user stops trusting it and opens the terminal instead, the Wingman has failed at its
one job, regardless of whether it "returned something."

## 2. Where the Wingman is used (the surface, and it is growing)

All Wingman logic lives under `src/CcDirector.Core/Wingman/`. Today that includes:

- **Terminal-state classification** - what is the session doing right now (the badge
  colour): `WingmanService.ClassifyTerminalStateAsync` / `...ViaSessionAsync`,
  `TerminalStateDetector`, `SessionStatusWingman`.
- **Per-turn summary** - the Agent View headline + structured turn record:
  `WingmanService.SummarizeTurnAsync`, `TurnSummaryCache`.
- **Ask the Wingman** - faithful, full-access, verbatim answers to questions about a
  session: `WingmanService.AnswerViaSessionAsync`.
- **Explain briefing** - the terse "what's happening" voice/HTML briefing:
  `WingmanService.AskAboutSessionAsync` (explain mode).
- **Voice transcript cleanup + routing** - clean dictation and decide agent-vs-wingman:
  `WingmanService.CleanVoiceTranscriptAsync`.
- **Rules / memory enforcement** - CLAUDE.md violation checks: `CheckRulesAsync`.
- **Goal tracking** - on-track / drifting / complete: `AssessGoalAsync`.
- **Crash recovery prompt** - `BuildRecoveryPromptAsync`.

**Rule of placement:** all Wingman code MUST live under `src/CcDirector.Core/Wingman/`
so the audit gate (section 4) covers it automatically. Do not scatter Wingman model
calls into other namespaces where the audit cannot see them.

## 3. Hard invariants

These hold for every Wingman path, present and future.

1. **Strong model only - NEVER a cheap model.** The Wingman runs on one strong model,
   `WingmanService.Model` (currently `opus`). There is no cheap/fast tier. A weak model
   cannot read a screen faithfully, answer without summarizing, or judge state reliably,
   and a Wingman the user cannot trust is worse than no Wingman. No Wingman code may pass
   or default a cheap model (e.g. `haiku`) to any model call.

2. **Read-only - never writes, sends, or resizes any session.** The Wingman observes; it
   never mutates the partner session. Full-power Wingman sessions get a read-only tool
   allow-list only (`Read`, `Grep`, `Glob`); never a write/execute tool. It has no path
   to send to or resize a partner PTY (a resize is an indirect write that self-injects
   via the monitoring loop). See the read-only invariant in agent memory.

3. **Faithful, not summarizing, when content is asked for.** Status-at-a-glance outputs
   (badge, terse briefing) may be short. But when the user asks for content - "read me
   the article", "what did it say", "the whole thing" - the Wingman reproduces it
   VERBATIM, complete, with no length cap. It never silently summarizes away what was
   asked for.

4. **Stateless side-calls.** Each Wingman call is fresh (`--no-session-persistence`,
   `--strict-mcp-config` empty MCP). The Wingman has no hidden conversation memory
   between calls; its only inputs are the prompt and the session state it is handed/reads.

5. **Fail closed - never fabricate.** On any failure (no CLI, timeout, parse error) the
   Wingman returns an explicit unknown/error result. It never invents a state, a file
   name, a decision, or content. No fallback that produces a confident-looking wrong
   answer. (Ties to the project no-fallback rule.)

6. **One model source of truth.** Model selection goes through `WingmanService.Model`
   (and its back-compat aliases). No Wingman path hardcodes a different `--model` value.

## 4. The audit gate (continuous enforcement)

Because the Wingman is everywhere and growing, the invariants are checked by an automated
audit that runs as part of the normal test suite and **fails the build** on any
violation: `CcDirector.Core.Tests/Wingman/WingmanCharterAuditTests.cs`.

It enforces, over every file under `src/CcDirector.Core/Wingman/`:

- **No cheap-model literal.** No quoted cheap-model name (e.g. `"haiku"`) appears in any
  Wingman source file - the only thing that can actually invoke a cheap model.
- **Strong model.** `WingmanService.Model` is a known strong model and is not a cheap one.
- **Read-only tools.** Every `allowedTools:` argument in Wingman code is a subset of the
  read-only allow-list (`Read`, `Grep`, `Glob`); a write/execute tool fails the gate.

Run it directly:

```
dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj --filter "FullyQualifiedName~WingmanCharterAuditTests"
```

The audit is deliberately deterministic (a static check, no LLM call): a build gate must
be fast, free, and reliable. The audit is a floor, not a ceiling - it catches the
mechanical violations; the invariants above still bind judgment calls the audit cannot
see (e.g. whether a prompt actually preserves fidelity).

## 5. Scope notes / open questions

- The Wingman's invariants cover code under `src/CcDirector.Core/Wingman/`. Two adjacent
  components feed the same voice experience but live elsewhere and currently use a cheap
  model: the voice spoken-summary path (`Core/Voice/Services/ClaudeSummarizer`) and the
  conductor recap (`Core/Claude/RecapGenerator`). Decision pending: fold these under the
  charter (and move/scan them), or keep them out of scope. Until decided, they are NOT
  covered by the audit.
- If a high-frequency path (e.g. terminal-state classification, which runs whenever a
  session goes quiet) makes `opus` too slow or costly in practice, the charter can define
  a second **strong** tier (e.g. `sonnet`) for hot paths - still never a cheap tier. This
  is a tuning decision, not a licence to reintroduce Haiku.
