# Agent capability matrix (at a glance)

This is the summary. The per-agent files are authoritative; open them for detail, exact field
names, source links, and verification status. Ratings: Strong / Partial / None / Unknown.

## Context injection and reset detection

| Agent | Inject at launch | Signal/hook on /clear | Signal/hook on compact | Mechanism family | Our driver |
|-------|------------------|-----------------------|------------------------|------------------|------------|
| [Claude Code](claude-code.md) | Strong (SessionStart additionalContext) | Strong (hook, source=clear) | Strong (hook, source=compact) | A shell-hook | ClaudeDriver |
| [Codex](codex.md) | Strong (SessionStart additionalContext) | Strong (hook, source=clear) | Strong (hook, source=compact) | A shell-hook | CodexDriver |
| [Gemini](gemini.md) | Strong (SessionStart additionalContext, or GEMINI.md) | Strong (hook, source=clear) | Detect via PreCompress; GEMINI.md persists | A shell-hook + D file | GenericDriver |
| [Grok](grok.md) | Strong (instruction files CLAUDE.local.md / .grok/rules) - hook stdout is IGNORED, NO additionalContext (verified vs binary 0.2.67) | Detect via hook; re-inject via file | Detect via PreCompact/PostCompact; re-inject via file | D instruction-file (A hooks exist but cannot inject) | GenericDriver |
| [opencode](opencode.md) | Strong (AGENTS.md) | Detect (session.created over SSE bus) | Detect (session.compacted over SSE bus) | B event-bus / C plugin | GenericDriver |
| [Cursor](cursor.md) | Strong (sessionStart additional_context; rules) | Unknown (sessionStart re-fire undocumented) | Detect via preCompact (observe only) | A shell-hook | CursorDriver |
| [Copilot](copilot.md) | Partial (instruction files; CLI hook output ignored) | None documented | Detect via preCompact | D instruction-file | CopilotDriver |
| [pi](pi.md) | Strong (flags, AGENTS.md, before_agent_start) | Strong (extension, session_start reason=new) | Strong (extension, session_before_compact) | C in-process extension | PiDriver |

## Context-usage reporting (the live context gauge)

Can the driver answer "how full is the context window right now" (capability `ContextUsage`), so the
Director shows a live gauge without the user typing a slash command? This is narrower than
`TranscriptRead` (parse the whole conversation): an agent may report context usage without a full
transcript parser. Ratings: Strong / Partial / None.

| Agent | Context-usage reporting | Mechanism | Our driver |
|-------|-------------------------|-----------|------------|
| [Claude Code](claude-code.md) | Strong (implemented) | Transcript: latest assistant line's input + cache tokens; model-id -> window table for the percent | ClaudeDriver |
| [Codex](codex.md) | None yet (planned) | Rollout transcript carries a usage block; extractor not written | CodexDriver |
| [pi](pi.md) | None yet (planned) | In-process extension `ctx.getContextUsage()` (no transcript parse needed) | PiDriver |
| Gemini / Grok / opencode / Cursor / Copilot | None | not declared | GenericDriver / per-driver |

Only ClaudeDriver declares `ContextUsage` today; Codex and pi are deliberately separate follow-up
issues. A driver that does not declare the flag throws `NotSupportedException` from
`ReadContextUsage`, and the desktop gauge / `GET /sessions/{sid}/context` are simply absent for it.
The window denominator is a driver-owned per-model table (200,000 tokens for the standard Claude
models, 1,000,000 for the `[1m]` Opus id); an unmapped model falls back to the raw used-token count
with no percent.

## The four mechanism families

- A - shell-command hook that injects context. A config file registers a command we own; it
  prints the preamble as additionalContext. Agents: Claude (wired), Codex, Gemini, Cursor.
- B - out-of-process event bus. We subscribe to the agent's event stream and push the preamble
  on a new/cleared/compacted session. Agents: opencode (opencode serve, GET /event SSE).
- C - in-process extension or plugin we author and load. Agents: pi (TypeScript extension),
  opencode (plugin), Copilot (only via its SDK, not the stock CLI).
- D - instruction file the agent re-reads every prompt (passive, self-healing, no event).
  Agents: Copilot (primary), Grok (its hook output is ignored, so file is the only injector),
  and a universal fallback for everyone (AGENTS.md / CLAUDE.md).

## Notes that bite

- "Only Claude can do this" was WRONG: it described what we had WIRED, not what the CLIs
  support. Most CLIs have converged on the Claude hook model; the work is wiring each plugin.
- These hook systems are recent and churning (Gemini, Codex, Grok, Cursor, Copilot all have
  open issues). Every field name needs a live check against the installed binary before we
  depend on it. See each file's "Caveats and verification needed".
- `cc-devthrottle message ask` (ask another session and read its reply) needs the TranscriptRead capability, which
  today only ClaudeDriver declares. So cross-agent "ask" only works Claude -> Claude so far.
- The shared piece for all families is the Director endpoint GET /sessions/{sid}/fleet-preamble,
  which already exists and is agent-agnostic.
- CORRECTION (verified against binaries): Grok was previously listed as a Family A hook-injector
  by analogy to Claude. That is wrong - Grok 0.2.67's shipped hooks guide says passive-event hook
  stdout is ignored, and a binary scan found no `additionalContext`/`hookSpecificOutput`. Grok is
  Family D. Treat all "by analogy to Claude" claims as suspect until checked against the binary.

## Live-verification status on this dev machine (SOREN_NORTH)

What was actually checked against an installed binary versus researched from docs only:

| Agent | Installed here | Notes |
|-------|----------------|-------|
| Claude Code | Yes | Fleet preamble wired AND proven live (startup + clear). Reference impl. |
| Grok | Yes (0.2.67) | Ships its full user guide at ~/.grok/docs/user-guide/. Hook injection NOT available - use instruction files. |
| Gemini | Yes (0.1.11) | Binary PREDATES the hooks system, --output-format, --resume. Family A cannot be wired until upgraded and re-probed. Also: current docs say Gemini DOES persist transcripts (our code assumes it does not - re-check). |
| Codex | Yes (0.141.0) | WIRED + proven live: hook auto-installs into ~/.codex/hooks.json, preamble injects (fires at first turn). Also fixed a programmatic-submit bug (echo-verified submit). |
| opencode | Not confirmed here | Docs only. Family B needs the TUI to expose a reachable server (it may not by default). |
| Copilot | Not confirmed here | Docs only. Docs contradict each other on whether the CLI sessionStart can inject - verify live. |
| Cursor | NO (cursor-agent absent) | Spawn fails with CreateProcess failed. All Cursor facts are docs-only and unverifiable here. |
| pi | Not confirmed here | Docs only (read from the repo raw markdown). Richest event system; Family C extension. |

Every per-agent file ends with a "Caveats and verification needed" section listing the exact
fields and behaviors to confirm against the installed binary before code depends on them.
