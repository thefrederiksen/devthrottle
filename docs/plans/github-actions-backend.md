# GitHub Actions Remote Backend

## Goal

Let cc-director dispatch Claude Code work to GitHub-hosted runners instead of the
local machine. A "session" becomes a handle to a GitHub conversation thread (an
issue or PR) watched by the Claude GitHub App. The local box does zero compute
beyond a few REST calls; the Anthropic tokens and the actual work run on GitHub's
runner.

This offloads local CPU/RAM/ConPTY. It still spends Anthropic API tokens
(runner-side) and GitHub Actions minutes. It moves the work off the box; it does
not make it free.

## Concept

One session == one GitHub issue (or PR) thread. Each turn you send becomes a
`@claude ...` comment, which triggers a workflow run on a GitHub-hosted runner.
cc-director polls the run plus the action's live progress comment and pumps the
text into the session's terminal buffer.

Trigger model: issue-thread + `@claude` comments (conversational, multi-turn).
The `workflow_dispatch` one-shot model is a later subset for fire-and-forget jobs.

## What maps onto what

| cc-director concept     | GitHub Actions reality                                    |
|-------------------------|-----------------------------------------------------------|
| Create session          | Create (or attach to) an issue, post initial `@claude`    |
| Session window output   | Streamed runner logs + the action's sticky progress comment |
| Send text to session    | Post another `@claude <text>` comment on the thread       |
| ProcessId               | 0 (no local process)                                      |
| Session "Working" (blue)| Run status queued / in_progress                           |
| Session "WaitingForInput" (red) | Last run completed, no run in flight -> your turn |
| Session "Failed"        | Run conclusion failure / cancelled / timed_out            |
| Kill session            | Cancel in-flight run; thread stays on GitHub              |
| Backend output buffer   | Reuse CircularTerminalBuffer, write formatted text lines  |

## New code

- `SessionBackendType.GitHubActions` - new enum case.
- `GitHubActionsBackend : ISessionBackend` - `src/CcDirector.Core/Backends/GitHubActionsBackend.cs`.
  - Constructed with a `RemoteSessionConfig`.
  - `Write` / `SendTextAsync` -> POST a `@claude {text}` comment.
  - `Resize` -> intentional no-op (no PTY).
  - `GracefulShutdownAsync` -> cancel the in-flight run.
  - `ProcessId` = 0; `Buffer` = a real CircularTerminalBuffer.
  - Fires `ProcessExited` only on explicit kill - a completed run is a turn
    ending, not a session ending.
- `RemoteSessionConfig` - `src/CcDirector.Core/Backends/RemoteSessionConfig.cs`
  (Owner, Repo, BaseBranch, TriggerMode, InitialPrompt, ThreadNumber?, PollIntervalMs).
- `SessionManager.CreateGitHubActionsSession(RemoteSessionConfig)` - dedicated
  create method, mirroring CreatePipeModeSession / CreateEmbeddedSession. Does not
  abuse the generic (executable, args, workingDir, cols, rows) signature.
- `IGitHubClient` - thin wrapper over the REST calls (create issue, post comment,
  get comment, list runs, get run, get run logs, cancel run). Stubbable in tests.

## Activity state - driven by the API, not by silence

`TerminalStateDetector` infers state from PTY byte silence. For a remote session
the real run status is authoritative, so:

- `TerminalStateDetector` skips its silence rule for `BackendType == GitHubActions`.
- `GitHubActionsBackend` maps real run status -> ActivityState:
  - queued / in_progress -> Working
  - completed + no run in flight -> WaitingForInput
  - failure / cancelled / timed_out -> Failed (reason text in the buffer)

`StatusColor` stays Wingman-owned; the Wingman reads the same buffer text.

## Poll loop (v1) and the correlation problem

Per active session, a background loop:

1. After posting a comment, find the run it triggered. Primary signal: parse the
   run URL out of claude-code-action's sticky progress comment. Discovery path
   before that appears: `GET /repos/{o}/{r}/actions/runs?event=issue_comment&created=>{ts}`.
2. Poll run status every ~4s while active; pump status transitions + new
   progress-comment text (delta since last seen) into the buffer.
3. On run completion, pump the final reply comment + branch/PR link, flip to
   WaitingForInput.
4. When no run is in flight, back off to ~20s polling.

Rate limits: authenticated REST is 5000 req/hr; this cadence stays well under for
a dozen concurrent sessions. v2 upgrade is webhooks -> a Control API endpoint
fronted by the existing Tailscale Serve (push instead of poll). Polling ships
first because it needs no inbound plumbing.

## Control API surface

```
POST /sessions/github
  body: { owner, repo, baseBranch, triggerMode, initialPrompt, threadNumber? }
  -> 201 SessionDto
```

Everything else reuses existing routes: `GET /sessions/{sid}/stream`,
`POST /sessions/{sid}/prompt` (-> posts a comment), `DELETE /sessions/{sid}`.

`SessionDto` additive fields: RemoteRepo, RemoteThreadUrl, RemoteRunUrl,
RemoteRunStatus. `BackendType` gains the value "GitHubActions".

## UI

The remote session opens the same terminal view (streams the buffer). Add a header:
repo name + "Open thread on GitHub" + "Open run" links, and the send box posts a
comment instead of typing into a PTY. New-session dialog gains a backend selection.

## Target-repo requirements (one-time per repo)

1. Claude GitHub App installed.
2. `.github/workflows/claude.yml` using `anthropics/claude-code-action` triggered
   on issue_comment / pull_request_review_comment / issues when `@claude` is present.
3. Auth secret. Default: `CLAUDE_CODE_OAUTH_TOKEN` (from `claude setup-token`) so runs
   draw on your Max subscription rather than metered API billing. Alternative:
   `ANTHROPIC_API_KEY` for pay-as-you-go API rates.

## Auth (cc-director side)

GitHub token (PAT or App installation token) with repo + actions + issues scopes,
stored in `%LOCALAPPDATA%\cc-director\config\credentials.env` as `GITHUB_TOKEN`,
read at point of use by IGitHubClient, not at session start.

## Failure handling (explicit, no fallbacks)

- Comment posted but no run within N seconds -> explicit buffer error naming the
  fix (App installed? workflow present?) + Failed state.
- Run failure -> pump runner error tail + Failed + link.
- 401/403 -> clear token-invalid/scope message.
- Rate-limit 403 -> respect Retry-After, surface a visible "throttled" line.

## Testing

- `StubGitHubClient` (mirrors StubSessionBackend): scripted run statuses + comment
  bodies, assert buffer text + ActivityState transitions.
- Unit: comment->run correlation, status->activity mapping, cancel, error surfacing.
- Live test gated behind `GITHUB_LIVE_TESTS=1` against a throwaway repo.

## How we test it together (once the App is installed)

Phase 1/2 are implemented and the cc-director->GitHub plumbing is verified live
(`GitHubRestClientLiveTests`, gated by `GITHUB_LIVE_TESTS=1`, passes against the real
API using the token in credentials.env). The only thing that needs YOUR one-time setup
is the runner side:

1. Install the Claude GitHub App on the target repo: https://github.com/apps/claude
2. Auth (default = subscription, cheaper than API rates): run `claude setup-token` in
   Claude Code (needs Max) and add the result as repo secret `CLAUDE_CODE_OAUTH_TOKEN`.
   API-billing alternative: set `ANTHROPIC_API_KEY` and swap the `with:` line in the workflow.
3. Copy `docs/plans/sample-claude-workflow.yml` to `.github/workflows/claude.yml` and commit.

Then test from the desktop app: New Session -> "GitHub (Remote)" tab -> owner/repo/branch +
a task -> Start Remote. Or via REST against a running Director:

```
curl -X POST http://127.0.0.1:7879/sessions/github \
  -H "Content-Type: application/json" \
  -d '{"owner":"<owner>","repo":"<repo>","initialPrompt":"Summarize the CI workflow and suggest one improvement."}'
```

Without the App/workflow, a session still creates the issue and posts @claude, then
surfaces the explicit "No workflow run appeared" guidance after ~90s - that path is the
honest failure mode, not a silent hang.

## Phasing

1. IGitHubClient + GitHubActionsBackend (issue-thread mode) + CreateGitHubActionsSession
   + poll loop + activity mapping. Driveable via a unit harness.
2. POST /sessions/github + DTO fields + new-session dialog backend selection + UI header.
3. workflow_dispatch one-shot mode; webhook push to replace polling.
