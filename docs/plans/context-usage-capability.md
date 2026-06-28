# Plan: A "context usage" capability for the agent drivers

Status: PROPOSAL - not implemented. This document is a plan only.

## 1. What the user asked for

Make each session's context-window usage visible at a glance - "how full is the
context right now" - without having to type a slash command (Claude's `/context`,
Codex's `/status`, pi's footer). The user wants to see context GROWING so they can
act (clear, compact, hand over) before it becomes a problem, instead of it being
hidden behind a command.

The request: add ONE more capability check to all the agent drivers, the same way
the existing capabilities (Cancel, Interrupt, ClearContext, History, ...) work, and
surface it in the always-visible part of the user interface.

## 2. The "capability document" - where it lives

There are TWO things in this repository that together are the "capability document",
and a new capability touches both:

1. The per-agent reference documents (prose):
   `.claude/skills/agent-expert/agents/`
   - `README.md` - the cross-agent capability matrix (at-a-glance table).
   - `claude-code.md`, `codex.md`, `pi.md` (and gemini, grok, opencode, cursor,
     copilot) - one authoritative file per agent.
   - `_template.md` - the template a new agent's file is created from.
   This is an INTERNAL reference, not shipped with the installer (see the
   agent-expert skill).

2. The capability declaration in code:
   `src/CcDirector.Core/Drivers/IAgentDriver.cs`
   - `enum DriverCapabilities` (the `[Flags]` enum: ClearContext, Cancel,
     TranscriptRead, PreassignedSessionId, Interrupt, History, ModelSelection).
   - `interface IAgentDriver` (the method each capability promises will work).

Each agent's driver declares which flags it supports and implements the matching
methods; an unsupported verb is DECLARED absent and its method throws
`NotSupportedException` - never emulated. The implemented drivers are:

- `src/CcDirector.Core/Drivers/ClaudeDriver.cs` (the reference, all 7 flags)
- `src/CcDirector.Core/Drivers/CodexDriver.cs` (Cancel | Interrupt | ClearContext)
- `src/CcDirector.Core/Drivers/PiDriver.cs` (Cancel | ClearContext)

Drivers are registered in `src/CcDirector.Core/Drivers/AgentDrivers.cs` (an
`AgentKind` -> driver factory). The capability flags are exposed to the user
interface two ways:
- Desktop (Avalonia): `Session.Driver.Capabilities` is read in-process by
  `src/CcDirector.Avalonia/Controls/SessionActionBar.axaml(.cs)`, which shows or
  hides each action button by flag.
- Control API / Cockpit (web): `GET /settings/agents/catalog` returns the flag
  names (`src/CcDirector.ControlApi/AgentsEndpoint.cs`, `CapabilityNames(...)`).

## 3. What already exists (important - we are not starting from zero)

Claude Code already computes context size:

- `src/CcDirector.Gateway.Contracts/SessionUsageDto.cs` has a `ContextTokens`
  field documented as "how full the context window currently is" (the latest
  assistant line's input + cache read + cache creation), plus per-turn deltas.
- `src/CcDirector.Core/Claude/SessionTokenUsage.cs` computes it mechanically from
  the Claude JSONL transcript.
- `GET /sessions/{sid}/usage` (`src/CcDirector.ControlApi/SessionUsageEndpoint.cs`)
  serves it. This is wired to the Cockpit (web) "session story" panel, NOT to the
  always-visible desktop bar.
- This lives behind the existing `TranscriptRead` capability, which TODAY only
  `ClaudeDriver` declares.

What is MISSING, and is the actual gap to close:

a. There is NO context-window-SIZE / percentage anywhere. `ContextTokens` is a raw
   number; "how full" needs a denominator (the model's window, e.g. 200,000 or the
   1,000,000-token Opus). Nothing in the code knows the window size.
b. Codex and pi do not expose context size at all yet:
   - Codex: its rollout transcript DOES carry usage data, but `CodexDriver.ReadUsage`
     throws NotSupported - the reader never extracts it (confirmed in `codex.md`
     section 8 and `CodexDriver.cs`).
   - pi: no transcript reader. BUT pi's in-process extension API exposes
     `ctx.getContextUsage()` directly (see `pi.md` - ExtensionContext highlights),
     and pi's own interactive footer already shows token/cache/context usage. So
     pi can report context usage WITHOUT us parsing a transcript.
c. The desktop user interface has no live per-session context indicator. The only
   "usage" surface on the desktop is `StatsDialog`, which reads Claude's daily
   `~/.claude/stats-cache.json` aggregate - not per-session, not live context.

## 4. Proposed design

### 4a. New capability flag

Add one flag to `DriverCapabilities` in `IAgentDriver.cs`:

```
/// <summary>The driver can report how full the context window currently is
/// (used tokens, and where known the window size and percent), so the Director
/// can show a live context gauge without the user typing a slash command.</summary>
ContextUsage = 128,
```

Naming note: keep it distinct from `TranscriptRead`. `TranscriptRead` means "I can
parse the whole conversation (widgets + token totals)"; `ContextUsage` means "I can
answer the narrower question 'how full is the window right now'". A driver may have
one without the other - pi can answer ContextUsage (via its extension) but not
TranscriptRead (no transcript parser). Keeping them separate avoids forcing pi/Codex
to implement full transcript reading just to show a gauge.

### 4b. New driver method + DTO

Add to `IAgentDriver`:

```
/// <summary>How full the context window is right now (capability ContextUsage):
/// used tokens, the window size when known, and the percent. Null when it cannot
/// be determined yet (no turn has happened). Throws NotSupported when the flag is
/// absent.</summary>
ContextUsageDto? ReadContextUsage(string agentSessionId, string workingDirectory);
```

New contract type in `src/CcDirector.Gateway.Contracts/` (next to SessionUsageDto):

```
public sealed class ContextUsageDto
{
    public long UsedTokens { get; set; }          // tokens currently in the window
    public long? WindowTokens { get; set; }       // window size, null if unknown
    public double? PercentUsed { get; set; }       // 0-100, null if window unknown
    public DateTime? AsOfUtc { get; set; }         // when this was last true
}
```

Keep it deliberately SMALL and separate from `SessionUsageDto` (which is a Claude
JSONL-shaped totals object). `ContextUsageDto` is the agent-agnostic "gauge" the
user interface binds to.

### 4c. The window-size denominator

DECIDED: v1 shows a percent bar with a raw-number fallback (decision 1). The gap
"we have used-tokens but no percent" needs a window size. Sources, in order of
preference per agent (decision 2 - agent-reported when offered, otherwise a
hard-coded per-model table in the driver):

1. The agent tells us (pi `ctx.getContextUsage()` may return a fraction directly;
   if so, trust it and skip our own arithmetic). This is the preferred path WHERE
   the agent offers it - pi does; Claude and Codex do not hand us a window cleanly.
2. A per-driver hard-coded value tied to the model - the DEFAULT path for Claude
   and Codex. Add a small driver-owned lookup (the drivers already carry
   `KnownModels`); e.g. ClaudeDriver maps its configured model to a window (200,000
   for the standard models, 1,000,000 for the `[1m]` Opus). This stays inside the
   driver, no fallback guessing in the user interface. It is a table we own and must
   update as models change; an unmapped model simply falls back to source 3.
3. Unknown -> `WindowTokens`/`PercentUsed` stay null and the user interface shows
   the raw used-token count with no bar (the raw-number fallback). Explicit, not a
   fake 100%.

### 4d. Per-agent implementation

- ClaudeDriver: declare `ContextUsage`. Implement `ReadContextUsage` by reusing the
  existing `SessionTokenUsage.ComputeFromFile(...).ContextTokens` as `UsedTokens`,
  plus the model->window lookup for `WindowTokens`/`PercentUsed`. Lowest effort -
  the hard part (reading the number) already works.
- CodexDriver: declare `ContextUsage`. Implement by extracting the usage block the
  Codex rollout already carries (the `codex.md` notes say the data is present; the
  reader just doesn't read it today). This is the medium-effort item: write the
  minimal rollout usage extractor. Confirm Codex's window size from `/status` /
  config before depending on a number.
- PiDriver: declare `ContextUsage`. Implement via pi's extension `ctx.getContextUsage()`
  surfaced through the existing pi extension/RPC channel, NOT by parsing a transcript.
  This is the one that needs a live check against the installed pi binary (pi is
  "docs only, not confirmed on this machine" per the README), so verify the
  extension call and its return shape first.
- Every other driver (GenericDriver-backed: Gemini, OpenCode, Grok; plus Cursor,
  Copilot): do NOT declare the flag. `ReadContextUsage` throws NotSupported and the
  gauge is simply absent - same pattern as every other capability.

### 4e. Surfacing it in the user interface

Primary target = the always-visible desktop bar, because that is exactly the
"without a slash command" the user asked for:

- `SessionActionBar.axaml(.cs)` already renders from `Session.Driver.Capabilities`.
  Add a compact, non-button context gauge to the right of the action buttons,
  visible only when `caps.HasFlag(DriverCapabilities.ContextUsage)`:
  - text form: `ctx 42k / 200k (21%)`, or just `ctx 42k` when the window is unknown.
  - a thin progress bar that escalates color as the window fills (decision 4 -
    three bands): neutral/green below 70%, amber from 70% to 90%, red above 90%. The
    color is pre-attentive - growth is noticeable in peripheral vision before the
    number is read. Exact thresholds are easy to tune later once watched on real
    sessions; the point is having the visual escalation at all. When the window size
    is unknown (raw-number fallback) there is no percent, so the bar stays neutral.
- Refresh (decision 3 - poll on a timer for v1): poll `ReadContextUsage` on a
  low-frequency timer (every ~3-5 seconds) on a background task, then update the
  bound property on the user-interface thread. Uniform across all agents, no
  dependency on per-agent turn-detection. A future optimization may switch to the
  Director's existing turn-complete signal, but polling a once-every-few-seconds file
  read is robust and responsive enough for v1. Follow the repository's responsive
  user-interface rule (immediate render, async load, no synchronous file input/output
  on the user-interface thread).

Secondary (cheap, do at the same time): expose it on the Control API for the Cockpit
web view and the fleet, e.g. `GET /sessions/{sid}/context` returning `ContextUsageDto`,
and add it to the `agents/catalog` capability-name list so the matrix stays honest.

### 4f. Documentation updates (the prose half of the "capability document")

- `.claude/skills/agent-expert/agents/README.md`: add a "Context-usage reporting"
  row/column to the matrix (Strong/Partial/None per agent, and which mechanism:
  Claude=transcript, Codex=rollout, pi=extension call).
- Each per-agent file (`claude-code.md`, `codex.md`, `pi.md`): add a short section
  describing how that agent exposes context usage and our driver's support, with the
  same `[VERIFIED]`/`[INFERRED]` discipline the files already use.
- `_template.md`: add the new capability section so future agents document it.

## 5. Work breakdown (suggested order, smallest blast radius first)

1. Contracts + interface: add `ContextUsageDto`, the `ContextUsage` flag, and the
   `ReadContextUsage` interface method (with the default-throw in the drivers that
   don't support it). Pure additive, compiles against all drivers.
2. ClaudeDriver implementation (reuses existing `ContextTokens`) + model->window
   lookup. This alone gives a working gauge for the most-used agent.
3. Desktop user-interface gauge in `SessionActionBar`, capability-gated, live-refresh.
   Now the user gets the feature for Claude end to end.
4. Control API endpoint + catalog flag + Cockpit binding (parity for web/fleet).
5. CodexDriver implementation (rollout usage extractor; verify window size).
6. PiDriver implementation (extension `getContextUsage`; verify live against binary).
7. Documentation (README matrix + per-agent files + template).
8. Tests: `SessionTokenUsage`-style unit tests for each new extractor; a driver test
   per agent asserting the flag is declared and `ReadContextUsage` returns expected
   values on a sample transcript / fake.

Steps 1-3 are a complete, shippable slice for Claude. 5 and 6 extend it to the other
two implemented agents and can land independently.

## 6. Decisions (settled 2026-06-27)

1. Percent bar vs. raw number: SETTLED - percent bar with a raw-number fallback.
   Show the percent + bar when the window size is known; fall back to the raw
   used-token count when it is not. (Drives section 4c.)
2. Window-size source of truth: SETTLED - agent-reported when the agent offers it
   (pi), otherwise a hard-coded per-model table inside the driver (Claude, Codex).
   Unmapped model -> raw-number fallback. (Drives section 4c.)
3. Refresh trigger: SETTLED - poll on a low-frequency timer (~3-5s) on a background
   thread for v1; the Director turn-complete signal is a possible later
   optimization. (Drives section 4e.)
4. Threshold colors: SETTLED - three bands, neutral below 70%, amber 70-90%, red
   above 90%. Tunable later. (Drives section 4e.)
```
