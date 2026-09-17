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
5. The dictation lock includes the PENDING record, which never expires by design; a dictation that never
   completes holds the doorbell until it is delivered or abandoned (the messages wait; nothing goes stuck).
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
