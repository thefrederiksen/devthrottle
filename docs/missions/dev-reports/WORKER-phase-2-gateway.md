# Dev Reports phase 2 - Gateway record and delivery - Worker report

Branch `dev-reports/p2-gateway`, worktree `D:\ReposFred\devthrottle-dev-reports-p2-gateway`, cut from
828eb0482. Everything in `PLAN-phase-2.md` except the tool.

## What was built (file list)

Gateway (`src/CcDirector.Gateway/`):

- `Data/Entities/DevReportEntity.cs`, `DevReportVersionEntity.cs`, `DevReportItemEntity.cs`,
  `DevReportReplyEntity.cs` - tenant scoped, Gateway minted keys.
- `Data/GatewayDbContext.cs` - four tables (`dev_reports`, `dev_report_versions`, `dev_report_items`,
  `dev_report_replies`), unique indexes (tenant, session, key), (tenant, report, version),
  (tenant, report, client item id), tenant query filters, collation "C" on the Postgres natural keys.
- `Data/Migrations/20260916193150_AddDevReports` (SQLite) and
  `src/CcDirector.Gateway.Migrations.Postgres/Migrations/20260916193221_AddDevReports` (Postgres), generated
  last. `has-pending-model-changes` answers no changes for both (the Postgres one run with a build).
- `DevReports/DevReportItemStates.cs` - the state fold: queued, held, delivered, replaced, refused, and the
  labels the clients render verbatim.
- `DevReports/DevReportItem.cs` - item and anchor shapes per CONTRACT.md section 3; a batch is parsed
  whole or refused whole.
- `DevReports/DevReportPromptFold.cs` - the prompt the agent receives.
- `DevReports/DevReportStore.cs` - publish (new report or new version, SHA-256 of the bytes), list, get,
  versions, items, replies, open counts, replacement of an earlier answer to the same question.
- `DevReports/DevReportDelivery.cs` - send and drain, one lock per (account, session).
- `DevReports/DevReportTurnEndLauncher.cs` - turn end to drain, fire and forget, never throws.
- `DevReports/DevReportTitle.cs` - title from the report's `<title>`, else the header, else the file name.
- `Api/DevReportEndpoints.cs` - every session and owner route.
- `Util/SessionKeyGuard.cs` - the session routes allowed; owner routes stay refused.
- `GatewayHost.cs` - construction, liveness, turn end wiring, route mapping.
- `src/CcDirector.Core/Sessions/SubmissionProvenance.cs` - route `gateway-dev-report`.

Tests:

- `src/CcDirector.Gateway.UnitTests/DevReports/DevReportPromptFoldTests.cs`, `DevReportItemStatesTests.cs`,
  `DevReportItemParseTests.cs`, `DevReportDeliveryTests.cs`.
- `src/CcDirector.Gateway.UnitTests/SessionKeyGuardTests.cs` (allow and refuse rows),
  `Rules/RulesTypeNothingGuardTests.cs` (the delivery named as a direct caller of the prompt send seam).
- `src/CcDirector.Gateway.Tests/DevReportRoutesHostedTests.cs` - 12 tests on a real Gateway with a fake
  tunnel Director.

Logging: every public method in the store, delivery, launcher and endpoints logs entry, result and
failure with `FileLog.Write("[Class] Method: ...")`.

## Every route with its real answer

Taken from the hosted run on 2026-09-16 (the logged request and answer lines), except where marked.

Session routes (the caller's own session key, and the path session must be the caller):

| Route | Answer |
|---|---|
| `POST /sessions/{sid}/dev-reports` `{key, html}` | 200 `{"report":{"id":"9a270088-...","sessionId":"6f9e94fa-...","key":"C:\\work\\journey.html","title":"Gateway failures","status":"waiting-on-you","version":1,"publishedAtUtc":"...","updatedAtUtc":"...","sessionEnded":false,"openItems":0},"created":true}` |
| same, 10485761 bytes | 413 `{"error":"This report is 10485761 bytes. A dev report can be at most 10485760 bytes (10 megabytes).","code":"report_too_large","bytes":10485761,"limitBytes":10485760}` (exactly 10485760 answered 200) |
| same, wrong shape | 422 `{"error":"The report does not have the dev report shape: 4 problem(s). Fix every one and publish again.","code":"shape_check_failed","errors":["The report has no header. Add an element with data-dev-report=\"header\" ...", ...]}` |
| same, with a device key | 403 `{"error":"Only a session may call this route, with its own session key.","code":"session_key_required"}` |
| same, bad body (from the code, not run) | 400 `bad_request_body` |
| `GET /sessions/{sid}/dev-reports/{reportId}` | 200 `{"report":{...},"items":[{"id":"n1","kind":"note","text":...,"status":...,"statusLabel":...,"sentAtUtc":...,"deliveredAtUtc":...}],"replies":[...]}` |
| same, another session's key | 403 `{"error":"A session may publish, read and reply only on its own reports, and 126646f9-... is not this session.","code":"not_your_session"}` |
| `POST /sessions/{sid}/dev-reports/{reportId}/replies` `{text}` | 200 `{"reply":{"id":"9c2ee1fb-...","text":"Fixed - see section 2","at":"2026-09-16T22:02:16.7735692Z"}}` |
| same, a report that is not this session's | 404 `{"error":"There is no dev report b12dbadc-... here.","code":"report_not_found"}` |
| same, empty or over 20000 characters (from the code, not run) | 400 `reply_empty` / `reply_too_long` |
| `GET /sessions/{sid}/dev-reports` | 200 `{"count":N,"reports":[summary...]}` - NOT exercised over the wire (see below) |

Owner routes (device key; refused to every session key by the guard, and again by each route itself):

| Route | Answer |
|---|---|
| `GET /dev-reports?sessionId=` | 200 `{"count":1,"reports":[{summary}]}`; another account: 200 `{"count":0,"reports":[]}` |
| `GET /dev-reports/{reportId}` | 200 `{"report":{...},"items":[],"replies":[]}`; another account: 404 `report_not_found` |
| `GET /dev-reports/{reportId}/html?version=` | 200 `text/plain; charset=utf-8`, the exact stored bytes, header `X-Dev-Report-Version`; unknown version 404 `version_not_found`, non-number 400 `bad_version` (these two from the code) |
| `POST /dev-reports/{reportId}/send` `{items}` session working | 200 `{"updates":[{"id":"n1","status":"held","statusLabel":"Delivered when the agent finishes its turn"},{"id":"a1","status":"held",...}]}` |
| same, session idle | 200 `{"updates":[{"id":"a1","status":"delivered","statusLabel":"Delivered to the session"}]}` |
| same, session ended | 200 `{"updates":[{"id":"a1","status":"refused","statusLabel":"This session has ended"}]}` |
| same, a malformed item | 400 `{"error":"Nothing was sent: item 2 is not a valid note or answer: a note must carry an anchor object.","code":"malformed_item"}`, and the detail afterwards shows `"items":[]` |
| same, another account | 404 `report_not_found` |
| any owner route with a session key | 403 `{"error":"a session key may not call GET /dev-reports; ...","code":"session_key_out_of_scope"}` (GET list, GET detail, GET html, POST send all shown) |

Delivered prompt: a single prompt per drain, `AgentDriven=false` (an owner turn), provenance route
`gateway-dev-report`, text per the fold pinned in `DevReportPromptFoldTests`.

## What is proven and how

Unit tests (`Gateway.UnitTests`, real migrated SQLite through `GatewayDbTestHarness`):

- Prompt fold byte for byte: table-cell (with and without row or column label), svg-part (labelled and
  unlabelled, with and without quote), text, element; answers with and without comment; the "changes the
  owner's earlier answer" line; text verbatim (leading spaces, newlines, quotes); several reports.
- State fold: every outcome to its state and label; open states.
- Item parsing: the contract examples; every malformed shape refuses the whole batch; 20000 at the
  limit accepted, 20001 refused.
- Delivery: working holds; idle delivers one prompt with provenance; the same id twice is one item and
  one prompt; resend while held; a later answer replaces a held one (replaced, one prompt carrying the
  later answer); a later answer after delivery carries the change line; ended refuses and stores
  nothing; a drain across two reports is one prompt; busy again keeps items held; a send that never
  left stays held; an unanswered send is "not confirmed" and never retried; a send racing a drain
  delivers each item exactly once; held items survive reopening the database and drain once; the
  account scope is entered around the send; another account's items are invisible; publishing the same
  key makes version 2.
- Session key guard: allow rows for the four session routes, refusal rows for every owner route shape.

Hosted tests (`Gateway.Tests`, real Gateway, 12/12 green): the full journey (publish, send while working,
held, turn end, exactly one prompt, agent reads detail, agent replies); idempotent resend while idle;
another account gets 404 on read, html and send; html bytes and version header; a session key refused
every owner route; a device key refused publish; session A cannot publish, read or reply for session B;
a session whose Director closed it is refused "This session has ended"; held items survive a Gateway
restart and the next Gateway's catch-up delivers them once; exactly 10 MB accepted, one byte more 413
with the exact body; wrong shape 422 with every error; malformed send 400 with nothing stored.

A real bug found by the restart test and fixed (commit 2750f9bd4): on a hosted Gateway the tunnel send
drops a command when no account scope is ambient. Deltas carry that scope, but the catch-up sweep after
a restart raises the turn end without one, so held items were never delivered. The delivery now enters
the account's scope around the send; a unit test pins it and the hosted restart test proves it.

### Revert proofs (guard, test, red message)

Each guard was reverted on committed code, the test run with a build and watched red, then restored
with `git checkout`, diff confirmed clean, and rerun WITH a build (never `--no-build`).

| Guard reverted | Test | Red message |
|---|---|---|
| Idempotency: known ids no longer skipped in `DevReportDelivery.SendAsync` | `SendAsync_SameIdTwice...`, `SendAsync_SameIdResentWhileHeld...`, `SendAsync_LaterAnswer...` | `DbUpdateException` (the unique index on client item id is the second layer that caught it) |
| Owner check: guard allows `GET dev-reports` for a session key (unit) | `SessionKeyGuardTests.The_account_surface_is_refused` | `GET /dev-reports must NOT be allowed` (3 failed) |
| Owner check (hosted) | `ASessionKey_IsRefusedEveryOwnerRoute` | `Assert.Equal() Failure: Expected: Forbidden Actual: OK` |
| Session key scoping: path session no longer compared with the caller in `OwnSession` | `OneSessionsKey_CannotPublishReadOrReplyForAnotherSession` | `KeyNotFoundException`; the log shows the publish to B's path answered 200 under A's key |
| Ended refusal (unit): the ended branch in `SendAsync` disabled | `SendAsync_EndedSession...` | `Assert.All() Failure: 2 out of 2 items in the collection did not pass` |
| Ended refusal (hosted): a history ending read as busy | `SendToASessionItsDirectorClosed_IsRefusedThisSessionHasEnded` | `Expected: "refused" Actual: "held"` |
| Held while working: busy treated as idle | `SendAsync_SessionWorking...`, `DrainAsync_SessionBusyAgain...` | `Assert.All() Failure: 2 out of 2 ...`; `Assert.Empty() Failure: Collection was not empty` |
| Restart drain: account scope no longer entered for the send | `HeldItems_SurviveAGatewayRestart_AndTheNextGatewaysCatchUpDeliversThemOnce` | `Timed out waiting for the held items to be delivered after the restart` |

Restore runs: unit 338 passed (the dev report and guard tests); hosted 12/12 passed.

The four hosted mutations were applied together in ONE mutated build, because each hosted run waits on
the machine-wide Gateway test lock (other sessions held it for long stretches). They touch disjoint code
and each red belongs to its own test; the other eight hosted tests, including the journey, stayed green
in that build, which shows no mutation leaked into another test.

### The owner routes refuse a session themselves (added on the Manager's instruction, commit 856d93bcb)

Every owner route (`GET /dev-reports`, `GET /dev-reports/{id}`, `GET /dev-reports/{id}/html`,
`POST /dev-reports/{id}/send`) now checks `AuthMiddleware.CallingSession` first and answers 403
`session_key_out_of_scope` to any session identity, so an edit to the guard's allow list is not enough to let
an agent read the owner's queue or send in the owner's name. In normal operation the guard still answers
first, so the real answer is unchanged. Proof, on committed code, each step built: with the guard mutated to
allow `dev-reports/...` for GET and POST, `ASessionKey_IsRefusedEveryOwnerRoute` stayed GREEN, and all four
refusals carried the route's own message (`"a session key may not call GET /dev-reports; the dev report owner
routes are for the owner's own devices, never a session."`), which shows the route check, not the guard,
answered. With the route check also disabled it went RED: `Assert.Equal() Failure: Values differ Expected:
Forbidden Actual: OK`, the log showing `GET dev-reports -> 200` under the session key. Restored with
`git checkout`, no mutation left in either file, rebuilt without `--no-build`: all 12 hosted tests passed.

### Gates

- `.\scripts\test-local.ps1`: exit 0, 1,907 tests, all completed.
- `.\scripts\test-local.ps1 -Parked`: see the final line of this section, written when the run finished.
- A full `Gateway.UnitTests` run outside `-Parked` showed two failures that are not ours:
  `GatewayInputStatsAggregatorTests` failed once and passed alone (timing), and
  `HostedSchemaRefusesAnUnownedRowTests` could not reach Postgres (no rig outside `-Parked`).

The `-Parked` run (in its own detached worktree at 51ff27a2a, tracked session 94c0980c) exited 1 with
two failures:

- `CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres`
  - OURS. The proof keeps an explicit list of every column with collation "C", and the four dev report
    columns were not acknowledged in it (`Expected: [("account_trials","subject"), ("device_credentials","DeviceId"), ...]`,
    `Actual: [("account_trials","subject"), ("dev_report_items","ClientItemId"), ...]`). This also shows the
    Postgres migration applied on a real Postgres and put the collation where the plan says. Fixed in
    82f313241 by adding the four columns.
- `CcDirector.Setup.Engine.Tests.PythonToolsHealAndShimTests.InstallAsync_FailedVenvRebuild_LeavesNoManagedShim`
  ("managed shim survived a failed venv rebuild") - NOT ours. The branch changes nothing under `tools`
  (`git diff --name-only 828eb0482..HEAD -- tools` is empty). Run alone it PASSED on the parent commit
  828eb0482 (in a detached worktree) and PASSED on this branch. In the gate it took 31 seconds against
  under one second alone, with every suite running at once; it looks like a timing problem under load.

Everything else in `-Parked` passed: Gateway.UnitTests 4,962 passed (2 skipped by design), Core.Tests
4,420, Gateway.Tests 2,499 of 2,504 (the one above failed, 4 skipped by design), and the default suites.

After the fix, `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Postgres"` at 82f313241, with
its own throwaway Postgres: exit 0, TRX total 46, 42 passed, 0 failed, and the collation proof among the
passes. The 4 not run are the `GatewayDatabaseLivePostgresProofTests`, which need the hosted connection
variable and skipped in the full gate too. The rest of Gateway.Tests was not rerun, because the fix changed
only that test's list. That run went past the tool's ten-minute limit while it queued for the Gateway lock,
and the tool moved it to the background. It was left to finish rather than stopped, because stopping it
would have left its Postgres container and the lock behind.

## What is NOT proven

- The owner turn stamp on a real Director. Delivery sends `AgentDriven=false`, which the Director code maps
  to user input and `LastOwnerTurnAtUtc`; that is read from the code, not run end to end. The prompt has no
  surface, so it is not counted in the typed or voice tally.
- `GET /sessions/{sid}/dev-reports` (the session's own list) was not called over the wire; the store list
  under it is unit tested.
- A second send can land right after an idle delivery, before the roster shows the session working, and
  type into a turn that has just started. The roster is the only signal; nothing closes that window.
- Two Gateway processes at once (a deploy swap): the lock is in process, so a double drain is possible then.
- `director-stopped` counts as ended, per the plan. Ruled by the Manager: it stays ended and refused; a refused
  item stays in the owner's queue and can be resent once the session is restored and its history row reopens.
- A body above the 128 MB transport limit gets the server's own 413, not the dev report body.
- The title is capped at 200 characters (not in the plan).
- The Postgres migration was applied only inside the `-Parked` rig; no hosted database has run it.

## Fix round (the independent review, `REVIEW-phase-2.md`)

Brief: `BRIEF-phase-2-fix.md`. Commits f37a3339b (the fixes and their tests) and c1cd33791 (the crash test's fake
Director stops re-arming the crash once the Gateway is back, so its red says "sent again" rather than "crashed
again"). Every revert proof below mutated a committed tree, rebuilt, ran, restored with `git checkout -- src`, and
the restore runs rebuilt (no `--no-build` on a restore).

### Critical 1 - a crash between send and commit

What changed: a new state `sending`, label "Sending to the session" (`DevReportItemStates.SendingState`). The drain
commits every item a prompt carries to `sending` BEFORE the prompt leaves, then writes the answer: accepted is
delivered, unanswered is delivered "Sent to the session, not confirmed", never-left and a definite refusal go back to
held. Every settle pass first rules any item still in `sending` delivered "Sent to the session, not confirmed" and
never sends it. "No drain in this process owns it" is made true by the lock: the only drain that could own an item
holds the same per-(tenant, session) lock and commits a final state before it releases it, so anything in `sending`
seen under the lock was left by a restart or by a drain that threw. `sending` counts in `openItems`
(`IsOpen`), but is NOT replaceable by a later answer and is never drained (`IsWaiting` is queued or held only).

Tests:
- `SettleAsync_GatewayDiesAfterTheDirectorAccepted_TheNextProcessNeverSendsItAgain` (unit). The fake Director
  accepts, and the Gateway's next clock read - the one taken to record the answer - throws, so nothing after the
  send is written. A new store and delivery over the same database then settle an idle session.
  Red without the `sending` commit: `Assert.Equal() Failure: Values differ, Expected: 0, Actual: 1` - the new
  process sent it again.
- `SettleAsync_AnItemFoundSendingInTheStore_IsSettledNotConfirmedAndNotSent` (unit) and
  `AnItemACrashLeftSending_OnTheNextGateway_IsSettledNotConfirmedAndNeverSent` (hosted: item set to `sending`, the
  Gateway stopped, a NEW Gateway started, the session reported idle, the watcher's catch-up sweep run, the owner reads).
  Red without the orphan settle: unit `Assert.Equal() Failure: Values differ`; hosted
  `Expected: "delivered", Actual: "sending"`.

A first attempt to stand in for the crash by throwing from the fake Director was wrong and did not go red:
`DirectorCommandRouter.TrySendAsync` turns a thrown send into a synthesized failure, so it read as unanswered. The
clock seam is where the death is now.

Not proven: a real process kill. Two Gateway processes at once (a deploy swap) share the database but not the lock:
the other process's settle can rule an item this one is still sending "not confirmed", and this one then overwrites
it "delivered". Neither sends it twice; the label can move once.

### High 1 and High 2 - held items for an ended session, and idle sessions no transition reaches

What changed: one pass, `DevReportDelivery.SettleAsync(tenant, session)`: orphaned sends first, then Ended refuses
every held item "This session has ended", Idle drains, Busy leaves them. `DrainAsync` is gone; the send route, the
turn-end launcher, the timer and the owner's read all call the one pass. Callers:
- (a) the turn-end launcher, as before.
- (b) `DevReportSettleSweep`, a `TenantScopedSweep` on the `SessionHistorySweep` pattern exactly: GatewayHost owns a
  `System.Threading.Timer` every 30 seconds with an overlap guard and a boundary try/catch, `ForEachTenantAsync`
  enters each tenant's scope, the body reads `ITenantContext.Current` and settles each distinct session with a queued,
  held or sending item (`DevReportStore.SessionsWithOpenItems`). Its comment says it is what reaches a Director that
  reconnects already idle and a session that exits. Test seam `GatewayHost.DevReportSettleSweepScheduleForTests`.
- (c) `GET /dev-reports/{id}` settles the report's session before answering; `POST /dev-reports/{id}/send` settles
  inside `SendAsync`, under the lock.

Tests:
- `HeldItem_ThenTheSessionCloses_TheOwnersDetailShowsItRefused` (hosted, 30-second timer never fires in it). Red with
  the settle removed from the read route: `Expected: "refused"` (it read held).
- `HeldItem_DirectorDropsAndReconnectsAlreadyIdle_TheSettleTimerDeliversItOnce` (hosted, its own Gateway with a 300 ms
  timer): the watcher sees the session waiting, the Director drops, the owner sends (held), the Director reconnects
  reporting the same waiting state, exactly one prompt arrives. Red with the timer callback made a no-op:
  `Timed out waiting for the settle timer to deliver the held item`.
- `SettleAsync_HeldAndTheSessionHasEnded_RefusesThemAndTypesNothing` (unit). Red with the Ended branch emptied:
  `Assert.All() Failure: 2 out of 2 items in the collection did not pass`.

Not proven: the timer on the hosted deployment. `GET /dev-reports` (the list) does not settle; its `openItems` can be
stale for up to one timer tick. One session whose settle throws stops the rest of that tenant's sessions for that tick
(the per-tenant isolation of `TenantScopedSweep`, followed as found).

### High 3 - the idle check and the send are not atomic - ACCEPTED GAP, NOT FIXED

Nothing changed in the code but the class comment of `DevReportDelivery`, which names it. The window: the settle pass
reads the pushed roster and sees the session waiting; the prompt is then composed, committed to `sending` and sent
over the tunnel with `WaitForIdle = false`; the Director's prompt verb (`SessionCommandExecutor.SendPromptAsync`)
refuses only an exited or failed session and writes the text straight to the session. A turn that starts anywhere
between that roster read and the Director writing the text - an agent resuming by itself, or the owner typing
directly into the terminal - receives the owner's items mid-turn. Its width is the settle's own database work plus
one tunnel round trip, plus however stale the pushed roster already was. Closing it needs the Director to refuse a
prompt to a working session, which is a Director change shipped in a release. No Gateway-side re-check was added.

### High 4 - a definite Director refusal recorded as delivered

What changed: `SessionVerbClient.PromptSendKind.DirectorRefused`, returned for the prompt verb's `Conflict`
("session has exited") and `NotFound` ("session not found") - both returned by `SessionCommandExecutor.PromptAsync`
before it touches the session. Every other failure is still `Unanswered`. Session Rules map `DirectorRefused` to
`Unknown` exactly as before, explicitly, with a comment (`GatewayRuleEnvironment`), and so does the turn verdict
answer channel (`Unanswered`). `PostPromptAsync` already passed any non-never-left kind through as the detail. For
dev reports a refusal puts the items back to held, then re-reads the session's reach: ended refuses them; otherwise
they wait for the next settle pass and are NOT re-sent in the same pass, so a Director that keeps refusing a session
the roster still shows idle cannot loop.

Tests:
- `SettleAsync_DirectorRefusesAndTheSessionHasEnded_IsRefusedNotDelivered` (unit, theory over `Conflict` "session
  has exited" and `NotFound` "session not found", the Director's real shapes).
- `SettleAsync_DirectorRefusesWhileTheRosterStillSaysIdle_StaysHeldAndIsNotSentAgainInThatPass` (unit).
- `A_director_that_refused_an_exited_session_is_still_unknown_for_rules` (rules, added beside the existing NotFound
  case). The rules guard tests (`RulesTypeNothingGuardTests`) and every rules test stayed green.
Red with the refusal mapping reverted to unanswered: all three dev report cases failed,
`Assert.Equal() Failure: Values differ` (they read delivered, not confirmed).

Not proven: a refusal from a live Director; the shapes are taken from the executor's code.

### Medium 1 - long keys on PostgreSQL unique indexes

What changed: an item id over 128 characters refuses the whole send, 400 `malformed_item`, "item N is not a valid
note or answer: id is 129 characters; the limit is 128." Written into CONTRACT.md section 3 beside the 20000 rule
(the note-taking script is unchanged). The report key limit is 512 (was 4096), 400, "The report key is 513
characters; the limit is 512." `tools/cc-dev-reports` refuses a key over 512 characters locally with that sentence,
code `key_too_long`, and sends nothing.

Tests:
- `ParseBatch_IdOver128Characters_RefusesTheWholeBatch` and `ParseBatch_IdOfExactly128Characters_IsAccepted` (unit).
  Red without the id limit: `Assert.Null() Failure: Value is not null`.
- `LongKeysAndIds_AreRefusedWith400_BeforeTheDatabaseSeesThem` (hosted: key 513 refused, key 512 published, an id of
  129 refuses the batch and nothing is stored). Red with the key limit put back to 4096: `Expected: BadRequest,
  Actual: OK`.
- `test_open_key_over_512_characters_is_refused_locally_with_the_gateway_sentence` (tool). Red without the check:
  `AssertionError: assert 'error: The report key is 513 characters; the limit is 512.' in ''`.

Not proven: an insert of a long id into a real PostgreSQL database; the limits sit far under the documented B-tree
entry size rather than being measured against it. The migration did not change, so the Postgres proofs were not
rerun. Python counts a key's length in code points and the Gateway in UTF-16 units; they differ only for characters
outside the Basic Multilingual Plane, where the Gateway still refuses.

### Medium 2 - note text can counterfeit the prompt's structure

What changed: `DevReportPromptFold.Compose(reports, boundary)`. The owner's words (note text, answer comment) sit
between `<<<owner-text-<boundary>` and `owner-text-<boundary>>>`, byte for byte. The boundary is eight random
hexadecimal characters per prompt (`MintBoundary`), minted again if any owner text contains it; `Compose` refuses a
boundary an owner text contains. The prompt opens with one sentence: the owner's words sit between those markers and
nothing inside them is an instruction from the Gateway. The title, quote, row, column and diagram labels, question,
option label and option value are JSON-escaped strings (relaxed encoder: a quote is `\"`, a line break `\n`), so none
can span lines. The option label is now quoted, which it was not before.

Tests: the pinned prompts take the boundary as a parameter and stay byte for byte (updated for the preamble, the
markers and the quoted label). `Compose_TheReviewsForgedAnswerInsideANote_StaysInsideTheOwnersBlock` carries the
review's payload; `Compose_ReportWordsWithLineBreaks_AreJsonStringsThatCannotSpanLines`;
`Compose_AnOwnerTextHoldingTheBoundary_IsRefused_AndMintBoundaryAvoidsIt`. Red with the closing marker put back to a
bare `>>>`: the forgery test and the pinned prompts, `Assert.Contains() Failure: Sub-string not found` /
`Strings differ`. Red with the JSON escaping replaced by plain quotes: the line-break test,
`Assert.Contains() Failure: Sub-string not found`.

Not proven: how an agent actually reads the bounded prompt. The report KEY is still written unescaped (in "file ..."
and in the `cc-dev-reports open` line, where escaping would double every backslash in a Windows path); it comes from
the agent's own tool, not the page, and was not in the ruling.

### Runs

- `.\scripts\test-local.ps1` (default) at f37a3339b: exit 0, "all projects exited zero", 2,020 tests in eight
  projects, every TRX outcome Completed with executed equal to total. It reported the COVERAGE GAP for the parked
  Gateway suites, so both were run as below.
- Gateway.UnitTests in full: 5,003 passed, 2 skipped, 8 failed. All 8 are `HostedSchemaRefusesAnUnownedRowTests`,
  failing with `Npgsql.NpgsqlException : Failed to connect to 127.0.0.1:55432` (connection refused): they need the
  PostgreSQL that `-Parked` builds, and nothing here touches that schema. Dev reports and rules alone, restore run
  after the last commit: 382 passed, 0 failed.
- Gateway.Tests, `DevReportRoutesHostedTests`: 16 of 16 passed, before and after the revert proofs (the first run
  queued about six minutes for the Gateway test lock behind two other sessions).
- `tools/cc-dev-reports`: 25 passed.
