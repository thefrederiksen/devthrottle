# Proof - Issue #627 (Spike: Evaluate ACP as a cross-agent transport)

This is a **research spike**. Its Definition of Done is a decision document plus a
throwaway proof-of-concept - it ships NO production code and touches no
`CcDirector.*` project. The proof here is that the recommendation rests on REAL,
re-runnable wire evidence from a live `copilot --acp`, not on spec prose.

## Deliverables

- Decision document: [`docs/spikes/acp/RECOMMENDATION.md`](../../../spikes/acp/RECOMMENDATION.md)
  - Contains all 6 required sections (wire transcript, capability comparison,
    client-burden list, generality finding, UX-parity note, recommendation) and
    answers Q1-Q9 each with Expected vs Actual.
- Throwaway PoC: [`docs/spikes/acp/poc/`](../../../spikes/acp/poc/) - C# JSON-RPC-over-stdio
  client (`AcpPoc.csproj`), deliberately NOT in `cc-director.sln`.
- Real captured transcripts (verbatim, committed):
  - `docs/spikes/acp/poc/transcripts/acp-live-transcript.txt` - lifecycle + a trivial
    prompt turn ("what is 2+2" -> streamed "4") + a `session/cancel`.
  - `docs/spikes/acp/poc/transcripts/acp-permission-transcript.txt` - lifecycle + a
    tool-using turn showing a full `session/request_permission` round-trip
    (`allow_once`/`allow_always`/`reject_once`) and `tool_call` / `tool_call_update`
    streaming; the agent really wrote `notes.txt` after approval.

## How the live evidence was produced (QA re-run)

Subject binary: GitHub Copilot CLI 1.0.63 (`copilot --acp`). Auth: a GitHub CLI OAuth
token (`gh auth token`, a `gho_...` token) supplied via `COPILOT_GITHUB_TOKEN`
(a supported token type; classic `ghp_` PATs are not). No token was pre-set in the
environment.

From `docs/spikes/acp/poc/`:

```
dotnet build AcpPoc.csproj
COPILOT_GITHUB_TOKEN="$(gh auth token)"  ACP_POC_SCENARIO=cancel      dotnet run --no-build -- transcripts/acp-live-transcript.txt
COPILOT_GITHUB_TOKEN="$(gh auth token)"  ACP_POC_SCENARIO=permission  dotnet run --no-build -- transcripts/acp-permission-transcript.txt
```

The PoC runs the agent with `cwd` set to a fresh throwaway sandbox under the OS temp
directory - never this repository.

## Recommendation reached

**Hybrid** - build a production ACP transport (a parallel transport beneath the driver
abstraction, not another `IAgentDriver`) alongside the existing terminal drivers, and
retire a terminal driver per agent only once an ACP conversation/permission view reaches
UI parity. The decisive wins are programmatic tool-call permission handling and clean
in-band cancellation, both observed live; the one true loss is the live terminal tab,
which is also the largest build item - hence hybrid, not rip-and-replace.

## Build sanity (no production code changed)

`dotnet build cc-director.sln` was run from the worktree to confirm the solution still
builds - the spike changed no `CcDirector.*` project (the PoC is excluded from the
solution).
