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

- Phase (17 September 2026, latest of all): slice 3 (replies without blocking) is BUILT and pushed on
  `mission/message-load`, on top of the slice 2 fix round - see "Slice 3" at the end. No pull request opened.
- Phase (17 September 2026, before slice 3): the slice 2 FIX ROUND is pushed on `mission/message-load` - see "Slice 2
  fix round" at the end. Next: inspection 5, and the Architect's decision on the snooze reading under item 8.
  No pull request opened.
- Earlier: slice 2 (the doorbell) is BUILT and pushed on `mission/message-load`, rebased on origin/main
  `dc6d6547` (17 September 2026). See "Slice 2" below. No pull request opened (the Architect's). Next: the
  Architect's inspection of slice 2, and the decisions in "Findings the Architect should decide on".
- Open decision, the owner's: the Director-restart restore step versus the spawn owner pin (see
  "OPEN - needs the Architect" above and the Architect's recommendation: make restore a Director act).
  Not blocking slice 2.

## Slice 2 - the doorbell (17 September 2026, Manager seat 2)

Built on `mission/message-load` from origin/main (slice 1 merged). No pull request opened. Evidence is in
`slice-2-evidence/`, which has its own README.

### What was built

The contract (`Gateway.Contracts/FleetMessageRequests.cs`):
- The tunnel verb `ring` (`FleetDoorbellVerbs.Ring`), whose payload is `FleetRingRequest { UnreadCount }`.
- The answer is `FleetRingResponse`, with `Outcome` either `rung` or `deferred`.
- A deferral carries a `Reason`: `working`, `composer-holds-text`, `menu-open`, `exited` or
  `screen-unreadable`.

Gateway (`Messaging/FleetDoorbell.cs`):
- **The schedule.** `FleetRingSchedule` is two pure rules: IsDue and IsStuck.
  - A message is due when it has never been rung, or when its last ring is at least the grace old. It also
    needs fewer than 3 rings.
  - A message is stuck when it has had 3 rings and the grace after the third has passed.
  - With the product numbers, a message is rung at 0, 5 and 10 minutes and marked stuck at 15.
- **The ringer.** `FleetDoorbell` does the work:
  - It rings on the settled edge (`OnSettled`, called from the turn-end watcher's callback in
    `GatewayHost`).
  - It rings on a 15-second heartbeat (`SweepAsync`, one pass per tenant through the tenant pass). Each
    heartbeat first marks stuck messages, then rings every session that has a message due.
  - It asks the owning Director through `DirectorCommandRouter`. The ring is counted only when the answer
    is `rung`, and then only on the messages that were due.
  - Stuck marks the rows and queues one system notice to the sender. The notice is written by the Gateway
    as sender, with the System exemption.
- **The store** (`FleetMessageStore`) gained four operations:
  - `RecipientsWithOpenMessages` and `OpenMessagesFor` read the open messages.
  - `MarkRung` and `MarkStuck` write, under the store lock, and only touch rows that are still unread.
  - A read now clears `StuckAtUtc`.
- **Test seams** in `GatewayHost`:
  - `FleetDoorbellHeartbeatEnabled` is switched off in the `Gateway.Tests` module initializer, like the
    turn-end sweep.
  - `FleetDoorbellLimitsOverride` lets a proof shorten the grace.
  - `GatewayDatabaseForTests` exposes the database to the proof.
- **Every decision is logged**: rung, deferred with its reason, unreachable, not connected, exited, and
  stuck. A skip because the session is working is not logged; it would repeat on every heartbeat.

Director:
- **The safety check.** `Core/Drivers/DoorbellSafety.cs` is a pure check over two screen frames. It holds
  the composer reader for Claude Code and for Codex. It also defines `FleetDoorbellLine`, the one fixed
  line: `[DevThrottle doorbell] N fleet message(s) ... To read, run: cc-devthrottle message inbox`. The
  code comment states exactly how "composer empty" is decided from the rows, and what is not covered.
- **The ringer.** `Core/Sessions/FleetDoorbellRinger.cs`:
  - It gathers the facts: exited, the Director's own working state, whether the session has a rendered
    terminal, whether the product left retained text, and two frames 120 milliseconds apart.
  - When the check allows it, it types the line with `SendSource.Agent` and a fleet-message provenance.
  - Only one ring runs at a time per session.
- **The verb.** `ControlApi/FleetDoorbellExecutor.cs` is the `ring` verb, registered as its own command
  area.
- **Supporting changes:**
  - `ComposerRetention.MayHoldText` reads the retained-text mark without clearing it.
  - `Session.ProductMayHaveLeftComposerText` exposes that to the ringer.

Real captured screens (`src/CcDirector.Core.Tests/TestData/doorbell/`, with a README):
- Thirteen screens from Claude Code 2.1.274 and Codex 0.154.0, captured through the Director's own
  terminal renderer.
- They cover: idle, owner text, a two-line draft, a collapsed paste, a typed slash command, mid-turn, the
  model picker, the trust dialogs, and the Codex placeholder and rate-limit menu.

### Judgement calls for the inspector

1. **"Stuck after 3 unanswered rings"** is read as three rings plus the grace after the third, so there
   is never a fourth ring.
2. **Only a `rung` answer counts.**
   - A deferral, an unreachable Director, or an exited or working session moves nothing towards stuck.
   - Consequence: a session that always defers is never marked stuck. That covers owner text left in the
     composer for hours, and an agent without a composer reader. Those messages simply wait.
3. **The Gateway does not ask when its roster says Working, Starting or Exited.** This saves a tunnel
   call every 15 seconds during long turns. The settled edge asks again. The Director still decides
   independently.
4. **A ring covers all unread messages, but is counted only on the ones that were due**, so each message
   keeps its own stuck clock. The line's count includes stuck messages, because they are still in the
   inbox.
5. **A read clears stuck, and no second notice tells the sender it was read after all.** The notice stays
   in the sender's inbox.
6. **A stuck notice is a message like any other**, so its recipient is rung for it. A stuck notice that
   is itself a system notice tells nobody.
7. **The Director checks its own activity state as well as the screen.** Working or Starting in either
   one defers the ring.
8. **Retained text the product left behind defers the ring.** When a `ComposerRetention` mark stands,
   the next send would press Escape first, and a doorbell must never be that send.
9. **Menus are recognised by their hint rows or a selected numbered option**, in the bottom 8 rows. For
   Claude Code this check runs only when no framed composer is found. For Codex it always runs first.
10. **A second ring while one is being typed is deferred as `working`**, not queued.
11. **The submit check's short-turn blind spot is handled for the doorbell alone** (found by the proof,
    see below).
    - `SubmitVerifier` calls a submit proven only after 2,048 bytes of output. When it throws on the
      doorbell line and the composer then reads empty, the ring is answered `rung`.
    - The shared verifier itself was not changed. It is used by every send, and changing it is outside
      this slice.
12. **A ring that fails part-way is reported as unreachable** (Director status Conflict). It is not
    counted. If the line was left half-typed, later checks see text and defer until the owner clears it.

### Findings the Architect should decide on

- **Ruling 15 is only half true.** The doorbell is agent-origin on the Director, so it does not stamp an
  owner turn. But the Gateway's snooze law (17 July 2026) deletes an ARMED snooze on ANY Working edge,
  and a doorbell makes the session work. So **a doorbell ends an armed snooze on the Gateway.** Not
  changed here, because it is the owner's law. Issue 2853 asks the same question about fleet messages.
- **The submit check nudges.** `SubmitVerifier` presses Enter on "dead" output windows for up to about
  10 seconds after any submit.
  - If the owner starts typing in that window and the agent's reply is short, a nudge could submit the
    owner's half-typed words.
  - This is true of every product send, not only the doorbell.
  - Recommend a separate change: no nudges for agent-origin sends, or nudge only a composer whose text is
    the one just typed.
- **Issue 2845, collapsed paste.**
  - The capture shows that Escape does NOT clear a collapsed paste in Claude Code 2.1.274.
  - The product's own `SendTextAsync` typed straight onto the end of it (see the capture harness notes:
    `claude-collapsed-paste`, then the next send's text was welded to the paste).
  - The doorbell never types there, because the collapsed placeholder reads as text. Other product
    sends still do.
- **Dictation lock.** The ring does not consult the Gateway's dictation lock. A ring can land while the
  owner's dictation is in flight, before the dictated text arrives. It is not the owner's words that are
  lost, but the order of the two is.

### What is proven, with red-then-green evidence

Every guard below was broken on purpose and seen red, then restored and seen green. The breaks and their
results are in `slice-2-evidence/guards-watched-failing/`.

**Director safety check.** 22 breaks, all red in the final state. The rules covered:
- the working marker, the composer text, continuation rows and framing;
- menus, both frames, exited, retained text and the Director's own working state;
- a missing grid, a blank screen, one frame only;
- the Codex cursor position, the Codex hidden cursor, and unknown agents.

Round 1 left 3 breaks green and 4 that did not compile. Each was answered in round 2: a new test for a
continuation-row draft, the hidden Codex cursor and the blank-screen detail, plus compiling breaks.

**Gateway schedule and ringer.** 18 breaks, all red in the final state. The rules covered:
- the grace, its boundary (inclusive), and the ring cap on the settled edge;
- stuck before the grace, and stuck one ring early;
- counting every open message, counting a deferral, counting an unreachable Director;
- the sender not told, working or exited sessions asked anyway, the count taken from due messages only;
- the settled edge doing nothing, a system notice's notice, and the heartbeat not marking stuck.

Round 1 left two breaks green, and both exposed a real weakness:
- The ring cap was only enforced because the heartbeat marks stuck first. A test now drives the settled
  edge alone.
- The stuck threshold was stated twice, once in the store's query. The query no longer restates it.

**Store guards.** 5 breaks, all red after 3 new store tests were added:
- a read undoes stuck;
- stuck never marks a message already read;
- a ring is not recorded on a message already read;
- a stuck message is not offered for ringing, and its recipient is not listed.

**Host wiring.** 3 breaks, all red (`FleetDoorbellRouteTests`, a real Gateway host with a tunnel
Director):
- the settled edge not wired to the doorbell;
- the Director's answer ignored;
- the count not sent.

**End to end on this Mac, real Claude Code sessions.** All three runs passed, and the evidence is saved.
- **Run 1.** A message sent mid-turn is not typed until the turn ends. The doorbell comes about 10
  seconds after the turn ends (the detector's silence timer). Exactly one doorbell is typed and one ring
  counted. The agent's own record shows `message inbox` returned all three lines, and the record is read.
- **Run 2.** Owner text in the composer defers three heartbeats in a row. The draft is untouched. After
  it is cleared, the doorbell rings and the message is read.
- **Run 3.** A worker that never reads is rung three times, about 72 seconds apart (a one-minute grace
  plus 15-second heartbeat granularity). The message is then marked stuck, the sender receives the
  Gateway's notice, is rung for it, and reads it.

**The double-doorbell defect.** Red before the fix: the first run 3 showed four doorbell lines for three
counted rings. Green after: exactly three.

### What is NOT proven

- **The graphical Director** (the screen was locked) and **the owner's installed Director**. The proof
  used the Director host code without its window.
- **The hosted Gateway and PostgreSQL.**
  - The new store queries ran on SQLite only. There is no schema change: the columns came with slice 1.
  - The heartbeat's per-tenant pass on a hosted Gateway was not exercised. Only the single-tenant self-host
    pass ran.
- **The five-minute grace live.** The live proof used one minute; five minutes is proven on a fake clock.
- **Codex live.** Codex hit its usage limit during capture, so:
  - no Codex session was rung end to end;
  - the Codex "working" rule is tested on a written row only.
- **Agents other than Claude Code and Codex.** They are never rung (`screen-unreadable`). Their messages
  wait until read and never go stuck.
- **The race with the owner's keystrokes.**
  - A keystroke that lands between the second frame and the first doorbell keystroke is not seen.
  - The submit check's nudges (see findings) are not addressed.
- **A Claude Code placeholder suggestion.** It would read as text and defer the ring. No such screen was
  captured.
- **An interactive menu, live.** The menu case is proven on captured screens (the model picker, and both
  trust dialogs) and not in a live run.
- **`scripts/test-local.ps1`** was not run (no PowerShell on the Mac); the suites were run directly.

### Test totals (Mac, 17 September 2026)

- **Gateway unit tests**, full run before the rebase: 5072 total, 5057 passed, 8 skipped.
  - **7 failed, the same 7 Mac-only failures named in slice 1**: RuleCandidateFilter 1, RulePrimitives 1,
    WorkListStorePersistence 1, SessionCommandExecutorLiveness 3, CronJobStore 1.
  - No new failure.
- **Gateway route tests**, full run before the rebase: 2537 total, 2470 passed, 51 skipped.
  - **16 failed, exactly the 16 named in slice 1**: ContextLessRouteCensus 1, FleetSpawnMissionAttach 2,
    FleetSpawnOrigin 4, TunnelRosterPushReadProof 3, WorkflowSeat 2, GatewayTestSuiteLock 2,
    HostedProcessControlDeny 2.
  - The 51 skipped include the three live proofs, which are skipped unless their variables are set.
- **Core tests**, full run: 4421 total, 4351 passed, 8 skipped, 62 failed.
  - 61 of the 62 are the known Mac-only set.
  - The 62nd was `CircularTerminalBufferTests.ConcurrentReads_DuringDispose_NeverThrow`. It passed when
    rerun alone; it is a timing test, and nothing in this slice touches the terminal buffer.
  - Three of the known set (`SessionEdgeCaseTests.CreateSession_EmbeddedType_ThrowsInvalidOperation`,
    `HomeStatusSessionsRowTests.DifferentInstall...`, `TurnDetectionShadowRetentionTests.An_append_past_the_contention_budget...`)
    were rerun on a clean origin/main worktree and fail there identically.
- **Core unit tests**: 524 passed on the first full run (before the new tests), and the doorbell safety
  tests are now 31, all passing.
- **cc-devthrottle tests** (scratch environment): 3101 passed. No Python changed in this slice.
- **After the rebase onto `dc6d6547`**, the touched classes:
  - Core unit `DoorbellSafetyTests`: 31 passed.
  - Gateway unit `FleetDoorbell*` and `FleetMessage*`: 93 passed.
  - Gateway route `FleetDoorbell*`, `FleetMessageRouteTests` and `DoorbellEndToEndProof`: 33 passed, 3
    skipped (the live proofs).
  - The full suites were not rerun after the rebase. Main's one new commit (#2997, Fleet Manager records)
    touches no doorbell file.
- **The live proofs** ran before the rebase, all three passing (evidence folder).

## Slice 1 merge with main (17 September 2026)

Merge commit `dfdaf8b5` brings `origin/main` into `mission/message-load` (3 commits: `56fcdde9` AXI
step 6b help and errors, `75efc250` cc-ship, `cd299403` Cockpit Missions board). Pull request 2970 now
reports MERGEABLE. Nothing is merged to main.

### What conflicted

`tools/cc-devthrottle/src/cli.py` (2 hunks) and `tools/cc-devthrottle/src/session_ops.py` (18 hunks).
Main's `56fcdde9` rewrote the message and session commands around "delivered" and `message ask`, and
moved every error to standard error with a `help[N]:` block of next steps. This branch had made those
commands queue, removed `message ask`, and added `message inbox`.

### How it was resolved

Slice 1's behaviour and wording were kept (rulings 2, 10, 17). Main's conventions were applied to them:
- `message send`: `Queued` or `Not queued:`. The Gateway's sentence goes out verbatim through
  `axi_cli.fail` (plain text on standard error, no highlighting). The next steps are `message inbox` and
  `session list`, and never `message ask`. Main's usage errors (`--everyone`/`--reason`/`--grant`
  misuse, a blank message) are kept. The summary is shortened to one line under 80 characters.
- `message send all`: the outcome counting and `BROADCAST_EXIT_RULE` are unchanged. Rows are now named
  by full id, not a short one. An answer with no `results` list is now treated as unconfirmed and exits
  1, following main's rule that an absent answer is not an empty one.
- `message inbox`: ends with its help block. An answer without an `unread` list exits 1 and points at
  `--all`. The messages were already marked read, so it must not print "0 unread".
- `session report`: queued, with the full parent id. The next steps are `message inbox` and
  `session done`.
- `session prompt` and `session interrupt`: main's error form, with `message send <id>` as the first
  next step. They no longer suggest each other, because both are refused to agents.
- `compact-continue`: main's error form. Its refusal test now reads standard error.
- `session spawn`: main's usage error is kept, reworded so it no longer offers `--controlled-by` for
  another session. The old printed block is dropped.
- `selftest`: main's Windows-only check and its flag counting, with this branch's single throwaway
  and its "message send queues" check.
- Main's tests were rewritten to the queue. `test_axi_step_6b_recheck6.py` (all `message ask`) and the
  `message ask` part of `recheck5` are deleted. In `test_help_and_errors_axi.py` the mutation, error
  and unconfirmed-answer tables, the selftest tests and the broadcast tests now assert queued
  semantics, and `message inbox` is added to all three tables.
- `docs/cli-reference.md`: the Message Send section is rewritten and Message Ask is replaced by
  Message Inbox. Still NOT done (slice 5 words): the `--controlled-by` option line in Session Spawn
  still says "a session id".

### Evidence

- cc-devthrottle tests (Mac, scratch environment): **3101 passed, 0 failed**. The first run after the
  merge had 79 failures, all in main's `message ask` and "delivered" tests.
- `test_axi_step_6c_help_and_errors.py::test_actions_json_is_unchanged` passed without a repin: the
  action catalogue did not move.
- The new inbox guard failed when made permissive (the `unread: null` case went red) and passed again
  when restored.
- Pull request 2970 checks on `dfdaf8b5`, all **pass**: Build & Test (.NET) (1h25m), Build & Test
  (web), Inventory drift check, and Tool contracts (Python) on macOS, Ubuntu and Windows, floor and
  latest (6 jobs).

### Not proven

Gateway suites were not rerun locally. The merge changed no Gateway file, and the continuous
integration .NET job passed.

## Architect rulings on the slice 2 findings (17 September 2026)

1. **A doorbell must not end a snooze** (ruling 15 stands; the Manager found the Gateway's 17 July
   law deletes an armed snooze on any Working edge). Fix, as slice 2b after inspection: the Director's
   activity push says whether the Working edge was owner-driven or agent-driven (it already knows, see
   `IsOwnerDriven` in `Session.cs`), and the Gateway's snooze registry ends an armed snooze only on an
   owner-driven edge. Guard: a doorbell on a snoozed session leaves the snooze armed; the owner typing
   ends it. Same for any agent-origin send.
2. **The ring respects the dictation lock** (slice 2b): while the owner's dictation for that session is
   in flight, the ring is deferred with reason `dictation`. Guard with a fake lock.
3. **The submit verifier's nudges** can submit the owner's half-typed words after ANY product send.
   Pre-existing, every send, not this mission's. Filed as an issue on main by the Architect.
4. **Collapsed paste (issue 2845).** The capture evidence (Escape does not clear it; a product send
   welds onto it) is posted on the issue by the Architect. The doorbell is safe by construction; other
   sends are not. Not this mission's.
5. **Agents other than Claude Code and Codex are never rung.** Accepted for now: their messages wait
   until read and never go stuck. The row line (slice 4) shows "N messages waiting" so the owner sees
   it. A composer reader per agent is later work.
6. Judgement calls 1 to 12 stand, subject to inspection 4.

Inspection 4 (Codex, adversarial) covers slice 2. Then slice 2b, then the slice 2 pull request.

## Architect rulings on inspection 4 (17 September 2026) - the slice 2 fix round

Verdict FAIL for merge. Every finding accepted. Rulings, in the order they are fixed; the two slice 2b
items are folded in here because the inspector is right that they are merge prerequisites.

1. **The window before the send is closed as far as a terminal allows** (high 1). The ringer samples a
   third frame and the Director's activity state immediately before the first byte, and aborts with
   `working` if anything changed. What remains is the interval between that last sample and the first
   byte, milliseconds, plus a turn the agent starts on its own (a background task completing). The
   worst case there is one short fixed line queued behind the current tool call, never lost text. That
   residual is written into the code as a gap and into the brief's ruling 7 as its limit.
2. **No nudges for the doorbell, and `rung` only when the submit was verified** (high 1, medium 4).
   The doorbell send uses the echo-verified submit with the nudge ladder OFF. Verified: answer `rung`.
   Not verified: if the composer now reads EXACTLY the doorbell line, the Director clears it (the text
   is ours, so clearing is safe) and answers `deferred, not-submitted`; if it reads anything else, leave
   it and answer `deferred, composer-holds-text`. A deferral is never counted as a ring. Guards: a
   backend that swallows Enter; a backend that clears the line; owner text typed after the send.
3. **Whitespace is text** (high 2). Any character after the prompt glyph, including spaces and tabs,
   means the composer holds text. Fixture: both idle captures with spaces after the glyph, red on the
   current reader.
4. **Stuck and its notice are one write** (high 3). `MarkStuckAndNotify` writes the stuck marks and the
   sender notices in one SaveChanges under the store lock. Guard: a throw between the two leaves neither
   persisted; a sweep after a crash re-marks and re-notifies exactly once.
5. **The store enforces the cap and the recipient** (medium 5). `MarkRung` never takes a count past the
   cap and refuses an id whose recipient is not the session being rung. Guards for both.
6. **The heartbeat is bounded** (medium 6). One query fetches every open message with its recipient;
   the stuck scan reads only rows at the cap; ring calls run with a bounded degree of 8 and a per-call
   timeout of 5 seconds, and the tick logs its duration. Guard: a tick over 40 recipients issues at most
   three queries; a delayed ring does not delay the tick beyond the timeout.
7. **Wire strings pinned** (low 7). Every deferral reason and the verb name are asserted as literals
   on both sides, and the Gateway refuses an unknown reason with a log line.
8. **A doorbell does not end a snooze** (slice 2b, ruling 15). The Director's activity push carries
   whether the Working edge was owner-driven; the Gateway's snooze registry ends an armed snooze only on
   an owner-driven edge. Guard: a doorbell on a snoozed session leaves it armed; owner typing ends it.
9. **The ring respects the dictation lock** (slice 2b). Dictation in flight for the session defers the
   ring with reason `dictation`. Guard with a fake lock.

Then: the touched suites in full, each guard watched failing, this note updated, commit, push,
`session raise`, stop. Inspection 5 follows.

## Slice 2 fix round (17 September 2026, Manager seat 3)

Eleven commits on `mission/message-load` after `56206340`, one per ruling plus one follow-up and one
for the live proof. Every guard was watched failing: the fix reverted or the code mutated, the named tests
red with the symptom, the file restored. The breaks and their results are in
`slice-2-evidence/fix-round/guards-watched-failing/`; the live runs are in `slice-2-evidence/fix-round/`
with their own README. No fleet message was sent.

### What changed, per item

1. **A last look before the first byte** (`a27bdbe2`). After the check approves two frames, the ringer
   re-reads the Director's state, its exited state and a third frame immediately before typing, and defers
   (`working`, or `exited`) if anything moved - rows, cursor column, cursor row or visibility. The ringer
   now drives an `IDoorbellTarget` (the product's is `SessionDoorbellTarget`) so every step is testable on
   the captured screens (`FleetDoorbellRingerTests`, Core unit). The remaining interval is written into the
   ringer's comment, into `DoorbellSafety`'s "not covered" list, and into ruling 7 of the brief as its
   limit. Red: the whole last look removed (6 tests); each of the Director state, the third frame, the
   cursor and the exit check removed (1 to 3 tests each).
2. **One Enter, no nudges, `rung` only when verified** (`de619615`). The doorbell no longer goes through
   the shared submit. `TerminalSubmit.DoorbellSubmitAsync` types the line, waits for its echo (bytes, or
   the rendered composer holding exactly the line), presses Enter ONCE and watches the screen for ten
   seconds: no nudge ladder, no Escape, no retype, no retained-composer step. "Verified" means the screen
   shows the line out of the composer AND either the working marker or one more doorbell row in the
   transcript than before the send (`DoorbellSafety.ShowsDoorbellSubmitted`). Not verified: a composer
   holding exactly the line (wrapping ignored) is erased one Backspace at a time and re-read until empty
   -> `deferred, not-submitted`; an empty composer -> `deferred, not-submitted`, nothing erased; anything
   else -> `deferred, composer-holds-text`, untouched. New reason `not-submitted`. The byte-count fallback
   from slice 2 (judgement call 11) is gone. Guards: a backend that swallows Enter (exactly one Enter, no
   Escape); a line the interface cleared (not verified, not counted); owner text typed after the Enter
   (never submitted by the doorbell); a line that never echoes (no Enter, no Escape); plus ringer tests for
   each composer outcome and for an older doorbell already on screen. Red: nudges restored (3 tests);
   Escape on an echo miss; Enter without an echo; verified on Enter alone (3); last look ignored; the old
   "empty means rung" rule (1 and 6 tests); erase skipped (4); anything erased (1); erase trusted without a
   re-read (1); a parked line counted as submitted (5); the transcript witness ignored (3) or counting rows
   already there (1, after a new test - it was green first); the working witness ignored (1); wrapping not
   ignored (1).
3. **Whitespace is text** (`7b498adc`). Rows reach the reader trailing-trimmed, so a draft of spaces leaves
   nothing on the row; the cursor is the witness. Both readers now strip exactly one separator after the
   glyph (Claude Code draws a non-breaking space, Codex a space) and treat any further character as text;
   an empty Claude composer additionally needs the VISIBLE cursor on the prompt row at column 2
   (`EmptyComposerCursorColumn`), a hidden cursor over a bare prompt is unreadable, and a blank
   continuation row holding the cursor is text; an empty Codex row needs the cursor at column 2. Two
   derived fixtures (`claude-idle-whitespace-draft-after-turn` and `-fresh`: the idle captures with the
   cursor at column 5, which is what three spaces produce) are documented as derived in the fixture
   README. **The live run confirmed the derivation:** three real spaces gave row `❯` and cursor (27, 5)
   (`fix-round/run2/02b-whitespace-draft.json`). Red: the whole pre-item reader against the new tests (11
   tests, both fixtures among them); each rule removed (1 to 5 tests each), including one that was green
   until a test for an untrimmed whitespace continuation row was added.
4. **Stuck and its notice are one write** (`079de73d`). `FleetMessageStore.MarkStuckWithNotices` stages the
   marks and the system notices (judged by the same policy, including the unread-duplicate rule) in one
   context and saves once under the store lock; `MarkStuckAndNotify` uses it. Guards: a throw between
   staging and the save (`BeforeStuckSave` seam) persists neither; a notice that cannot be built (the
   roster read throws) leaves the message open; the sweep after that crash marks and notifies exactly once;
   the mark and the notice carry the same moment. Red: the slice 2 method restored (2 tests - the crash and
   the retry); marks saved first (3); the save moved ahead of the failure point (1); notice never staged
   (5).
5. **The store enforces the cap and the recipient** (`4fa4d463`). `MarkRung(tenant, rungSession, ids, now,
   cap)` leaves alone - and logs - a row for another session or one already at the cap. Red: recipient not
   checked; cap not checked (2); cap off by one (2); recipient spelling not normalised (1); the doorbell
   recording against the wrong session (16). **Green by design:** the doorbell passing no cap - the
   schedule never offers a row at the cap, so the store's cap is a second wall only a direct caller can
   reach, and the store tests cover it.
6. **The heartbeat is bounded** (`8f8843a7`). One tick: the stuck scan loads only rows at the ring cap; one
   read (`UnreadForScheduling`) fetches every unread row with its recipient and WITHOUT its text; rings run
   8 at a time (`RingParallelism`), each abandoned after 5 seconds (`RingTimeout`, also a cancellation);
   the rings are recorded by `MarkRungMany` in one read and ONE update statement (SQLite otherwise issued
   one update per row - measured 43 commands for 40 rings before the change); a rung session stays held
   until its ring is recorded; the tick logs its duration and its attempt counts. Measured with a command
   counter on the framework's own diagnostic events: 40 recipients, all deferred -> 2 commands; all rung
   -> 4 commands (3 queries and 1 update). Red: the slice 2 heartbeat restored (4 tests); one ring at a
   time (1); no effective timeout (1, the tick took 30 seconds); session released before the record (1);
   stuck scan ignoring its floor (1); text in the scheduling read (1); one update per row (1). **Green by
   design:** the doorbell not passing the cap as the stuck-scan floor - the schedule's own rule filters the
   same rows; only the rows loaded change.
7. **Wire strings pinned** (`c48e865c`). `FleetRingDeferReasons.All` and `IsKnown`; the Gateway refuses an
   answer whose outcome is not `rung`/`deferred` or whose reason is not in the list (new attempt
   `InvalidAnswer`, logged, never counted). Literals asserted on the Director's side
   (`FleetDoorbellExecutorTests`: the body read as text, the command built with the literal `ring`) and on
   the Gateway's (`FleetDoorbellTests`: answers parsed from literal JSON; the route test asserts the verb
   the Director receives is `ring`). Red: a reason renamed on both sides at once - the inspector's case (3
   tests); the verb renamed (4); an outcome renamed (9); a reason missing from the list (2); a
   case-insensitive check (1); unknown reason or outcome accepted (3 and 1).
8. **A doorbell does not end a snooze** (`9b2d9dd0`, follow-up `b3d2f8ff`). The Director reports
   `SessionDto.WorkingOrigin` (`owner`, `agent`, or null) - set at the submission choke point with the
   same test as `IsOwnerDriven`, cleared when the session settles or exits, carried by
   `ControlEndpoints.Map`. The Gateway's working edge in `SnoozeLandingObserver` keeps an ARMED snooze
   when the origin is exactly `agent`, and logs it. The owner-driven half is untouched: an owner turn
   (`ClearIfSupersededByOwnerTurn`) still runs first, and owner-started work still deletes the snooze.
   **Follow-up found by writing the live run:** the origin was stamped only after a submit was verified,
   so a Working push during verification carried no origin and would have ended the snooze. The origin is
   now set before a send (put back if it fails) and, for the doorbell, immediately before its Enter. Red:
   the edge exemption removed (4 tests); any origin sparing the snooze (4); unexplained work sparing it
   (6); a loose comparison (2); the owner-turn edge skipped for agent work (1); every submission reported
   as the owner's (4); voice-as-framework reported as agent (3); origin never cleared (5); the push
   dropping the field (1); origin set after the submit (1); a failed send keeping it (1); the doorbell hook
   never called, called after Enter, or called before the echo (1, 1, 2). Host-level test on the real
   Gateway (`SnoozeEndToEndTests`) and live run 4, both green.
9. **The ring respects the dictation lock** (`b7628d01`). `FleetDoorbell` takes `dictationInFlight`; the host
   wires it to a running transcription, the phone's Speak mark, or a PENDING dictation record. In flight
   -> the Gateway defers with reason `dictation` (`DeferredDictation`) and asks no Director. Guards with a
   fake lock (defers, rings once the lock ends, another session's dictation does not hold this one, 30
   minutes of lock moves nothing towards stuck) and one route test through the real host with the Speak
   mark. Red: lock not consulted (2); consulted for the wrong session (3); counted as a ring (3); the host
   not wiring the Speak mark (1); the literal renamed (3).

Also committed (`07953d0d`): the live proof reads real frames with the cursor (its composer check used a
fake cursor that item 3 now rejects), run 2 gained the whitespace step, and run 4 (snooze) is new.

### DECISION FOR THE ARCHITECT - item 8 against the owner's written law

`docs/new_architecture/sessions.html` records the owner's rules of 14 July 2026, restored 17 July: "Working
knocks a session out of snooze. Always."; "It does not matter WHO woke the terminal - any activity ends the
snooze."; "Do not weaken rule 1". The 17 July text names another agent's fleet message as exactly the case
that must end a snooze. Ruling 8 reverses that for agent-origin work. It was implemented as ruled, because
the Architect ruled it knowing the conflict, and **in its narrowest reading**: only work the Director
attributes to an agent or product SEND keeps the snooze. Work no submission explains (the agent starting
on its own, a repaint, an older Director) still ends it, as the law says. The ruling's literal words ("ends
an armed snooze only on an owner-driven edge") would also spare that case; switching is one condition in
`SnoozeLandingObserver.Observe` and the test `Work_no_submission_explains_still_ends_an_armed_snooze`.
`sessions.html` was NOT edited: it is the owner's law and the words are slice 5. **Recommendation:** put the
exception to the owner in one sentence before this merges, and update the law in `sessions.html` with his
answer.

### Judgement calls for inspection 5

1. An empty composer after an unverified submit is `not-submitted` rather than `composer-holds-text` (the
   ruling names only "exactly the line" and "anything else"); neither is counted.
2. The verification witness is the screen alone. Byte growth is no longer used for the doorbell.
3. An erase is one Backspace per character, 5 milliseconds apart, re-read up to 10 times.
4. An exit seen at the last look is reported as `exited`, not `working`.
5. The dictation lock includes the PENDING record. **Corrected by inspection 5, ruling 3:** the record does
   expire. The Gateway's stale-upload sweep runs every six hours and abandons a PENDING record after 24 hours
   without activity, so on a working host a stale lock holds the doorbell for up to about 30 hours, not
   forever; only a failed or stopped sweep leaves it longer. The messages wait meanwhile and nothing goes
   stuck. Since fix round 2, a hold longer than 30 minutes is logged once as a warning.
6. A ring answered after the 5-second timeout is not counted; if the line was typed, the next ring may type
   a second one (ruling 8 calls that harmless).
7. A detector flicker to a settled state in the middle of a doorbell turn clears the origin; the next
   working push is then unexplained and ends the snooze - the error falls on the owner's side of the law.

### Live runs (Mac mini, Claude Code 2.1.274, one-minute grace)

All four pass; details in `slice-2-evidence/fix-round/README.md`.
- **Run 1** (1 minute 34 seconds): mid-turn message, nothing typed during the turn, one doorbell verified from
  the screen, one ring, all three lines in the agent's record, read.
- **Run 2** (2 minutes 33 seconds): owner draft held the ring for 50 seconds, untouched; three live spaces
  held it for 35 more (two `composer-holds-text` deferrals, the frame saved); cleared, rung, read.
- **Run 3** (4 minutes 58 seconds): three rings at 08:38:20, 08:39:32 and 08:40:32 UTC, each answered with
  one word and each verified from the screen, three lines on screen, stuck at 08:41:47, notice read by the
  sender.
- **Run 4, snooze** (1 minute 43 seconds, second attempt): the armed snooze was kept through the doorbell
  turn ("kept (ruling 15)" logged 5 times), still armed after it settled, and deleted by the owner's first
  working push. The first attempt (kept as `run4-snooze-attempt1`) showed the same snooze behaviour, then
  failed in the proof's own owner prompt. That prompt asked for one word, so the SHARED submit check
  pressed Enter six more times and threw. This is the pre-existing nudge defect (Architect finding 3), seen
  live on an owner send. The prompt now asks for a long answer.
- 15 doorbell submits in all, every one verified; no `not-submitted`, refused or timed-out ring.

### What is NOT proven

- **The erase path and `not-submitted` live.** No live ring went unverified, so erasing a parked line from a
  real Claude Code composer is proven only on scripted screens. Whether a burst of Backspaces behaves the
  same in every agent is not known.
- **Codex live**, as in slice 2. The Codex whitespace and cursor rules are proven on written rows.
- **The remaining race** (ruling 1's limit) is disclosed, not closed.
- **PostgreSQL**: `MarkStuckWithNotices`, `MarkRungMany` (its `ExecuteUpdate`) and `UnreadForScheduling`
  ran on SQLite only. There is no schema change. The command counts are SQLite's.
- **The hosted Gateway's per-tenant pass** with more than one tenant.
- **The host wiring of the PENDING-record and running-transcription arms** of the dictation lock. The route
  test drives the Speak mark only.
- **The tick's duration log line** is written but no test reads it.
- **The desktop window and the owner's installed Director**; `scripts/test-local.ps1` (no PowerShell on the
  Mac). The suites were run directly.
- **The short-turn nudge defect in the shared submit check** is untouched and was seen live on an owner
  send (run 4, first attempt).

### Test totals (Mac, this tree, 17 September 2026)

- **Core unit tests**: 555 passed, 0 failed.
- **Core tests**: 4445 total, 4376 passed, 8 skipped, **61 failed**. That is the known Mac-only count, and
  every one is a Windows path, process, worktree reaper, lifecycle signal, tool path, link or mutation-pin
  test that this round does not touch. The list is in `fix-round/full-suite-failures.txt`; the slice 2
  timing flake did not recur. No new failure.
- **Gateway unit tests**: 5246 total, 5231 passed, 8 skipped, **7 failed - the same 7 named in slice 1**.
- **Gateway route tests** (full suite, 35 minutes): 2578 total, 2510 passed, 52 skipped, **16 failed -
  exactly the 16 named in slice 1**. The 52 skipped include the four live proofs, which run only with
  their variables set.
- **cc-devthrottle tests** (scratch environment): 3176 passed, 0 failed. No Python changed.
- **Live proofs**: 4 of 4 passed (run 4 on its second attempt; see above).


## Architect note after the slice 2 fix round (17 September 2026)

All nine rulings landed and proven. Judgement calls 1 to 7 stand, subject to inspection 5.

Two decisions are with the owner, asked on 17 September; neither blocks inspection 5:
1. **Snooze versus the doorbell.** The owner's written law of 14 and 17 July 2026 says any activity ends
   a snooze, naming another agent's message as the case. Ruling 15 and fix item 8 make an exception for
   work the Director attributes to an agent-origin send (a doorbell, or any product send). Recommendation
   to the owner: keep the exception in its narrow form as built; a snooze is the owner's wish to be left
   alone, and a doorbell from another agent should not make his phone go red. If he agrees, slice 5
   updates the law in `sessions.html` with the date. If not, one condition in `SnoozeLandingObserver`
   and one test flip back.
2. **Restore versus the spawn owner pin** (open since slice 1).

Inspection 5 (Codex) covers the fix round. Then the slice 2 pull request, after the owner's answer on 1.

## Slice 3 - replies without blocking (17 September 2026, Manager seat 4)

Built on `mission/message-load` on top of `e59622d1`, with no rebase and nothing outside slice 3 touched (the
slice 2 fix round is under inspection separately). Two product commits: the Gateway half, then the command
line half. No pull request opened, and no fleet message sent. The guard breaks are in `slice-3-evidence/`.

### What was built

Gateway:
- **Asking.** `POST /sessions/{sid}/message` takes `replyWanted` and `replyByMinutes`.
  - The record gets a correlation id (32 hexadecimal characters, separate from the message id) and a
    deadline: 60 minutes by default, 1 to 1440 allowed (`FleetMessageLimits.DefaultReplyWindow`,
    `MinReplyWindow`, `MaxReplyWindow`).
  - The answer carries `correlationId` and `replyByUtc`.
  - A deadline outside the range is refused 400 with the sentence. So is `replyByMinutes` without
    `replyWanted`.
- **Replying.** `POST /fleet/reply` with body `{ id, text }`. The id is the correlation id or the message id.
  The route is added to `SessionKeyGuard` in that one shape only.
  - The reply is written as kind `reply` into the inbox of whoever SENT the original, whatever their
    relationship. It carries the original's correlation id and message id.
  - The original's new `RepliedAtUtc` is stamped in the same save, under the store lock
    (`FleetMessageStore.TryReply`).
- **The reply rule** is in `FleetMessagePolicy` (`DecideReply`, with the reply context in
  `FleetReplyOriginal`):
  - Only the session the original was sent to may reply, and only to its sender. Anyone else, and any
    reply to a Gateway notice, is refused 403.
  - Held to the text rules and the duplicate rule, and to one reply per question (a second is refused
    409).
  - NOT held to the hourly limit or the spacing. The store also leaves replies out of the hourly count and
    out of the spacing, so a reply is not counted against the replier either.
  - The store's lookup refuses an unknown id (404) and a message that did not ask for a reply (409).
- **Reading.** Each inbox row now carries these fields, so the command line only lays them out
  (project rule 7):
  - `replyWanted`, `correlationId`, `replyByUtc`, and `replyHint` (the exact command that answers the
    message);
  - `notice` (`no-reply`);
  - `inReplyTo`: the question's id, correlation id, recipient, text, sent time, deadline, and `late`.
  - The question's text is returned only to the session that SENT the question.
- **No reply by the deadline.** `FleetMessageStore.MarkReplyOverdueWithNotices` marks the new
  `ReplyOverdueAtUtc` and stages one system notice to the sender, in ONE save under the store lock. It uses
  the same `StageSystemNotice` step that stuck notices now share.
  - The notice is kind `system` and carries the question's correlation id and message id.
  - It runs in the doorbell's heartbeat (`FleetDoorbell.SweepTenantAsync`), after the stuck scan and before
    the unread read, so the notice is rung on the same tick.
  - A marked question is never scanned again, so the notice is sent once. An answered question is never
    marked.
- **A late reply** still lands, is shown with `late: true`, and brings no second notice.
- **Data.** Two columns (`RepliedAtUtc`, `ReplyOverdueAtUtc`) and an index on (tenant, `ReplyByUtc`).
  Migration `AddFleetMessageReplyMarks` exists for SQLite (`20260917094429`) and PostgreSQL
  (`20260917094441`), generated with the migration tool. Both snapshots changed by exactly these lines.

Command line (`tools/cc-devthrottle`):
- **Asking.** `message send <id> "text" --reply-wanted [--reply-by <minutes>]` sends the ask and prints
  `correlation id: <id> (reply wanted by <time>)` with a line saying not to wait.
  - `--reply-by` without `--reply-wanted` is a usage error. So is `--reply-wanted` with `all`.
  - The range check is the Gateway's; its sentence is printed verbatim.
- **Replying.** `message reply <id> "text"` posts `fleet/reply` with the id trimmed.
  - It prints `Reply queued for <asker> (reply <id>, answering message <id>)`.
  - A refusal goes to standard error with the Gateway's sentence and next steps.
  - A duplicate is exit 0.
  - A "queued" answer with no reply id or recipient is "Not confirmed" and exit 1.
  - Next steps: `message inbox` and `session report`.
- **The inbox.** Rows are headed `message`, `reply` or `no-reply notice` from the Gateway's labels.
  - A question shows `reply wanted by`, `correlation id` and `to answer: <the Gateway's command>`, with its
    quotes kept.
  - A reply or notice shows `answers:`/`about:`, `asked of`, `deadline`, `late` (replies only) and the
    question in full, or `(no longer kept)`.
  - The inbox help gains `message reply <correlation-id> "<answer>"`.
- **Catalogue and fixture.** The action catalogue gains `message-reply`, and the `message-send` command and
  arguments name the two flags. The pinned actions fixture is regenerated (86 to 87 actions). The help and
  error tables gain rows for `message send --reply-wanted`, `message reply`, their usage errors, refusals
  and unconfirmed answers.

### Judgement calls for the inspector

1. **One reply per question.** A reply is exempt from the rate limits, so unlimited replies to one question
   would be an unmetered channel to the asker. The second reply is refused 409 and pointed at the report.
2. **A reply is not counted either**: it is left out of the hourly six AND out of the per-recipient spacing,
   so answering does not use up the replier's next ordinary message.
3. **`POST /fleet/reply` with the id in the body**, no session id in the path, like the inbox.
   - The original is looked up across the account, and the policy says why a session that was not asked is
     refused.
   - The refusal names only the id the caller gave. The asker's short id appears only when the caller WAS
     the recipient and named the wrong target, which the product itself never sends.
4. **A reply must name a message that asked for one** (409 otherwise). A reply cannot name another reply or
   a notice: those carry the question's correlation id, and the lookup skips any row that is about another
   message.
5. **`--reply-wanted` is one session only.** `message send all --reply-wanted` is a usage error, and the
   broadcast route has no reply field. A team-wide question would need one correlation id per copy; that
   was left out rather than guessed.
6. **The route accepts `replyWanted` with kind `report`.** `session report` has no flag for it.
7. **Two new columns and an index**, where the brief said slice 1 made every column. An overdue mark that
   is written once needs a stored mark, and "already replied" and the bounded scan need a replied mark
   rather than a join on every heartbeat.
8. **A dropped duplicate of a question answers with the waiting copy's ids.** Its correlation id is null
   if the waiting copy did not ask for a reply, and the command line then prints no correlation line.
9. **The no-reply label is the Gateway's.** A system row about a question is labelled `no-reply`. A stuck
   notice carries no label and is shown as a plain message from the Gateway, and the command line does not
   guess.
10. **The question's text is shown only to its sender.** A reply row always goes to the sender, so the
    filter only bites a shape the product does not write; a test writes that shape directly.
11. **A question that was read but not answered still gets the notice.** Reading is not replying.
12. **The heartbeat reads the database once more per tick** (the no-reply scan). The slice 2 bound test
    was changed from 2/4 commands to 3/5, with a comment saying why. Each notice written costs one history
    query, as each stuck notice already did.
13. **A reply to a session that has exited still lands.** The reply route does not look the asker up on
    the roster; the record is the delivery.

### What is proven, with red-then-green evidence

Each break below was made on purpose, the named tests went red, and the file was restored. See
`slice-3-evidence/README.md`.

- **Gateway: 26 breaks, all red** (`gateway-guards-watched-failing.json`).
  - Policy: the reply's recipient not checked; the replier not checked; a reply judged by the ordinary
    rules; a reply held to the hourly limit; a reply held to the spacing; one reply per question not
    enforced; a reply to a Gateway notice queued.
  - Store: replies counted in the hourly six; replies starting the spacing; the overdue mark saved before
    its notice (the crash test persisted the mark alone); an overdue question scanned again; an answered
    question scanned; the reply not stamping the question; a reply taken for a question; a late reply
    refused; the question's text shown to any reader; the question not joined to the read.
  - Doorbell: the heartbeat not running the no-reply scan.
  - Service: the notice not labelled; the notice without its link to the question; `late` never set; the
    default window 30 minutes; the upper bound 48 hours.
  - Guard: `fleet/reply` not allowed.
  - Route: `replyWanted` ignored; a deadline without the ask accepted.
  - Three breaks first failed to COMPILE, which is not a watched failure. They were rewritten to compile
    and were red on real test failures.
- **Command line: 20 breaks, all red** (`cli-guards-watched-failing.json`).
  - Sending: the ask not sent; the deadline dropped; both usage errors removed; the correlation id not
    printed.
  - Replying: the id not trimmed; an unconfirmed reply accepted; a refused reply treated as success.
  - The inbox: the reply heading, the no-reply label, the notice line, the question, `late` and the answer
    command each removed; the answer command escaped.
  - Help and wiring: the inbox help missing `reply`; the reply next steps changed; the command not wired;
    the catalogue entry reworded; the send flags not passed on.
- **Also seen red in development.** The first run of the answer-command test failed because the shared
  quoted-value escaping turned `"` into `\"`, which cannot be pasted. The line now uses the plain-ASCII
  helper, and the break above re-proves it.
- **Real host** (`FleetMessageRouteTests`, 10 new tests, recording tunnel Director):
  - the round trip, with the wire names pinned and no verb sent to the Director;
  - an unasked session refused;
  - a reply allowed after the relationship is gone, where a plain message is refused;
  - a reply inside the spacing that refuses a second message;
  - second reply 409, unknown id 404, no-reply-wanted 409;
  - three bad deadlines 400;
  - a blank id refused, and the owner's key refused;
  - the heartbeat's no-reply notice once, then a late reply shown late.
  - The deadline is moved into the past in the database, because the host runs on the real clock.

### What is NOT proven

- **PostgreSQL.** The migration was generated, not applied (Docker is not on this Mac). The new queries
  (the reply lookup, the overdue scan and the question join) ran on SQLite only.
- **Live.** Nothing ran against a live Director, a real agent, or the hosted Gateway.
  - The doorbell ringing an asker for a reply or a notice is proven on the unit rig (the notice is rung on
    the same tick) and not live.
  - The multi-tenant heartbeat pass was not exercised.
- **Words (slice 5).**
  - `docs/cli-reference.md` has no `message reply` section and no `--reply-wanted`/`--reply-by` lines.
  - The fleet preamble, the fleet-comms skill and `docs/FleetMessaging.md` do not mention replies.
  - Only code strings (help, catalogue, docstrings, comments) were written here.
- **A reply's own sentence to an asker who never reads.** A reply is rung and can go stuck like any
  message; the stuck notice then goes to the replier. That is existing behaviour, not tested again here.
- **`scripts/test-local.ps1`** was not run (no PowerShell on the Mac); the suites were run directly.
- **Core** was not rerun: nothing in Core changed in this slice.

### Test totals (Mac, this tree, 17 September 2026)

- **Gateway unit tests**: 5304 total, 5289 passed, 8 skipped, **7 failed - the same 7 Mac-only failures
  named in slice 1** (CronJobStore 1, SessionCommandExecutorLiveness 3, RuleCandidateFilter 1,
  WorkListStorePersistence 1, RulePrimitives 1). No new failure. The touched classes (`Messaging`,
  `SessionKeyGuard`) passed 412 of 412.
- **Gateway route tests** (full suite, 34 minutes): 2589 total, 2521 passed, 52 skipped, **16 failed - exactly
  the 16 named in slice 1** (ContextLessRouteCensus 1, FleetSpawnMissionAttach 2, FleetSpawnOrigin 4,
  TunnelRosterPushReadProof 3, WorkflowSeat 2, GatewayTestSuiteLock 2, HostedProcessControlDeny 2). No new
  failure. The new reply tests in `FleetMessageRouteTests` all passed.
- **cc-devthrottle tests** (scratch environment with the declared dependencies): 3208 passed, 0 failed.

### Open, unchanged by this slice

The two owner decisions after the slice 2 fix round (snooze versus the doorbell, and restore versus the
spawn owner pin) are still open. Inspection 5 of the slice 2 fix round is separate from this slice.

## Owner decisions, 17 September 2026 ("go with your recommendation")

1. **Snooze.** The exception stands: work the Director attributes to an agent-origin send (a doorbell,
   any product send) does not end an armed snooze; the owner's own work, and work nothing explains,
   still do. Slice 5 updates the 14 and 17 July law in `docs/new_architecture/sessions.html` with this
   date and the reason: a snooze is the owner's wish to be left alone, and another agent ringing must
   not undo it.
2. **Restore.** The spawn owner pin stays. The Director restart's restore step becomes a Director act:
   the Director performs the restore spawns itself through its own relayed arm, which the Gateway
   already trusts to name owners, instead of writing `--controlled-by <id>` commands for a session to
   run. This is a slice of its own, after slice 3 and before the words: "Slice 6 - restore is a
   Director act". The director-restart skill's restore step is reworded in the same slice.

## State after slice 3 (17 September 2026)

- Slice 2 head pinned at `e59622d1` on branch `mission/message-load-slice2`, awaiting inspection 5
  (Codex, from 07:47 when its limit resets) and then its pull request.
- Slice 3 head `80adce1e`, awaiting inspection 6.
- Next seat: slice 6, restore is a Director act (owner decision 2), because the spawn owner pin is
  live on main since slice 1 and a Director restart today would fail to restore controlled seats.
- Then slice 4 (the row line), slice 5 (the words, including the snooze law), the record, the release.

## Slice 6 - restore is a Director act (17 September 2026, Manager seat 5)

Owner decision 2. The spawn owner pin from slice 1 stays exactly as it is. The restore moved instead: the
Director starts every restored seat itself, through the spawn door the Gateway already trusts a Director
to name owners on. Commits `5a9edbf2`, `668cb2eb`, `a7aab9d8`, `de7a512a`, `726e4a0f` on
`mission/message-load`, on top of `ecf96447`. No pull request opened.

### Design as built

**Who triggers it, and where.** A new route, `POST /gateway/workspaces/{id}/restore`, body
`{ directorId, seats?, seeds? }` (`src/CcDirector.Gateway/Api/WorkspaceEndpoints.cs`). Not a Control API
verb: that listener no longer exists, and there was no restore endpoint to reuse - the only thing that
"restored" before was a session running the drain's spawn lines. The owner's device key or a session key
may call it (`SessionKeyGuard.IsWorkspaceRoute`, four-segment `restore`, POST only). The Gateway checks the
workspace exists, the Director exists in the caller's account, and the Director is on the machine the
workspace was captured on (409 otherwise). It stamps WHO ASKED from the verified credential (the session
id of a session key, nothing for the owner) - never from the body - and relays a `workspace-restore` tunnel
verb (`WorkspaceRestoreVerbs`, `src/CcDirector.Gateway.Contracts/WorkspaceRestoreDtos.cs`) to that
Director. The Director's answer is relayed: 202 with the seats it took, or its refusal with its reason
(400/404/409/504/502).

**On the Director.** `ControlApiHost.StartWorkspaceRestoreAsync` claims a one-at-a-time gate, runs
`DirectorRestore.PrepareAsync` (reads the workspace from the Gateway, refuses an authored workspace, a
named seat that is not decided "restore" or has already come back, or nothing left to bring back), and
only then answers "taken" and runs `DirectorRestore.RunAsync` in the background
(`src/CcDirector.ControlApi/Drain/DirectorRestore.cs`). It answers before the spawns finish on purpose:
every spawn rides back down this same stream as a `create`, so a command that waited for its own spawns
would wait on itself.

**The spawn.** For each seat, `GatewayClient.SpawnOnThisDirectorAsync` posts to
`POST /directors/{this Director}/sessions` on the Director's own credential. That is the existing spawn
door: the Gateway still resolves the mission and the workflow seat and still sends the ordinary `create`.
`SpawnOrigin` leaves a Director's key alone (it is neither a person's device nor a session key), so the
owner the Director names is kept. No second spawn path.

**Where the owner comes from.** Only from the seat's `ReportsTo`, which the Gateway observed at capture and
restores from its stored copy on every write (`WorkspaceStore`), so neither the caller nor anyone writing
the workspace can change it. That is why an authored workspace is refused.

**The placeholder, resolved in order.** Seats are ordered owners first (depth in the reporting chain among
the seats in this run, then the drain's sort order), fixed once as ids; each seat is looked up in the
freshly stored copy after every save. Per seat:
- no owner: the user's (`controllerSessionId` null);
- owner not a seat in this workspace (it lives elsewhere and survived): its id, verbatim;
- owner is a seat that has come back (this run or an earlier one): its restored id;
- owner is a seat that BLOCKED the drain and was never closed: its current id (the restore without a
  restart after a blocked drain; that owner is still running) - added in `a7aab9d8`;
- otherwise (the owner failed in this run, is decided close, or has not been restored): the seat FAILS
  with that reason and is not started - never unowned, never under a dead id.

**Who asked is recorded as the parent, never the owner.** A session's request gives each create
`origin = agent`, `parentSessionId = that session`; the owner's gives `origin = human`. The workspace's
`restoredBy` names the asking session (null for the owner) and the Director.

**A failure per seat.** Two new judgment fields on `WorkspaceSeatRestore`: `failure` and
`attemptedAtUtc` (capped like every other judgment; refused on an authored workspace). After every seat the
Director writes `restoredSessionId` (success, failure cleared) or `restore.failure`, stamps the attempt, and
saves. A refused spawn, a missing handover, a client-side timeout (recorded as "MAY have been started -
check before asking again", `726e4a0f`) are all per-seat; only the seats that depended on a failed owner
fail with it. A seat that already came back is never started again.

**The restoring session's command line.** `DrainRestoreCommand.Build(workspaceId, sessionId)` now writes
`cc-devthrottle director restore "<workspace>" --director "<the NEW director id>" --seat <id>` - no
`--controlled-by` for anyone, not even the caller (the caller owns nothing it restores). The new command
(`tools/cc-devthrottle/src/machine_ops.py restore_workspace`, `cli.py director restore`) takes
`--director`, `--seat` (repeatable), `--seed <id>=<path>` (repeatable, the skill's seed files), and
`--wait-seconds` (default 600, 0 = ask only). It refuses the literal placeholder. It reads the workspace
back until each seat asked for has an answer: RESTORED, FAILED with the Director's reason, or PENDING when
the wait runs out; an earlier run's failure is not taken for this run's answer (the attempt stamp must
change). Exit 0 only when every seat came back. Action catalogue entry `director-restore`; command
reference section added under `director list`.

**The skill.** `director-restart` pulled, edited, pushed as a DRAFT, not published. Draft v3 existed
already, unpublished, from 7 September (session `536bbef4`, it adds the `machine restart-capability`
step 0 and its refusal table); I pulled that draft and built on it, so **publishing v3 publishes those
7 September changes too**. My edits: step 4 says the restore reads the Gateway workspace and shows how a hand
drain captures one before closing anything; step 8 replaces the `session spawn --controlled-by` block with
"the Director does the spawning, you ask it", including decisions written onto the workspace first,
`--seat` for head-first restores, `--seed` for seed files; the blocked-drain recovery names the command;
the "write restoredSessionId only on a pass" rule becomes "the Director wrote it; a failed check goes in
the report"; the open question becomes "what asks the Director to restore after a restart". The full diff
against the pulled draft is `slice-6-evidence/director-restart-skill-draft.diff`. **The Architect
publishes it with the words slice.** `.claude/skills` has no `director-restart` copy, so there is nothing to
regenerate in the repository.

### Judgement calls for the inspector

1. **A session key may ask for a restore.** The charter said "the owner or the restoring session". It grants
   no owner-naming: owners come from the capture, and the caller is recorded as parent. What it does let a
   session do is bring back any captured, restore-decided, not-yet-restored seat in its own account, and it
   could first PUT a seat's decision to "restore" (decisions are judgments any writer may set - that was
   already true of the workspace routes). I judged that acceptable because the seats it can bring back had
   exactly those owners before the drain.
2. **The machine check.** A restore onto a Director on a different machine is refused (repository paths
   belong to a machine). This also refuses a deliberate move of a fleet to another computer; that is a
   different operation (the move-session skill).
3. **A worker whose owner is not coming back fails** rather than starting unowned or under the dead id. The
   old skill said "restore the head, not the tree", so this should be rare; when it happens the record says
   why and the owner or the senior decides.
4. **Blocked-and-never-closed means still running.** Read from `drainState == blocked` and no `closedAtUtc`.
   If a blocked seat was later closed by hand without the record being updated, the restored worker is named
   under a dead id. The drain writes `closedAtUtc` when it verifies a close; a hand close does not.
5. **Answered "taken" before the spawns.** Reasoned, not measured: the SignalR client handles one incoming
   invocation at a time on this connection, so a handler awaiting a create that comes back down the same
   connection would stall until the Gateway's 30-second command timeout. The restart cycle already answers
   "taken" for its own reason; this copies that shape.
6. **Parent = the asking session**, origin agent. An alternative was parent = the resolved owner (mirroring a
   manager spawning its worker). I kept "who made the call" because that is what `parentSessionId` means
   everywhere else (`SessionOrigin`).
7. **`seatOutcome` is not written by the Director.** It is a terminal answer with strict validation, and a
   partial (`--seat`) run cannot give one honestly. The per-seat fields are the report; the restoring session
   still writes the outcome, as the skill says.
8. **No `--args` through a restore.** The old spawn line carried none either; a restored seat gets the
   Director's default agent settings.

### What is proven - every guard watched failing, then restored

Every mutation below was applied, the named tests run and seen red, and the file restored; the record of
each run is `slice-6-evidence/red-runs.md`.
- **A restore of a controlled seat by a Director comes back with the right owner, on a real host.**
  `WorkspaceRestoreRouteTests.A_Director_restoring_controlled_seats_starts_each_under_its_real_owner`: a
  hosted Gateway, a tunnel Director on its own enrolled workstation key, a workspace captured by the real
  capture route, the real `DirectorRestore` over the real `GatewayClient`. The `create` the Director receives
  names the Manager's NEW id for the Worker, the other Director's session for the seat owned from elsewhere,
  and nobody for the Manager. Red when the placeholder is not resolved (mutation 1b).
- **The same request from a session key is refused.** `The_same_create_from_a_session_key_is_refused...`:
  the identical create, sent with the restoring session's key, answers 403 and no `create` reaches the
  Director. Red with the owner pin removed from `SpawnOrigin` (mutation 2).
- **A placeholder resolves to the new controller's id.** Unit tests: worker listed before its manager, a
  three-level chain, an owner restored in an earlier run. Red with the old id named (mutation 1), with no
  ordering (4), with already-restored seats re-selected (9).
- **A per-seat failure is reported and does not stop the rest.** Unit tests: one refused seat among three; an
  owner's failure failing its worker with the owner's reason; a timeout recorded as maybe-started; a failed
  seat retried and cleared. Red with the failure thrown (3), with the owner's failure unrecognised (5), with
  the timeout escaping (18).
- **The route stamps who asked and relays the Director's answer**: a session key's order names that session
  even when the body claims another; the owner's names nobody; the Director's refusal comes back as 409 with
  its words; another machine's Director is refused before anything is sent; no Director is a 400. Red with
  the stamp dropped (6), the guard entry removed (7), the machine check removed (10).
- **An authored workspace is refused; blocked owners handled; the drain's command names no owner; the new
  fields are capped and refused on authored workspaces**: mutations 8, 16, 17, 11, 12, 13.
- **Command line**: sends no owner; reports RESTORED / FAILED / PENDING; an old failure is not this run's
  answer; exit 1 on any failed or pending seat; refuses the placeholder and a malformed seed; no read when
  not waiting; in the action catalogue as state-changing. Red with the freshness check removed (14) and the
  exit code dropped (15).

### What is NOT proven

- **No live run.** No real Director restored anything: the tunnel Director in the route test is the fake
  that records commands, so the Director's own `create` handling of a restored seat (owner on another
  Director, role, mission) is not exercised, and nothing checks the restored session actually reads its seed.
- **`ControlApiHost.StartWorkspaceRestoreAsync` has no test.** The verb dispatch, the claim/release around
  `PrepareAsync`, the "taken" body and the background run are exercised by no suite; the pieces it calls are.
- **The stall that "taken first" avoids** (judgement call 5) was not reproduced.
- **Concurrency.** Two different Directors restoring the same workspace are not serialised; a session writing
  the workspace during a restore can overwrite a seat's result with its older copy (both stated in the class
  comment). The one-at-a-time gate is per Director process.
- **Nothing asks the Director to restore after a restart by itself.** A restart whose driver died still
  restores nothing until someone runs the command. The drain inside the Director is still not wired into the
  restart cycle on this tree (`NoDrainOnThisBuild`), unchanged by this slice.
- **The 10-second client timeout** on the Director's Gateway client is shorter than the Gateway's 30-second
  wait for a `create`; a slow create is recorded as "maybe started" rather than waited for.
- **PostgreSQL**: the two new fields live inside the workspace's JSON document (no schema change); the store
  tests ran on SQLite only.
- **The skill draft** was not read by any agent performing a real restart.
- `scripts/test-local.ps1` not run (no PowerShell on the Mac); suites run directly.

### Test totals (Mac, this tree, 17 September 2026)

- **Gateway unit tests** (which hold the ControlApi drain and restore tests; there is no separate ControlApi
  test project): 5331 total, 5316 passed, 8 skipped, **7 failed - the same 7 Mac-only failures named in
  slice 1** (CronJobStore 1, SessionCommandExecutorLiveness 3, RuleCandidateFilter 1, WorkListStorePersistence
  1, RulePrimitives 1). Run again after the last code commit (`726e4a0f`). No new failure. New:
  `DirectorRestoreTests` 20, three validation tests, the guard cases, the rewritten drain command test.
- **Gateway route tests** (full suite, 20 minutes, run at `a7aab9d8`): 2596 total, 2528 passed, 52 skipped,
  **16 failed - exactly the 16 named in slice 1** (ContextLessRouteCensus 1, FleetSpawnMissionAttach 2,
  FleetSpawnOrigin 4, TunnelRosterPushReadProof 3, WorkflowSeat 2, GatewayTestSuiteLock 2,
  HostedProcessControlDeny 2). The census failure is its existing `DELETE /exes/slots/{n}` difference and does
  not involve the new route. `WorkspaceRestoreRouteTests` (7) was run again after `726e4a0f`: 7 passed.
- **Core unit tests**: 555 passed, 0 failed. **Core tests**: 4445 total, 4376 passed, 8 skipped, **61 failed -
  the known Mac-only count**; nothing in Core changed in this slice.
- **cc-devthrottle tests** (scratch environment): 3225 passed, 0 failed (9 new in
  `test_director_restore.py`; the action pin moved from 87 to 88 entries, adding only `director-restore`).

## State after slice 6 (17 September 2026)

- Slice 6 head is this commit on `mission/message-load`, awaiting inspection. No pull request opened.
- **The `director-restart` skill draft (v3) is pushed and NOT published.** It also carries the unpublished
  7 September changes that were already in that draft. The Architect publishes it with the words slice.
- Unchanged: slice 2 awaiting inspection 5, slice 3 awaiting inspection 6. Next: slice 4 (the row line), then
  slice 5 (the words), the record, the release.

## Architect rulings on inspection 5 (17 September 2026) - slice 2 fix round 2, on branch mission/message-load-slice2

Verdict FAIL for merge. Findings accepted. The fixes are made on the pinned slice 2 branch
(`mission/message-load-slice2`, from `e59622d1`) in the worktree `~/ReposFred/devthrottle-inspect-slice2`,
so the slice 2 pull request stays one slice; the branch is merged back into `mission/message-load` after.

1. **Exactly means exactly** (high 1). The composer is compared with the doorbell line character for
   character, allowing only the row breaks a wrap inserts; any other difference, including a trailing
   space, tab or non-breaking space, means "anything else, leave it". Guards: the line extended by
   each of those three characters is never erased; the wrapped unextended line still is.
2. **No stuck mark without its notice** (high 2). The notice text carries the message id, so it can
   never be an exact duplicate for a different message; it is built to fit the text cap whatever the
   names; and if the policy still refuses it, the stuck mark is NOT written, the refusal is logged with
   the reason, and the next sweep retries. Guards: a preloaded identical unread notice for the SAME
   message leaves the mark written (the sender already holds that notice) and writes no second notice;
   a refusal for any other reason leaves the row open.
3. **Dictation lock observability** (medium 3). Judgement call 5 is corrected in the record: a stale
   PENDING record is abandoned by the existing sweep after 24 hours. A ring deferred by dictation for
   more than 30 minutes is logged once at warning level with the session id.
4. **Snooze** (medium 4): the owner decided on 17 September; the words slice updates the law. The
   attribution loss on a settled flicker is accepted (it errs on the owner's side of the law).
5. **Submit verification** (low 5): accepted as the stated limit of a screen witness.

Then the touched suites, each guard watched failing, a 'Slice 2 fix round 2' section in handoff.md
(committed on the slice 2 branch), push, stop. Inspection 7 follows on that branch.

## Architect rulings on inspection 6 (17 September 2026) - slice 3 fix round, on branch mission/message-load-slice3

Verdict FAIL for merge. Findings accepted. Fixes on the pinned slice 3 branch in the worktree
`~/ReposFred/devthrottle-inspect-slice3`; merged back into `mission/message-load` after.

1. **No overdue mark without its notice** (high 1), the mirror of the stuck ruling: the no-reply
   notice carries the question's message id so it is never a duplicate for another question; it is
   built to fit the text cap whatever the names; if the policy still refuses it, the overdue mark is NOT
   written, the refusal is logged with its reason, and the next sweep retries. Guards: a preloaded
   identical unread notice for the SAME question leaves the mark written and adds no second notice; a
   refusal for any other reason leaves the question open.
2. **A duplicate is judged per question and per kind** (high 2, medium 3). The duplicate key is
   recipient, sender, kind, the question it answers (`InReplyToMessageId`, null for none), whether a
   reply is wanted, and the text. So a reply to question B is never dropped because a reply to A or a
   plain message says the same words, and a send with `--reply-wanted` is never reduced to a plain
   unread duplicate: it queues with its own correlation id. Guards: the inspector's two sequences,
   both red on the current rule; the original same-question duplicate test stays green.
3. **Colour terminals print Gateway sentences verbatim** (found by the inspector's run, not slice 3's
   defect, fixed here because this branch holds the test): `test_broadcast_sentences_are_printed_verbatim`
   (colour terminal) fails with `FORCE_COLOR=1`; make the broadcast rows print the sentence unstyled the
   way single sends already do, and run the tool's tests with and without colour forced.

Then the touched suites, each guard watched failing, a 'Slice 3 fix round' section in this file on this
branch, push, stop. Inspection 8 follows on this branch.

## Architect rulings on inspection 7 (17 September 2026) - slice 6 fix round, on branch mission/message-load-slice6

Verdict FAIL for merge. Every finding accepted. Fixes on the pinned slice 6 branch in the worktree
`~/ReposFred/devthrottle-inspect-slice6`; merged back into `mission/message-load` after.

1. **Restored ids are provenance, never judgment** (critical). `restoredSessionId`, `restore.failure`,
   `attemptedAtUtc` and any new restore mark are written only by the Director's restore path, through
   the Gateway's provenance stamp on that route; the store restores them from its stored copy on every
   caller write, exactly as it does `ReportsTo`. A session key or device key may set only the decision
   and the handover path. Guard: a PUT that changes `restoredSessionId` is ignored (route test), and the
   inspector's owner-of-X sequence ends with the worker owned by B's restored id, never X.
2. **Only a drained seat is restored** (high). A seat is a restore target only when its captured
   session is closed (drain state closed, or its captured session id absent from the live roster). A
   seat whose captured session is still running is refused with "still running", never started again.
   Guard: capture a live seat, set its decision, restore: refused; the same after the seat closes: starts.
3. **A seat starts at most once** (high). Before the create, the Director writes a started mark and a
   restore token on the seat and saves; the create carries the token where the Gateway stores it on the
   new session. On a retry, a seat with a started mark and no restored id is resolved by looking the
   token up on the roster: found means restored (mark it), not found and the starting Director alive
   means "in progress, ask later", otherwise "may have been started; check the roster before asking
   again" and NO second create without an explicit `--force-seat <id>`. Guard: the timeout test now
   retries and asserts one create; the die-after-create case resolves by token.
4. **One restore per workspace across Directors** (high). The Gateway grants a per-workspace restore
   lease to one Director for the run (expiring after 15 minutes without a save) before relaying; a
   second Director is refused 409 with the holder named. Guard: two Directors, one lease.
5. **Owners must be running** (medium). An outside or blocked owner is checked on the live roster; not
   running means the seat fails with "owner not running", never a start under a dead id. Guard for both.
6. **Ask-only is not success** (low). `--wait-seconds 0` prints "accepted, not waited" and exits 3; the
   help says exit 0 means every seat came back. Guard on the exit code and the sentence.
7. **The skill draft** is published in slice 5 only after this round passes inspection; the record
   notes that v3 also carries the 7 September step 0 from the restart mission.

Then the touched suites, each guard watched failing, a 'Slice 6 fix round' section in this file on this
branch, push, stop. Inspection 9 follows on this branch.

## Slice 2 fix round 2 (17 September 2026, Manager seat 4)

Three code commits on `mission/message-load-slice2` after `b7bdb3cf`, one per ruling that needed code. Items 4
and 5 are record only. Every guard was watched failing: the fix reverted or one rule mutated, the named tests
red with the symptom, the file restored. The breaks and their results are in
`slice-2-evidence/fix-round-2/` with a README. No fleet message was sent. No live run was made in this round.

### What changed, per item

1. **Exactly means exactly** (`c9388ebf`). `DoorbellSafety.ComposerHoldsExactly` no longer squeezes out every
   whitespace character. It reads the composer as rows: Claude Code's prompt row after the glyph and its one
   separator, then each continuation row after exactly the two-column indent; Codex's cursor row alone (a
   wrapped Codex composer is never "exactly"). The rows, joined, must equal the line character for
   character; the one allowed difference is the single space of the line that a word wrap consumes at a row
   break. No row may be empty or start with whitespace. The rows arrive trailing-trimmed (the grid snapshot
   trims tabs and non-breaking spaces too), so the cursor is the witness for a trailing addition: it must be
   visible, on the last row, straight after the line's last character. The same check is also the
   rendered-composer witness for the echo before the Enter; the byte echo is the other witness and is
   unchanged, so this does NOT stop an Enter on a line the owner extended while it echoed (the last look and
   the one-Enter rule are what bound that).
   Guards: the line extended by a space, a tab and a non-breaking space is never erased - with the row
   trimmed and the cursor one further, with the character still on the row, and on a wrapped line; a space
   inside the line; an empty continuation row after it (Shift+Enter); an empty prompt row before it; a
   hidden cursor; part of the line; a widened indent at a wrap on the last and on a middle row; a wrap that
   absorbs only one space; Codex exact and extended. Still erased: the unextended line, char-wrapped and
   word-wrapped. Red: the squeezed comparison restored (15 tests, all the extended-line and inner-space
   cases); cursor ignored (10); cursor column ignored (9); no space allowed at a wrap (1); any whitespace
   absorbed at a wrap (1); leading-whitespace rows allowed (1); empty rows allowed (1); a prefix accepted
   (1); indent not required (1). The last five were GREEN until their tests were added.
   **Limit, written into the code:** a whitespace character the owner types exactly at a word-wrap break can
   be absorbed by the wrap without moving the cursor. Only a screen narrower than the line (about a hundred
   columns) wraps it.
2. **No stuck mark without its notice** (`cf0ffe88`). The notice already named its message; that is now a
   stated rule with a test (two messages never share a notice text). `FleetDoorbell.StuckNoticeText` takes
   the cap the policy judges by (`_messages.Limits.MaxTextLength`) and fits it: a long recipient name is
   shortened with "...", then left out; only a cap shorter than the name-less notice cuts the text.
   `FleetMessageStore.MarkStuckWithNotices` sets `StuckAtUtc` only after the notice is decided: queued ->
   mark and notice; `DuplicateDropped` -> mark, no second copy, logged (the identical unread notice is this
   message's own); any other refusal -> no mark, logged with the outcome and reason, and the next sweep
   retries. A message whose notice is null (a stuck system notice) is still marked. Guards: a preloaded
   identical unread notice for the same message -> marked, one notice; a roster name longer than the cap ->
   marked, notice written within the cap and naming the message; a blank or over-long notice at the store ->
   row open, no notice, and the next sweep marks and notifies once; one refused notice does not hold back
   another message's mark; the fitting rule at three caps. Red: a refusal still marking (3 tests); the
   duplicate exception removed (1); the duplicate writing a second copy (1); the name not fitted (2); the
   message id left out of the text (6); the policy's cap not passed (1).
   **Not proven:** the product's only remaining refusal for a system notice is the duplicate, so the
   "refused for another reason" path is reached only through the store with a hand-made draft.
3. **Dictation lock observability** (`5a32701d`). `FleetDoorbell` tracks, per tenant and session, when a run
   of `DeferredDictation` outcomes began; any other outcome for that session ends the run. A run longer than
   `DictationWarnAfter` (30 minutes) writes one line tagged `WARNING:` with the session id, the tenant, the
   start and the length. The file log has no levels, so the tag is the level. The hold counts only while a
   ring is due: during the grace after a ring the outcome is `NotDue`, which ends the run. Judgement call 5
   is corrected in place above. Guards (fake clock, 15-second sweeps): no warning at exactly 30 minutes, one
   just after, none more over three hours; a new hold after a ring counts its own 30 minutes from when the
   next ring fell due; the constant pinned. Red: never warned (2 tests); warned every sweep (2); the boundary
   inclusive (2); the run never reset (1); the run not tracked (2); the threshold changed (3); the session id
   missing from the line (1).
   **Not proven:** the inspector's observation that the row shows nothing while a PENDING record past its
   progress window holds the lock is not changed and has no test; the warning is in the log only.
4. **Snooze.** No code. The owner decided on 17 September that the agent-origin exception stands; slice 5
   (words) updates the law in `docs/new_architecture/sessions.html`. The attribution loss on a settled
   flicker (judgement call 7) is accepted: it errs towards ending the snooze, which is the law's side.
5. **Submit verification.** No code. Accepted as the stated limit of a screen witness: an older doorbell row
   scrolled into view, or an unrelated turn starting, after a swallowed Enter whose line the interface
   cleared, can read as a verified submit. Judgement call 2 is qualified accordingly.

### What is NOT proven (in addition to the slice 2 fix round's list, which stands)

- Item 1 on a live screen: no live run was made. The cursor-after-the-line rule rests on the fix round's live
  whitespace run (three spaces put the cursor at column 5) and on the parser's trimming; a live parked
  doorbell, a real narrow-screen wrap, and Codex are not captured.
- Item 1's wrap-break limit above.
- Item 2 on PostgreSQL; the store's refusal path only through a hand-made draft.
- Item 3's warning reaching the hosted Gateway's log; the per-tenant hold with more than one tenant.

### Test totals (Mac, this tree, 17 September 2026)

- **Core unit tests**: `DoorbellSafetyTests|FleetDoorbellRingerTests` 84 passed, 0 failed (62 before this
  round); the whole project 577 passed, 0 failed.
- **Core tests**: 4445 total, 4375 passed, 8 skipped, **62 failed**: the known 61 Mac-only failures, same
  names as `fix-round/full-suite-failures.txt`, plus ONE new name,
  `TerminalThroughputTests.ParseThroughput_IsNotSuperlinear` ("10 MB took 16.35x longer than 1 MB"). It is
  a timing test, it ran while the Gateway route selection was running on the same machine, it passed three
  reruns on its own, and this round changed no terminal parser code. Named here as a load-sensitive timing
  failure, not fixed. The list is in `fix-round-2/core-tests-failures.txt`.
- **Gateway unit tests** `FleetDoorbell*|FleetMessage*|Snooze*`: 373 passed, 0 failed (363 before this round).
  In eight reruns of this selection, one run hit the pre-existing disposed-SQLite race once (see the
  evidence README).
- **Gateway route tests** `FleetDoorbell*|FleetMessageRouteTests|SnoozeEndToEndTests`: 55 passed, 0 failed.
- Not run this round: the full Gateway unit and route suites, the cc-devthrottle Python tests (no Python
  changed), `scripts/test-local.ps1` (no PowerShell on the Mac), live proofs.

Next, as the Architect ruled: inspection 7 on this branch, then the slice 2 pull request.

## Slice 3 fix round (17 September 2026, Manager seat 5)

Made on `mission/message-load-slice3` in `~/ReposFred/devthrottle-inspect-slice3`, on top of `5074cb98`.
Three product commits, one per ruling, in order. No pull request opened, no fleet message sent. The breaks are
in `slice-3-evidence/fix-round-guards-watched-failing.json` (12 breaks, all red).

**Merging with the slice 2 fix round.** The stuck path (`MarkStuckWithNotices`) and the shared
`StageSystemNotice` are NOT changed, not even in their bodies. The overdue path no longer calls
`StageSystemNotice`: it needs to tell a duplicate from a refusal, which that step's return value cannot say, so
it has its own small step, `JudgeOverdueNotice`, which makes the same policy call and also returns the verdict.
When the slice 2 fix lands its own version of the same rule, the two steps can become one; that is left for
the merge rather than guessed here. The one shared piece this round DID change is `ReadHistory` (ruling 2),
which the stuck notices also go through - see ruling 2 below.

### Ruling 1 - no overdue mark without its notice

- `FleetMessageStore.MarkReplyOverdueWithNotices` now writes the overdue mark according to the notice's verdict:
  - queued: the mark and the notice are written in the one save, as before;
  - dropped as a duplicate (the asker already holds an identical unread notice about THIS question): the mark is
    written, no second notice is, and a log line says so;
  - refused for any other reason: nothing is written for that question, the refusal is logged with its outcome
    and reason, and the next sweep tries again.
  - A null draft (a question with no sending session, which the product does not write) is still marked, so it
    is not scanned for ever.
- The notice carries the question's message id in its text (it already did) and, since ruling 2, in its
  duplicate key.
- **Built to fit the cap.** `FleetMessageService.NoReplyNoticeText(message, name, maxLength)` is new; the
  heartbeat passes the limits' `MaxTextLength`. Only the roster name is ever cut, ending in `...`, and it is
  left out when fewer than four characters of it would fit. The two ids and the advice are never cut. The
  two-argument form is unchanged (no cap), so the wording pinned in slice 3 is unchanged.
- **Retried for ever, by design of the ruling.** A notice the policy keeps refusing is retried and logged on
  every heartbeat until the question is answered or deleted. With the name cut to fit, the only remaining
  refusal is a text cap smaller than about 300 characters, which the product's 16,000 never is.
- Guards (5 breaks, all red): the mark written on a refusal (the inspector's finding) - red on
  `A_notice_the_policy_refuses_leaves_the_question_open_and_the_next_sweep_retries` and
  `A_blank_notice_is_refused_and_leaves_the_question_open`; a same-question duplicate treated as a refusal - red
  on `An_identical_unread_notice_for_the_same_question_leaves_the_mark_written_and_adds_no_second`; the cap not
  passed, the name kept whole, and a name cut below four characters - red on
  `The_no_reply_notice_fits_the_text_cap_whatever_the_recipients_name` and
  `The_no_reply_notice_text_is_cut_only_in_the_name`.
- Before the fix, 6 of the new tests failed. The same-question duplicate test PASSED on the old code: that is
  the control the ruling asks to keep ("leaves the mark written"), and the second break is what proves it
  guards anything.

### Ruling 2 - a duplicate is judged per question and per kind

- `FleetMessageStore.ReadHistory`: an unread row is a duplicate only with the same recipient, sender, kind,
  `InReplyToMessageId` (null for none), reply request, and text. "Reply request" is read from the stored
  `ReplyByUtc`, which only a question carries.
- The same key now applies to system notices, since they go through the same step. For a stuck notice this
  changes nothing: it has kind `system` and no question, exactly like every earlier stuck notice.
- Effects: a reply to question B is never dropped because a reply to A, or a plain message, said the same words;
  a `--reply-wanted` send is never reduced to a waiting plain message (it queues with its own correlation id,
  subject to the spacing as any send); a plain send is not dropped against a waiting question; a report is not
  dropped against a waiting message with the same words. A reply-wanted send identical to a waiting QUESTION is
  still dropped and answers with that question's ids (the original test, still green). Judgement call 8 of
  slice 3 is therefore narrower: a dropped duplicate of a question always has a correlation id now.
- Guards (5 breaks, all red): kind ignored; question ignored (the inspector's finding 2); reply request ignored
  (finding 3); a plain send allowed to match a waiting question; system notices on the old key. The six new
  tests (both of the inspector's sequences among them) were all red on the old rule before the fix.
- Note for the inspector: `An_unread_plain_message_with_the_same_words_does_not_swallow_a_reply` is held by two
  parts of the key at once (the kind and the question), so neither single break turns it red. It is a
  sequence test; the single breaks are guarded by the other five.
- `FleetMessagePolicy`'s description of the duplicate rule says what "identical" means now. No policy code
  changed.

### Ruling 3 - Gateway sentences print whole on a colour terminal

- **The cause, reproduced.** The inspector's failure does not appear with only `FORCE_COLOR=1` on this Mac
  (3208 passed before any change). It appears with `TERM=dumb FORCE_COLOR=1`: the console library ignores the
  fixture's width of 500 on a terminal named `dumb` unless a height is given too, and wraps at 80. The refused
  broadcast row (a full session id, a label and the sentence) is longer than 80, so a line break was inserted
  into the Gateway's sentence. The inspector's exact environment was not recorded; `TERM=dumb` is the variable
  that reproduces it here, and it is what a non-interactive agent shell commonly sets.
- **Why single sends were already whole.** A single send's refusal is written by `axi_cli.fail` to standard
  error as plain text and never goes through the console. The broadcast rows and a single send's duplicate note
  go through `_say_gateway`.
- **The fix.** `_say_gateway` prints with `soft_wrap=True` (still escaped, still no highlighting), so the
  console never inserts a break into the sentence; the terminal folds it for display.
- **The guard.** `either_console` (in `tests/conftest.py`) gains a third run, "narrow colour terminal" (width
  40), and every run gives a height, so the fixture's size holds whatever `TERM` says. That turns the defect red
  on any machine, not only under `TERM=dumb`. Seven tests use the fixture, so the suite grew by seven.
- Guards (2 breaks, all red): hard wrapping back - red on the duplicate-note and broadcast tests, narrow run;
  highlighting back - red on both, colour and narrow runs.

### Test totals (Mac, this tree, 17 September 2026)

- **Gateway unit, filter `FleetMessage|FleetReply`**: 130 passed, 0 failed (117 before, plus 13 new).
- **Gateway unit, whole suite**: 5317 total, 5302 passed, 8 skipped, **7 failed - exactly the 7 Mac-only
  failures named before** (SessionCommandExecutorLiveness 3, CronJobStore 1, RuleCandidateFilter 1,
  RulePrimitives 1, WorkListStorePersistence 1). No new failure.
- **Gateway route, filter `FleetMessageRouteTests|FleetReply`**: 41 passed, 0 failed. The whole route suite
  was not rerun.
- **cc-devthrottle, full `tests/`** (scratch environment installed from `tools/cc_storage`, `tools/cc_shared`
  and `tools/cc-devthrottle` with their declared dependencies; the same package versions as the inspector's):
  - without colour forced: 3215 passed, 0 failed;
  - `FORCE_COLOR=1`: 3215 passed, 0 failed;
  - `TERM=dumb FORCE_COLOR=1`: 3215 passed, 0 failed.

### What is NOT proven

- PostgreSQL: the new duplicate query (`Kind`, `InReplyToMessageId` with a null parameter, `ReplyByUtc` null or
  not) ran on SQLite only. No migration changed; the columns all existed.
- The duplicate query is not served by a dedicated index; it narrows on the same columns as before
  (recipient, sender, unread, text hash) plus three more, so it reads no more rows than it did.
- Nothing ran live (no Director, agent or hosted Gateway).
- The retry-for-ever of a refused notice is proven over two sweeps, not over time; its log line is not asserted.
- `scripts/test-local.ps1` was not run (no PowerShell on the Mac).

## Architect rulings on inspection 8 (17 September 2026) - slice 2 fix round 3

Verdict FAIL for merge on one finding that is a limit of any screen witness, not a coding error.

1. **The doorbell never erases** (high 1). The take-back path is removed. A doorbell line that was typed
   but not verified as submitted stays in the composer; the Director answers `deferred, parked` (a new
   reason, pinned on both sides), logs it with the session id, and the Gateway leaves the message due; the
   next ring is deferred `composer-holds-text` until the owner clears the line (the row line in slice 4
   shows the messages waiting). Nothing the product typed is ever deleted by the product, so no owner
   character can be deleted with it. Guards: the unverified-submit frames from rounds 2 and 3 all end with
   no erase and reason `parked`; the reason literal is pinned on the Director and the Gateway; a second
   ring on a parked line is `composer-holds-text`.
2. **The notice cap has a floor** (low 2). `FleetMessageLimits` refuses a `MaxTextLength` shorter than
   the notice prefix plus a full id at construction, so a fitted notice always names its message. Guard
   on the floor and on the fitting test at the floor.

Then the touched suites, each guard watched failing, a 'Slice 2 fix round 3' section here, push, stop.
Inspection 10 follows, narrow, then the slice 2 pull request.

## Slice 4 - the row line (17 September 2026, Manager seat 6)

Built on `mission/message-load` on top of `f04aa97e` (rebased onto `db128ae2`, inspection 5, before the push) and nothing outside slice 4 touched. Four product
commits: the Gateway and the Director (`f33aff80`), the desktop row (`a27031e8`), the generated client schema
(`db86d5e6`), the Cockpit and phone rows (`760683aa`). No pull request opened, no fleet message sent. Evidence is in
`slice-4-evidence/`, which has its own README.

### What was built

The fold (Gateway, `Messaging/FleetInboxLineFold.cs`):
- `FleetInboxCounts` - per session: waiting (message, report, team, everyone), replies, notices (system), stuck (any
  kind), and when the oldest stuck message was written.
- `FleetInboxLineFold.Fold` - PURE. Parts in this order, joined with `"; "`, a zero part left out, null when nothing
  waits:
  - `1 message stuck, unread for 20 minutes` / `3 messages stuck, the oldest unread for 2 hours`
  - `2 messages waiting`
  - `1 reply waiting`
  - `1 notice from the Gateway waiting`
  - Age: whole minutes under two hours, whole hours under two days, whole days after, rounded down; "less than a
    minute" under one minute and for a write time in the future. A stuck count with no write time and a negative
    count throw.
- `FleetInboxLineStamp.Stamp` - assigns `SessionDto.InboxLine` on EVERY row, both directions. No source or no
  account stamps null and reads nothing. Session ids are matched lower-cased, as the store keeps them.
- `IFleetInboxLineSource`, implemented by `FleetMessageStore.UnreadCountsByRecipient`: ONE grouped query per call
  (unread rows grouped by recipient, kind and stuck mark; count and oldest write time), no text, no rows.

Where it runs: inside `GatewayEndpoints.StampFleetRolesAndFold`, after the snooze-expiry stamp and before the colour
loop, at the fold's one moment (`foldNowUtc`). It reads nothing the colour, label or bucket read, and they do not
read it. Threaded (optional parameter `inboxLines`) through:
- the roster (`GET /sessions`), `GET /sessions/{sid}`, `FoldedAccountRoster` (the Fleet Manager digest),
- the display push (`GatewayHost.EnrichVoiceThenFoldForPush`), which also feeds the phone badge count's
  `FoldedFleet`.
The host passes its `FleetMessageStore` in all four.

The wire and the Director:
- `SessionDto.InboxLine` (JSON `inboxLine`) and `SetDisplayStateRequest.InboxLine`.
- `FleetDisplayStateObserver` puts the line in the payload and in the change-gate signature.
- `FleetDisplayStateExecutor` passes it to `Session.ApplyGatewayDisplayState` (new optional `inboxLine`), which
  stores `Session.GatewayInboxLine` (blank is null) and raises the change event when only the line changed.
- `ControlEndpoints.Map` echoes it as `SessionDto.InboxLine`.

The clients, one edit each, rendering the string verbatim, a presence check being the only condition:
- Desktop: `SessionViewModel.InboxLine` / `HasInboxLine`, raised with the rest of the fold projection; a
  `TextBlock` under the state line in `MainWindow.axaml` (11 px, message-count blue `#3B82F6` from the style
  guide, wraps).
- Cockpit: `roster-inbox` line under `roster-state` in `SessionRoster.tsx`, `var(--accent)`.
- Phone: `row-inbox` line under `row-meta` in `Home.tsx`, `var(--accent)`.
- Schema: `packages/client-core/src/api/schema.ts` regenerated with `openapi-typescript` from the document an
  in-process Gateway served (a test host on an operating-system port, not a standing Gateway). See judgement call 7.

### Judgement calls for the inspector

1. **Four parts, not two.** The brief names "waiting" and "stuck"; the charter adds replies and notices, so a
   reply and a Gateway notice are their own parts with their own words. A report, a team copy and a granted
   whole-account copy all count as "messages".
2. **A stuck message is counted only as stuck**, whatever its kind, so "1 message stuck" can be a stuck reply. A
   message is never in two parts.
3. **"Unread for" is measured from when the message was WRITTEN**, not from when it was marked stuck; with several
   stuck, the oldest is named.
4. **One colour for every line.** The clients do not colour a stuck line differently: that would be the client
   deciding what the words mean. If the owner wants stuck to stand out, that is a Gateway-folded tone field, not a
   client branch.
5. **The age ticks.** The line changes once a minute for a session with a stuck message, so the display push
   re-sends that row once a minute (the change gate stops everything else). Nothing else re-sends.
6. **The line does not change the colour, the label, the triage bucket, or the needs-you count** (a test pins
   this). A waiting message is not the owner's queue.
7. **The regenerated schema carries more than this slice.** The committed file was 31 routes and 8 schemas behind
   main; the generator brought all of that in. It also DROPS the four Windows-only routes (`/exes/list`,
   `/exes/slots/{n}`, `/exes/slots/{n}/build-start`, `POST /directors` and its `LaunchDirectorRequest`), because
   the Gateway maps them only on Windows and the generator ran on macOS. No client uses them (checked). A later
   generation on Windows puts them back. Every hunk was accounted for before committing.
8. **The Exes page (`/exes/list`, Windows-only, a local diagnostics page) gets no row line**: it passes no inbox,
   so the stamp writes null there.
9. **The cost is one query per fold pass.** A fold runs on every roster read, every accepted Director push and
   every 5-second display sweep per tenant, plus the phone badge count every 8 seconds; each of those is now one
   more grouped query over the unread rows of one account (index `TenantId, RecipientSessionId, ReadAtUtc,
   CreatedAtUtc`). No cache was added.

### What is proven - every guard watched failing, then restored

`slice-4-evidence/guards-watched-failing.json`: 29 breaks, each applied, the named suites run, the file restored.
28 went red on every suite named. The one exception, "a row with no counts keeps its old line", went red on the
unit guard and stayed green on the route test by design (an account with nothing unread takes the branch that
clears every row first); the file says so. One break first failed to COMPILE (my own extra parenthesis), which is
not a watched failure; it was fixed and re-run red.
- **Fold shapes** (`FleetInboxLineTests`, Gateway unit, 36 tests): plurals, replies, notices, stuck with one and
  several, all parts together, a zero part, the age boundaries, a future write time, the refusals, the separator.
  Red: always singular; age zero; hours at one hour; stuck not leading; replies worded as messages.
- **Store** (same class, real SQLite): the grouping over every kind with two stuck messages, read messages not
  counted, the account partition. Red: stuck mark ignored; read rows counted; the newest stuck taken as oldest.
- **Stamp**: both directions, no source or no account reads nothing. Red: a row keeping an old line; id case not
  folded; one read per session.
- **Cheap**: `One_fold_over_forty_sessions_is_one_query_against_the_inbox` - the whole shared fold over 40
  sessions with 40 unread messages, counted on the framework's own command events (slice 2's
  `DatabaseCommandCounter`): exactly 1 reader and 0 other commands. Red with one read per session.
- **The wire, on a real host** (`FleetMessageRouteTests.The_roster_the_session_read_and_the_desktop_push_carry_the_row_line_until_it_is_read`):
  a manager's message and a worker's report queued; `GET /sessions` shows `inboxLine: "1 message waiting"` on
  both recipients and none on the sender; `GET /sessions/{sid}` the same; a display sweep sends
  `set-display-state` with the line to the tunnel Director; after the worker reads, the roster line is gone and a
  later sweep sends the push with no line; no input verb was sent. Red: the fold not stamping; the roster, the
  single read, the host routes or the display push not given the store; the JSON name changed; the payload or the
  signature dropping the line.
- **The push** (`FleetDisplayStateObserverTests`): the line in the payload, a change to the line alone re-pushed,
  a cleared line pushed. **The Director** (`SessionCommandExecutorTests`): the verb stores the line and `Map`
  echoes it, and a stamp without it clears it. **Core** (`GatewayDisplayStateSignalTests`): the line alone raises
  the change, a repeat does not, a clear does. Red: executor dropping it; `Map` not echoing; the change not noticed.
- **Desktop** (`SessionRailInboxLineRenderTests`, Avalonia headless, the real row template at the rail's 264
  pixels): the longest four-part line drawn whole and wrapped inside the rail; a clearing stamp removes it; a line
  arriving later is drawn with no rebuild; no line draws nothing. Red: the projection not re-raised; the template
  binding another property; never visible; `Map` not echoing; the change not noticed.
- **Cockpit** (`rosterInboxLine.test.tsx`, 4) and **phone** (`HomeRosterCard.test.tsx`, 3 new): verbatim,
  absent when not sent, colour and label untouched, and through the phone's assembled Home roster read. Red: the
  line dropped; the client rewording it; the phone showing a line for every row.
- **Pictures**: `director-rail-row-line.png` (Skia, in-process `RenderTargetBitmap`), `cockpit-row-line.png`
  and `mobile-row-line.png` (headless Chrome), with the text read back in `web-render-result.json`.

### What is NOT proven

- **PostgreSQL.** The grouped query (`GroupBy` with `Count` and `Min` over a date) ran on SQLite only. No schema
  change.
- **Live.** No live Director, real agent, hosted Gateway or real phone. The pictures are the real row components
  in proof pages, not the running apps (see the evidence README).
- **Load.** The one-query-per-fold claim is measured; what one more grouped query per roster read and per push
  costs on the hosted Gateway at fleet scale is not.
- **The multi-tenant display pass** with more than one account was not exercised for this line; the fold takes
  the account the pass already runs under.
- **The minute tick** of a stuck line on the desktop is proven only as "a changed line is re-pushed"; no test
  waits a real minute.
- **Words (slice 5)**: no document mentions the row line.
- **`scripts/test-local.ps1`** not run (no PowerShell on the Mac); suites run directly.

### Test totals (Mac, this tree, 17 September 2026)

- **Gateway unit tests**: 5369 total, 5354 passed, 8 skipped, **7 failed - the same 7 Mac-only failures named in
  slice 1** (CronJobStore 1, RuleCandidateFilter 1, RulePrimitives 1, SessionCommandExecutorLiveness 3,
  WorkListStorePersistence 1). No new failure.
- **Core tests**: 4446 total, 4377 passed, 8 skipped, **61 failed - by name exactly the list in
  `slice-2-evidence/fix-round/full-suite-failures.txt`**. **Core unit tests**: 555 passed.
- **Avalonia tests**: 550 total, 543 passed, **7 failed, the identical 7 failing on the untouched head `f04aa97e`**
  (run in a throwaway worktree): LegacyWorkspaceImport 1, MicCaptureConstructionQueriesNoDevice 2,
  SpeakDialogCloseDuringStartup 1, SpeakDialogReadyCueBlanking 3 - microphone and workspace-import tests, not
  listed in earlier slices because no earlier slice ran this suite. New: 4 render tests, all passing.
- **Web** (typecheck clean on all workspaces; Cockpit and phone `vite build` succeed; lint clean on the changed
  files): client-core 1226 total, 23 failed; Cockpit 354 total, 24 failed; phone 81 total, 30 failed;
  cc-assistant 106 passed. **Every failure is present on the untouched head** (same names, listed in
  `slice-4-evidence/web-failures-before-and-after.txt`); the new tests (4 Cockpit, 3 phone) all pass. Most are
  this Mac's Node 26, where `localStorage` is undefined without `--localstorage-file`
  ("Cannot read properties of undefined (reading 'clear')"), plus the Your Throttle contract, the account layout
  and new-session tests.
- **Gateway route tests** (full suite, 26 minutes): 2597 total, 2528 passed, 52 skipped, **17 failed - the 16
  named in slice 1** (ContextLessRouteCensus 1, FleetSpawnMissionAttach 2, FleetSpawnOrigin 4,
  TunnelRosterPushReadProof 3, WorkflowSeat 2, GatewayTestSuiteLock 2, HostedProcessControlDeny 2) **plus a third
  GatewayTestSuiteLock test**, `TheLockFileNamesThisProcess_SoABlockedRunCanSayWhoIsBlockingIt`. Rerun alone, that
  class fails exactly the usual 2 and passes this one, on this tree and on the untouched head alike. It reads the
  machine-wide suite lock file, which other runs on this machine can touch; nothing in this slice goes near it.
  The new route test passed.

## State after slice 4 (17 September 2026)

- Slice 4 head is this commit on `mission/message-load`, awaiting inspection. No pull request opened.
- Unchanged: slice 2 awaiting inspection 5, slice 3 awaiting inspection 6, slice 6 awaiting inspection. Next:
  slice 5 (the words - which should now mention the row line), the record, the release.

## Slice 2 fix round 3 (17 September 2026, Manager seat 5)

One commit on `mission/message-load-slice2` after `3dacd248`, holding both rulings, their guards and this record. Every guard was watched
failing: ten breaks, each on the finished fix, the named tests red, the files restored. The breaks and their
results are in `slice-2-evidence/fix-round-3/` with a README. No fleet message was sent. No live run was made.

### What changed, per item

1. **The doorbell never erases.** `FleetDoorbellRinger.TakeBackUnverifiedAsync` is gone, and with it
   `IDoorbellTarget.EraseAsync`, `SessionDoorbellTarget.EraseAsync`, `Session.EraseComposerCharactersAsync`
   (its only caller) and `ErasePolls`. The product now has no way to press Backspace for a doorbell. After an
   unverified submit the ringer reads the composer once, only to name the reason, and writes nothing:
   - composer empty: `deferred, not-submitted` (unchanged: the line left, no turn was seen);
   - anything else - the doorbell line, the line with owner text, owner text alone, or an unreadable composer:
     `deferred, parked`, logged by the Director with the session id; the Gateway logs it with the session id
     and leaves the message due (its ring count is not raised). The next ring reads the parked line as text
     and is deferred `composer-holds-text` until the owner clears or sends it.
   `FleetRingDeferReasons.Parked = "parked"` is new and is in `All`, so the Gateway accepts it.
   **`DoorbellSafety.ComposerHoldsExactly` is kept.** Something else still needs it: it is the rendered-composer
   echo witness that `TerminalSubmit.DoorbellSubmitAsync` reads before its one Enter (`composerShowsLine`).
   Its tests stay. Its comments no longer call it the licence to erase, and they now name inspection 8's
   blind spot (a trimmed character that does not move the cursor).
   Guards: every unverified-submit frame from rounds 2 and 3 - the exact line, the char-wrapped and
   word-wrapped line, the line under an older doorbell row, the line extended by a space, tab and
   non-breaking space (trimmed with the cursor one further, kept on the row, wrapped), a space inside the
   line, an empty continuation row, owner words after the line, and inspection 8's frame (the owner's
   character trimmed and the cursor NOT moved, asserted identical to the bare line's frame) - ends `parked`
   with nothing after the send but screen reads; a second ring on a parked line is `composer-holds-text` and
   types nothing; `parked` pinned on the Director (ringer tests) and the Gateway (executor tests, the
   contract list, and a `parked` answer on the wire is a deferral that raises no ring count).
   Red: round 2's take-back restored (19); the same with its erase answering `parked`, so only the erase can
   fail (19, `Expected "frame", Actual "erase"`); `parked` answered as `not-submitted` (19) or as
   `composer-holds-text` (19); the literal changed (20 Director, 3 Gateway); `parked` left out of the contract
   (2); a parked doorbell line allowed to ring again (1).
2. **The notice cap has a floor.** `FleetMessageLimits.MinTextLength` is the stuck notice's fixed opening
   (`FleetDoorbell.StuckNoticePrefix`, "Your message ") plus `MessageIdLength` (32): 45. Setting
   `MaxTextLength` below it throws `ArgumentOutOfRangeException` when the limits are built, `with` included.
   `StuckNoticeText` refuses a cap below it the same way (it used to clamp to 1), so a notice fitted to any
   allowed cap opens with the whole message id. Guards: the floor is 45 and equals the opening plus 32; 45
   is accepted, 44 and 20 refused; at the floor two messages' notices are exactly "Your message <id>", differ,
   and each holds its id; a cap of 44 or 20 is refused. Red: the floor not enforced (1); the floor without
   the id (2); the notice cap not checked (1).

### What is NOT proven

- A live parked doorbell: no live run was made. That the next ring reads a parked line as
  `composer-holds-text` rests on the scripted frame, not on a captured screen.
- The row line that tells the owner messages are waiting behind a parked line is slice 4's; until then a
  parked line is visible only in the terminal and in the two logs.
- Codex frames for the parked path (the ringer tests are Claude Code frames; the reason mapping does not
  depend on the agent beyond `ReadComposer`).

### Test totals (Mac, this tree, 17 September 2026)

- **Core unit tests** `DoorbellSafetyTests|FleetDoorbellRingerTests`: 87 passed, 0 failed (84 before; one
  erase test removed, four added).
- **Core tests** `DoorbellSubmitTests` (the one caller of the kept witness): 9 passed, 0 failed.
- **Gateway unit tests** `FleetDoorbell*|FleetMessage*`: 142 passed, 0 failed (139 before; three added).
- **Gateway route tests** `FleetDoorbell*|FleetMessageRouteTests`: 34 passed, 0 failed.
- Not run this round: the full Core, Gateway unit and route suites, the cc-devthrottle Python tests (no
  Python changed), `scripts/test-local.ps1` (no PowerShell on the Mac), live proofs.

Next, as the Architect ruled: inspection 10, narrow, then the slice 2 pull request.

## Slice 6 fix round (17 September 2026, Manager seat 6)

On branch `mission/message-load-slice6`, worktree `~/ReposFred/devthrottle-inspect-slice6`. Code commit `f1cc4a05` on
top of `24a9a4e4`; this record in the commit after it. No pull request opened. No fleet message sent, no skill
published. Every mutation run is in `slice-6-evidence/fix-round-red-runs.md`.

### What changed, per ruling

**1. Restored ids are provenance (critical).** New route `POST /gateway/workspaces/{id}/restore/marks`
(`WorkspaceEndpoints`), body `WorkspaceRestoreMark` (`WorkspaceRestoreDtos.cs`): kinds `started`, `restored`, `failed`,
`finished`. It is the only writer of `restoredSessionId`, `restoredSeedFile`, `restore.failure`,
`restore.attemptedAtUtc`, the new `restore.startedToken` / `startedAtUtc` / `startedByDirectorId`, and the document's
`restoredBy`. It refuses a person's phone or browser (403); a session key never reaches it (`SessionKeyGuard` lists only
`.../restore`, and a guard case now pins `.../restore/marks` refused); a Director credential must hold the workspace's
live lease (409 otherwise). `WorkspaceStore.ApplyStoredProvenance` now puts back every one of those fields, and the
lease, from the stored copy on every ordinary write (`RestoreStoredMarks`), including when the caller drops the whole
`restore` block. The caller keeps the decision, its reason and command, and the handover path. The Director writes
nothing through PUT any more (`IRestoreGateway.SaveWorkspaceAsync` is gone; `RecordMarkAsync` replaced it). Guards: the
store test (a PUT setting all of them is ignored; a PUT clearing them keeps them), the inspector's sequence in the unit
suite over the real store, and the same sequence on a booted host with an unrelated session key: the PUT is ignored,
the Worker alone fails "has not been brought back yet", and after the Manager comes back the Worker is owned by the
Manager's restored id, never X. Two older tests that wrote `restoredSessionId` through PUT
(`WorkspaceOwedSeatsAndOriginRulesTests`, `WorkspaceProvenanceAndMalformedBodyTests`) now record it through a lease and
marks, as a restore would.

**2. Only a drained seat is restored.** `DirectorRestore.StillRunning`, read from the account's roster with each
Director's reachability (`GET /sessions?envelope=true`, `RestoreRoster`). A seat whose captured session is on the roster
under an online or wobbly Director is refused "still running" - even when its record says `closedAtUtc`, because that is
a drain judgment a writer can set. A seat still listed under an unreachable Director is refused unless the drain recorded
it closed (the Gateway keeps serving an unreachable Director's last rows, so "absent from the roster" alone would never
be true after a crash, and "listed" alone would block every restore after one). A named seat that is still running
refuses the whole request in `PrepareAsync`; asked for everything, a running seat gets its own failure and the rest come
back; if every target is running the request is refused. Guards: unit (named refused then starts after close; all-owed;
all running; unreachable Director with and without a close; recorded closed but running), route (capture a live seat,
decide, restore: refused; the seat leaves the roster: starts).

**3. A seat starts at most once.** Before a create, the Director writes a `started` mark with a fresh token; if that
write fails the create is not sent and the run stops. The create carries `NewSessionRequest.RestoreClaim` (workspace,
seat, token). The spawn door (`POST /directors/{id}/sessions`) refuses a claim from anything but a Director credential
(403) and, after the Director confirms the create, calls `WorkspaceStore.RecordRestoredByClaim`, which writes the new
id only onto the seat whose stored token matches, and never turns a recording failure into an HTTP failure. A late
`failed` mark does not erase a seat the Gateway already recorded restored. Only a create the Gateway refused outright
(a 4xx, `GatewaySpawnFailedException.NothingStarted`) clears the token; a timeout, a 5xx or an unreadable answer keeps
it. On a later run, a seat with a token and no restored id is: "still in progress, ask again in a few minutes" when the
start is younger than two minutes (`InProgressWindow`; the Gateway waits thirty seconds for a create); otherwise "MAY
have been started already ... ask again with --force-seat <id>", naming any running session on that Director with the
seat's name created since the start. `--force-seat` (`WorkspaceRestoreRequest.ForceSeats`, the command's
`--force-seat`, the action catalogue and its pinned fixture) starts it with a new token. Guards: the timeout test now
asks again three times and asserts one create until forced; create-then-timeout resolves by token and a retry is
refused "restored once"; a Director that dies after the create leaves the seat recorded and the retry starts nothing; a
relay error is a maybe; the token must match; a claim from a session key or a person is refused on a booted host; the
spawn door records by token on a booted host.

**4. One restore per workspace across Directors.** `WorkspaceDocument.RestoreLease` (Director, who asked, granted,
renewed), written only by the store: `TakeRestoreLease` in the restore route before anything is relayed (409 naming the
holder when another Director's lease is live), renewed by every mark, released by the `finished` mark the Director
writes when its run ends (also when the run throws), and void fifteen minutes after the last mark. The route gives back
a lease it granted when the Director is not connected or refuses (not on a timeout, and never a lease the Director
already held). The route now refuses an authored workspace itself (409) before taking a lease. Guards: store (second
Director refused by name, renew, lapse, finish), unit (a Director without the lease starts nothing), route (two
Directors on the captured machine: the second is refused with the first named and receives no restore command; a
refused restore gives the lease back).

**5. Owners must be running.** `ResolveOwner` checks the roster for an owner outside the workspace, a blocked
never-closed owner, and an owner restored in an EARLIER run (the ruling named the first two; the third has the same
hole). An owner restored in this run is used without the check, because the roster may not show it yet. Not running,
or running only on an unreachable Director, fails the seat "is not running on any Director this Gateway can reach".
Guards: unit, for all three plus the unreachable case.

**6. Ask-only is not success.** `director restore --wait-seconds 0` prints `ACCEPTED, NOT WAITED` and "accepted, not
waited", adds `"waited": false, "status": "accepted, not waited"` to `--json`, and exits 3. The help and
`docs/cli-reference.md` say exit 0 means every seat came back, 1 a seat failed or is pending, 3 accepted and not waited.
Guards: the exit code and the sentence (text and JSON), the help text, `--force-seat` sent only when given.

**7. No code.** The `director-restart` skill draft (v3) is still unpublished and still carries the 7 September step 0
(`machine restart-capability`) from the restart mission. It is published in slice 5 only after this round passes
inspection. Its "asking twice is safe" sentence is now true only with the qualification above - a seat that may have
started needs `--force-seat` after a check - and slice 5 should say so when it publishes.

### Judgement calls for inspection 9

1. **The token lives on the seat, not on the session.** Ruling 3 said the Gateway stores the token "on the new session"
   and a retry looks it up on the roster. There is no session field the Gateway stores and serves back without a
   Director-side change and a migration, so the Gateway that performs the create writes the new id onto the seat whose
   token matches - the lookup the ruling wanted, done at create time. What it does not cover: a Gateway process that
   dies between the Director's confirmation and that write leaves a token and no id; that seat is reported "may have
   been started" (with any same-named session on that Director listed) and needs `--force-seat`, never a blind retry.
2. **"The starting Director alive" became a time window.** A new run on the same Director is never concurrent with its
   old one (the process gate and the lease), so liveness of that Director says nothing about the old start. What can
   still be under way is a create the Gateway is waiting on after the Director's ten-second client timeout; two minutes
   covers the Gateway's thirty-second wait.
3. **Ruling 2 is stricter than written.** "Closed, or absent from the roster" is implemented as: never while running on
   a reachable Director (whatever the record says); listed under an unreachable Director only if recorded closed. The
   reasons are above.
4. **Drain judgments stay writable.** `drainState`, `closedAtUtc` and `blockedReason` are still judgments a session
   writes (the drain is a session act). Neither can now start a seat or name an owner by itself: both rulings 2 and 5
   read the roster, and the record only ever narrows what the roster allows.
5. **The Director-credential test is "not a session and not a phone or browser".** A workstation key is not bound to one
   Director id, so a workstation key of the same account could write marks claiming to be the lease holder. Only the
   product holds those keys; this is the same boundary `SpawnOrigin` states.
6. **`restoredBy` is provenance now** and is written by the `started` mark. The old hand restore wrote it through PUT;
   there is no hand restore any more.
7. **The restore claim is ignored, not refused, on the machine door** (`POST /machines/{machine}/sessions`): the
   Director does not use that door, nothing is recorded from it, and the Director's create ignores the field.

### What is NOT proven

- No live Director restored anything; the route tests use the recording fake tunnel Director, and the unit suite uses a
  fake Gateway over the real store. `ControlApiHost.StartWorkspaceRestoreAsync` still has no test (it now also refuses
  when the roster cannot be read).
- The critical symptom run (M1 with the "PUT ignored" assertion removed) shows the Worker SPAWNED although its boss never
  came back, and on the host that the Worker-alone restore no longer fails; the failure message does not print the
  controller id itself, so "under X" is read from the code path, not from the output.
- The lease is in the workspace document, so it survives a Gateway restart; a Gateway that restarts mid-run leaves the
  lease to lapse. Nothing measures the fifteen minutes against a real slow restore.
- A seat whose Gateway died between create and record is not resolved (judgement call 1).
- PostgreSQL: the new fields live in the workspace JSON document (no schema change); stores ran on SQLite only.
- The full Gateway route suite was not run this round (only the Workspace and spawn filters below);
  `scripts/test-local.ps1` not run (no PowerShell on the Mac).

### Test totals (Mac, this tree, 17 September 2026, after the last code change and after every mutation was restored)

- **Gateway unit tests, whole project** (it holds the ControlApi drain and restore tests; there is no separate ControlApi
  test project): 5358 total, 5343 passed, 8 skipped, **7 failed - the same 7 Mac-only failures** (CronJobStore 1,
  SessionCommandExecutorLiveness 3, RuleCandidateFilter 1, WorkListStorePersistence 1, RulePrimitives 1). No new one.
  Filter `DirectorRestore|DirectorDrain|SessionKeyGuard|Workspace|DrainRestoreCommand`: 479 passed, 0 failed (was 330
  under the inspector's narrower filter). New or rewritten: `DirectorRestoreTests` 36 (was 20),
  `WorkspaceRestoreMarksTests` 10, one guard case.
- **Gateway route tests**: `WorkspaceRestoreRouteTests` 14 passed (was 7); filter `Workspace` 28 passed, 0 failed.
  Filter `ControlApi|GatewayClient|FleetSpawn|SpawnOrigin`: 29 total, 23 passed, **6 failed - the known
  FleetSpawnMissionAttach 2 and FleetSpawnOrigin 4** (local Director create answers Error; they fail on origin/main).
- **cc-devthrottle** (scratch virtual environment from the declared local `cc_storage`, `cc_shared` and
  `cc-devthrottle` packages plus pytest): `test_director_restore.py` and `test_axi_step_6c_help_and_errors.py` 522
  passed; the whole suite **3228 passed, 0 failed** (3225 before, compared by collecting both trees: two new tests,
  one renamed and extended, and `test_usage_errors` gained a case for the new `--force-seat` option).
- **Mutations**: 14 unit runs, 7 route runs, 2 symptom runs, 2 command-line runs - every one red with the named test,
  every file restored, `git status` clean after each batch.

## State after the slice 6 fix round (17 September 2026)

- `mission/message-load-slice6` carries the fix round, pushed. Awaiting inspection 9 on this branch, then the merge back
  into `mission/message-load`.
- The `director-restart` skill draft (v3) is still unpublished (ruling 7).

## Slice 5 - the words (17 September 2026, Manager seat 7)

Built on `mission/message-load` on top of `c89a5d7d`. Words only: no behaviour changed. No pull request
opened, no fleet message sent, nothing published. The skill draft diffs are in `slice-5-evidence/`.

The words describe the mission's product as a whole, including the slice 2 fix rounds 2 and 3 (the doorbell
never erases; a parked line) and the slice 3 fix round (the duplicate key). Those live on
`mission/message-load-slice2` and `mission/message-load-slice3` and are NOT merged into this branch yet, so
on this branch alone a few details in `docs/FleetMessaging.md` are ahead of the code until those merges.

### Every file changed, and why

The preamble (every agent reads it, every session):
- `src/CcDirector.Core/Sessions/FleetPreambleTemplate.cs` - `message ask` and "every message you send
  interrupts the receiving agent" are gone. New: `message inbox` in the command list, `session report` and
  `session raise` (the raise-your-hand command, which works again since slice 1), and a short block:
  messages are rare; most sessions can message nobody; only the session that started you and the sessions
  you started; six an hour; anything else goes in your report; queued, never typed; one doorbell line says
  to run `cc-devthrottle message inbox`; `message send` (with `--reply-wanted`), `message send all` (the
  sessions you started), `message reply`; nobody waits. 20 lines changed, net 6 longer.
- `src/CcDirector.Core/Sessions/SessionManager.cs` - the `CC_FLEET_TOOLS` environment line (the one-line
  command list every session gets) drops `message ask`, adds `message inbox` and `message reply`, and says
  messages are rare, queued and limited to the two relationships. The Codex hook installer and the Pi
  preamble writer have no text of their own: both print the same rendered template, so they changed with it.
- `src/CcDirector.Core.Tests/Sessions/fleet-preamble-default.approved.txt` - regenerated from the real
  output (the golden test's `.received.txt`), after watching the golden test fail on the new text.
- `src/CcDirector.Core.Tests/Sessions/FleetPreambleTests.cs` - the existing command test now asserts
  `message inbox` and the absence of `message ask`; new `Build_TeachesTheQueuedInboxAndNotTheInterrupt`.
- `src/CcDirector.Core.Tests/Pi/PiPreambleWriterTests.cs` - the same swap for the Pi file.

The built-in skills (shipped files are the only source; see "drafts" below):
- `src/CcDirector.Gateway/Skills/Content/fleet-comms.skill.md` and its regenerated copy
  `.claude/skills/fleet-comms/SKILL.md` (body identical; the frontmatter description no longer says "ask
  another session a question and get its answer"). The Messages and broadcast sections are rewritten:
  who may message whom, the limits, "put it in your report", queued not delivered, the real doorbell line,
  `message inbox` and `--all`, re-rings, stuck, the row line, `--reply-wanted` / `--reply-by` /
  `message reply`, `session raise`, the whole-fleet grant, and that typing into a session is the owner's
  alone. The `--controlled-by <session-id>` owner line is removed (the Gateway refuses it since slice 1).
  Two other false sentences in the same file fixed on the way: the reaping step no longer tells an agent to
  `curl -X DELETE http://127.0.0.1:7878/...` (it says `session stop <target> --reason`), and the health
  check describes the real self-test (Windows only, one throwaway, "queued").
- `src/CcDirector.Gateway/Skills/BuiltInSkills.cs` - the fleet-comms summary (the line in every preamble's
  skill index) no longer says "message, ask". String only.
- `src/CcDirector.Gateway/Skills/Content/terminology.skill.md` and `.claude/skills/terminology/SKILL.md` -
  said `message send all` reaches your mission or checkout; it reaches only the sessions you started. The
  older-names table row changed to match.

The mission workflow (built-in; shipped file is the source):
- `src/CcDirector.Gateway/Workflows/Content/mission.instructions.md` and the `.claude/skills/mission/SKILL.md`
  copy (it duplicates the workflow): "Fleet messages truncate at the first newline" is replaced by "the
  review goes in a file", reported with one `session report` line; the Worker section says report with
  `session report`, messages are rare and queued, blocked means `session raise`; the hygiene bullet "A
  message interrupts the receiving agent" is replaced by the queue rules.

Documents:
- `docs/FleetMessaging.md` - rewritten: commands, who may message whom, how a message reaches its reader
  (record, doorbell, inbox, re-ring, stuck, row line), replies, the snooze exception, and the honest limits
  (only Claude Code and Codex are rung; a parked doorbell line is never erased).
- `docs/cli-reference.md` - overview list gains `session raise` and `message reply`; Message Send gains its
  options and the reply paragraph; new Message Reply section; Message Inbox describes the row headings;
  Session Spawn no longer offers "a session id" as an owner; Selftest describes the real test.
- `docs/SessionIntercommunication.md` - a dated STALE TRANSPORT note at the top pointing to
  `FleetMessaging.md`, and a one-line retired note under sections 5, 7 and 8. The July design text is kept
  as history.
- `docs/new_architecture/sessions.html` - (1) the 13 September "It INTERRUPTS the owner, deliberately" bullet
  is replaced by the 16 September ruling, dated, with the owner's words, saying it replaces the 13 September
  one and why the doorbell still meets that ruling's purpose. (2) The 14 July snooze law: rule 1 carries a
  dated amendment pointer; a new dated law callout records the owner's 17 September decision (agent-origin
  work keeps an armed snooze; the owner's work and unexplained work still end it), the reason (a snooze is
  the owner's wish to be left alone; another agent ringing must not undo it), and how it works
  (`WorkingOrigin`, `SnoozeLandingObserver`). The 17 July paragraph, the "Working clears the hold" table row
  ("it does not matter WHO woke the terminal") and the scenario row ("another agent's fleet message wakes
  it") each say what changed on 17 September.
- `docs/public/tools/01-overview.md` - `message ask` example replaced with inbox/reply; the owner sentence no
  longer offers "a session id"; one paragraph on the queue.

Plugins (shipped separately from the Gateway):
- `plugins/devthrottle/skills/devthrottle-sessions/SKILL.md` and `plugins/devthrottle/README.md` - the
  "every message interrupts" and "ask and wait" sections rewritten to the queue; the spawn examples now
  declare an owner (they were refused since 13 September); the self-test paragraph corrected.
- `plugins/agent-discipline/skills/checks-that-fail-open/SKILL.md` - the anecdote about `message ask` is put
  in the past tense and says what the strong claim is now.

Command line (help and docstrings only):
- `tools/cc-devthrottle/src/cli.py` - `session report` says a doorbell line announces it and it is held to
  the hourly limit but not the spacing; `session hold` says another agent's message does not end a hold, and
  that owner work and unexplained work do (it used to say a repaint never does); `session compact-continue`
  no longer says "a supervising agent can rescue a worker" - the Gateway refuses it to every session key;
  `selftest` and its `--timeout-ms` no longer mention an ask step. Action catalogue: `session-hold` and
  `fleet-selftest` descriptions reworded to match.
- `tools/cc-devthrottle/src/session_ops.py` - the `--controlled-by self` usage error outside a session no
  longer suggests `--controlled-by <session-id>`; it suggests `--standalone --why`.
- `tools/cc-devthrottle/tests/fixtures/actions_json_before_step_6c.json` - regenerated from the real
  `actions --json` output after the pin test failed on exactly the two reworded descriptions.
- `tools/cc-devthrottle/tests/test_help_and_errors_axi.py` - the "spawn self outside" row asserted the old
  `--controlled-by <session-id>` advice; it now asserts `--standalone --why`.
- `tools/cc-devthrottle/tests/test_message_load_words.py` - new, 6 tests on the reworded help.

The guard:
- `src/CcDirector.Core.UnitTests/Skills/RetiredMessagingWordsTests.cs` - new. Fails if "message ask",
  "interrupts the receiving", "interrupts the session that receives", "truncate at the first newline" or
  "--controlled-by <session-id>" appears in: the preamble template, `SessionManager.cs`, every shipped
  skill and workflow file, their `.claude/skills` copies, every plugin markdown file, `docs/public`,
  `docs/FleetMessaging.md`, `docs/cli-reference.md`, and `tools/cc-devthrottle/src/*.py`. It scans text
  surfaces, not Gateway code (the Gateway still names the old ask in order to refuse it). Presence checks:
  every named file is read, each directory yields files, and the preamble and skill still teach the inbox.

### Drafts pushed - and the two that could not be

- **fleet-comms: NO draft.** `cc-devthrottle skill push fleet-comms` is refused: "'fleet-comms' is a built-in
  DevThrottle skill and cannot be edited." That is the product's rule (the repository's project instructions, "A built-in skill is
  changed in one place"). The shipped file above is the change; it reaches the fleet when the Gateway is
  deployed. Nothing for the Architect to publish.
- **mission workflow: NO draft.** `cc-devthrottle workflow push mission` is refused the same way (built-in).
  Same answer: the shipped `mission.instructions.md` is the change, and it goes out with the Gateway deploy.
- **fleet-naming: draft v5 pushed, NOT published.** Not a built-in and not in this repository. It still
  taught the 13 September ruling ("It interrupts them, and that is intended", and "IT WILL INTERRUPT YOU"
  in a spawn example). Rewritten to the queue. Diff: `slice-5-evidence/fleet-naming-v4-to-draft-v5.diff`.
- **checks-that-fail-open: draft v9 pushed, NOT published.** Same anecdote as the plugin copy. Diff:
  `slice-5-evidence/checks-that-fail-open-v8-to-draft-v9.diff`.
- **director-restart: draft v3 updated, NOT published.** It already held slice 6's changes and the
  7 September changes. Added: a KNOWN GAP note in step 2 (below), "twenty doorbells", the `send all`
  sentence, and step 3 no longer says messages must be one line. Diff:
  `slice-5-evidence/director-restart-draft-v3-slice-5.diff`. **Publishing v3 publishes slice 6's and
  7 September's changes too.**
- The three drafts were written back with LF line endings; the published fleet-naming v4 had CRLF, so a raw
  version comparison shows every line changed. The diffs above ignore that.

### Sentences that cannot be made true without a code change (left for the Architect)

1. **move-session (built-in), step 2 and step 5** tell the mover to MESSAGE the source (for its handover)
   and the target (late facts), and to message the source before closing it. The mover is usually neither
   the source's owner nor its worker, so the Gateway refuses those messages since slice 1. Needs a design
   decision (a file-based handover request, or a relationship for moves). Not edited.
2. **director-restart, steps 2, 3 and 5** message every mission's senior seat and every drained session. A
   restarting session that did not start them is refused. Written into the draft as a KNOWN GAP; the drain
   needs a redesign (the Director's own drain, or the owner's screens).
3. **The action catalogue has no `session-report` or `session-raise` entry**, although the preamble now
   teaches both. Adding catalogue entries changes the pinned `actions --json` shape; left out as not "words".
4. **The doorbell's deferral reason is the wire literal `parked`**, while the terminology skill says "say
   snooze, not parked". Different idea (a line left in the composer), same word. Renaming it is a wire
   change on both sides.
5. **`FleetMessaging.BuildFramedMessage`** (unused since slice 1) and its doc comment still describe the old
   framed, typed message and `message ask`. Dead code; removing it is a code change.
6. **The skill index line in every preamble** comes from the Gateway's skill store. Whether the seeder
   republishes a changed built-in SUMMARY (not only the body) on deploy was not checked.
7. **The preamble this session received** still says "Every message you send interrupts the receiving
   agent" and offers `message ask`: it comes from the installed Director, and changes only when a Director
   built from this branch is released.

### What is proven

- The golden preamble test failed on the new template, the approved file was regenerated from its
  `.received.txt`, and it passed. With the template put back to `HEAD`, the new preamble test, the reworded
  command test and the Pi test all went red (3 failed), and passed again when restored.
- The retired-words guard: eight breaks, each red, then restored green - "message ask" appended to the
  preamble template; "interrupts the receiving" to the shipped skill; "truncate at the first newline" to the
  workflow; "interrupts the session that receives" to the plugin skill; "--controlled-by <session-id>" to
  the command reference; "message ask" to `cli.py`; the workflow file removed (the presence check); and
  "MESSAGES ARE RARE" lowered in the preamble (the presence check).
- The command-line help tests: with `cli.py` and `session_ops.py` put back to `HEAD`, 5 of the 6 new tests
  went red (the sixth, "no `message ask` command", was already true since slice 1 and is a control). The
  actions pin failed on exactly the two reworded descriptions before it was regenerated; the "spawn self
  outside" row failed on the new advice before it was updated; the one-line summary test failed on a
  92-character selftest summary, which was shortened.

### What is NOT proven

- **No agent read the new words.** No live session was started with the new preamble, and nobody followed
  the rewritten skill or workflow.
- **The built-in path to the fleet**: the new fleet-comms, terminology and mission bodies reach agents only
  when the Gateway is deployed; that was not done (not this seat's).
- **The three drafts** were not read by any agent and are not published.
- **`FleetMessaging.md` against this branch alone**: the parked-line and duplicate-key details describe the
  slice 2 and slice 3 fix-round branches, not yet merged here.
- **Suites not run in full**: the full Core tests, the full Gateway unit and route suites, the web tests, and
  `scripts/test-local.ps1` (no PowerShell on the Mac). Only the suites below.
- **Colour terminal**: under `TERM=dumb FORCE_COLOR=1` one command-line test fails,
  `test_message_queue.py::test_broadcast_sentences_are_printed_verbatim[colour terminal]`. That is the
  defect slice 3's fix round (ruling 3) fixed on `mission/message-load-slice3`, not merged here; nothing in
  this slice touches it.

### Test totals (Mac, this tree, 17 September 2026)

- **Core tests**, filter `FleetPreamble|PiPreambleWriter|InjectedText|WorkflowIndexStore`: 95 passed, 0
  failed (94 before the new test).
- **Core unit tests**, whole project: 558 passed, 0 failed (555 before, plus the 3 guard tests).
- **Gateway unit tests**, filter `Skill|Workflow|Preamble`: 114 passed, 0 failed.
- **Gateway route tests**, filter `Skill|Workflow|Preamble`: 52 total, 49 passed, 1 skipped (PostgreSQL),
  **2 failed - the two `WorkflowSeatTests` already in the known 16** (`An_unseated_spawn_isUnaffected...`,
  `A_gateway_resolved_seat_isStamped...`).
- **cc-devthrottle tests** (scratch environment with `cc_storage`, `cc_shared` and the tool installed):
  3231 passed, 0 failed; with `TERM=dumb FORCE_COLOR=1`, 3230 passed and the 1 colour failure above.

## State after slice 5 (17 September 2026)

- Slice 5 head is this commit on `mission/message-load`, awaiting inspection. No pull request opened.
- Drafts pushed and NOT published: fleet-naming v5, checks-that-fail-open v9, director-restart v3 (which also
  carries slice 6 and 7 September). fleet-comms and the mission workflow are built-ins: their change is the
  shipped files and goes out with the Gateway deploy.
- Open for the Architect: the seven sentences above that need code, above all move-session and the
  director-restart drain, which message sessions the gate now refuses.
- Next: the merges of the slice 2 and slice 3 branches, the record, the release.

## Landing plan (Architect, 17 September 2026, 09:10)

Every slice is built. Branches and heads:
- `mission/message-load-slice2` (doorbell, three fix rounds) - awaiting inspection 10, narrow.
- `mission/message-load-slice3` (replies, one fix round) - awaiting inspection 9.
- `mission/message-load-slice6` (restore, one fix round) - awaiting inspection 11.
- `mission/message-load` (also `-slice4` at `c89a5d7d`, `-slice5` at `d1ded8f1`): row line and words -
  awaiting inspection 12 (both, one review).
Inspections wait on Codex (limit resets 12:51) unless the owner provides another reviewer.

Two pull requests, not five. Reason: the later branches were cut from the slice 2 head BEFORE its fix
rounds, so a squash-merge of slice 2 followed by a squash-merge of any later branch would revert the
slice 2 fixes unless every later branch is rebased first; main moves several times an hour and each
check run takes an hour and a half. The inspections were done per slice, which is what slicing is for.
1. **Pull request A**: `mission/message-load-slice2` rebased onto main, after inspection 10 passes.
2. **Pull request B**: the mission branch with the slice 3 and slice 6 fix branches merged in (an
   integration Manager merges, resolves, runs every suite, rebases onto main after A lands), after
   inspections 9, 11 and 12 pass.
Then: publish the fleet-comms and mission drafts if any exist on the Gateway (slice 5 found the shipped
files are the source), publish the director-restart skill draft v3, cut the release, write the report.

## Integration (17 September 2026, integration Manager seat 2)

The slice 2, slice 3 and slice 6 fix branches merged into `mission/message-load`. The slice branches were not
touched. No pull request opened, no fleet message sent, nothing published.

### The three merge commits

1. `fb42fcf6` - `origin/mission/message-load-slice2` (made by the previous integration Manager, whose account hit a
   spend limit before it built or tested anything).
2. `acdf35c7` - `origin/mission/message-load-slice3`.
3. `0f81d590` - `origin/mission/message-load-slice6`.

Then `11959d58`, the fix for the one failure a merge caused (below).

### Every conflict and how it was resolved

- **Slice 2, `handoff.md`**: both sides appended sections; every section kept, ordered by when it was written.
- **Slice 2, `FleetMessageStore.MarkStuckWithNotices`**: the mission branch had moved notice staging into
  `StageSystemNotice`; slice 2's body (inspection 5, ruling 2: the verdict tells a duplicate from a refusal) was
  taken as written.
- **Slice 3, `handoff.md`**: the previous Manager had already written the union but not staged it. Checked, not
  trusted: against the slice 3 head the only line not present is judgement call 5's old wording, which slice 2
  fix round 2 corrected (inspection 5, ruling 3); against the mission branch nothing is missing.
- **Slice 3, `FleetMessageStore`** (auto-merged, plus an unstaged change the previous Manager left): the overdue
  path's `JudgeOverdueNotice` and the stuck path's inline copy of the same judgement are now one helper,
  `JudgeSystemNotice`, and the unused `StageSystemNotice` is gone. Same logic for both paths: queued writes the
  mark and the notice; a duplicate writes the mark and no second notice; any other refusal leaves the row open
  for the next sweep (inspection 5 ruling 2, inspection 6 ruling 1). Kept, and committed inside the merge.
- **Slice 6, `handoff.md`**: union, ordered by commit time: inspection 7 rulings after inspection 6 rulings; the
  slice 6 fix round and its state note after slice 2 fix round 3 and before slice 5. The same corrected judgement
  call 5 line is the only branch line not kept.
- **Slice 6, `GatewayEndpoints.Map` parameter list**: slice 4 added `inboxLines`, slice 6 fix round added
  `workspaces`, both as the last optional parameter. Both kept (`inboxLines`, then `workspaces`); every caller
  names its arguments, so nothing else changed.
- **Auto-merged, checked**: for each file both sides touched (`Session.cs`, `FleetMessageRequests.cs`,
  `FleetDoorbell.cs`, `FleetDoorbellTests.cs`, `FleetMessageLimits.cs`, `FleetMessagePolicy.cs`,
  `FleetMessageService.cs`, `session_ops.py`, `GatewayHost.cs`, `cli.py`, `docs/cli-reference.md`, the actions
  fixture), every line the branch added is present in the merged file, except the notice-judge lines folded into
  `JudgeSystemNotice` above. Every file only the branch touched is identical to the branch head. No conflict
  markers anywhere (`git diff --check` clean; no `<<<<<<<` under src, tools, packages, docs/missions).

### The one failure a merge caused, fixed

`FleetMessageReplyStoreTests.A_notice_the_policy_refuses_leaves_the_question_open_and_the_next_sweep_retries`
(slice 3 fix round) built limits with a 10-character text cap; slice 2 fix round 3 (inspection 8, ruling 2)
refuses any cap below 45, so the test threw before reaching the store. It now uses
`FleetMessageLimits.MinTextLength` and a notice one character longer, so the policy still refuses the notice.
Watched failing: with the overdue refusal branch changed to write the mark, it and
`A_blank_notice_is_refused_and_leaves_the_question_open` went red (`Assert.Empty() Failure`); restored, 46 of 46
green.

### The actions fixture

Not regenerated: `test_actions_json_is_unchanged` passes against the real `actions --json` output on the merged
tree (88 actions, `message-reply` and `director-restore` both present), so the auto-merged pin is already what the
tool prints.

### Test totals (Mac mini, merged tree, 17 September 2026)

- **Build**: Core unit tests, Core tests, Gateway unit tests, Gateway route tests, Avalonia tests and the Avalonia
  app projects all build with 0 errors. The whole solution cannot build on a Mac (`CcClick` and
  `CcDirector.Terminal` target Windows).
- **Core unit tests**: 583 passed, 0 failed.
- **Core tests**: 4447 total, 4378 passed, 8 skipped, **61 failed - every one by name in
  `slice-2-evidence/fix-round/full-suite-failures.txt`**. No new one.
- **Gateway unit tests** (after the fix): 5422 total, 5407 passed, 8 skipped, **7 failed - the known 7 Mac-only**
  (CronJobStore 1, RuleCandidateFilter 1, RulePrimitives 1, SessionCommandExecutorLiveness 3,
  WorkListStorePersistence 1). Before the fix: 8, the eighth being the merge-caused failure above.
- **Gateway route tests** (full suite, 20 minutes, merged tree before the test-only fix, which is not in this
  project): 2604 total, 2536 passed, 52 skipped, **16 failed - every one in the known list** (ContextLessRouteCensus
  1, FleetSpawnMissionAttach 2, FleetSpawnOrigin 4, GatewayTestSuiteLock 2, HostedProcessControlDeny 2,
  TunnelRosterPushReadProof 3, WorkflowSeat 2). No new one.
- **cc-devthrottle** (scratch virtual environment with the local `cc_storage`, `cc_shared` and the tool, plus
  pytest): **3241 passed, 0 failed**; with `TERM=dumb FORCE_COLOR=1`: 3241 passed; with `FORCE_COLOR=1` alone:
  3241 passed. The colour failure slice 5 recorded is gone, as slice 3's fix round intended.
- **Web**: typecheck clean on every workspace; mobile and Cockpit `vite build` succeed. Tests: client-core 1226
  total, 23 failed; Cockpit 354 total, 24 failed; phone 81 total, 30 failed; cc-assistant 106 passed. The 77
  failing names are exactly those in `slice-4-evidence/web-failures-before-and-after.txt`; no merge touched
  `packages` or `apps`.

### What is NOT proven

- **Avalonia tests** were built but not run (not asked; no merge touched the desktop app).
- **`scripts/test-local.ps1`** not run (no PowerShell on the Mac); every suite above was run directly.
- **PostgreSQL**: nothing ran against it; every store ran on SQLite.
- **Live**: no Director, agent, hosted Gateway or phone was used. Nothing here re-proves a slice's guards beyond
  the suites passing; only the one repaired guard was watched failing again.
- **Windows and Linux**: not run.
- **The interaction of the three fix rounds** is covered only by the unit and route suites; no test exercises a
  restore (slice 6) and the doorbell or replies (slices 2 and 3) together.
- **Rebasing onto main** (landing plan, pull request B) is not done; this is the merged mission branch only.

## Landing plan, revised (Architect, 17 September 2026, after integration)

The mission branch now holds every slice with every fix round (integration section above). One pull
request lands the rest of the mission from this branch; the separate slice 2 pull request is dropped,
because it would cost a second hour-and-a-half check run for code that is already integrated and
inspected per slice. Order of work:
1. A Manager merges origin/main into this branch and reruns every suite (main has moved a long way).
2. Two inspections (Codex, from 12:52) at the merged head, in throwaway worktrees, covering exactly
   what no inspector has yet passed: (A) slice 2 fix round 3, the slice 3 fix round, the integration
   merges and the merge with main; (B) the slice 6 fix round, slice 4 (the row line) and slice 5 (the
   words).
3. Fix rounds if they fail; then the pull request, merged the moment its checks are green.
4. Publish the director-restart skill draft, cut the release, write the report.

## Merge with main (17 September 2026, merge Manager seat)

Merge commit `93e3f573` brings `origin/main` (`ef411aa0`, 10 commits past the last merge base `dc6d6547`: dev reports
phase 2, the Wingman stops route and Cockpit tab, the Wingman narration call, cc-secrets settings and edit,
credentials from cc-secrets, pull-request-scoped continuous integration jobs, and the AXI mission record) into
`mission/message-load`. Then `f21cb495`, the regenerated client schema. No pull request opened, no fleet message
sent, nothing published.

### What conflicted, and how it was resolved

Only two files conflicted. Main did not touch `cli.py`, `session_ops.py`, `machine_ops.py`, the actions fixture, the
preamble template or its approved file, the shipped skills, or `docs/new_architecture/sessions.html`.

- **`GatewayHost.cs` and `GatewayEndpoints.cs`, the display fold.** Main moved every input of the display push's fold
  into one class, `Fleet.DisplayFold` (so the Wingman inspector's trace colour and the push cannot name different
  inputs), and added a `writes` parameter to `StampFleetRolesAndFold` and `EnrichVoiceThenFoldForPush`. Slice 4 had
  added `inboxLines` to the same two parameter lists and to the push's inline lambda. Resolved:
  - Both parameters kept on both methods (`inboxLines`, then `writes`); the inner call passes both.
  - The push's inline lambda is main's `_displayFold.Push(...)`.
  - `DisplayFold` gains an optional `Func<IFleetInboxLineSource?> inboxLines` (read at fold time, because the store is
    built later in the host's constructor); the host passes `() => _fleetMessages`.
  - **Judgement call for the inspector:** only the push stamps the row line (`inboxLines: writes ? ... : null`). A
    trace records the colour and label only, and slice 4 ruling 12 says the row line reads nothing they read, so the
    trace skips a database read it has no use for. This is a difference between the two callers beyond the three
    main's class comment lists; the class comment now names it.
  - Proven: with the push's source replaced by null, `FleetMessageRouteTests.The_roster_the_session_read_and_the_desktop_push_carry_the_row_line_until_it_is_read`
    went red (`Assert.NotNull() Failure`); restored, 42 of 42 green. Main's `DisplayFoldTraceMatchesPushTests`
    (only `DisplayFold` calls the push fold) passes.
- **Auto-merged, checked by reading main's side:** `SessionKeyGuard.cs` and its tests (dev-reports shapes and the
  wingman-stops refusal; no overlap with the fleet message shapes), `GatewayDbContext.cs` and both model snapshots
  (dev-report tables and the trace row colour beside `fleet_messages`), `SessionManager.cs` (one doc comment),
  `docs/cli-reference.md` (cc-dev-reports and cc-secrets sections only).
- **Migrations:** main's `AddDevReports` and `AddTurnVerdictTraceRowAndClock` sort after this branch's
  `AddFleetMessageReplyMarks`; the merged snapshots carry both. Their designer files do not carry each other's
  entities, which EF does not read at apply time. Not run against PostgreSQL.
- **Retired words:** main added no text that offers `message ask`, says a message interrupts, or offers
  `--controlled-by <another id>` on any surface this mission owns. The only hits are inside other missions' records
  under `docs/missions/` (the AXI and dev-reports missions), which are history and were left alone. The retired-words
  guard passes.

### Pinned files

- **Client schema** regenerated with `openapi-typescript` from the document an in-process Gateway served (a scratch
  console host on an operating-system port, home directory redirected to a scratch folder). Additions only, 343
  lines: main's `wingman-stops` and `dev-reports` routes, and slice 6's `/gateway/workspaces/{id}/restore/marks`, which
  the committed file had never carried. Typecheck clean after.
- **Actions fixture** and **preamble approved file**: not regenerated, because their pin tests pass against real
  output on the merged tree (`test_actions_json_is_unchanged`; the golden preamble test is not among the Core test
  failures). Main touched neither source.

### Test totals (Mac mini, merged tree, 17 September 2026)

- **Build**: Core unit tests, Core tests, Gateway unit tests, Gateway route tests, ControlApi, Avalonia app and
  Avalonia tests: 0 errors.
- **Core unit tests**: 594 passed, 0 failed.
- **Core tests**: 4447 total, 4378 passed, 8 skipped, **61 failed (56 methods) - every one by name in
  `slice-2-evidence/fix-round/full-suite-failures.txt`**. No new one.
- **Gateway unit tests** (hold the ControlApi drain and restore tests): 5586 total, 5571 passed, 8 skipped, **7
  failed - the known 7 Mac-only** (CronJobStore 1, RuleCandidateFilter 1, RulePrimitives 1,
  SessionCommandExecutorLiveness 3, WorkListStorePersistence 1).
- **Gateway route tests** (full, 21 minutes): 2626 total, 2558 passed, 52 skipped, **16 failed - the known 16**
  (ContextLessRouteCensus 1, FleetSpawnMissionAttach 2, FleetSpawnOrigin 4, GatewayTestSuiteLock 2,
  HostedProcessControlDeny 2, TunnelRosterPushReadProof 3, WorkflowSeat 2).
- **cc-devthrottle** (fresh scratch virtual environment with the local `cc_storage`, `cc_shared`, the tool and
  pytest): 3241 passed, 0 failed; with `FORCE_COLOR=1`: 3241 passed; with `TERM=dumb FORCE_COLOR=1`: 3241 passed.
- **Web**: typecheck clean on every workspace; mobile and Cockpit `vite build` succeed. Tests: client-core 1239
  total, 23 failed; Cockpit 355 total, 24 failed; phone 81 total, 30 failed; cc-assistant 106 passed. The 77 failing
  names are exactly those in `slice-4-evidence/web-failures-before-and-after.txt`; the new totals are main's Wingman
  tab and stops tests, all passing.

### What is NOT proven

- **Avalonia tests** built, not run.
- **`scripts/test-local.ps1`** not run (no PowerShell on the Mac); every suite above was run directly.
- **PostgreSQL**: nothing ran against it, including the interleaved migrations.
- **Live**: no Director, agent, hosted Gateway or phone was used.
- **Windows and Linux**: not run.
- **Main's dev-report delivery against this mission's rules**: dev reports type the OWNER's notes into a session at
  its turn end. That is the owner's typing, which this mission leaves to him, so no conflict was assumed; no test
  exercises a dev-report delivery and a fleet doorbell on the same session.
- **cc-dev-reports and cc-secrets tests** (main's tools, not this mission's) were not run.

## Architect rulings on inspections 10 and 11 (17 September 2026) - the final fix round

Inspection 10 (messaging core): PASS. Its two notes are accepted as stated limits.
Inspection 11 (restore, row line, words): FAIL. Findings accepted. Rulings:

1. **A restore mark comes only from the Director that holds the lease** (high). The mark route binds
   the caller's credential to the Director it names: the Gateway resolves which registered Director the
   authenticated workstation credential belongs to, and refuses 403 any mark whose `directorId` is not
   that Director, or whose Director does not hold the workspace's lease. A `restored` mark must carry
   the started token the same Director wrote; a mismatch is refused. `finished` releases the lease only
   for its holder. Guards: two workstation keys in one account, the second key's mark, restored id,
   failure, token and `finished` all refused while the first holds the lease; the holder's own marks
   accepted; a `restored` mark without the matching token refused.
2. **The law page says one thing** (medium). `docs/new_architecture/sessions.html` near line 1657: the
   "surviving" ask-and-wait paragraph is rewritten in the past tense with the date it was removed and
   what replaced it (`--reply-wanted`, `message reply`).
3. **Current-tense text teaches the current product** (medium). Fix: the comment in
   `src/CcDirector.Gateway.Contracts/FleetMessaging.cs` (and delete `BuildFramedMessage` and its tests if
   nothing calls it any more; say which); `packages/client-core/src/sessions/tree.ts` and
   `src/CcDirector.Gateway.Contracts/SessionTree.cs` (who may be named as owner); `tools/cc-ship/src/fleet.py`
   (newline truncation); `ARCHITECT-HANDOVER.md`; the active briefs under `missions/` that say a message
   interrupts. Dated historical plans and reviews (for example `docs/MISSION-source-control-tab-2026-07-23.md`)
   are history and are left alone. The retired-words test's inventory gains `sessions.html` and every
   file fixed here, and the inspector's search expression becomes a second test over the tree with an
   explicit, commented list of historical files that are exempt.
4. **Main's collation census line** (from `postgres-proof-2.md`): add `("fleet_manager_marks", "SessionId")`
   to the hand-kept list in `PostgresProviderProofTests`, with a comment that it is main's column from
   pull request 2997, so the census runs to its end on this branch. One line; it cannot be run on the
   Mac, say so.

Then the touched suites, each guard watched failing, a 'Final fix round' section here, push, stop.
Inspection 12, narrow, then the pull request.

## Final fix round (17 September 2026, Manager seat for the final fix round)

On `mission/message-load`. Commits `60673999` (item 1), `eb945bbb` (item 2), `7703087d` (item 3), `9f2f7b46`
(item 4); this record in the commit after. No pull request opened, no fleet message sent, nothing published.

### What changed, per item

**1. A restore mark comes only from the Director that holds the lease.** The Gateway already bound a Director
id to a credential: the Director hub's `Hello` runs on the connection its device key authenticated, and binds
the id to that connection. That binding was never kept anywhere a route could read it. Now `DirectorHub.Hello`
passes `AuthMiddleware.RegisteringCredential(ctx)` (`device:<device id>` for a device key, `machine-token` for a
self-hosted shared token, null for a session key) to `DirectorRegistry.RegisterFromStream`, which keeps it per
(tenant, Director id), re-binds it on a reconnect, drops it when a registration names none, and clears it with
the entry. `DirectorRegistry.IsRegisteredByCredential(tenant, id, credential)` is the one question. No new
identity was invented: it is the device id of the key the hub already authenticated.
- `POST /gateway/workspaces/{id}/restore/marks` refuses 403 any mark whose `directorId` is not the Director
  the caller's credential said Hello as, before the store is asked. The store's lease check (409) still follows.
  So a second workstation key cannot write `started`, `restored`, `failed` or `finished`, and cannot release
  the lease.
- A `restored` mark must carry the token of the seat's start, and that start must be the same Director's:
  no token 400, another token or another Director's start 409. `DirectorRestore` now sends the token on the
  `restored` mark.
- The same binding on the spawn door: a `restoreClaim` on `POST /directors/{id}/sessions` is refused 403 unless
  the credential is Director `{id}`'s, and `WorkspaceStore.RecordRestoredByClaim` now takes the Director id and
  records only when the seat's start was that Director's. (The ruling named the mark route; the claim is the
  other writer of `restoredSessionId`, so it got the same rule.)
- Guards: route `A_second_workstation_key_of_the_account_cannot_write_any_mark_under_the_lease_holders_name`
  (a second workstation key enrolled in the same account; restored with the right token, failed, started,
  finished all 403; nothing stored changed, the lease still held; then the holder's own restored and finished
  accepted and the lease released), `A_restored_mark_without_the_token_its_Director_started_the_seat_with_is_refused`,
  `A_restore_claim_from_a_second_workstation_key_is_refused_and_nothing_reaches_the_Director`. Unit
  `DirectorRegistryCredentialBindingTests` (3), `WorkspaceRestoreMarksTests` +2.

**2. The law page.** `docs/new_architecture/sessions.html` row 10 is in the past tense: the ask-and-wait verb
"was", "REMOVED 16 September 2026" by this mission on the owner's ruling of that day, the Gateway refuses
`waitForIdle` with 400, and what replaced it is `message send ... --reply-wanted`, `message reply`, and the
inbox. The row keeps the history and its lesson.

**3. Current-tense text.**
- `FleetMessaging.BuildFramedMessage` had no caller (only its four tests). Deleted with its tests; the class
  keeps `ShortId`, which the Gateway's messaging log lines use, and its summary says why the framing went.
- `SessionTree.cs` and `tree.ts`: "the owner named at spawn may live on any Director" instead of
  "--controlled-by takes any session id". `tools/cc-ship/src/fleet.py`: the newline sentence is gone.
  `ARCHITECT-HANDOVER.md`: the one-line reply is kept for the file-pointing reason and dated as corrected.
- `missions/stop-a-session/`: fourteen briefs rewritten (both phrases, including five sentences wrapped across
  two lines that a line-by-line search does not see). Their `architect-state.md` still says ACTIVE though that
  mission merged 9 September (#2799); left alone, it is that mission's record.
- Found beyond the inspector's list: `.claude/skills/agent-expert/agents/` (README, claude-code, codex, grok, pi)
  taught the ask as a live, Claude-only feature. The inspector's `rg` skips hidden directories, so it never saw
  them. Rewritten. `FleetMessageService.cs` had "the message asks for a reply", a false hit, reworded.
- `RetiredMessagingWordsTests`: the inventory gains `sessions.html`, every file fixed here, the stop-a-session
  briefs and the agent-expert files; the phrases gain the two wrapped variants and "--controlled-by takes any";
  matching now collapses whitespace so a wrapped sentence is found. New test
  `Nothing_in_the_repository_outside_the_named_history_uses_the_retired_messaging_words` walks every text file
  (about 5,700; it fails if it reads fewer than 1,000) with the inspector's expression, and `TreeExemptions` lists
  each exempt path with its reason: dated history (`docs/missions/`, `docs/plans/`, `docs/reviews/`, the two dated
  and two finished `docs/MISSION-*` files, `PHASE-2-REPORT.md`, the token study) and code or tests that name
  the words to refuse or forbid them. `The_tree_scan_reads_the_files_this_round_fixed_and_every_exemption_exists`
  fails on an exemption that names nothing.
- Judgement call: in `sessions.html` the removed verb is named as "the `ask` subcommand of `cc-devthrottle
  message`" rather than the two words together, so the page can be in the inventory without an exemption. It
  still says exactly what was removed.

**4. Census line.** `("fleet_manager_marks", "SessionId")` added to the hand-kept collation list in
`PostgresProviderProofTests`, with a comment that it is main's column from pull request 2997. Not run: there
is no PostgreSQL on the Mac (the test is skipped here).

### Red, then green (every mutation restored; `git status` clean after each)

| Mutation | Red |
|---|---|
| Mark route binding check disabled | route `A_second_workstation_key...`: expected Forbidden, actual OK (the forged `restored` mark naming X accepted) |
| Store: `restored` token and Director checks disabled | unit `RecordRestoreMark_ARestoredMarkWithoutTheStartsToken_IsRefused`; route `A_restored_mark_without_the_token...`: expected BadRequest, actual OK |
| Spawn door claim binding and the claim's Director check disabled | route `A_restore_claim_from_a_second_workstation_key...`: expected Forbidden, actual Created; unit `RecordRestoredByClaim_ForADirectorThatDidNotStartTheSeat_RecordsNothing` |
| Hub records another credential for every Hello (control: the holder's own path depends on the binding) | 7 of 17 route tests red, the holder's marks Forbidden |
| `DirectorRestore` omits the token on `restored` | 20 of 36 `DirectorRestore` unit tests red |
| Old `sessions.html` | both word tests red at `sessions.html:1657: "message ask"` |
| Old `FleetMessaging.cs` | both red at `FleetMessaging.cs:37: "message ask"` |
| Old `tree.ts` and `fleet.py` | both red at `tree.ts:44` and `fleet.py:117` |
| Old `brief-qa-report.md` (sentence wrapped over lines 84-85) | both red at `:84 "truncate at the first newline"` - the whitespace collapse is what finds it |
| `docs/plans/` exemption removed | tree test red, `docs/plans/...html:148: "message ask"` and on |
| An exemption renamed to a path that does not exist | the exemption test red: "names nothing in the repository"; the tree test red at `PHASE-2-REPORT.md:86` |

### Totals (Mac, this tree, after the last change)

- Gateway unit, whole project: 5591 total, 5576 passed, 8 skipped, **7 failed - the same 7 Mac-only failures**
  (CronJobStore 1, SessionCommandExecutorLiveness 3, RuleCandidateFilter 1, WorkListStorePersistence 1,
  RulePrimitives 1). No new one. Filter `DirectorRestore|DirectorDrain|SessionKeyGuard|Workspace|DirectorRegistryCredentialBinding`: 498 passed.
- Gateway route: `WorkspaceRestoreRouteTests` 17 passed (was 14); filter
  `WorkspaceRestoreRouteTests|FleetMessage|Workspace|SessionServingReadIsolation|DirectorHub` 82 passed;
  `FleetMessagingFramingTests` 1 passed (was 5; four deleted with the helper).
- Core unit `RetiredMessagingWords`: 5 passed (was 3).
- cc-ship (scratch virtual environment with local `cc_storage` and `cc-ship`): 171 passed. cc-devthrottle not
  touched, not run.
- Web typecheck on every workspace: clean (only a comment in `tree.ts` changed).

### What is NOT proven

- No live Director. The binding is proven on a booted hosted Gateway with fake tunnel Directors on real device
  keys; the self-hosted shared-token path (`machine-token`) is not exercised by any test.
- Several Directors on one machine share that machine's device key, so one of them can still write marks naming
  another. The lease and the start token are what separate those; the route comment says so.
- A Director registered only from an instance file or the old HTTP registration carries no credential, so its
  marks are refused. Nothing current restores that way; stated, not tested beyond the registry unit test.
- The start token is readable by any caller of the account that can read the workspace, so the token check proves
  "this Director's start", not a secret. The credential binding is what keeps other keys out.
- The collation census line: not run (no PostgreSQL on the Mac).
- The tree scan does not join a phrase wrapped across comment markers (`///` lines), reads only the listed text
  extensions, and skips `bin`, `obj`, `node_modules`, `dist` and virtual environments.
- `scripts/test-local.ps1` not run (no PowerShell on the Mac); the full Gateway route suite was not run, only the
  filters above.

## State after the final fix round (17 September 2026)

- All four rulings on inspections 10 and 11 are built and pushed on `mission/message-load`. Next: inspection 12
  (narrow), then the pull request - the Architect's.

## Second merge with main (17 September 2026, second merge Manager seat)

Merge commit `5fcc7ae9` brings `origin/main` (`00e184a9`, five commits past the last merge base `ef411aa0`)
into `mission/message-load`; then `4f89ede4`, the regenerated client schema. Main's five commits are the
Gateway telling the Fleet Manager what its sessions did with its conduct shipping as a built-in
(`00e184a9`), the Cockpit Wingman tab (`42677ed0`), the narration needing a Pro account (`cd8b6233`),
Dev Reports phase 3 (`8d21335f`), and the mission workflow's "whoever starts a session stops it" rule
(`0b8e11f6`). No pull request opened or touched, no fleet message sent, nothing published.

### What conflicted, and how it was resolved

Four files conflicted. Main did not touch `session_ops.py`, `machine_ops.py`, the actions fixture, the
preamble template or its approved file, `fleet-comms.skill.md`, or `docs/new_architecture/sessions.html`.

- **`docs/cli-reference.md`** - the command summary block. Main rewrote the `session report` line into two
  lines ("... at the end of your turn (sends nothing when a Fleet Manager owns you: the Gateway tells it)");
  slice 3 had added a `session raise` line after it. Resolved: main's two lines, then this mission's `session
  raise` line. The merged `cli.py` docstring says both things in the same order, so the document and the tool
  agree. That block is hand-written prose, not a capture of `--help` - the real `--help` prints a Typer table
  with a different shape - so there was nothing to regenerate; it was resolved by reading, not by pinning.
- **`src/CcDirector.Gateway.Tests/TestEnvironment.cs`** - the module initialiser. Both sides turn off a
  process-wide sweep so a host test decides when it runs: this mission's `GatewayHost.FleetDoorbellHeartbeatEnabled`
  and main's `Fleet.FleetManagerEventSweep.Enabled`. Resolved: both lines, both comments.
- **`src/CcDirector.Gateway/Api/GatewayEndpoints.cs`** - four sites, all the same shape. Slice 4 had added
  `inboxLines` to `StampFleetRolesAndFold` and main's step 4 added `fleetManagerMark`; the signature itself
  auto-merged and carries `inboxLines`, `writes` and `fleetManagerMark`. Resolved: every call site passes both
  new arguments - the roster read and the single-session read pass `inboxLines` and the tenant's Fleet Manager
  mark, and `FoldedAccountRoster` takes both as optional parameters and forwards both.
- **`src/CcDirector.Gateway/GatewayHost.cs`** - the Fleet Manager digest's `FoldedRoster`. Resolved: it passes
  `_fleetMessages` (this mission's row-line source) and `_tenantSettingsResolver.FleetManagerSessionId` (main's
  mark).

**`Fleet.DisplayFold` needed no change.** Main's own `DisplayFold` passes no Fleet Manager mark - the display
push stamps `OwnedByFleetManager` false, which is main's behaviour, not a loss - so the class keeps exactly the
`inboxLines` wiring the first merge gave it, and main's `DisplayFoldTraceMatchesPushTests` passes.

**Auto-merged, checked by reading main's side:** `SessionKeyGuard.cs` (main adds only the two Fleet Manager
event shapes; the agent-input refusal above the allow list is untouched), `GatewayDbContext.cs` and both model
snapshots (main's `fleet_manager_events` tables beside this branch's reply-mark columns on `fleet_messages`;
both are in both snapshots), `tools/cc-devthrottle/src/cli.py` (the `session report` docstring keeps this
mission's "QUEUED in their inbox, never typed into them" and gains main's Fleet Manager paragraph),
`.claude/skills/mission/SKILL.md` and `src/CcDirector.Gateway/Workflows/Content/mission.instructions.md`,
`tools/cc-devthrottle/tests/test_axi_step_6c_help_and_errors.py` (main added its own new actions to
`_ACTIONS_ADDED_SINCE_PIN`).

**Migrations:** main's `AddFleetManagerEvents` and `AddFleetManagerEventDelivery` (20260917110000/110100
SQLite, ...09/...109 PostgreSQL) sort after this branch's `AddFleetMessageReplyMarks` (20260917094429). Not
applied against PostgreSQL.

### The mission workflow text, where main and slice 5 both wrote (watch item a)

The auto-merge is exactly right and was verified by diffing the merged file against BOTH sides. Main's new
rule - "Whoever starts a session stops it the moment its work is done" - is present in full; so is every
sentence slice 5 rewrote (the review goes in a file rather than a message, `session report` and `session raise`
in the Worker's bullets, and "Messages are rare, and they queue" with the six-an-hour limit, the doorbell, and
`--reply-wanted`/`message reply`). Both the shipped
`src/CcDirector.Gateway/Workflows/Content/mission.instructions.md` and the `.claude/skills/mission/SKILL.md`
copy carry both. The two files' only differences are the two that existed before this merge on both sides (the
copy says "THIS FILE" and "a link to this file" where the shipped text says "THIS WORKFLOW" and names the
`workflow instructions` command).

### Main's built-in Fleet Manager conduct, reworded (watch item b)

`00e184a9` ships a new built-in, `src/CcDirector.Gateway/Skills/Content/fleet-manager.skill.md` (283 lines).
It had two sentences this mission retired, both in its "Messages are rare" section, and both are rewritten:

- "Every word sent into a session interrupts it" is now "Reaching into a session costs it, and the owner has
  ruled that it must be rare", followed by what the product actually enforces: a message is never typed into a
  session while it works, the Gateway queues it, the recipient's Director rings one doorbell line when the
  session is free, and one session may send at most six messages an hour.
- "You never use `message send` or `message ask` for routine coordination" loses the retired verb and gains
  the replacement: "Nobody waits for an answer either: a question goes with `--reply-wanted` and its answer
  arrives in your inbox."

Nothing else in the file offers `message ask`, says a message interrupts, or offers `--controlled-by` with
another session's id - its four `--controlled-by` uses are all `self`, which this mission allows.
**Nothing was added to any exemption list**: all five `RetiredMessagingWords` tests pass, including the
whole-tree scan and the exemption-existence test, with `TreeExemptions` exactly as the final fix round left
it. A scan of every one of the 283 files main changed found the two retired phrases only in
`GatewayEndpoints.cs`, which is exempt because it names the retired verb in order to refuse it.

**Red, then green, on the reword** (the guard was watched failing, and the tree is clean afterwards): with
main's original `fleet-manager.skill.md` put back, both word tests went red at
`src/CcDirector.Gateway/Skills/Content/fleet-manager.skill.md:222: "message ask"` - the whole-tree scan and the
taught-inventory scan, each naming the line. The reworded file was restored, `git status` was clean, and all
five passed again.

### Main's new routes against ruling 17 (watch item c)

Main added exactly two Gateway routes, both under the Fleet Manager prefix: `GET
/gateway/fleet-manager/events` and `POST /gateway/fleet-manager/events/ack`. That is the whole set - the
regenerated OpenAPI document gained only those two paths, and they are the only route registrations in main's
diff of `src/CcDirector.Gateway`. Read against the inspection-10 census:

- `ListEvents` reads the event store and a delivery note and returns them. No prompt.
- `AcknowledgeEventsAsync` marks events acknowledged and returns counts. No prompt.
- Both are restricted further than the guard: only the account's marked Fleet Manager session may call them.
- `SessionKeyGuard.IsAgentInput` is unchanged and still runs BEFORE the allow list, so `POST
  /sessions/{sid}/prompt`, `interrupt`, `escape`, `fanout` and the verdict answer stay refused to every
  session key. `SessionKeyGuard` tests: 262 passed.

**No new route lets a session key put text into a session, so there is nothing to stop for.** One honest
note, recorded rather than waved past: main's event DELIVERY does type a prompt, but it types into the marked
Fleet Manager's OWN session, it is driven by the Gateway's event sweep reacting to a session stop or death
rather than by any route, and the text is the Gateway's own fixed line plus its stored event records - not
text a session key supplied. It is not a session-key path into another session.

### OPEN FINDING for the Architect - main's new built-in teaches a command the product refuses

Not fixed, because fixing it either way is a product call and this seat will not guess.

`fleet-manager.skill.md` has an "Answering a session" section that tells the Fleet Manager to run
`cc-devthrottle session prompt <session> "<their words, exactly>"`, and calls it the way to pass the owner's
answer on and to take the Wingman's `answerVia: reply` option. **The Fleet Manager is a session, so its key
is a session key, and this mission's ruling 17 refuses that route to every session key** -
`SessionKeyGuard.IsAgentInput` matches `POST /sessions/{sid}/prompt` before the allow list is consulted and
returns the typing refusal. So the shipped conduct hands the Fleet Manager a command it will be told 403 for,
which is the exact failure the "a shipped skill must not show what the product refuses" rule exists to stop.

The two ways out are a product decision:
1. The Fleet Manager is granted the prompt route (a named exception in `IsAgentInput`, bound to the account's
   mark), because passing the owner's own words on is the owner typing by proxy; or
2. The skill's "Answering a session" section is rewritten to the queue, and the owner's words reach a session
   from the owner's own screens.

The reworded "Messages are rare" section above is consistent with either. Nothing was changed in "Answering a
session".

### The break the merge exposed, fixed

`src/CcDirector.Gateway.UnitTests/Fleet/FleetManagerEventServiceTests.cs` (main's, from `00e184a9`) did not
pass the `narrationPlan` argument that `cd8b6233` made a required constructor parameter of
`GatewayTurnVerdictEnvironment`. Both commits are on main, so **`origin/main`'s `CcDirector.Gateway.UnitTests`
project does not compile** - `00e184a9` was prepared before `cd8b6233` landed and squash-merged after it, and
that suite is PARKED out of the default gate, so nothing compiled the two together until this merge did.
Fixed forward in the merge commit: the call passes `narrationPlan: _ => NarrationPlan.Allowed`, with a comment
saying the class judges stops and does not narrate. Main's sibling class
`FleetManagerOwnedSessionsAreJudgedTests` already passed the same argument, which is how the value was chosen
rather than invented.

### Pinned files, regenerated from real output

- **Client schema** (`packages/client-core/src/api/schema.ts`): regenerated with the repository's own
  `openapi-typescript` 7.4.4 from `/openapi/v1.json` served by an in-process Gateway console host on port
  7878, with `HOME` and `CC_DIRECTOR_ROOT` redirected into a scratch directory and the document fetched with
  that throwaway Gateway's own token. Additions only, 68 lines: the two Fleet Manager event routes. Typecheck
  clean on every workspace afterwards. The scratch host was stopped with a signal, not a force-kill.
- **Actions fixture** (`tools/cc-devthrottle/tests/fixtures/actions_json_before_step_6c.json`): NOT rewritten,
  and that is correct - it is a deliberate historical pin of what main printed before step 6c, and additions
  since are named one by one in `_ACTIONS_ADDED_SINCE_PIN`. Main added its own new ids to that list.
  `test_actions_json_is_unchanged` passes against real `actions --json` output on the merged tree, which is
  the proof the pin still matches.
- **Preamble approved file** (`src/CcDirector.Core.Tests/Sessions/fleet-preamble-default.approved.txt`): NOT
  rewritten. `FleetPreambleDefaultGoldenTests` compares the real generated preamble to it and passes on the
  merged tree (run on its own: 49 passed with the retired-words filter), so the approved file already IS the
  real output. Rewriting it would have produced an empty diff.

### Test totals (Mac mini, merged tree, 17 September 2026)

- **Build**: 0 errors for the Gateway, ControlApi, Core unit tests, Core tests, Gateway unit tests, Gateway
  route tests, Engine tests, HostedAgent tests, Launcher tests, the Avalonia app and the Avalonia tests. The
  whole-solution build still fails on `CcClick` and `CcDirector.Terminal` with NETSDK1100 - both target
  Windows and neither can build on this machine at all; unchanged by this merge.
- **Core unit tests**: 596 total, 596 passed, 0 failed, 0 skipped (was 594; main added two).
- **Core tests**: 4447 total, 4378 passed, 8 skipped, **61 failed**. Every failing name is in
  `slice-2-evidence/fix-round/full-suite-failures.txt`; compared mechanically (the set difference is empty).
  **No new failure.**
- **Gateway unit tests**: 5763 total, 5748 passed, 8 skipped, **7 failed - the known 7 Mac-only**
  (CronJobStore 1, RuleCandidateFilter 1, RulePrimitives 1, SessionCommandExecutorLiveness 3,
  WorkListStorePersistence 1). No new one.
  - Filter `SessionKeyGuard`: 262 passed.
  - Filter `FleetManagerEvent|DisplayFold|FleetManagerOwnedSessions|FleetMessage|FleetDoorbell`: 356 passed.
- **ControlApi tests** (they live in the Gateway unit test project; there is no separate ControlApi test
  project on this branch): filter `ControlApi|DirectorDrain|DirectorRestore|SessionCommandExecutor`, 170
  total, 167 passed, **3 failed - the known SessionCommandExecutorLiveness 3**.
- **Retired words / shipped skills**: Core unit `RetiredMessagingWords` 5 passed (all five, including the
  whole-tree scan); Core unit `Skills` (which includes `BuiltInSkillsHaveOneSourceTests` and
  `ShippedSkillsTeachOwnershipTests`) 9 passed; Core tests `FleetPreamble|RetiredMessagingWords` 49 passed.
- **cc-devthrottle** (fresh scratch virtual environment with the local `cc_storage`, `cc_shared`, the tool and
  pytest): 3271 passed, 0 failed; with `FORCE_COLOR=1`: 3271 passed; with `TERM=dumb FORCE_COLOR=1`: 3271
  passed. (Was 3241; main added 30.)
- **Web**: typecheck clean on every workspace; `vite build` succeeds for the phone and the Cockpit. Tests:
  client-core 1275 total / 23 failed, Cockpit 359 total / 24 failed, phone 81 total / 30 failed, cc-assistant
  106 passed. The 77 failing names are exactly those in
  `slice-4-evidence/web-failures-before-and-after.txt`, compared mechanically in both directions (both set
  differences are empty). The grown totals are main's new Wingman tab and Dev Reports phase 3 tests, all
  passing.
- **Gateway route tests** (full, 21 minutes 21 seconds): 2634 total, 2566 passed, 52 skipped, **16 failed -
  the known 16** (ContextLessRouteCensus 1, FleetSpawnMissionAttach 2, FleetSpawnOrigin 4,
  GatewayTestSuiteLock 2, HostedProcessControlDeny 2, TunnelRosterPushReadProof 3, WorkflowSeat 2). Same
  classes and same counts as the first merge with main. No new one. (Was 2626 total; main added 8.)

### What is NOT proven

- **`scripts/test-local.ps1` was not run** - there is no PowerShell on this machine. Every suite above was run
  directly with `dotnet test`, `pytest` and `npm`, so nothing here was gated by the script the repository's
  rule names, and the two installer suites it also runs were not run at all.
- **PostgreSQL**: nothing ran against it. That includes main's two new migrations interleaving with this
  branch's, the PostgreSQL model snapshot, and the collation census line the final fix round added - all three
  are read-and-reasoned here, not executed.
- **Avalonia tests** built, not run. `CcClick` and `CcDirector.Terminal` cannot build on this machine at all.
- **Windows and Linux**: not run.
- **Live**: no Director, no agent, no hosted Gateway, no phone. Nothing was deployed and nothing published.
- **cc-ship, cc-dev-reports and cc-secrets** test suites were not run (not in this seat's list; cc-ship was
  run by the final fix round and nothing in this merge touched it).
- **Main's Fleet Manager event delivery against this mission's doorbell**: no test drives a Fleet Manager event
  prompt and a fleet doorbell against the same session, so the two typing paths are not proven to stay out of
  each other's way. They are argued apart above (the doorbell types only into a session that is not working
  with an empty composer; the event prompt is sent with `OnlyWhenWaitingForInput` and refuses while the owner
  has unsent text), not tested together.
- **The open finding above is a claim about behaviour, read from the guard's code and its tests, not observed
  against a live Fleet Manager session.** `SessionKeyGuard` refusing `POST /sessions/{sid}/prompt` to a session
  key IS covered by its 262 passing tests; that the marked Fleet Manager's key is one of those keys, with no
  exception anywhere, is read from `IsAgentInput` running before the allow list and from there being no Fleet
  Manager branch in `AuthMiddleware`. No test asserts it for a Fleet Manager specifically.

## State after the second merge with main (17 September 2026)

- `mission/message-load` holds `origin/main` up to `00e184a9`, merged as `5fcc7ae9`, with the client schema
  regenerated in `4f89ede4` and this record in the commit after. Everything is pushed.
- **One thing needs the Architect, and it is the open finding above**: main's new built-in Fleet Manager
  conduct tells the Fleet Manager to run `cc-devthrottle session prompt`, which this mission's ruling 17
  refuses to every session key. Either the Fleet Manager gets a named exception or that section of the skill is
  rewritten. This seat changed only the two retired sentences and left the decision alone.
- Next, unchanged: inspection 12 (narrow), then the pull request - the Architect's.

## Architect ruling on the open finding of the second merge (17 September 2026)

**No exception for the Fleet Manager.** Ruling 17 stands for every session key, the Fleet Manager's
included. The Fleet Manager started the sessions it manages, so it may message them; the owner's answer
reaches the session as a queued message and one doorbell at the next safe moment, which for a session
that is waiting on that answer is at once. This is what the firstmate handover of 16 September asked for
("use Message Load's inbox for that direction rather than building a second one"). The "Answering a
session" section of the built-in Fleet Manager conduct (`src/CcDirector.Gateway/Skills/Content/fleet-manager.skill.md`
and any repository copy) is rewritten to `cc-devthrottle message send <session> "<their words, exactly>"`,
the Wingman's `answerVia: reply` option is described the same way, and the retired-words tests gain
`session prompt` as a phrase no shipped conduct may teach to an agent, with the file in the inventory.
The Fleet Manager mission's Architect learns this from this record and from the merged skill text; no
message is sent.

## Fleet Manager conduct words (17 September 2026)

The Architect's ruling above, carried out. Nothing else was touched, no pull request exists, and no
fleet message was sent.

**The shipped conduct** (`src/CcDirector.Gateway/Skills/Content/fleet-manager.skill.md`, the one
source; there is no `.claude/skills/fleet-manager` copy to regenerate, which is the cleanest state and
what `BuiltInSkillsHaveOneSourceTests` allows):

- "Answering a session" is now `cc-devthrottle message send <session> "<their words, exactly>"`. It
  says why that is the only way the Fleet Manager has - typing is the owner's own, from the owner's own
  screens, and the Gateway refuses every typing route to every session key, the Fleet Manager's
  included - and that it may message the sessions it started, so the sessions it owns are exactly the
  sessions it can answer.
- It says the words reach the session **as a queued message and one doorbell at the next safe moment**:
  `message send` answers `queued`, never `delivered`; the doorbell rings only when the session is not
  working, its composer is empty, no menu is open and the owner is not dictating into it; the session
  reads the words in full with `message inbox`; nothing is typed into its work and nothing is cut
  short. A session that stopped to ask for exactly that answer is idle with an empty composer, so its
  next safe moment is now.
- The Wingman's `answerVia: reply` option is described the same way, in the same words.
- `answerVia: keys` was the sentence that told the Fleet Manager to type a menu number and then check
  the screen. It now says nothing it can run answers a menu - a key is typing, and the doorbell does
  not ring while a menu is open, so queued words would simply wait - so the menu goes to the owner, who
  answers it on their own screen. Never guess at keys.
- Two other sentences: the Wingman-reading table now says `reply` is "words you can pass on as a
  message" and `keys` is for the owner alone; the "Messages are rare" bullet says you QUEUE words for a
  session in those two cases, and that either way they arrive as a queued message and one doorbell.

**The guard** (`src/CcDirector.Core.UnitTests/Skills/RetiredMessagingWordsTests.cs`):

- `cc-devthrottle session prompt` is now a phrase no text an agent reads may teach, in the taught
  inventory AND in the whole-tree scan. It is held as `RetiredTypingCommand`, apart from the other
  retired phrases, because unlike them it has a legitimate use in text an agent reads.
- That legitimate use is written down as `TypingCommandExemptions`, three entries with a reason each,
  and it is honest about what each one is: `tools/cc-devthrottle/src/session_ops.py` (the command
  itself, whose docstring and blank-text usage name it and whose only behaviour is to print the
  Gateway's refusal), `Skills/Content/fleet-comms.skill.md` (teaches that typing is the owner's alone by
  naming the refused command) and `.claude/skills/fleet-comms/SKILL.md` (that skill's repository copy).
  Both scans consult the list, so a file documenting the refusal is not forced to teach the ban by
  silence. `docs/FleetMessaging.md` needs no exemption: it names the bare `session prompt`, not the
  command line.
- The fleet-manager skill is now named in `RequiredFiles`, so the inventory proves it read it rather
  than relying on the `Skills/Content/*.md` glob. `The_preamble_and_the_skill_teach_the_inbox` also
  asserts that skill still HAS an "Answering a session" section, that it teaches
  `cc-devthrottle message send <session>`, and that it carries the sentence about a queued message and
  one doorbell - so deleting the section could not pass the ban by teaching nothing.
- `The_tree_scan_reads_the_files_this_round_fixed_and_every_exemption_exists` now also asserts every
  typing-command exemption names a file that exists.
- One instrument fix the new phrase exposed: the tree scan was reading
  `tools/cc-devthrottle/build/lib/cc_devthrottle/session_ops.py`, a local Python build's copy of the
  command line's own sources. `.gitignore` ignores `build/` and no tracked file lives under any
  directory of that name, so `build` joined `bin`, `obj` and `dist` in the skipped directories, with
  the reason written above the set. Without it the scan reported the same source twice and depended on
  whether somebody had run a build.

**The guard was watched failing.** With the old line restored
(`cc-devthrottle session prompt <session> "<their words, exactly>"`) three tests go red naming the
symptom - `Nothing_in_the_repository_outside_the_named_history_uses_the_retired_messaging_words` and
`No_text_an_agent_reads_teaches_the_retired_messaging` both print
`src/CcDirector.Gateway/Skills/Content/fleet-manager.skill.md:234: "cc-devthrottle session prompt"`,
and `The_preamble_and_the_skill_teach_the_inbox` fails on the missing `message send <session>` - while
the two control tests (`The_scan_reads_every_named_surface` and the fixed-files-and-exemptions test)
stay green. Restored afterwards.

**Runs (Mac mini, 17 September 2026, `dotnet test` directly - there is no PowerShell on this machine,
so `scripts/test-local.ps1` was NOT the gate):**

- Core unit `RetiredMessagingWords`: 5 of 5 passed.
- Core unit, whole suite: 596 total, 596 passed, 0 failed, 0 skipped.
- Core tests `FleetPreamble|RetiredMessagingWords`: 49 of 49 passed.
- Gateway unit `BuiltInSkills|ShippedSkills`: 6 of 6 passed - `BuiltInSkillsHaveNoDeadDoorTests`, its
  theory once per shipped skill file (the five: `dev-throttle`, `fleet-comms`, `fleet-manager`,
  `move-session`, `terminology`) plus its own pattern check. Named accurately because the filter's other
  two classes do NOT live in that project: `BuiltInSkillsHaveOneSourceTests` (2 facts) and
  `ShippedSkillsTeachOwnershipTests` (2 facts) are in Core unit, and all four are inside the 596 above -
  run again on their own here, 4 of 4 passed.

**What is NOT proven, and one thing for the Architect:**

- Nothing was run live: no Director, no Fleet Manager session, no Gateway. That the Gateway refuses
  `POST /sessions/{id}/prompt` to a marked Fleet Manager's key is still read from `IsAgentInput` and its
  262 passing tests, exactly as the open finding said; this round changed words, not behaviour.
- The `fleet-manager` WORKFLOW (`src/CcDirector.Gateway/Workflows/Content/fleet-manager.instructions.md`,
  main's) was deliberately left alone - the ruling names the skill, and the workflow names no command,
  so the guard is green on it. Two of its sentences are now looser than the skill: "the owner's answer
  goes straight to the session", and "send the Wingman's option exactly: keys when the session is
  showing a menu, words otherwise", which asks for something the Fleet Manager cannot do. Whether that
  conduct text is reworded is the Architect's call, and it belongs to the Fleet Manager mission's
  Architect as much as to this one.
- The words only reach the fleet when the Gateway is deployed, which is not this mission's step.
