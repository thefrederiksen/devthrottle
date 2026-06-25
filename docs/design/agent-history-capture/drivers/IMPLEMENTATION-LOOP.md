# History drivers: implementation loop runbook

The instructions every iteration of the driver implementation loop follows. The Codex driver
(issue #707, already merged into branch `feat/agent-history-capture`) is the worked template;
each remaining driver repeats its shape. One driver per iteration, in the order below.

## Branch and worktree

Work on branch `feat/agent-history-capture` in the worktree `D:\ReposFred\devthrottle-history-capture`.
Do NOT touch the main worktree. Build/test only `CcDirector.Core` and `CcDirector.Core.Tests`
unless a step needs the desktop app.

## Order (respect the one dependency)

1. **#708 Pi** - JSONL-file driver (same shape as Codex).
2. **#709 Grok** - JSONL-file driver; extra work is encoding the repo path into Grok's session
   directory name.
3. **#710 Copilot** - SQLite driver; ALSO builds the shared `SqliteSnapshotReader` (copy db +
   `-wal`/`-shm` to temp, open read-only, never lock the writer).
4. **#711 OpenCode** - SQLite driver; REUSES `SqliteSnapshotReader` from #710. Must come after Copilot.

## The template (what every driver is)

Mirror the Codex driver exactly:

- `src/CcDirector.Core/<Agent>/<Agent>SessionLocator` (or `...HistoryReader` for the SQLite ones):
  resolve the newest store for the session's repo cwd; cache per session id; re-scan if the cached
  path/row disappears. Expose a testable `Scan(repoPath, storeDir)` (or equivalent) that does not
  touch the user profile.
- `src/CcDirector.Core/<Agent>/<Agent>TranscriptReader.Read(path)` (JSONL) or
  `<Agent>HistoryReader.Read(repo)` (SQLite): map the agent's store into the agent-agnostic
  `CcDirector.Core.History.ConversationHistory` (Messages -> Role + ordered Parts:
  Text / Thinking / ToolUse / ToolResult). Open files with `FileShare.ReadWrite` and tolerate a
  truncated final line; for SQLite, snapshot-then-read-only.
- `src/CcDirector.Core/History/SessionHistoryReader.cs`: add the `AgentKind.<Agent>` branch to BOTH
  `IsSupported`, `ResolveTranscriptPath`, and `Read`. This is the only wiring point - the History tab
  (`HistoryView`) already renders any `ConversationHistory`, so NO `CcDirector.Avalonia` change.
- `src/CcDirector.Core.Tests/<Agent>/...`: a reader test against a real-shaped fixture (incl. a
  truncated/garbage line) asserting the canonical mapping, and a locator test (newest-for-cwd,
  ignores other cwds and older entries). Follow the Codex tests as the pattern.

The canonical contract, the wiring, and the test pattern are identical across drivers; only the
parsing of the specific store differs. Read the matching plan doc first:
`docs/design/agent-history-capture/drivers/{pi,grok,copilot,opencode}.md`.

## Per-iteration checklist

1. Read the issue and its plan doc.
2. Implement locator + reader + the `SessionHistoryReader` branch.
3. Add unit tests; `dotnet test ...CcDirector.Core.Tests --filter "FullyQualifiedName~<Agent>"` green.
4. Prove the reader against REAL on-disk data for that agent (see store map below) with a THROWAWAY
   test (machine-specific path) - delete it before finishing. This caught a real Codex-version
   change; do it for every driver.
5. Build the desktop app once (`scripts\local-build-avalonia.ps1 -Slot 5`) to confirm the wiring
   compiles into the real Director.
6. Live in-app QA (the literal acceptance): see recipe below.
7. Leave the worktree clean (no throwaway files). Commit policy: ASK before committing - the standing
   rule is never commit without being explicitly asked.

## Store map (where each agent persists history)

- Pi: `~/.pi/<ts>_<session-id>.jsonl` - role user/assistant/toolResult, id/parentId tree, cwd.
- Grok: `~/.grok/sessions/<encoded-cwd>/<session-id>/chat_history.jsonl` (+ events.jsonl, summary.json).
- Copilot: `~/.copilot/session-store.db` (SQLite) - `turns` + `forge_trajectory_events` + `sessions`.
- OpenCode: `~/.local/share/opencode/opencode.db` (SQLite) - `message` + `part` + `session`.

## Live in-app QA recipe (per driver) - learned the hard way on Codex

1. Build slot 5 from the branch; launch the Director via the `cc-director-launch` scheduled task
   (NOT from this process - nested ConPTY kills grandchild agents). Read the Control API port from
   `%LOCALAPPDATA%\cc-director\logs\director\director-*-<PID>.log` (`Kestrel listening on ...`).
2. Create a throwaway repo with a unique MARKER file. To dodge a per-root TRUST prompt, do NOT
   `git init` it if it sits under an already-trusted ancestor (e.g. `c:\users\soren`); otherwise
   pre-trust it.
3. `POST /sessions` with the agent kind and the repo. **Use forward-slash paths in the JSON body** -
   backslashes break minimal-API binding (empty 400).
4. Send the tool-forcing prompt via `POST /sessions/{sid}/prompt {"text":"...","appendEnter":true}`.
   If the agent's TUI does not submit (Codex 0.141 did not), send an explicit Enter:
   `{"text":"\r","appendEnter":false}`.
5. Wait for the turn to finish; the store flushes DURING/after the turn, not at launch - don't
   conclude "no store" too early. Sort by mtime; Git Bash `find -newermt` is unreliable here.
6. Run the EXACT History-tab path (`SessionHistoryReader.Resolve...`/`Read`, via the agent's
   locator+reader) against the live store in a throwaway test; assert the prompt, the tool call, its
   result, and a unique marker render in order. Delete the throwaway test.
7. Delete the QA session; stop ONLY your slot-5 Director (verify the path matches your slot before
   `Stop-Process` - never touch the user's main/1-4 Directors).

## Done = for each driver

Locator + reader + `SessionHistoryReader` branch + passing unit tests + a real-data smoke + the live
in-app QA above, with the worktree left clean. Regroup with the user at each driver boundary (or per
the agreed cadence) rather than running all four unattended.
