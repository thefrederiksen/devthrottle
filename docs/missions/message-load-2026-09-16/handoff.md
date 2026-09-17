# Message Load - the running note

This is the note a fresh Manager is rebuilt from. Short on purpose. Everything the Architect knows is
here, in `brief.md`, or in `findings.md`, never only in a conversation.

## Where the rest lives

- The brief and rulings: `docs/missions/message-load-2026-09-16/brief.md` (this folder)
- What is true today, with file references: `findings.md`
- The owner-facing page he approved: `design-and-questions.html`
- Conduct: `cc-devthrottle workflow instructions mission`
- Firstmate handover (internal repository): `devthrottle_internal/docs/research/firstmate/handover-fleet-manager-and-message-load.html`

## Branch

`mission/message-load`, worktree `~/ReposFred/devthrottle-message-load` on devthrottle-mac-mini,
cut from `origin/main` at `66609c3c`. Rebase onto origin/main before each slice.

## Rules of this mission that are easy to forget

- NOBODY on this mission sends a fleet message, to anyone, for any reason. Report by writing this
  file, committing, pushing, then `cc-devthrottle session raise "<one line>"`.
- One slice at a time, in the brief's order. A slice is a pull request the Architect merges.
- Every guard is watched failing before it is called a guard.
- Mac tests: 61 Core.Tests failures (not 16) are pre-existing on origin/main on the Mac mini, 16 September 2026. Name which ones you
  saw, do not "fix" them.
- Build to slot 5 or higher for any test Director; never touch the owner's running Directors.

## State

- Phase: slice 1 (the inbox and the gate) is BUILT, inspected (FAIL), and its FIX ROUND is pushed on
  `mission/message-load` (16 September 2026; see "Slice 1 fix round" at the end). No pull request opened
  (the Architect's).
- Next: the Architect's decision on the open question in the fix round section, a re-inspection if wanted,
  then slice 2 (the doorbell).
- Open decisions: none new. Judgement calls made inside the brief are listed below for the inspector.

## Slice 1 - what was built

Gateway (`src/CcDirector.Gateway/Messaging/`):
- `fleet_messages` table (`FleetMessageEntity`), SQLite and Postgres migrations `AddFleetMessages`. The
  ring/stuck columns (slice 2) and reply columns (slice 3) exist now so the table needs one migration pair;
  nothing in slice 1 writes them.
- `FleetMessagePolicy` - the one pure ruling: only your supervisor (`ControllerSessionId`) and the sessions
  you started; 6 per rolling hour per sender; 1 per recipient per 10 minutes; an unread identical message
  is dropped (answers 200 "duplicate"). Limits live in `FleetMessageLimits` (also ring grace 5 minutes,
  3 rings, 30-day retention, 16,000-character text cap).
- `FleetMessageStore` - check and write under one lock; limits counted from the rows (survive a restart);
  the inbox read returns every unread message in full and marks it read in the same step; session ids
  trimmed and lower-cased; 30-day purge of read and stuck rows (never an unread one), on the judged-stop
  retention timer tick.
- `FleetMessageService` - decides, writes, reads; one log line per send, refusal and read (text never
  logged, only its length).
- Routes: `POST /sessions/{sid}/message` answers `queued` / `duplicate` / `refused` (403 relationship,
  429 rate, 400 text) with the reason in `error`; `waitForIdle` (old `message ask`) is refused 400;
  `kind` may be `message` or `report` only. `POST /fleet/broadcast` queues one copy per worker
  (`ControllerSessionId` == sender); `--everyone` still needs a reason and a human grant, and its copies
  are queued. `GET /fleet/inbox[?all=true]` reads the CALLER's own inbox (no id in the path; a device key
  gets 403).
- `SessionKeyGuard` refuses `POST /sessions/{sid}/prompt|interrupt|escape` and `POST /fanout` to a session
  key with `AgentInputRefusal.Typing` (names the queued message). The compaction route refuses a
  `continuePrompt` from a session key (`AgentInputRefusal.CompactContinue`); a plain compaction is open.
  The owner (device key or shared token) is unchanged on all of them.
- `MessageSteward` (Core) and its options and tests are deleted; the policy replaces it.
  `BroadcastGovernor` still owns the grants and the whole-account broadcast rate.

Command line (`tools/cc-devthrottle`):
- `message send` prints "Queued ... (message <id>)" or "Not queued: <Gateway sentence>" (exit 1);
  a duplicate is exit 0; an answer in the OLD `{"accepted": true}` shape is treated as not queued.
- `message send all` reports per recipient, counts by outcome, exits 1 if nothing was queued.
- `message inbox [--all] [--json]` prints every unread message in full (ASCII, escaped), help lines.
- `message ask` removed from commands and the action catalogue; `message-inbox` added; `message-send`
  and `session-compact-continue` descriptions rewritten.
- `session report` posts `kind: report`; `session prompt` / `session interrupt` help says the Gateway
  refuses them to agents; the self-test spawns a worker it controls and checks "queued".
- `packages/client-core/src/api/schema.ts` request type hand-aligned (no client calls these routes).

## Judgement calls the inspector should look at

1. `escape` and the raw `/fanout` are refused to session keys along with prompt and interrupt - ruling
   17 names prompt, interrupt and compact-continue; escape is an agent's soft interrupt and fanout types
   into sessions, so leaving them open would be a side door.
2. A REPORT is exempt from the 10-minute per-recipient spacing (every refusal says "put it in your
   report"); it is still held to the hourly limit, the relationship and the duplicate rule.
3. A granted `--everyone` copy is exempt from the relationship and rate rules and is NOT counted toward
   the sender's hourly six; the duplicate rule still applies.
4. Team broadcast copies each count toward the sender's six an hour.
5. A duplicate = same sender, same recipient, identical text, earlier one still unread (no time window).
6. `turn-verdict/answer` (writes a verdict's option into a session) is still open to session keys; it is
   the owner's tap path by design but a session key can call it. Not changed; flagged.

## What is proven (each guard watched failing, then restored)

Command line, each red on the named test: old accepted shape read as queued; inbox body truncated;
report sent without `kind`.
Gateway, each red on the named tests: guard without the agent-input check (4 route tests); compact-
continue refusal removed (1); the message route also typing the text into the recipient ("nothing is
typed" test); broadcast not limited to workers (2); relationship always holding (9); report held to the
spacing (2); hourly limit off by one (3); read not marking read (7); granted broadcasts charged (1);
purge deleting unread (1); inbox read missing from the guard (1); id not lower-cased (1).
One mutation stayed GREEN: removing the null handling from the policy's id comparison alone. It is
unreachable (the policy never compares supervisor to supervisor); the test's comment now says so, and the
combined "siblings may talk" widening turns it red (5 tests).

## What is NOT proven

- Postgres: the collation census and migration proofs SKIPPED (Docker not running on the Mac). The
  census list was updated by hand for the three new "C" columns; the generated Postgres migration shows
  them. Run `-Parked` on a machine with Docker.
- `scripts/test-local.ps1` not run (no PowerShell on the Mac); the suites were run directly instead.
- Nothing ran against a live Director or the hosted Gateway. No end-to-end with a real agent.
- Until slice 2 lands, a queued message is NEVER announced: nothing rings, so a recipient only sees it if
  it runs `message inbox` on its own. Merging slice 1 alone makes fleet messages silent. The Architect
  should decide whether slice 1 merges before slice 2.
- Words are slice 5: the fleet preamble (`FleetPreambleTemplate.cs`), the fleet-comms skill,
  `docs/cli-reference.md` and `docs/FleetMessaging.md` still teach `message ask` and interrupting
  messages. `FleetMessaging.BuildFramedMessage` is now unused by product code (kept; its tests remain).
- The retention sweep's timer wiring is not tested (the store's purge is).
- Baseline (per-day message count): not attempted in slice 1.

## Test output (Mac, rebased tree, 16 September 2026)

- Gateway unit tests: 4969 total, 4954 passed, 8 skipped, 7 failed - all 7 fail identically on
  origin/main (Mac-only): CronJobStoreTests.LegacyJson_RenameFailsAfterImport...,
  WorkListStorePersistenceTests.Import_RenameAsideFails..., SessionCommandExecutorLivenessTests (3),
  RuleCandidateFilterTests.A_rule_scoped_to_this_sessions_repository..., RulePrimitivesTests.IsPathInside_follows_a_link...
- Gateway parked tests (full run, before the final rebase): 2517 total, 2451 passed, 48 skipped, 18
  failed. 16 fail identically on origin/main (ContextLessRouteCensus 1, FleetSpawnMissionAttach 2,
  FleetSpawnOrigin 4, TunnelRosterPushReadProof 3, WorkflowSeat 2, GatewayTestSuiteLock 2,
  HostedProcessControlDeny 2). The other 2 were PromptAttributionIsGatewayAuthoritativeTests driving
  `/prompt` with a session key - rewritten to assert the refusal. After the rebase the touched classes
  (FleetMessageRouteTests 25, attribution, fanout, framing) passed 49, skipped 6 (Postgres).
- Core tests: 4417 total, 61 failed - the identical 61 fail on origin/main (4428 there; the 11 fewer are
  the deleted steward tests). Far more than the "about 16" in the note above: most are Windows path and
  shim tests, worktree reaper, lifecycle signal and mutation-pin tests. None touched by this slice.
- cc-devthrottle tests: all passed (1738 on the rebased tree); shared contract and output helper: 124 passed.

## Report channel failed (16 September 2026)

`cc-devthrottle session raise "slice 1 pushed"` was REFUSED by the live hosted Gateway: "a session key may
not call POST /sessions/<id>/needs-manager". The session key guard has no entry for `needs-manager`, so the
report channel this mission relies on does not work for any agent today. No message was sent instead
(mission rule). This note and the pushed branch are the report. Worth a fix in slice 1 or its own change:
add `needs-manager` to `SessionKeyGuard` with a test.

## Architect rulings after slice 1 (16 September 2026)

- Slice 1 merges after inspection even though nothing rings yet. Main is not production: the hosted
  Gateway and the Directors pick the change up at the release cut at the end of the mission, by which
  time the doorbell (slice 2) and the words (slice 5) are on main too. Recorded so nobody re-argues it.
- `needs-manager` is added to `SessionKeyGuard` with a test, in the slice 1 fix round. `session raise`
  is the report channel of this mission and it must work for agents.
- `turn-verdict/answer` is closed to session keys too (judgement call 6): an agent writing an answer
  into a session is agent input, and ruling 17 admits no side doors.
- Judgement calls 1 to 5 stand as made.
- Inspection 1 (Codex, adversarial) writes `inspection-1.md` in this folder. The Postgres proofs run
  on SOREN_NORTH, result in `postgres-proof-1.md`.

## Architect rulings on inspection 1 (16 September 2026) - the slice 1 fix round

Inspection 1 verdict: FAIL for merge. Every finding is accepted. Rulings, in the order they are fixed:

1. **Verdict answer closed to session keys** (high). `POST /sessions/{sid}/turn-verdict/answer` is
   refused to a session key by `SessionKeyGuard`, with a guard test that fails on the current tree.
2. **A session key cannot invent a supervisor** (high). For a spawn made with a session key, the
   Gateway accepts `ControllerSessionId` only when it is the caller's own id or empty (standalone with
   its reason). Any other id is refused with a sentence that says why. The owner's spawns (device key
   or shared token) are unchanged. Test: a session-key spawn naming an unrelated live session is
   refused; naming itself is accepted. *Inferred* from rulings 1 and 17.
3. **Needs-manager opened to session keys.** `POST /sessions/{sid}/needs-manager` for the caller's
   OWN id is allowed to a session key, with a guard test; `session raise` works again.
4. **Read-then-lost recovery widened** (medium). `GET /fleet/inbox?all=true` returns every message
   read in the last 24 hours, not the latest 20. The read-marks-read protocol (ruling 9) stands; the
   doorbell in slice 2 re-rings unread messages only. Help text for `message inbox --all` says what it
   returns and why.
5. **Constants pinned** (low). The text cap and both advice sentences are asserted against literals
   in the tests, so a changed default goes red.
6. **Broadcast exit code stated** (low). An all-duplicate broadcast exits 0, matching a duplicate
   single send; the help text and the code comment say so in the same words.

Then: the full Gateway unit and route suites, the cc-devthrottle tests, each new guard watched
failing. Update this note, commit, push, `session raise "slice 1 fix round pushed"`, stop.

## Slice 1 fix round (16 September 2026)

Six commits on `mission/message-load`, one per ruling, in order, on top of `2d529de5` (the Postgres
proofs). Every guard below was watched failing with the fix reverted, then restored and seen green.

### What changed, per item

1. **Verdict answer closed** (`1682086d`). `SessionKeyGuard.IsAgentInput` now refuses
   `POST /sessions/{sid}/turn-verdict/answer` with `AgentInputRefusal.Typing`; the allow entry is gone. The
   feedback route beside it stays allowed. The route's own shadow rule is kept as a second wall (comment
   says so). `TurnVerdictAnswerRouteTests` rewritten: a session key now gets the guard's 403
   `session_key_out_of_scope` with colours ON and a live verdict; the device key on the same verdict still
   reaches the screen read.
   Red with the fix reverted: 2 guard cases (the literal path and a case-folded one) and 2 route tests - the
   session key got 409 "screen unreadable", meaning it had reached the typing step.
2. **No invented supervisor** (`e1cf6b5c`). `SpawnOrigin.TryEstablish`: a session key may declare only its
   own id (compared as a GUID, stored in the Gateway's spelling) or `none`. Any other id is refused 403 with
   `SpawnOrigin.NamedAnotherOwner` (pinned to a literal). Device keys and Director relays are unchanged.
   Tests: unit (refused, self accepted, device unchanged, relay unchanged, wording pinned) and three route
   tests on the real host with the tunnel Director (stranger named: 403 and no `create` sent; self named:
   `create` sent with controller and parent = caller; owner's token naming the stranger: created).
   Command line help (`--controlled-by`, and the "say who owns it" error block) no longer offers
   `--controlled-by <another id>`.
   Red with the fix reverted: the unit refusal test, and the route test answered **201 Created** for a
   session owned by the stranger.
3. **`session raise` works** (`f9b087d6`). `needs-manager` added to the guard's session POST list; the route
   refuses a session key raising any hand but its own (403, `GatewayEndpoints.NeedsManagerNotYours`, pinned
   in the test). **Found while doing this: the route carried the only `.RequireAuthorization()` in the
   Gateway, and the Gateway registers no authorization middleware, so the route answered 500 on every
   Gateway to every caller - owner included.** The marker is removed (comment says why). Route tests: own
   hand 200 with the reason echoed and nothing sent to the Director; another session's hand 403.
   Red, three separate reverts: guard entry removed (both route tests and the guard case red, the own raise
   403); own-id check removed (raising worker B's hand from worker A answered 200); marker restored (both
   route tests 500).
4. **Inbox recovery widened** (`b717f0d6`). `FleetMessageLimits.RecentReadWindow` = 24 hours;
   `FleetMessageStore.ReadInbox` returns every message whose READ time is within the window, no count cap
   (`RecentReadCount` deleted). The service passes the limit. Help for `message inbox --all`, its action
   catalogue entry, the function docstring and the contract comment say "every message read in the last 24
   hours" and why (a lost read is recovered this way). Tests: 30 separately-read messages all return; the
   window is measured from the read, not the write (a message written three days earlier and read a minute
   ago comes back), inclusive at 24 hours, gone a minute later; the service uses the product window; the
   24 hours pinned; the help text asserted.
   Red: cap of 20 restored (30-message test); window filter removed (2 tests); window set to 48 hours
   (3 tests); help reworded (help test).
5. **Constants pinned** (`2b3b14ac`). `MaxTextLength` asserted as 16,000 and the boundary test uses the
   literals 16,000 / 16,001; both advice sentences asserted against their full literal text; a test that the
   policy tests run with `FleetMessageLimits.Default`.
   Red: cap 8,000 (2 tests); each sentence reworded (1 test each).
6. **Broadcast exit code stated** (`d09930fd`). One sentence, identical in `message send --help` and the
   `_report_broadcast` docstring (`BROADCAST_EXIT_RULE`, referenced on the exit line): exit 0 when queued or
   an identical message already waits unread (for `all`, true of at least one worker), 1 when nothing was
   queued and nothing was waiting. Behaviour unchanged. Tests: an all-duplicate broadcast exits 0; the
   sentence is in both places.
   Red: exit condition narrowed to "queued only" (the all-duplicate test); help sentence reworded (the
   wording test).

### OPEN - needs the Architect: item 2 breaks the Director-restart restore command as written

`DrainRestoreCommand.Build` (`src/CcDirector.ControlApi/Drain/DrainRestoreCommand.cs`) writes restore
commands of the form `cc-devthrottle session spawn ... --controlled-by <the controller's id>` (a placeholder
for a controller restarted in the same drain, the real id for one on another Director). Whoever runs those
commands is normally a session (the director-restart skill), and the controller is normally not that
session. Under ruling 2 the Gateway now **refuses** every such spawn with 403. The drain itself is
unchanged and its tests stay green, because they only check the command text; nothing here runs it. The
same `--controlled-by <session-id>` line is still taught in the shipped `fleet-comms` skill
(`src/CcDirector.Gateway/Skills/Content/fleet-comms.skill.md` and its `.claude/skills` copy) and in
`docs/cli-reference.md` - those are slice 5 words and were not edited here.
Recommendation: keep ruling 2 (it is the relationship boundary) and change the restore so the restoring
session spawns with `--controlled-by self` and hands the seat to its real controller some other way, or let
the owner's device run the restore. That is a design choice, so it is left for the Architect.

### What is proven

The six rulings, each by a guard watched failing as listed above, on unit tests and, for items 1-3, on a
real Gateway host with a recording tunnel Director.

### What is NOT proven

- Nothing ran against the hosted Gateway or a live Director. `session raise` from this session will only
  work once this branch is deployed - until then the hosted Gateway refuses it at the guard, and had the
  guard let it through, the route would have answered 500.
- Item 2 on a live spawn: the route test proves the Gateway refuses before sending `create`; it does not
  exercise a real Director's create path. The relayed-spawn arm (a Director's own key) is still trusted for
  whatever controller the body names, as before - that is the boundary `SpawnOrigin` already documents.
- Item 4 on PostgreSQL: the new `ReadAtUtc >= readSince` filter ran on SQLite only. No schema change.
- The read-marks-read gap itself (inspection finding 3) is narrowed, not closed: a lost read is recoverable
  for 24 hours with `--all`, and the recipient has to know to ask.
- `scripts/test-local.ps1` not run (no PowerShell on the Mac); suites run directly.

### Test totals (Mac, this tree, 16 September 2026)

- Gateway unit tests: 4982 total, 4967 passed, 8 skipped, **7 failed - the same 7 named above**
  (CronJobStore 1, WorkListStorePersistence 1, SessionCommandExecutorLiveness 3, RuleCandidateFilter 1,
  RulePrimitives 1). No new failure.
- Gateway route tests (full suite): 2522 total, 2458 passed, 48 skipped, **16 failed - exactly the 16
  already named above** (ContextLessRouteCensus 1, FleetSpawnMissionAttach 2, FleetSpawnOrigin 4,
  TunnelRosterPushReadProof 3, WorkflowSeat 2, GatewayTestSuiteLock 2, HostedProcessControlDeny 2). No new
  failure; the two attribution failures from slice 1 stay fixed.
- cc-devthrottle tests: 1741 passed (1738 before, plus 3 new).
- `tools/cc_shared` tests (a sanity run, not touched here): 220 passed, 1 failed with
  `ModuleNotFoundError: mdit_py_plugins` - a package missing from this machine's scratch environment, not
  a code failure.
- Core tests: not rerun - nothing in Core changed in this round.

## Architect rulings on inspection 2 (16 September 2026) - fix round 2

Verdict FAIL for merge; both highs from inspection 1 confirmed closed. Rulings:

1. **Recovery read bounded** (medium). `GET /fleet/inbox?all=true` returns messages read in the last
   24 hours, newest first, at most 200 rows; the response carries `truncated: true` and the command
   line prints "showing 200 of N read in the last 24 hours" when it is. The rows are materialised
   outside the store lock. Test: 201 read rows return 200 and the flag; 200 return 200 and no flag.
2. **Residual read-loss interval accepted** (medium). Marking read before the response is sent is the
   protocol of ruling 9. The gap is written into the store's code comment as a gap, and into the
   command help for `--all`. No code change beyond the comment.
3. **The `none` spelling pinned on both sides** (low). A Gateway test asserts `SpawnOrigin.UserOwned`
   is the literal `none`; a command line test asserts the literal it sends is `none`. Both name the
   other side in their comment.
4. **Empty broadcast exits 1** (low). `message send all` with no workers exits 1 and prints "no
   workers to send to"; the existing test that asserted exit 0 is changed to assert this.
5. Restore breakage: NOT in this round. The owner is deciding. Facts from inspection 2: only a
   controlled seat restored by a session that is not its controller is refused; standalone seats and
   owner-run restores are unaffected.

Then the touched suites, each guard watched failing, this note updated, commit, push,
`session raise "fix round 2 pushed"`, stop.

## Slice 1 fix round 2 (16 September 2026)

Four commits on `mission/message-load`, one per ruling, on top of `078d5f65`. Each guard was watched
failing with the fix reverted or mutated, then restored and seen green. Item 5 (restore) untouched.

### What changed, per item

1. **Recovery read bounded** (`2067164b`). `FleetMessageLimits.RecentReadCap = 200`. `FleetMessageStore.ReadInbox`
   reads the recent rows BEFORE the store lock, untracked (`AsNoTracking`), counts the whole window, and takes
   the newest 200; `FleetInboxRead` carries `RecentTotal` and `RecentTruncated`. `FleetInboxResponse` gains
   `recentTotal` and `truncated` (a route test pins both names on the wire). `message inbox --all` prints
   "earlier: showing 200 of N read in the last 24 hours" when truncated, "earlier: N read before" otherwise.
   Tests: 201 read rows return 200, total 201, truncated, newest first; exactly 200 return 200, not truncated;
   a plain read reports 0 and not truncated; the 200 pinned in the limits literal test. Red, each alone:
   `.Take(cap)` removed (201-row test, 201 rows came back); truncation never reported (201-row test,
   `Assert.True`); cap default 201 (literal pin and 201-row test); command line truncation line removed
   (the truncated-output test).
2. **Read-loss interval stated** (`65f43cd2`). The store's `ReadInbox` comment has a "KNOWN GAP, ACCEPTED"
   paragraph (marked read before the response is built; recoverable with `--all` for 24 hours, only by
   asking). The `--all` help, the action catalogue entry and the `read_inbox` docstring say the same. No
   behaviour change. The existing help test now asserts "at most 200", "marks messages read before their
   text reaches you" and "only for 24 hours after that read"; red with the previous help text.
3. **`none` pinned on both sides** (`8631ff98`). `SpawnOriginTests.The_user_owned_spelling_is_the_literal_the_command_line_sends`
   asserts `UserOwned == "none"` and that a body carrying the literal `"none"` is accepted;
   `test_spawn_ops.py::test_standalone_sends_the_literal_the_gateway_recognises` asserts the command line
   sends `"none"`. Each comment names the other. Red: `UserOwned = "user"` turned the new test (and the
   existing case-insensitive test) red; the command line sending `"user"` turned the new test and three
   existing ones red.
4. **Empty broadcast exits 1** (`ae06ae3f`). `_report_broadcast` with no results prints
   "Not queued: no workers to send to." (plus the Gateway's warning when there is one) and exits 1.
   `test_send_all_with_no_workers_says_so_and_succeeds` became `..._says_so_and_fails` (exit 1), plus a
   no-warning case. Red on both with the old code (exit 0).

### Test totals (Mac, this tree, 16 September 2026)

- Gateway unit tests: 4986 total, 4971 passed, 8 skipped, **7 failed - the same 7 Mac-only failures named
  in earlier rounds** (WorkListStorePersistence 1, RuleCandidateFilter 1, CronJobStore 1,
  SessionCommandExecutorLiveness 3, RulePrimitives 1). No new failure.
- Gateway route tests, filter `FleetMessage|SpawnOrigin|FleetSpawnOrigin`: 42 total, 38 passed, **4 failed -
  the existing `FleetSpawnOriginTests` local-create failures** already listed (they fail on origin/main).
- cc-devthrottle tests: 1745 passed (1741 before, plus 4 new).

### What is NOT proven

- The bound is proven on SQLite only; the `Count` plus `Take` query did not run against PostgreSQL. No
  schema change.
- No load test: the cap bounds rows per request (200 times 16,000 characters at most), it does not rate-limit
  repeated `--all` reads.
- Reading the recent rows before the lock means a read racing another read of the same inbox may leave out
  rows that the other read marked a moment earlier; `--all` again returns them.
- The read-loss interval itself is unchanged, by ruling.
- `scripts/test-local.ps1` not run (no PowerShell on the Mac); suites run directly. Command line tests ran in
  a scratch environment with the declared dependencies.

## Architect ruling on inspection 3 (16 September 2026) - slice 1 lands

Verdict PASS for merge. The one low finding (a concurrent read can make the recovery count and page
disagree; it cannot mark a row twice or leave one unread) is accepted as-is: the 200-row bound holds
and the metadata is advisory. The four `FleetSpawnOriginTests` failures fail identically on
origin/main (local RawCli create on this Mac) and are not this slice's.

Slice 1 is landed by the Architect as one squash-merged pull request. After the merge the mission
branch is reset to origin/main and slice 2 (the doorbell) starts from there. The mission record in
this folder lands with it.

## Disposed SQLite investigation (17 September 2026)

Question: pull request 2970's .NET check (run 35176906244, job 105060497759) failed one test of about 5000,
`MorningReportMicrophonesTests.AHealthyMicrophoneIsReportedWithNoAdvice`, with "Cannot access a disposed
object. Object name: 'SQLitePCL.sqlite3'" while opening a fresh test database. Can slice 1 cause it?

### Verdict: not slice 1. A race that already exists on main, in how every Gateway database is disposed.

`GatewayDatabase.Dispose` calls `SqliteConnection.ClearAllPools()` (`src/CcDirector.Gateway/Data/GatewayDatabase.cs`,
line 543). That call clears the connection pool of EVERY SQLite file in the process, not just its own. It
has been there since `a2361e5e` (pull request 1770, 17 July 2026), unchanged on origin/main and on this
branch. Every test that uses `GatewayDbTestHarness` disposes a database at teardown, so during a parallel
run, tests keep clearing each other's pools.

Inside Microsoft.Data.Sqlite 9.0.2 (the version the Gateway pins; source read at tag v9.0.2), a pool clear
ends with `ReclaimLeakedConnections`, which treats a connection as leaked when it is marked active but has
no owning `SqliteConnection` yet. `SqliteConnectionInternal.Activate` sets those two fields one after the
other (`_active = true`, then `SetTarget(owner)`), and the pool lookup happens outside the factory's lock.
A clear that runs in that gap reclaims the connection and, because the clear has shut the pool down,
disposes its native handle. The owning test has already opened it, so its next statement fails
exactly as in the continuous integration log: `SafeHandle.DangerousAddRef` inside
`SqliteCommand.PrepareAndEnumerateStatements`. In the failing test, that statement was the
`PRAGMA journal_mode=WAL` right after `Migrate()` in `GatewayDatabase.Open` (line 418).

### Evidence

1. **Reproduced with no Gateway code at all.** `sqlite-pool-race-repro.cs.txt` beside this file: eight
   threads each open a fresh file, run a statement, close it, open it again and run
   `PRAGMA journal_mode=WAL` (the shape of `GatewayDatabase.Open`), then clear the pool the way a test
   teardown does. Microsoft.Data.Sqlite 9.0.2, this Mac, 90 seconds per run:

   | Clear at teardown | Runs | Opens | Disposed-handle failures |
   |---|---|---|---|
   | `ClearAllPools()` (what `GatewayDatabase.Dispose` does) | 2 | 190,571 | **38** (29 at `PrepareAndEnumerateStatements`, the continuous integration site) |
   | `ClearPool(own connection)` | 2 | 591,949 | **0** |

   An earlier variant, with a separate thread clearing all pools non-stop, also failed (3 in 30 seconds,
   3 in 120 seconds).
2. **(a) The purge timer cannot reach this test.** The fleet message purge rides the judged-stop
   retention timer, which is created only by `GatewayHost` (six-hour period, start delayed). No test in
   `CcDirector.Gateway.UnitTests`, where the failing test lives, constructs a `GatewayHost`. The purge
   never calls a pool clear, and it reads only its own database. A purge running against a disposed
   database would fail inside its own `using` on a disposed service provider, not inside another
   test's `Open` of a different file.
3. **(b) The `AddFleetMessages` migration holds nothing open.** It is plain table and index creation.
   The failing test does not even run migrations: the harness copies a migrated template, so `Migrate()`
   finds nothing pending. The handle died on the statement after it.
4. **(c) No shared path, static or connection with `FleetMessageRouteTests`.** Those tests are in
   `CcDirector.Gateway.Tests`, a separate test assembly and a separate test process. The failing test's
   database path is its own GUID folder. The only shared state is the process-wide pool, which
   `FleetMessageStoreTests` (in the same assembly) touches the same way as the other 111 files in that
   assembly that use the harness. Slice 1 adds that one file and a non-parallel collection
   (`AdminServiceTokenCollection`), so the order the tests run in differs slightly from main. That
   changes when the race fires, not whether it can.
5. **The fixture already records this signature.** `GatewayDbTestHarness` explains that a
   `ClearAllPools()` in the template builder made "every full run fail exactly one database test, a
   different one each time". That caller was removed. The one in `GatewayDatabase.Dispose` was not.
   About 50 more `ClearAllPools()` calls remain in this assembly's tests, most of them statistics database tests.
6. **Local runs of the real suite did not reproduce it** (the window is nanoseconds wide): five
   filtered runs (Morning report, fleet message, data and statistics classes; 230 tests) all passed.
   Three full `CcDirector.Gateway.UnitTests` runs (5041 tests) each failed only the seven known
   Mac-only tests listed in earlier rounds, with no disposed-handle failure.
7. The branch's earlier red run (35164108818) failed on two unrelated Wingman charter audit tests, not
   this one.

### Fix

None on this branch. Slice 1 does not cause it, and the test is not weakened or skipped. Recommended
separately, against main: `GatewayDatabase.Dispose` should clear only its own pool
(`SqliteConnection.ClearPool` on a connection built from its own connection string), and the statistics
tests should do the same. The reproduction above is the guard to watch: 0 failures with a per-file
clear against dozens with the process-wide one.

### What this does NOT prove

- The race was not caught inside the real Gateway test suite. The link from the continuous integration
  failure to this mechanism is the matching stack, the fresh file, and the fact that only a pool clear
  can dispose a handle another thread has open.
- The reproduction ran on macOS, not on the Windows runner.
- Checked only on Microsoft.Data.Sqlite 9.0.2, the pinned version.

### Rerun result

The rerun of the .NET job (run 35176906244, attempt 2, job 105073863475) passed: every test run in it
succeeded, including the Gateway unit tests (5041 tests), and `AHealthyMicrophoneIsReportedWithNoAdvice`
passed in 222 milliseconds. Every other job in the run passed too. The same commit, `dfdaf8b5`, failed once and
passed once, which is what a timing race looks like.

## Slice 1 landed (17 September 2026)

Merged to main as `a8fa8041` (pull request 2970, squash). The mission branch `mission/message-load`
was recreated from that main; the whole record above is on main. Three inspections, two fix rounds,
two CI rounds, one merge with main, one flake investigation (the disposed SQLite failure is a
pool-clear race already on main; see the section above and `sqlite-pool-race-repro.cs.txt`).

Lesson for the remaining slices: the .NET check takes about an hour and a half and main moves several
times an hour in the evening, so a pull request that waits is a pull request that conflicts. Keep each
slice small, rebase right before opening the pull request, and merge the moment the checks are green.

## State

- Phase: slice 2, the doorbell. Next: seat the Manager.
- Open decision, the owner's: the Director-restart restore step versus the spawn owner pin (see
  "OPEN - needs the Architect" above and the Architect's recommendation: make restore a Director act).
  Not blocking slice 2.
