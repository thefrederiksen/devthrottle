# Fix report - the Grok transcript test no longer depends on what this machine has ever run

Written by the Developer on the mission "A test that does not depend on what this machine has ever
run" (issue 3029), 18 September 2026, on SOREN_NORTH, in the worktree
`D:\ReposFred\devthrottle.worktrees\wt05` on branch `mission/grok-transcript-test-3029`.

Read with the task it answers: `DEVELOPER-TASK.md` beside this file.

## 1. What I changed

One file, and it is a test file. **No product code was touched.** The product was right, exactly as
issue 3029 diagnosed.

`src/CcDirector.Gateway.UnitTests/TurnsVerbUnresolvedTranscriptTests.cs`

All six tests in that class shared one helper, `NewSession()`, which built the session on
`Path.GetTempPath()`:

```csharp
var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
```

That helper is now a small disposable fixture, `UnresolvableSession`, which creates its own
directory, uses it as the repository path, and removes it when the test ends:

```csharp
RepoPath = Path.Combine(Path.GetTempPath(), "ccd-turns-unresolved-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(RepoPath);
Manager = new SessionManager(new Core.Configuration.AgentOptions());
Session = Manager.CreateEmbeddedSession(RepoPath, null, new ExecuteActionTestBackend());
```

`Dispose` disposes the session manager and then deletes the directory. The delete is deliberately
not wrapped in a swallowing `catch`: the directory is the run's own, nothing holds it open, and a
failure to remove it is a real fault worth seeing rather than hiding.

Two consequences, both handled:

- **Every test in the class shares that fixture**, so all six moved to it - the three unresolved
  cases (Pi, Codex, Grok), the unsupported control (Cursor), the resolved-but-empty case (Gemini),
  and the Claude Code empty-transcript case. That is the "whatever it shares its fixture with" the
  task asked about; the assumption was made once, in the helper, and is now fixed once.
- **The Claude Code case now removes the project folder it creates.** Claude Code files a transcript
  under a folder named after the working directory. With the old shared temporary path that folder
  already existed on a developer's machine, so deleting just the transcript file was enough. With a
  fresh working directory per run the folder is new every time, so deleting only the file would drop
  an empty folder into the developer's real `~/.claude/projects` on every run. The test now deletes
  the folder it created. Verified below.

The assertions are unchanged. Nothing was weakened, nothing was skipped, and the Grok case is still
asserted.

## 2. Why that directory can never be resolved by any locator

The verb resolves through `SessionHistoryReader.ResolveTranscriptPath`. For the three agents this
test asserts an unresolved transcript for:

| Agent | Locator | What it keys on | Why a directory created microseconds ago cannot match |
| --- | --- | --- | --- |
| Grok | `GrokSessionLocator.Resolve(session.Id, session.RepoPath)` | A folder under `~/.grok/sessions` whose percent-decoded name equals the repository path | Grok writes that folder when it RUNS in a working directory. No agent has ever run in a directory that did not exist a moment ago, so no such folder exists, and `Scan` returns null. |
| Codex | `CodexRolloutLocator.Resolve(session.Id, session.RepoPath, session.CreatedAt)` | A `rollout-*.jsonl` under `~/.codex/sessions` whose `session_meta.cwd` equals the repository path | Same argument: a recorded rollout's `cwd` is a directory Codex actually ran in. |
| Pi | `PiSessionLocator.Resolve(session.ClaudeSessionId)` | The AGENT SESSION ID, not the path | An embedded test session has no agent session id at all, so this one was already machine-independent. It moved to the fixture for consistency, not because it was broken. |

This is why the old premise was false on this machine and true elsewhere: `Path.GetTempPath()` is a
directory agents really do get run from. The proof it was false here, taken before any change:

```
$ ls ~/.grok/sessions/C%3A%5CUsers%5Csoren%5CAppData%5CLocal%5CTemp/
01a0af13-0f3e-7d13-b917-7515e73be39f
prompt_history.jsonl
$ find .../01a0af13-.../ -name chat_history.jsonl
-rw-r--r-- 1 soren 197609 82629 Sep 17 07:14 .../chat_history.jsonl
```

82,629 bytes of real Grok conversation, last written 17 September. `GrokSessionLocator` found it,
`SessionHistoryReader.Read` parsed it, the history was non-empty, and the verb answered `ok` -
correctly. The test, not the product, was wrong.

**The limit of that argument, stated plainly.** It holds for every locator that keys on the WORKING
DIRECTORY or on an id the session does not have. It does NOT hold for a locator that keys on a
GLOBAL STORE: `CopilotHistoryReader.DefaultDatabasePath` and `OpenCodeHistoryReader.DefaultDatabasePath`
resolve to one database for the whole machine, which exists or does not exist for reasons that have
nothing to do with this session's path. Those two agents were already deliberately left out of this
test's assertions (the class comment says why, and records that an earlier version of the test did
include them and failed on exactly that), and this change does not bring them in. A future locator
keyed on the working directory is covered; one keyed on a global store would not be.

## 3. The runs, with their real numbers

Every run is `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~TurnsVerbUnresolvedTranscriptTests"`
on SOREN_NORTH. No run used `--no-build`; each one rebuilt from source first, so each result
certifies the source it claims to.

| # | State of the tree | Result | Log |
| --- | --- | --- | --- |
| 1 | Clean mission branch at `849e2b39d`, before any change | **Failed: 1, Passed: 5** - Grok case, `Expected: "no_transcript"  Actual: "ok"` | `evidence/developer-1-before.txt` |
| 2 | With the fix | **Failed: 0, Passed: 6** | `evidence/developer-2-after.txt` |
| 3 | Fix committed; product's `no_transcript` guard disabled | **Failed: 3, Passed: 3** - Pi, Codex and Grok all fail, `Expected: "no_transcript"  Actual: "empty_history"` | `evidence/developer-3-revert-proof-product-broken.txt` |
| 4 | Fix committed; product returned to its pre-issue-2561 behaviour (both guards removed, the branch stamps `ok`) | **Failed: 4, Passed: 2** - Grok fails with `Expected: "no_transcript"  Actual: "ok"` | `evidence/developer-4-revert-proof-pre-2561-product.txt` |
| 5 | Product restored, rebuilt | **Failed: 0, Passed: 6** | `evidence/developer-5-after-restore.txt` |
| 6 | Unchanged, run again | **Failed: 0, Passed: 6** | `evidence/developer-6-repeat.txt` |

Run 1 reproduces the Delivery Lead's measured before-run in `evidence/before-filtered.txt` exactly.

## 4. The revert proof - the test can still fail, and fails for the right reason

A test that has never been watched failing is decoration, so the fix was committed first (a
`git checkout --` after mutating would otherwise have eaten it) and then the PRODUCT was broken
twice, under the fixed test.

**Run 3** disabled only the `no_transcript` guard. All three unresolved cases went red at once -
Pi, Codex and Grok - each reporting `empty_history` where `no_transcript` was expected. Grok going
red here is the whole point: under the old test Grok never reached that branch at all, because the
locator resolved a real transcript.

**Run 4** removed both guards, returning `SessionReadExecutor.Turns` to what it did before issue
2561 - stamping `Status = "ok"` on that branch unconditionally. The Grok case failed with:

```
Expected: "no_transcript"
Actual:   "ok"
```

That is byte-for-byte the symptom in run 1 - but for the opposite reason. In run 1 it was the
machine's Grok history making the premise false. In run 4 it is the product actually reporting an
unresolved transcript as a successful read, which is the defect the assertion exists to catch and
the one that left a Pi session silent for 48 minutes.

**Run 5** restored `src/CcDirector.ControlApi/SessionReadExecutor.cs` with `git checkout --`, and
the run rebuilt `CcDirector.ControlApi` before testing (`CcDirector.ControlApi -> ...dll` appears in
that log) - so the green describes the restored source, not a stale binary. `git diff HEAD` over
product code is empty.

## 5. Cleanup verified, not assumed

After runs 2, 5 and 6, both of these were checked and both found nothing:

```
ls -d "$LOCALAPPDATA"/Temp/ccd-turns-unresolved-*        -> No such file or directory
ls -d ~/.claude/projects/*ccd-turns-unresolved*          -> No such file or directory
```

So the test leaves neither its working directory nor a Claude Code project folder behind, and two
consecutive runs do not interfere with each other.

## 6. What is NOT proven

Named deliberately, because a proof that does not state its edges reads as bigger than it is.

- **The whole suite was not run.** The task reserved that for the Delivery Lead. This report covers
  six tests in one class. A neighbour broken by this change would not show up here.
- **Only this machine.** Every run above is SOREN_NORTH, Windows 11, .NET 10, Debug. The change
  removes a dependence on machine history by construction, but it has not been executed on Linux or
  macOS, and `Path.Combine(Path.GetTempPath(), ...)` plus `Directory.Delete` are the only platform
  surfaces it adds.
- **The store-backed agents are still uncovered here**, and this change did not make them coverable.
  Section 2 says why. `WingmanVoiceServiceTests` drives their statuses directly.
- **A future locator keyed on something other than the working directory or the agent session id**
  could resolve for this fixture. The argument in section 2 is about the locators that exist today,
  read at `origin/main` plus this branch's commits, not a guarantee about code not yet written.
- **The nine remaining baseline failures were not looked at.** The eight
  `HostedSchemaRefusesAnUnownedRowTests` failures and the intermittent
  `GatewaySessionConcurrencyStoreTests` collision are named out of scope in the mission document and
  were not touched, run, or investigated.
- **This is self-testimony.** It is the Developer's own report of its own work, and it has not been
  reviewed. The Delivery Lead runs the check itself and sends the code to a Reviewer on a different
  agent; nothing here should be taken in place of either.
