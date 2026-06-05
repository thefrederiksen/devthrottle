# Director on Drivers - migrate cc-director onto the IAgentDriver layer

Builds on docs/plans/agent-driver.md (drivers + HostedAgent, QA'd 12/12). This plan
moves the Director itself onto the same layer, adds the missing session action
buttons, and writes the PiDriver. Discipline: STRUCTURE changes and BEHAVIOR changes
ship in separate phases - Session is the most load-bearing code in the product.

## Phase 1 - Wire drivers into the Director (zero behavior change)

- `AgentDrivers.For(AgentKind)` registry in Core/Drivers: ClaudeCode -> ClaudeDriver,
  Pi -> PiDriver (phase 4), everything else -> `GenericDriver(kind)` - an honest
  "unverified tool" declaration that reproduces today's exact keystrokes (blind
  SendTextAsync submit, Esc cancel, Ctrl+C interrupt) with minimal capability flags.
  Not a fallback: it is the explicit statement of what we have verified for that CLI.
- New driver verb `InterruptAsync` (hard Ctrl+C) alongside CancelAsync (soft Esc):
  the Director exposes BOTH today (/interrupt, /escape) with bytes hardcoded in the
  endpoints; the bytes move into the drivers, endpoints delegate.
- `Session` gains `Driver` plus thin verbs: `CancelTurnAsync()`, `InterruptAsync()`,
  `ClearContextAsync()`. Endpoints call them. For Claude sessions the bytes on the
  wire are IDENTICAL to today.
- `Session.ClearContextAsync` does the FULL dance the spike exposed: driver clear ->
  watch the transcript dir (driver.ListTranscripts) -> update ClaudeSessionId
  in place. This fixes the Director's known stale-relink gap after /clear.
- REST: POST /sessions/{sid}/clear-context (new); /interrupt + /escape re-plumbed.

## Phase 2 - Session action buttons from capabilities

- `SessionDto.DriverCapabilities` (string list) + populated in Map().
- Desktop: a new self-contained `SessionActionBar` control (own .axaml, minimal
  insertion into the session view) rendering buttons from the session's
  capabilities: STOP (cancel), INTERRUPT, CLEAR CONTEXT, HISTORY (phase 3).
  Unsupported = button absent. No per-agent special cases in the UI.
- Keep shared-file touches minimal (MainWindow is concurrently edited by another
  workstream); the bar is one element insertion.

## Phase 3 - ClaudeDriver extras

- `ShowHistoryAsync` - claude's double-Esc "jump to a previous message" picker.
  Visible-terminal feature; surfaced in the Director UI, NOT used by HostedAgent.
- Capability flag `History`. Byte-level unit tests; live verification in QA.

## Phase 4 - PiDriver

- Research step against the real pi CLI (exe from PiAgent/AgentOptions): cancel
  keystroke, interrupt behavior, whether a context-clear command exists, whether
  any readable transcript exists. Each answer LIVE-verified before the driver
  declares the capability. Expected start: Cancel + Interrupt only.
- Verified through a Director Pi session (the HostedAgent harness needs
  PreassignedSessionId + TranscriptRead, which Pi likely lacks - fine, drivers
  serve hosts AND the Director).

## Phase 5 - Echo-verified submit in the Director (behavior change, last)

- `Session.SendTextAsync` routes through `driver.SubmitAsync` - Claude sessions get
  the echo-verified submit (the composer-race fix from the hosted-agent QA, which
  the Director's sends are equally exposed to). Raw keystroke passthrough from the
  terminal view is NOT affected (that is the terminal, not a programmatic send).
- GenericDriver keeps blind submit, so non-Claude sessions are byte-identical.

## QA (docs/features/director-drivers/QA_REPORT.html)

| # | Case |
|---|---|
| DQ-1 | All unit suites green (Core.Tests driver/session additions + existing) |
| DQ-2 | Slot-5 Director (scheduled task): Claude session created; /escape + /interrupt byte-identical behavior via drivers |
| DQ-3 | STOP button cancels a running turn from the desktop UI (screenshot) |
| DQ-4 | CLEAR CONTEXT button: context provably reset AND ClaudeSessionId relinked in place (/usage follows the new transcript - the old D-1 gap closed) |
| DQ-5 | HISTORY button opens claude's double-Esc picker (screenshot), Esc closes it |
| DQ-6 | Pi session: bar shows only Pi's declared buttons; STOP verified live against a running Pi turn |
| DQ-7 | Echo-verified submit: prompt-bar + REST /prompt sends still work; composer-race protection logged |
| DQ-8 | Existing Director surfaces unbroken: queue send, voice path compile, wingman read-only invariant untouched |

## Out of scope

- Codex/Gemini/OpenCode drivers (same recipe, on demand)
- Cockpit button bar (follows desktop once the REST surface exists; Cockpit.razor is
  under concurrent edit)
- HostedAgent for Pi (needs transcript support Pi may not have)
