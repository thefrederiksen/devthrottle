# Independent inspection: Wingman narration repair

**Result: PASS**

## Scope and evidence

This inspection was performed against the complete `origin/main..3392a6f3b` diff and the mission
documents `brief.md`, `inspection-wingman.md`, and `narration-repair-report.md`. The previous
inspection's high-severity finding was treated as untrusted until the production path and its
guards were read again.

Focused checks completed successfully:

- `VoiceSweepBudgetTests`: 3 passed.
- `WingmanNarrationSourceTests`, `WingmanVoiceServiceTests`, `WingmanTranslatorTests`, and
  `TerminatingFaultClassifierTests`: 153 passed.
- `VoiceServingLoopIsolationTests` and `DisplayStateSweepOverlapGuardTests`: 7 passed.

## Previous high-severity finding

**Resolved.**

The periodic sweep no longer skips a session merely because any cached audio exists. In
`GatewayHost.SweepVoiceSessionsAsync`, each eligible session is prepared before generation, and
the sweep passes the prepared input into `GenerateAsync`. In
`WingmanVoiceService.PrepareSweepGenerationAsync`, a stored conversation whose latest speaker is
the user is identified with `WingmanNarrationSource.NeedsLiveScreen`, the owning Director's
current screen is read, and the conversation is refreshed after that asynchronous read.

The generation path then selects from the refreshed conversation and the captured screen. If the
later user message has no positively classified terminal failure, the old ready clip is removed
and the result is marked as nothing to narrate. If the terminal failure is positively classified,
the old ready clip is removed before provider work begins and the terminal source identity is used
for regeneration. An unchanged source still takes the identity-aware no-regeneration path.

The production regression in `VoiceSweepBudgetTests.Cached_audio_cannot_hide_a_terminal_failure_after_a_later_user_message`
seeds the exact stale clip and later user message, runs the real sweep, requires a `screen-grid`
command, requires the stale `NothingToNarrate` state to clear, and proves that the old reply is
not left playable. This directly exercises both parts of the prior finding: the cached-audio
bypass and the missing sweep terminal read.

## Invariant inspection

### Preparation and callback ordering

The asynchronous terminal read is isolated in `PrepareSweepGenerationAsync`, before
`GenerateAsync` is called. `GenerateAsync` rejects a provider-budget callback without prepared
input. The callback is invoked at the provider commit point in `GenerateOnceAsync`, after all
no-cost exits and before the first provider await. Therefore the sweep can make the next budget
decision synchronously while still allowing the terminal read to be asynchronous.

The preparation failure boundary is per session. A failed preparation is logged and does not
terminate the tenant pass or the whole sweep.

### Tenant scope

The sweep runs inside `ITenantPass.ForEachTenantAsync`, which holds the ambient tenant scope
across awaits. It resolves the session, Director, conversation, voice state, and command route
within that tenant. `VoiceServingLoopIsolationTests` proves that the live screen read reaches
only the owning tenant's Director and that ready audio is not visible in another tenant's
partition.

### Global budget and overlap gate

The generation count remains global across tenants and is incremented only by the synchronous
provider-commit callback. No-op sessions therefore do not consume the shared provider budget.
`VoiceSweepBudgetTests` proves that four no-op sessions in one tenant cannot starve a narratable
session in another tenant during the same pass.

`Interlocked.Exchange` admits only one asynchronous sweep at a time, and the guard is released
in `finally`. The overlap-focused checks passed. The per-tenant inspection budget remains
separate from the global provider budget, so bounding one does not recreate cross-account
starvation.

### Failure isolation

The outer sweep catches and logs unexpected sweep failures. Session preparation catches and logs
its own terminal-read failure, and generation retains its per-session exception boundary. A bad
session cannot abort the remaining tenant sessions.

### Persistence and cache identity

Ready audio is removed from both the in-memory ready map and the durable cache when a later user
message supersedes it. The source identity distinguishes an agent reply from a terminal failure,
and the identity-aware comparison prevents repeated synthesis of an unchanged source. The
terminal identity includes the bounded terminal content, so a changed failure window is not
mistaken for the prior failure.

### Direct Explain and Generate paths

Direct generation still reads the live screen when the stored conversation ends with a user
message. The on-demand paths continue to use the same source selection and identity rules.
Terminal text is passed through the dedicated terminal-failure translation path rather than being
treated as an agent answer or recent conversational context.

### Constant-substitution challenge

A constant substitution that restored the old cached-audio bypass would fail the production
regression because no `screen-grid` command would be observed and the stale state would remain.
A substitution that removed the terminal source selection would fail the same test because the
old ready reply would remain or the terminal failure would not clear the stale state. A
substitution that counts dispatched sessions rather than provider commits would fail the
five-session budget test. A substitution that removes tenant scope would fail the positive
tenant-routing test. A substitution that permits overlapping sweeps is covered by the overlap
guard check.

## Findings by severity

| Severity | Finding |
|---|---|
| Critical | None. |
| High | None. |
| Medium | None blocking this slice. |
| Low | The focused tests prove the repaired production path, but they do not measure durable audio-file deletion on disk independently from the ready-state API. The deletion helper is directly called by the stale-clip path; a separate filesystem assertion would strengthen the evidence. |

## Proof gaps

The focused run does not replace the repository's complete local gate, and this inspection does
not claim that the parked or environment-bound suites passed. The regression test permits the
provider call to finish asynchronously; it intentionally proves the stronger pre-provider
property that stale audio is removed before generation can produce replacement audio, but it
does not wait for a successful replacement clip. The report also relies on the existing direct
tests for provider failure and persistence behavior rather than adding a second full end-to-end
provider fixture.

These gaps do not reopen the previous finding. The production sweep now reads the current
terminal before source selection, cannot preserve the old clip after a later user message, and
keeps tenant, budget, overlap, and failure boundaries intact.

**PASS — the previous high-severity periodic-sweep finding is fixed.**
