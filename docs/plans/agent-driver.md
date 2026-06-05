# Agent Drivers - per-CLI interaction protocol under the HostedAgent

Builds on docs/plans/hosted-agent.md. The HostedAgent proved a self-hosted headless
Claude; this plan extracts the missing middle layer - the per-CLI **driver** - so the
tool-specific keystrokes (cancel, clear, submit quirks) live in exactly one class per
CLI, and the HostedAgent becomes a generic host that can run any of them headless.

## The three layers (locked)

```
IAgentDriver          the CLI's PROTOCOL: how to submit, cancel, clear, read replies
  ClaudeDriver        (Esc cancel, /clear, JSONL transcripts, @-file paste trick...)
  CodexDriver/...     (later, one class per CLI, each live-verified)
        runs on top of
ISessionBackend       the TERMINAL: ConPty pseudoconsole, bytes in/out (unchanged)
        composed by
HostedAgent           the HOST: lifecycle (spawn/readiness/quiet clock/restart/health)
                      + the brain verbs (Ask/Cancel/Clear/Restart/Kill/Health)
```

Key insight surfaced in the design discussion: ConPty is windowless - every session is
already invisible. "Headless" vs "visible" is purely whether a UI renders the buffer,
so the same driver+backend pair serves the HostedAgent today and the Director later.

## Step 1 - IAgentDriver + ClaudeDriver (in CcDirector.Core/Drivers/)

Core is the home so the Director can adopt drivers later without new references.

```csharp
public interface IAgentDriver
{
    AgentKind Kind { get; }
    DriverCapabilities Capabilities { get; }                  // flags
    string ResolveExecutable(string? configuredPath);          // PATH lookup, fail loud
    AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? preassignedSessionId);
    Task SubmitAsync(ISessionBackend backend, string text);    // submit semantics
    Task CancelAsync(ISessionBackend backend);                 // claude: Esc (0x1B)
    Task ClearContextAsync(ISessionBackend backend);           // claude: submit "/clear"
    // Transcript access (capability TranscriptRead; throws NotSupported otherwise):
    List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workdir);
    SessionUsageDto? ReadUsage(string agentSessionId, string workdir);
    List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workdir);
}

[Flags] enum DriverCapabilities { None, ClearContext, Cancel, TranscriptRead, PreassignedSessionId }
```

- `ClaudeDriver` extracts knowledge that exists today: launch args from `ClaudeAgent`
  (`--dangerously-skip-permissions --session-id <guid>`), submit via
  `backend.SendTextAsync` (which carries the large-input @-file trick), cancel = a
  single Esc byte (same as the Director's POST /escape), clear = submit `/clear`,
  transcripts via `ClaudeSessionReader`/`WidgetBuilder`/`SessionTokenUsage`.
  Internal `ITranscriptReader` seam kept for hermetic tests.
- Verbs a tool lacks throw `NotSupportedException` - declared via Capabilities,
  never emulated (no fallback programming).
- Deliberately OUT of the driver v1: screen-state classification (stays in the
  Director's detector; the brain runs on the byte-quiet clock), shutdown keystrokes
  (the host uses `backend.GracefulShutdownAsync`), composer-clear/double-Esc
  (tier-3 extra, add when a consumer needs it).

## Step 2 - HostedAgent hosts a driver

- Ctor becomes `HostedAgent(options, IAgentDriver? driver = null, Func<ISessionBackend>? backendFactory = null)`;
  default driver = ClaudeDriver. Convenience: `HostedAgent.For(AgentKind, options)` -
  ClaudeCode returns a working host, every other kind throws NotSupported with a
  clear "write the driver first" message.
- All tool-specific calls route through the driver: spawn args, submit, cancel,
  clear, transcript reads. The ITranscriptReader ctor seam moves into ClaudeDriver.
- **New verb: `CancelAsync`** on IAgentBrain + both implementations:
  HostedAgent -> driver.CancelAsync (Esc); AgentBrainClient -> the Director's
  existing POST /sessions/{sid}/escape. Panel gets a CANCEL TURN button.
- Public API otherwise unchanged - the 15 HostedAgent tests keep passing with a
  FakeDriver, proving the refactor moved behavior without changing it.

## Step 3 - Tests

- ClaudeDriverTests (new): launch spec args, capabilities, Esc byte written on
  cancel, "/clear" submitted on clear, executable resolution failure, transcript
  delegation (fake reader).
- HostedAgentTests: rewired to FakeDriver + FakeBackend; add cancel pass-through and
  For(AgentKind) factory cases (Codex/Gemini/Pi -> NotSupportedException).
- AgentBrain tests: CancelAsync -> POST /escape route.

## Step 4 - Live QA (the proof) and report

Panel rebuilt and relaunched via the `agent-brain-panel-launch` scheduled task (the
nested-ConPty rule). Full sequence rerun on the FINAL binaries - the pre-refactor HQ
results are discarded, everything below is evidence of the driver build:

| # | Case | Pass criterion |
|---|---|---|
| HQ-1 | Unit suites | HostedAgent + ClaudeDriver + AgentBrain tests all green |
| HQ-2 | START HOST (headless spawn) | claude.exe is a CHILD of the panel process, carrying the driver's pre-assigned --session-id |
| HQ-3 | Ask | full reply + latency + context tokens in the log |
| HQ-4 | Long answer | >2000 chars intact (transcript path, no truncation) |
| HQ-5 | Clear context | codeword -> CLEAR -> recall returns CONTEXT-EMPTY; transcript id switched; same claude.exe pid (process never restarted) |
| HQ-6 | Auto-clear mode | second ask has no memory of the first |
| HQ-7 | Restart | claude.exe pid CHANGES; fresh agent answers |
| HQ-8 | Crash recovery | claude.exe killed externally -> health DEAD -> RESTART heals -> answers |
| HQ-9 | Kill | claude.exe child gone from the process tree |
| HQ-10 | CANCEL TURN (driver Esc) | a long-running turn stops early; session stays usable and answers the next ask |
| HQ-11 | Two hosts, one process | console smoke (scheduled task) runs two HostedAgents side by side, both answer, killing one leaves the other alive |
| HQ-12 | Unsupported drivers fail loud | For(Codex/Gemini/Pi) throws NotSupported (unit-level, quoted in report) |

Report: docs/features/hosted-agent/QA_REPORT.html (cc-html boardroom, screenshots
embedded) - written to show the ClaudeDriver running fully headless in the harness.

## Out of scope (explicit)

- Codex/Gemini/Pi/OpenCode drivers (need per-tool live keystroke verification; write
  them with their first consumer)
- Director/Session migration onto drivers (phase 4 of the discussion; incremental,
  starting with /interrupt + /escape)
- Screen-state classification in the driver
