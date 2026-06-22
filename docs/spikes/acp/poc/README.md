# THROWAWAY ACP proof-of-concept (issue #627)

This is **throwaway spike scaffolding**, not production code. It is deliberately
**not** part of `cc-director.sln` and must never be referenced by a production
project. Its only job is to produce REAL observed `copilot --acp` wire transcripts
for `docs/spikes/acp/RECOMMENDATION.md`.

## What it does

`Program.cs` launches `copilot --acp` (via `node npm-loader.js --acp`, bypassing the
Windows `.cmd` shim) and acts as a full JSON-RPC 2.0 peer over the agent's stdio:

- sends `initialize`, `session/new`, `session/prompt`, `session/cancel`
- answers the agent's `session/request_permission` (auto-approves the first request)
- implements `fs/*` and `terminal/*` client handlers (Copilot never calls them)
- writes every line in both directions verbatim to a transcript file

## Auth

Copilot CLI accepts `COPILOT_GITHUB_TOKEN` / `GH_TOKEN` / `GITHUB_TOKEN`. A GitHub CLI
OAuth token (`gh auth token`, a `gho_...` token) is a supported type. Classic PATs
(`ghp_...`) are not supported.

## Re-run

```
dotnet build AcpPoc.csproj
COPILOT_GITHUB_TOKEN="$(gh auth token)"  ACP_POC_SCENARIO=cancel      dotnet run --no-build -- transcripts/acp-live-transcript.txt
COPILOT_GITHUB_TOKEN="$(gh auth token)"  ACP_POC_SCENARIO=permission  dotnet run --no-build -- transcripts/acp-permission-transcript.txt
```

The agent's `cwd` is a fresh throwaway sandbox under the OS temp directory - never this
repo. Committed transcripts live in `transcripts/`.
