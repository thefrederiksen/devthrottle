# Mail Desk, phase 1: the Gateway store and commands

Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` (issue #4213), section 6 row 1, sections 5.6, 5.8 and 5.10.
The contract every Developer of this phase built to: `CONTRACT.md` (this folder).

Everything in this phase is behind the Mail Desk switch, which is off for every account by default. Nothing in this
phase touched the hosted Gateway, the real mailbox, any email, a desktop release or the website proxy.

## What was built

| Piece | Where | Pull request into the phase branch | Review |
|---|---|---|---|
| The per-account switch (no machine-wide switch), the route group that answers 404 with no body when it is off, the administrator route `GET`/`POST /gateway/admin/mail-desk`, the refusal of a Mail Desk check report (409 `mail_desk_off`, nothing recorded) for a trigger and for a timed start, and `scripts/mail-desk-switch.ps1` | `src/CcDirector.Gateway/MailDesk/MailDeskSwitch.cs`, `MailDeskStart.cs`, `Api/MailDeskGate.cs`, `Api/AdminMailDeskEndpoint.cs`, `TriggerService`, `DueCheckService` | 4220 | `reviews/pr-4220.md`: three findings, all accepted and fixed |
| The five tables in both databases (one migration pair, `AddMailDesk`), the rules document and its validation, the rules, channel and decision stores, the change record | `src/CcDirector.Gateway/MailDesk/`, both `Migrations` folders | 4222 | `reviews/pr-4222.md`: two findings, both accepted and fixed |
| The `cc-devthrottle maildesk` command: status, channels, mode, rules, the four lists, history, decisions | `tools/cc-devthrottle/src/maildesk_ops.py`, `cli.py`, `docs/cli-reference.md` | 4221 | `reviews/pr-4221.md`: two findings, both accepted and fixed |
| The ten Gateway routes under `/gateway/mail-desk`, who may change what, the channel fold (`signedIn`, `lastCheck`, `today`), the session key guard for exactly those routes | `src/CcDirector.Gateway/Api/MailDeskEndpoints.cs`, `Api/GatewayCaller.cs`, `MailDesk/MailDeskChannelFold.cs`, `Util/SessionKeyGuard.cs` | 4231 | `reviews/pr-4231.md`: one finding, accepted and fixed, and the fix reviewed again with no findings |

Every pull request was built by a Developer session and reviewed by a separate Reviewer session on a different
model. Every finding was answered by the Developer who built the work: the answers to 4220, 4221 and 4222 are in
those pull requests' comments and in their commit messages; the answer to 4231 is in its review file. Nothing a
reviewer raised is left unanswered.

The Developer of 4231 was stopped before it could answer its review, together with the first Tech Lead of this
phase. A fresh Developer took the branch over, accepted the finding (`GET /status` dropped a channel removed today
out of the account's day total, so it disagreed with `GET /decisions`), fixed it with a regression test that fails
on the old code, and a second Reviewer read the fix and reproduced that failure.

## The proof, in plain words

### The switch is off for everyone, and off means off

`MailDeskSwitchTests` (Gateway unit tests): nothing recorded means off for every account; switching one account on
leaves every other off; every one of the ten Mail Desk routes answers 404 with no body for an account that is off,
including when the factory agents switch is on; the route list is read from the application itself and pinned at ten,
so a route added later outside the gate fails the test. A check report from a Mail Desk start while the switch is off
is refused with 409 and leaves no run row, no kept state, no claim change and no started seat; a timed start of the
same check is refused the same way. Only the administrator route can switch it: a test reads every method of the
compiled Gateway for a call to `MailDeskSwitch.Set` and finds exactly one caller.

### The store keeps every change, never overwrites, and keeps no body text

The store tests (`MailDeskRulesStoreTests`, `MailDeskChannelStoreTests`, `MailDeskDecisionStoreTests`,
`MailDeskRulesValidatorTests`): every rules save is a new version with its change row in one transaction; a second
writer on the same version is refused as stale and writes nothing; a removed channel keeps its row and its decisions;
the decisions table has no body field (a test lists the entity's exact properties); every account sees only its own
rows. The same stores and their unique indexes run on a real PostgreSQL in `MailDeskPostgresTests`.

### The routes decide who may change what, and a refusal records nothing

`MailDeskEndpointTests` (83 cases) boot the real route map over the real stores. A factory's own session cannot add,
remove or switch the mode of a channel (403); the machine token cannot change anything (403); a change without a
`why`, missing or blank, is refused on all eight kinds of change. For every refusal the test compares every Mail Desk
row of the account before and after. A rival save between read and save is retried on the newest document, and gives
up with 409 after three tries. The day boundary is tested in a time zone that is not UTC, including the day the
clocks go back.

### The command decides nothing

`test_maildesk_ops.py` (90 tests) against a fake Gateway: every subcommand's request; every filter reaches the
Gateway and applies to `--json`; a missing `--why` and an unknown flag exit 2 before any request; the switch-off 404
prints one sentence and exits 1; a change is only reported as recorded when the Gateway's answer proves it.

## The gate

Run by this Tech Lead on devlinux (Linux), on the phase branch at `0e860882e` (rebased on origin/main `d8772d4d6`):
`scripts/test-local.ps1 -Parked`, with .NET 10 from `~/.dotnet`, `DOTNET_USE_POLLING_FILE_WATCHER=1`,
`EnableWindowsTargeting=true` and `LOCALAPPDATA` set (the script needs both on Linux). Run folder
`/tmp/cc-test-local-81b23b69`.

**The script's own verdict is NEVER RAN (exit 1), and it always is on this machine:** the two installer suites build
for Windows and run no test on Linux. So every red suite was compared, test by test, with the same suite on unchanged
origin/main `20491d6ef`, run on the same machine the same hour (run folder `/tmp/cc-test-local-4bc36570`). The one
main commit between `20491d6ef` and the rebase base, `d8772d4d6`, adds one review document and no code.

**The phase branch fails exactly the same tests as unchanged origin/main, name for name - 107 on each side, none on
one side only.** No re-runs were needed: this time Gateway.Tests had no machine noise from the inotify limit.

| Suite | Phase branch | Unchanged origin/main | Verdict |
|---|---|---|---|
| Gateway.UnitTests (holds the Mail Desk tests) | 14,712 passed, 3 failed, 2 skipped | 14,476 passed, the same 3 failed | clean; 236 more tests pass on the branch: the Mail Desk ones |
| Gateway.Tests (4 shards) | 1,771 passed, 10 failed, 1 skipped | the same 10 failed | clean |
| Core.Tests | 8 failed | the same 8 | clean (link detection with Windows paths) |
| Core.UnitTests | 5 failed | the same 5 | clean (Windows drive paths) |
| Launcher.Tests | 24 failed | the same 24 | clean (Windows-only behaviour) |
| Reclaim.Tests | 56 failed | the same 56 | clean (Windows-only behaviour) |
| Terminal.Avalonia.Tests | 1 failed | the same 1 | clean |
| cc-director-setup.Tests, cc-director-setup-engine.Tests | ran no test / aborted | the same | clean (they build for Windows) |
| Avalonia (1,115), Engine (85), HostedAgent (85), DeliveryQualification (22), cc-director-setup-cli (74) | passed | passed | clean |

The three Gateway.UnitTests that fail on main on Linux:
`VoiceUnheardCountTests.NotePlayed_AResetWhoseSaveFailed_IsWrittenAgainByTheNextPlay`,
`VoiceAutoOffLineTests.ClearForVoiceOn_WhenTheSaveFails_DoesNotThrowAndTheNextVoiceOnWritesIt`,
`HeldDeliveryDriverTests.ARecordWriteThatThrowsAfterTheClaimMark_IsPressedAgainByTheDriver_ResolvedWithNoRestart`.
Each expects a save to fail, and on this machine it does not.

The ten Gateway.Tests that fail on main here: six in session spawning (`FleetSpawnOriginTests` four,
`FleetSpawnMissionAttachTests` two), two in `WorkflowSeatTests`, `HostedProcessControlDenyTests.Hosted_director_launch_is_refused`
and `ContextLessRouteCensusTests.The_selfhost_context_less_route_set_adds_only_the_hosted_denied_families`. None is in
code this phase touched, and each fails the same way without it.

**After the gate, origin/main moved** to `30c27805e` (the API mode switch, #4208: Gateway code, no file shared with
this phase). The branch was rebased onto it (phase code head `7f37aeefc`) and the whole Gateway.UnitTests suite, where
both changes meet, was run again: **14,727 passed, the same 3 failed, 2 skipped**. The rest of the `-Parked` gate was
not run again on that head.

**No Windows gate ran.** Nothing in this phase was built or tested on Windows or macOS.

### The PostgreSQL migration

`scripts/test-database.ps1` on the same head `0e860882e`, with Docker on devlinux: **GREEN, 79 of 79 executed and
passed, none skipped**, including `MailDeskPostgresTests.TheMailDeskTables_AndTheirStores_OnPostgres` and the
collation census `PostgresProviderProofTests.Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres`.
On Linux the script calls `powershell` by name, which is not installed here; the run used a link named `powershell`
to `pwsh`, placed on the path for that run only.

### The command's Python tests

The gate runs no Python. Run by hand on the same head, the way the continuous integration job runs them (Python 3.11,
pytest, typer, rich, requests, cryptography, `FORCE_COLOR=1`):

- `tools/cc-devthrottle/tests`: **4,997 passed, 2 skipped**.
- The shipped-tool contract, the shared output helper and the tool failure reporter tests: **198 passed**.

The mission asks for these on every platform the tool's tests run on (Windows, macOS, Linux). Only Linux was run.
Start the continuous integration job after merging (`gh workflow run CI --ref main`) for the other two.

## How to run the proof again

```
export PATH=$HOME/.dotnet:$PATH DOTNET_USE_POLLING_FILE_WATCHER=1
dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~MailDesk"
cd tools/cc-devthrottle && python -m pytest tests/test_maildesk_ops.py -q
pwsh -NoProfile -File scripts/test-database.ps1        # needs Docker
```

## What is NOT proven

- **Windows and macOS.** No Windows gate ran; the comparison above is the whole of the gate evidence.
- **The routes behind the real sign-in.** The route tests stand in for a person's device, a session's key and the
  machine token with a test handler; the production wiring was traced by reading (review of 4231), not run.
- **Nothing ran against a real Gateway, the hosted one or a local one, except the switch command** (4220 ran
  `scripts/mail-desk-switch.ps1` against a Gateway started on this machine with a scratch root; see `switch/README.md`).
  `cc-devthrottle maildesk` was tested against a fake Gateway only.
- **Nothing writes a decision yet.** The decisions table, its insert and its reads exist and are tested; the screening
  that fills it is phase 3's.

## For the Delivery Lead to decide or carry forward

1. **The template offer still has no caller.** Phase 2's README says the Mail Desk template is offered as a draft by
   `MailDeskTemplateOffer`, which needs to be called when the switch is turned on (and, decided there, at Gateway
   start for every account whose switch is on). The switch now exists, but this phase did not add that call; on this
   branch `MailDeskTemplateOffer` still has no production caller. Phase 6 needs it before the owner can publish the
   factory. It is two calls and a test; recommendation: phase 3, which already owns the Gateway's Mail Desk start-up.
2. **A misspelt machine on a new account.** While an account has never had a Director report in, `POST /channels`
   takes the machines as given, so a misspelt machine name can be saved and its check would never run. Once a
   Director has reported, a machine it has not reported is refused. Phase 3 or phase 7 could show the mismatch on the
   channel row.
3. **`scripts/test-database.ps1` does not run on Linux as written**, because it calls `powershell` by name. Not this
   phase's code; worth one line on main.
