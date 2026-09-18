# Inspection 12 — the post-merge review

**Verdict: FAIL.** Inspection 11's high finding is not closed. A second workstation device key of the
account can still write every restore mark under the lease holder's name, name any live session as the
restored one, and release the lease. The new credential binding is defeated by the hub's own Hello,
whose Director id the caller writes. Proven on a booted hosted Gateway, on this tree, at the merged
commit. The merge itself is clean — no mission ruling and no addition of main was lost in any of the
three resolutions — the rail-test fix is correct and complete, and the Fleet Manager conduct SKILL now
describes only what the product allows; the WORKFLOW shipped beside it does not.

The owner merged first, so these are follow-up issues. Findings 1, 2 and 3 are the ones worth filing.

Inspected the merged head `ddc78ac4` (detached), the scope `cf2b4996..ddc78ac4` over `src tools packages
docs` excluding `docs/missions`, and the three merges on `mission/message-load` (`93e3f573`, `5fcc7ae9`,
`19822cc2`). No product code was changed. Two throwaway probe tests were added to
`WorkspaceRestoreRouteTests`, run, and reverted; `git status` was clean after each.

---

## Findings, ranked

### 1. High — the restore-mark credential binding is taken by a Hello, so a second workstation key still forges every mark

Ruling 1 said the Gateway "resolves which registered Director the authenticated workstation credential
belongs to". It does not resolve it; it **records whatever the last Hello claimed**, and the Hello's
Director id is written by the caller.

- `src/CcDirector.Gateway/Streaming/DirectorHub.cs:172-224` — `Hello` takes `hello.DirectorId` verbatim.
  The only identity check is the tenant (`:194-201`, and the comment at `:214-218` says so: naming
  another **account's** Director is impossible, "however the client chose hello.DirectorId"). There is no
  check that the id is already bound to a different credential, and `ResolveConnectionTenant`
  (`:738-758`) applies no device-type gate.
- `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs:335-392` — `RegisterFromStream` writes
  `_registeredBy[key] = registeredByCredential` unconditionally, last writer wins.
- `src/CcDirector.Gateway.UnitTests/DirectorRegistryCredentialBindingTests.cs:53-68` **asserts** this
  take-over as intended behaviour: `Hello(Account, "director-a", KeyB)` after `KeyA` makes `KeyB` the
  Director and `KeyA` not. The guard the ruling asked for is codified with the hole in it.
- `src/CcDirector.Gateway/Api/WorkspaceEndpoints.cs:231-236` and
  `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:4007-4012` then consult that binding and let the
  impostor through.
- The `restored` mark's token check (`src/CcDirector.Gateway/Workspaces/WorkspaceStore.cs:499-507`) does
  not help: `startedToken` and `startedByDirectorId` are ordinary public properties
  (`src/CcDirector.Gateway.Contracts/WorkspaceDtos.cs:412,417`) returned whole by
  `GET /gateway/workspaces/{id}` (`WorkspaceEndpoints.cs:262-274`), which is open to every device key and
  to every session key of the account (`src/CcDirector.Gateway/Util/SessionKeyGuard.cs:548`).

**Proved, not argued.** A probe added to `WorkspaceRestoreRouteTests` (the suite's own booted hosted
Gateway, two real workstation device keys enrolled in one account, the second key already present at
`:80-81`): the real Director takes the lease and writes `started` with `tok-1`; the second key reads
`tok-1` back from `GET /gateway/workspaces/{id}`; the second key opens a hub connection with
`FakeTunnelDirector.StartAsync(_gateway, _otherWorkstationKey, DirectorId, ...)` — a Hello under the
first Director's id — then posts the forged marks. Result, verbatim:

```
PROBE RESULT: forged restored=200 OK; forged finished=200 OK;
seat.restoredSessionId=d8d4f912-9792-435a-b3ce-d2bfdafde9f4; lease=(released)
```

That session id is an unrelated live session the impostor chose. This is inspection 11's high finding,
unchanged, with one extra step. The seventeen shipped tests in that class pass alongside it, because the
existing guard `A_second_workstation_key_of_the_account_cannot_write_any_mark_under_the_lease_holders_name`
(`src/CcDirector.Gateway.Tests/WorkspaceRestoreRouteTests.cs:307-348`) never connects the second key to
the hub — it only sends HTTP.

**How to prove it:** re-add the probe above, or by hand: enroll two workstation devices in one account,
let Director A take a captured workspace's lease and write a `started` mark, `GET
/gateway/workspaces/{id}` with key B to read the token, open a Director hub connection on key B saying
`Hello { DirectorId = "<A>" }`, then `POST /gateway/workspaces/{id}/restore/marks` from key B with
`directorId=A`, `kind=restored`, that token, and any `restoredSessionId`. It is recorded; `finished`
releases the lease. `DirectorRestore.ResolveOwner`
(`src/CcDirector.ControlApi/Drain/DirectorRestore.cs:556-615`) then reads that forged id as a seat's
owner, which is the consequence inspection 11 named.

**What would close it:** the binding must be established by something the caller does not choose. Refuse
a Hello whose Director id is already bound to a different credential (and make a genuine re-bind an
explicit, evidenced path), or bind the mark route to the Director id the caller's **connection** is bound
to rather than to a registry row any later Hello can rewrite.

### 2. Medium — any device key of the account can take the binding and lock the real Director out of recording its own restore

The same take-over needs no workstation key at all. A **browser** or **phone** key of the account may
say Hello as Director A (there is no device-type gate on the hub, `DirectorHub.cs:738-758`). Such a key
still cannot write marks — `WorkspaceEndpoints.IsDirectorCredential` (`WorkspaceEndpoints.cs:439-444`)
refuses it — but it has already overwritten `_registeredBy`, so **the real Director's own marks are now
403**. A restore in flight stops recording what came back, silently, from the Director's point of view.

**Proved.** Second probe, same harness: a browser device key registered with `deviceType: "browser"`
opens a hub connection under the Director's id, and the Director's own next mark is refused:

```
PROBE RESULT: real Director started=200 OK;
after a browser key said Hello as it, its own restored mark=403 Forbidden
```

**How to prove it:** as above, with `deviceType: "browser"` on the second device. Fixing finding 1
correctly fixes this too.

### 3. Medium — the shipped Fleet Manager WORKFLOW still teaches what the product refuses, and contradicts the skill the same session reads

The Architect's ruling named the skill, and the skill is right. The workflow shipped beside it, read by
the same session, was left as main wrote it and now says the opposite:

- `src/CcDirector.Gateway/Workflows/Content/fleet-manager.instructions.md:201-202` — "When you answer a
  session yourself, send the Wingman's option exactly: **keys** when the session is showing a menu, words
  otherwise." A key is typing. `SessionKeyGuard.IsAgentInput` (`src/CcDirector.Gateway/Util/SessionKeyGuard.cs:155-160`)
  refuses `POST /sessions/{sid}/prompt`, `/interrupt` and `/escape` to every session key before the allow
  list is consulted, and the Fleet Manager is a session.
- `:8` — "the owner's answer goes straight to the session"; `:218` — "their answer goes to the session
  exactly as they gave it".
- `src/CcDirector.Gateway/Skills/Content/fleet-manager.skill.md:237-254` says the opposite, on the
  ruling: the answer is a queued message and one doorbell, and "nothing you can run answers a menu … Never
  guess at keys."

The handoff records this as left for an Architect (`handoff.md`, "What is NOT proven" after the Fleet
Manager conduct words). It is still a shipped built-in that hands the Fleet Manager an instruction the
product refuses — the exact failure the rule exists to stop — and the retired-words guard cannot see it:
both scans match **command strings**, and this text names no command.

**How to prove it:** read the three passages together; run the `RetiredMessagingWords` suite and observe
all five pass.

### 4. Medium — on a self-hosted Gateway using the shared machine token the binding is a constant, so it is a no-op

`AuthMiddleware.RegisteringCredential` (`src/CcDirector.Gateway/Util/AuthMiddleware.cs:635-643`) returns
the literal `"machine-token"` for every caller that authenticated with the shared token. Two different
callers therefore produce the *same* credential string, so `IsRegisteredByCredential` is satisfied for
any Director that said Hello on that token, by anyone holding it. Such a caller also passes
`IsDirectorCredential`, because the shared token sets no `DeviceTypeItemKey` and
`SessionOriginSurfaces.FromDeviceType(null)` is `Unknown`. The handoff says this path "is not exercised
by any test"; what it does not say is that the check does nothing there. The route comment
(`WorkspaceEndpoints.cs:201-208`) presents the binding without that qualification.

**How to prove it:** boot a non-hosted Gateway with a shared token, connect two Directors on that token,
and post a mark for one from the other's process. Or read the three lines above.

### 5. Low — with authentication turned off, no restore can record anything

`GatewayHost.ResolveAuthEnabled` (`src/CcDirector.Gateway/GatewayHost.cs:254-265`) turns the auth
middleware off for `CC_GATEWAY_NO_AUTH=1` or `CC_GATEWAY_AUTH=0`, and the middleware is only added when
it is on (`:3521-3529`). With it off nothing stamps the auth items, so `RegisteringCredential` returns
null and `IsRegisteredByCredential` returns false for every caller (`DirectorRegistry.cs:263-271`). Every
restore mark and every restore claim is then 403, and a restore records nothing while appearing to run.
There is no "auth is off" branch anywhere on this path.

**How to prove it:** boot a Gateway with `authEnabled: false` and run a restore; expect 403 on the first
mark. Read from the code; **not run here.**

### 6. Low — the route comment claims a protection the start token does not give

`src/CcDirector.Gateway/Api/WorkspaceEndpoints.cs:205-208`: "several Directors on ONE machine share that
machine's key, so one of them can still name another. **The lease and the start token are what stop
those.**" They do not. In exactly that case the impostor names the lease holder (so the lease check
passes), `restore.StartedByDirectorId` already **is** the lease holder, and the token is readable from
`GET /gateway/workspaces/{id}` by any key of the account (finding 1). The disclosed limit is real; the
sentence that softens it is wrong, and it is the sentence a later reader will trust.

### 7. Low — the whole-tree scan's phrase list is a strict subset of the inventory's, and the asymmetry is load-bearing

`src/CcDirector.Core.UnitTests/Skills/RetiredMessagingWordsTests.cs:30-38` lists eight retired phrases
for the taught inventory; `:108-116` lists six for the whole-tree scan. `"interrupts the receiving"` and
`"--controlled-by <session-id>"` are in the first and not the second, and nothing says why. A file
outside the inventory may carry either and both tests stay green.

It is already load-bearing: `tools/cc-devthrottle/tests/test_message_load_words.py:68,70` contains
`--controlled-by <session-id>` today and needs no `TreeExemptions` entry only because the tree scan does
not look for it. Swept the tree for both phrases: no file currently teaches either outside the exempt
research transcripts, so this is a hole, not a breach.

**The rest of the exemption list is honest.** Checked every entry with the test's own
whitespace-collapsing match: all ten history exemptions and all ten code/test exemptions carry a real hit
(including `docs/MISSION-multilingual.md`, whose only hit is a phrase wrapped across lines and invisible
to a line-based search), and each of the five code/test files names the retired verb only to record its
removal or to assert the refusal — `src/CcDirector.Gateway.Contracts/FleetMessageRequests.cs:40`,
`src/CcDirector.Gateway.Tests/FleetMessageRouteTests.cs:327`,
`tools/cc-devthrottle/tests/test_stale_answer_caution.py:6`,
`tools/cc-devthrottle/tests/test_help_and_errors_axi.py:1164`,
`tools/cc-devthrottle/tests/test_axi_step_6b_recheck5.py:9-10`. The three `TypingCommandExemptions` are
honest for the same reason. One shape to watch, not a finding: the `GatewayEndpoints.cs` exemption is
whole-file, not phrase-scoped, on a file of five thousand lines.

### 8. Low — the desktop baseline of "the known 7" is not exact; this run has 8

Whole `CcDirector.Avalonia.Tests` project on this Mac at the merged head: **550 total, 542 passed, 8
failed.** Seven are the recorded Mac-only set (LegacyWorkspaceImport 1,
MicCaptureConstructionQueriesNoDevice 2, SpeakDialogCloseDuringStartup 1, SpeakDialogReadyCueBlanking 3).
The eighth,
`SpeakDialogMicEnumerationOffUiThreadTests.Open_ShowsGettingReadyAndOpensTheMicrophone_BeforeTheDeviceListArrives`,
is not on that list. Run alone it passes (3 of 3), so it is order- or timing-dependent within the whole
run. Nothing to do with this mission — no rail test failed at all — but "no new failure" compared against
a seven-name list can absorb a real eighth, which is what a baseline is for.

### 9. Low — public-repository hygiene: workstation paths and a machine name in committed evidence

Arrived from main (Dev Reports phase 3) through the merge, so it is in this diff.
`packages/client-core/browser-tests/dev-report-viewer-proof/evidence/rig-2026-09-17.json` and its
siblings (also the `dry-run-viewer-4be0f5aee/` copies) record absolute workstation paths, e.g.
`D:\ReposFred\devthrottle-dev-reports-p3-proof\packages\client-core\browser-tests\...`, and
`evidence/rig-cockpit-signed-in.png` shows the machine name `SOREN_NORTH`. Scanned for credentials, keys
and addresses: **none** — no bearer token, no session key, no email. This repository is public and the
owner's standing rule is that local paths do not go into committed files here.

---

## Values that can still change with every suite green

- **The restore lease's 15 minutes.** `src/CcDirector.Gateway.Contracts/WorkspaceDtos.cs:618`. No test
  asserts the literal; the only other references interpolate it into sentences
  (`WorkspaceStore.cs:399`, `DirectorRestore.cs:303`).
- **Every new refusal sentence.** The three added 403/400/409 messages
  (`WorkspaceEndpoints.cs:233-235`, `GatewayEndpoints.cs:4010-4011`, `WorkspaceStore.cs:504-507`) are
  asserted nowhere — the route tests check status codes only
  (`WorkspaceRestoreRouteTests.cs:326,373-375,398`). An agent reads those sentences; they are the product.
- **The shipped Fleet Manager skill's key sentences are pinned**, three of them, by
  `The_preamble_and_the_skill_teach_the_inbox` (`RetiredMessagingWordsTests.cs`) — `## Answering a
  session`, `cc-devthrottle message send <session>`, and `queued message and one doorbell at the next safe
  moment`. The rest of that 297-line file is free.

## What I checked and found sound

**The restore mark, everything except the binding.** A `restored` mark without a token is 400 and with
another token or another Director's start is 409 (`WorkspaceStore.cs:499-507`), and `DirectorRestore` now
sends the token. `finished` releases the lease only for the holder: the lease check
(`WorkspaceStore.cs:451-458`) compares `lease.DirectorId` to `mark.DirectorId` before the `Finished`
branch, and `ReleaseRestoreLease` (`:417-430`) is a no-op for anyone else. A session key never reaches
the mark route (`SessionKeyGuard.cs:540-557` lists only `POST .../restore`), and a phone or browser key is
refused (`WorkspaceEndpoints.cs:210-216,439-444`). The spawn door's `restoreClaim` got the same rule
(`GatewayEndpoints.cs:3994-4016`) and `RecordRestoredByClaim` now also requires the start to have been
that Director's (`WorkspaceStore.cs:552-570`). **A legitimate multi-slot restore is not broken**: two
Director slots on one machine share that machine's device key, so each satisfies the binding for its own
id — the binding lets slot B forge for slot A (the disclosed limit, see finding 6), it does not stop
either from recording its own work. A Director that registered only from an instance file carries no
credential and its marks would be refused, but a restore order rides the tunnel, so a Director that can
receive one has said Hello. `Workspace*|DirectorRestore*|SessionKeyGuard*|DirectorRegistry*` on the
Gateway unit project: **452 passed, 0 failed**. `WorkspaceRestoreRouteTests`: **17 passed, 0 failed**.

**The three merges lost nothing.** Compared the merged tree against main's head `e38217bc` — the parent
of the squash — file by file for the five named files. Every deletion is a line replaced by a longer one:
`GatewayEndpoints.cs` (3 deletions — the `Map` signature gaining `inboxLines` and `workspaces`, and the
`POST message` send gaining `replyWithin`; both `StampFleetRolesAndFold` call sites carry main's
`fleetManagerMark` **and** `inboxLines`), `GatewayHost.cs` (4 — the display observer, the
`WorkspaceEndpoints.Map` call, the Fleet Manager digest's `FoldedRoster` passing `_fleetMessages` beside
main's mark, and the fold's `inboxLines`), `TestEnvironment.cs` (0 deletions — both sweeps off, both
comments, `:34-40`), `FleetManagerEventServiceTests.cs` (2 — the comment, rewritten to carry main's pull
request number and both sides' intent; `narrationPlan: _ => NarrationPlan.Allowed` is byte-identical on
both sides), `docs/cli-reference.md` (11 — main's two-line `session report` entry is intact with `session
raise` after it, and every other deletion is a sentence this mission rewrote). `git diff --check` over
the whole scope: clean.

**No route main added lets a session key put text into a session.** Main added exactly two route
registrations in the whole scope, both under the Fleet Manager prefix
(`src/CcDirector.Gateway/Api/FleetManagerEndpoints.cs`, `GET /events` and `POST /events/ack`), and the
only `SessionKeyGuard` change is the `events` case (`SessionKeyGuard.cs:508-512`) — a read and an
acknowledgement, matched as literals at exact lengths. `IsAgentInput` (`:155-160`) is unchanged and still
runs before the allow list. The census of inspection 10 therefore still holds. **One honest note, the
same one the handoff records:** main's event *delivery* does type a prompt, into the marked Fleet
Manager's **own** session, driven by the Gateway's sweep rather than by any route
(`Fleet/FleetManagerEventService.cs:749-856`). It is not a session-key route into another session. Worth
one look by whoever owns it, which I did not prove either way: `FleetManagerEventPrompt.Build`
(`Fleet/FleetManagerEventPrompt.cs:53`) interpolates `e.SessionName` — a display name a session can set
for itself — into a line-structured prompt whose framing is plain ASCII. I did not establish that a name
can carry a line break through the rename path, so this is a question, not a finding; it is answered by
renaming a session to a string containing a newline and reading what the next delivery types.

**The Fleet Manager conduct SKILL describes only what the product allows.** Extracted every command it
names and checked each against the command line and the guard: `session spawn` (always `--controlled-by
self`, `:193-208`), `session hold`, `session stop`, `session done` (`request-deletion`) and
`session buffer` are all on the allow list (`SessionKeyGuard.cs:280-308`, `:201-202`); `fleet-manager
show/set/clear` reaches `GET`/`PUT /gateway/fleet-manager`, allowed at `SessionKeyGuard.cs:666`;
`fleet events`/`fleet ack` at `:508-512`; `message send`, `message inbox`, `mission create`,
`machine list`, `schedule list`, `director list`, `workflow instructions` all allowed. Every one exists
in `tools/cc-devthrottle/src/cli.py`. "Answering a session" (`fleet-manager.skill.md:231-254`) is the
queue, the ruling is carried out, and `cc-devthrottle session prompt` appears nowhere in it. There is no
`.claude/skills/fleet-manager/` copy to drift.

**The rail-test fix is correct and complete.** All seven converted classes now carry zero plain `[Fact]`
or `[Theory]` — `SessionRailStateTests` 19 `[AvaloniaFact]`, `SessionRailTreeTests` 18, `StatusPaletteTests`
8+1, `OfflineFloorRailColorTests` 5+5, `SessionRailModelBadgeTests` 4, `SessionViewModelAgentBadgeTests`
3+1, `PaletteAgreementTests` 1. The claim "no assertion was changed anywhere" holds mechanically: every
added or removed line in `src/CcDirector.Avalonia.Tests` across the whole scope is an attribute, a
comment or a `using`. Complete for the implicated pattern: no remaining plain-`[Fact]` class in that
project references `SessionViewModel` or `StatusPalette`, and none pumps `Dispatcher.UIThread.RunJobs()`
or constructs a control (checked per test method, not per file — the two mixed classes that own a `Pump`
helper, `MicCaptureConstructionQueriesNoDeviceTests` and `MicStartTimesTests`, call it only from their
`[AvaloniaFact]`s). The assembly disables parallelisation (`TestParallelization.cs`), so order within one
sequential run is exactly the variable the fix removes.

**The three tests the builder says are main's really are main's.** Read run `35270394665`, job
`105368330359` directly: `headBranch: main`, `headSha: e38217bc`, conclusion `failure`, and the failing
names are exactly the three claimed —
`Core.Tests.Wingman.ContentTurnRuleTests.With_the_switch_off_the_log_still_records_what_the_rule_would_have_done`,
`Gateway.Tests.Wingman.TurnVerdictServiceTests.HeldSession_…`,
`Gateway.Tests.Fleet.FleetManagerEventServiceTests.MoreEventsThanOnePromptCarries_…`. In the same job all
**nineteen** `SessionRailStateTests` passed and none failed, so the rail failures were the mission's to
fix, as the builder said. The proof the fix worked is still pending: main's run for `ddc78ac4`
(`35288524962`) was in progress while this was written.

**Nothing left behind, and no attribution.** Swept the whole scope diff (40,822 lines): no
no attribution trailer of any kind, no generated-with footer, no robot footer, no mention of any assistant or vendor — the
only hits on those vendor words name an agent kind the product runs. No `TODO`/`FIXME`/`HACK`/`XXX`, no
`Console.WriteLine`/`Debug.WriteLine`/`Debugger.Break`, no `Skip =`, no `.only(`/`fit(`/`fdescribe(`. One
`console.log` at `packages/client-core/browser-tests/dev-report-viewer-proof/run-proof.mjs`, which is a
proof harness's own progress line and belongs there.

**The words.** Core unit `RetiredMessagingWords|FleetPreamble`: **5 passed**. Core tests
`FleetPreamble|RetiredMessagingWords`: **49 passed**. `docs/new_architecture/sessions.html` row 10 is in
the past tense with the removal date and the replacement, as ruling 2 asked, and names the verb as "the
`ask` subcommand of `cc-devthrottle message`" so the page can sit in the inventory. `docs/cli-reference.md`
matches the product where I checked it: the `--controlled-by` paragraph's claim that a session may not
name another session as owner is enforced at `src/CcDirector.Gateway/Api/SpawnOrigin.cs:148`.

## What this inspection does not establish

- Nothing ran against PostgreSQL, and nothing ran live: no Director, no agent, no phone, no hosted
  Gateway. The two probes ran against an in-process hosted Gateway with fake tunnel Directors on real
  device keys, which is what makes finding 1 a proof rather than a reading.
- `scripts/test-local.ps1` was not run (no PowerShell on this machine), so the two installer suites were
  not run at all and nothing here was gated by the script the repository's rule names.
- The Gateway **route** suite was run only as `WorkspaceRestoreRouteTests` (17) plus the two probes; the
  Gateway **unit** suite only as the four filters above (452). The Core tests suite, the web suites and
  the command-line suite were not run in this seat.
- Finding 5 is read from the code and not run. The event-prompt question at the end of the census is
  explicitly not proven either way.
- Windows and Linux were not run; the desktop numbers above are this Mac's.
