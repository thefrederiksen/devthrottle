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

- Phase: slice 1 (the inbox and the gate) is BUILT, committed and pushed on `mission/message-load`,
  rebased onto origin/main at `e6f763f2` on 16 September 2026. No pull request opened (the Architect's).
- Next: the Architect's Codex inspection of slice 1, then slice 2 (the doorbell).
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
