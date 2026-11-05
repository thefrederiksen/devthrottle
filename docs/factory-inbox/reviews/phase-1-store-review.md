# Factory Inbox, phase 1, Task A - review of branch `factory-inbox/store`

Reviewed on 4 November 2026, by a separate review session, against origin/main `31db15d92`.
Branch commit under review: `e1d48573e` ("Factory Inbox phase 1: the store, both migrations, the routes and the
session key guard"), one commit, 22 files, 18,205 lines added (14,572 of them the two generated Designer files).

Judged against: `MISSION.md` sections 5 and 7, the Task A section and the FINAL JSON contract in `phase-1-plan.md`,
`docs/CodingStyle.md`, and the repository rules in `CLAUDE.md` (the Gateway owns every state word, no fallback
programming, the verbatim transcription rule).

## Verdict in one paragraph

The store, the routes, the guard and both migrations do what the plan says, and the four things I was told to look at
hardest hold: delegated is accepted only for a session whose recorded parent is the deciding Boss; a claim held by a
session the Gateway has recorded as ended is taken by the next Secretary by one conditional update; every query is
scoped to the account by the context's tenant filter and to the factory by an explicit predicate; the guard lets the
ten shapes through and refuses the owner's send, and the route refuses it a second time. Four findings below. One is a
crash I reproduced (a spoken send with a correction that has no `find` answers an internal error instead of a sentence).
One is a gap between the verbatim rule as written in `CLAUDE.md` and what the store actually proves. Two are low. The
Developer decides on each.

## Scope

### What I read

Every non-generated file in the diff, in full: `FactoryInboxStore.cs`, `FactoryInboxEndpoints.cs`,
`FactoryInboxEntities.cs`, the `GatewayDbContext.cs` diff, both `AddFactoryInbox` migrations (SQLite and PostgreSQL,
the non-Designer halves), the `SessionKeyGuard.cs` diff, the `GatewayHost.cs` wiring diff, the
`FactoryDefinitionCheck.cs` diff, `FactoryInboxFold.cs`, `FactoryInboxWords.cs`, `FactoryInboxSeats.cs`, and all five
test files (`FactoryInboxStoreTests`, `FactoryInboxEndpointTests`, `FactoryInboxSessionKeyGuardTests`, the
`FactoryDefinitionCheckTests` diff, `FactoryInboxPostgresTests`, and the `PostgresProviderProofTests` census diff).

On origin/main, the code the routes depend on: `SessionHistoryStore.FactoryOf` and `SessionFactoryLookup`
(`SeatIsInherited` is the rules-only kit), `GatewayHost.DevReportSessionLiveness` and `DevReportSessionReach`,
`SpawnOrigin.TryEstablish` and `FactorySeatStartEndpoints` (where `ParentSessionId` is set on a spawn request),
`SessionHistoryRecorder` (when a history row first exists), `SessionOriginSurfaces.FromDeviceType`,
`FactoryAgentsGate.Group`, the existing factory matchers in `SessionKeyGuard` and the route prefixes of every existing
factory endpoint file (to rule out a path collision with `/gateway/factory/...`), `DevReportStore.ClaimUnactedItems`
(the model the claim is copied from), `TranscriptEditEngine.Apply`, `Validate` and `ReplaceWithBoundaries`, and
`GatewayTranscriptionService.CleanupCoreAsync` (where the tenant's listed corrections come from).

I did not read the two Designer files or the two model snapshots line by line; I proved them instead (below).

### What I ran

Nothing was built, edited or run in the worktree. I exported the commit with `git archive` into a scratch folder and
ran everything there.

1. `dotnet build` of `CcDirector.Gateway.UnitTests` (Debug): 0 warnings, 0 errors.
2. `dotnet test` of that project with the filter
   `Gateway.Tests.Factory.Inbox | FactoryDefinitionCheckTests | MigrationTests | SessionKeyGuard`:
   833 passed, 0 failed, 0 skipped. The `MigrationTests` classes each assert `HasPendingModelChanges()` is false, so
   the SQLite model snapshot matches the model with the four new tables in it.
3. `scripts\test-database.ps1 -Filter "FactoryInboxPostgresTests | PostgresProviderProofTests"` on a throwaway
   PostgreSQL in Docker: 7 passed, 0 failed, 0 skipped. That is the PostgreSQL migration applied by the Gateway's own
   start-up path, the store run end to end on it, and the collation census (which this branch also fixes - see the
   notes at the end).
4. A test of my own, out of tree, because the new PostgreSQL test does not make the assertion its sibling migration
   tests make: on a fresh throwaway database, migrate to the newest migration, assert `HasPendingModelChanges()` is
   false, migrate down to `AddDevReportActionClaimsAndSeatIdentity`, migrate up to `AddFactoryInbox` again. Passed.
   So the PostgreSQL snapshot matches the model and the migration's `Down` works.
5. A reproduction of finding 1, out of tree, at both the store and the route (details under the finding).

### What I could not reach

- The hosted Gateway and a real Director: no live session called these routes. The identity stand-in in
  `FactoryInboxEndpointTests` puts the same items on the request the real `AuthMiddleware` would, and I checked the
  item keys and identity types against origin/main, but I did not run the real middleware in front of the routes.
- The `FactoryAgentsGate` switch in front of the group: I read it and did not exercise it.
- Task B (the `cc-devthrottle` commands) is not on this branch and was not reviewed.
- The full `scripts\test-local.ps1` and `-Parked` runs: not run by me. The Tech Lead's own run is the gate; my runs
  above are the parts this change can reach.

## Findings

### 1. A spoken send whose correction has no `find` crashes the route - reproduced

**Where.** `FactoryInboxEndpoints.cs`, the send route, builds the corrections as
`new TranscriptEdit(c.Find ?? "", c.Replace ?? "")`; `FactoryInboxStore.Send` then calls `TranscriptEditEngine.Apply`
with them. `Apply` calls `ReplaceWithBoundaries`, whose first line reads `find[0]` and `find[^1]`. On an empty `find`
that is an `IndexOutOfRangeException`.

**What breaks.** A body such as
`{"text":"Hello","source":"spoken","rawText":"Hello","corrections":[{"replace":"x"}]}` from the owner's phone answers
500 with no sentence. The store's contract ("a bad one is refused with a sentence") and the route's `Refusals` wrapper
(which catches only `FactoryInboxRefusedException`) are both bypassed. The engine has its own guard for exactly this -
`TranscriptEditEngine.Validate` rejects an edit with "empty find text" - and the dictation path runs `Validate`
before `Apply`; the inbox store calls `Apply` straight.

**Evidence.** Out of tree, in a copy of the unit test project: calling `Send` with `Corrections: [("", "x")]` throws
`System.IndexOutOfRangeException`; posting the body above through the mapped route (phone identity, registered
factory) answers status 500 with the same exception logged by a catch I put around the pipeline. Both ran once,
both reproduced.

**Why it must change.** The send is the one route an owner's device calls, and a device that ever serialises a
correction with a missing or empty `find` (a nullable field on the wire) turns a dictation into an internal error on
the Gateway rather than a refusal the phone can show. Not reachable by a session key (the route refuses those first).

### 2. The verbatim check proves the text matches what the client sent, not that the corrections were listed

**Where.** `FactoryInboxStore.Send` for a spoken message: it checks that `Apply(rawText, corrections) == text` and
stores the corrections it was handed. The corrections come from the request body, not from the tenant's dictionary.

**What breaks.** `CLAUDE.md`, the verbatim rule: "Only TWO changes are allowed between the speech model's output and
the agent's prompt: a deterministic, in-process, dictionary find/replace of individual terms the user listed by hand
..." and "Any edit whose Find is not in the tenant's Corrections list is a defect, not a feature." Here any
`{find, replace}` pair passes. A body with `rawText: "do not deploy"`, `corrections: [{find: "not", replace: "now"}]`,
`text: "do now deploy"` is accepted, stored as source `spoken`, and the row's own audit trail (`CorrectionsJson`)
says the change was a listed correction. The mission's section 7 test is worded "a spoken message equals the raw
transcript plus only listed corrections"; the branch's test `Send_SpokenWithItsListedCorrection_KeepsTheRawText`
passes a pair that nothing lists, so the word "listed" in its name is not what it proves. The phase 1 plan scoped
this to "store level: raw kept beside stored text", which is met; the rule in `CLAUDE.md` is wider than the plan.

**Why it must change, or be answered.** The Gateway already holds the tenant's list: `GatewayTranscriptionService`
reads it through `_dictionaryProvider(tenant)` (a `DictationDictionary`, whose `CommonMistranscriptions` map each
canonical term to its listed wrong forms). A check that each correction's `Replace` is a listed term (or that its
`Find` is one of that term's listed wrong forms) is the same deterministic rule the dictation path applies. If the
Developer's answer is that phase 4 moves the spoken path onto the Gateway's own dictation endpoint so no client ever
sends corrections, that answer should be written down, because until then this is an owner-facing route that will
store an unlisted edit as a listed one. The raw text is kept beside it, so the evidence of such an edit is queryable -
that part of the rule is met.

### 3. The owner's list and thread reads load every message of the account into memory - low

**Where.** `FactoryInboxStore.CountsByFactory` does `ctx.FactoryInboxMessages.AsNoTracking().ToList()` with no
predicate and groups in memory; `Thread` does `query.ToList()` on the whole factory and then orders and takes
`limit` in memory. Each row carries `Text` and `RawText` of up to 20,000 characters each.

**What breaks.** Nothing today. `GET /gateway/factory/inbox` is the list the phone and the Cockpit land on (mockup A)
and will be polled; nothing is ever deleted (by design, "NOTHING IS DELETED" on the entity). So every list call
grows linearly with the account's whole message history, text included, and the `(tenant_id, Factory, SentAtUtc)`
index added for "a factory's thread is read newest first" is not used for the newest-first read because the order
and the take happen after `ToList()`. A count-and-newest-per-factory and an ordered `Take(limit)` both translate on
both providers.

**Why I raise it at low.** It is a cost that only shows later, and the fix is small and local; the Developer may
decline it for phase 1 and note it for the screens phase, where the polling cadence is decided.

### 4. "Not started by this Boss" is also the answer when the Gateway does not yet have the delegated session's row - low

**Where.** The decide route reads `parts.ParentOf(tenant, delegated)`, which is the history row's `ParentSessionId`.
A history row is inserted by `SessionHistoryRecorder` when the Director's push stream first shows the session. A null
parent (no row yet) and a parent that is someone else both answer 403 `not_started_by_this_boss`, whose sentence
tells the Boss to "Start a session to do it". For the calling session itself, `Seat` distinguishes this case as
409 `session_not_yet_known` ("Try again in a moment").

**What breaks.** A Boss that spawns a session and decides `delegated` before the Director's next push is told the
session is not its own and to start one; an agent following that sentence starts a second session for the same
message. The window is one push cycle, and `cc-devthrottle session spawn` waits for the seat to appear on the roster
(the same push that creates the history row), so I could not make it happen and rate it low. The endpoint test
`Decide_DelegatedWithoutAStartedSession_IsRefused` pins the collapsed answer for "a session the Gateway has no record
of" on purpose; if the Developer keeps it, the test's comment should say the two cases are collapsed knowingly.

## Checked and found sound

- **Delegated only for a session this Boss started.** The route refuses unless `Guid.Parse(ParentOf(session)) ==
  caller.SessionId`; the parent is the one the Gateway stamped on the spawn request from the caller's own key
  (`SpawnOrigin.TryEstablish`, `FactorySeatStartEndpoints`), never from the body. A child that inherited the Boss seat
  (rules-only kit) is accepted as a delegate and refused as a decider. Tested at the route for: someone else's
  session, an unknown session, no session, and the Boss's own child.
- **Claims lapse when the claiming session ends.** `Receive` is one conditional `ExecuteUpdate` on
  `ClaimSessionId IS NULL OR = me OR IN (ended)`, the same shape as `DevReportStore.ClaimUnactedItems`; the ended set
  comes from `DevReportSessionLiveness` (roster Exited/Failed, or history `EndingKind`). A claim held by a live
  session is not taken, by id or by "everything". `ReceivedAtUtc` and `ReceivedBySessionId` keep the first
  Secretary's values when a second one takes the claim, so the trail shows when it was really received. Tested at the
  store, at the route, and on PostgreSQL.
- **Tenant isolation.** Every store method opens `_db.CreateContext(tenant)`; all four entities are in
  `ApplyTenantScope`, which the `ExecuteUpdate` calls also go through; every write sets `TenantId` explicitly; every
  session-route query is further scoped to `caller.Factory`, which is read from the caller's history row inside
  `_tenantBoundary.EnterScope(tenant)`. Another account reads nothing (tested on SQLite and PostgreSQL); another
  factory's Boss sees no bundles (tested at the route). The decision's unique index is `(tenant_id, MessageId)`.
- **The guard.** `IsFactoryInboxRoute` is consulted in both halves of `IsAllowed` (the reads and the writes) and
  rules by verb itself, so the placement is harmless. The nine allowed shapes and the owner's send are pinned by name,
  with ten neighbouring shapes refused. No existing matcher reaches `/gateway/factory/...` (the existing factory
  routes live under `/factory-definitions`, `/factory-records`, `/factory-briefs`, `/factory-memory`,
  `/gateway/factory-agents` and `/gateway/factories`), so no older rule lets the send through. The route refuses a
  session key and the machine token again itself.
- **State words on the Gateway.** `FactoryInboxWords` holds every stored word and every label; `FactoryInboxFold`
  builds `stateLabel`, `noteLabel`, the decision label and every trail sentence; the answer shapes match the FINAL contract
  field for field (I checked each of the eleven shapes).
- **No fallback programming.** Every bad input is a refusal with a code and a sentence; the one `catch` in the store
  (`Decide`, on `DbUpdateException`) re-reads the row to turn a lost race on the unique index into
  `already_decided` and rethrows anything else.
- **Migrations.** Columns, lengths, nullability and indexes match the entities on both providers; the PostgreSQL
  migration carries collation "C" on exactly the sixteen columns the context declares and the census lists; both
  snapshots match the model (runs 2 and 4 above); the PostgreSQL `Down` works (run 4).
- **The reserved seat name.** The definition check errors on a seat file whose key is `secretary`, case-insensitively,
  on the seat key's line; tested.
- **The collation census fix.** `cron_failure_notices.Cause` and `.JobId` were declared "C" in the context on
  24 October and never listed in the census, so the census test has been red since then. The branch adds them, and
  the census passes in run 3.

## Observations, not findings

- A Secretary whose Director goes offline is `Busy` ("not on the roster, and nothing says it ended"), not `Ended`, so
  its claim does not lapse until the interrupted ruling closes its history row. That is the dev report claim's
  behaviour too, and the mission gives the stuck alert (phase 2) for the owner to see it.
- `HandBundle` with explicit ids can hand the message a repeat was merged into without the repeat; the repeat is then
  decided with it (`Decide` picks up repeats by `MergedIntoId` regardless of bundle), so nothing is lost, and the
  repeat's trail has no "Handed to the Boss" step, which is true.
- The new PostgreSQL test proves the store on the migrated schema but, unlike its sibling migration tests, does not
  assert `HasPendingModelChanges()` is false or exercise `Down`. I did both out of tree (run 4); the Developer may
  want the assertion in the committed test so the next migration keeps it.
- `FactoryInboxWords.Surfaces.Session` exists and nothing produces it in phase 1. Harmless.

## Answers (the Developer, 5 November 2026)

All four findings are accepted and fixed, each with a test, and the observation about the PostgreSQL test is taken.

### 1. A correction with no `find` crashed the send - accepted, fixed

Fixed at the root by the answer to finding 2: the send no longer reads corrections from the client at all, so nothing
a client sends reaches `TranscriptEditEngine.Apply`. The store's only corrections now come from the dictation path's
own deterministic stage, which validates every edit before applying it. A body that carries a `corrections` field is
refused by name with 400 `corrections_not_accepted` and a sentence.

Test: `FactoryInboxEndpointTests.Send_ACorrectionWithNoFind_IsRefusedInWords_NotAnInternalError` posts the review's
exact body (`corrections: [{replace: "x"}]`) and gets 400 with the code, not 500.

### 2. "Listed" was not proved - accepted, fixed the way the Tech Lead proposed

A spoken send now carries `rawText` only. A body that carries `text` with a spoken source is refused
(`spoken_with_text`), and one that carries `corrections` is refused (`corrections_not_accepted`). The Gateway applies the
account's own dictionary, read exactly as the dictation path reads it (`TenantGlossary.Load`), through
`CleanupOrchestrator` with no judge - the dictation path's deterministic stage, which applies only hand-listed wrong forms
and never an unlisted correction. The store stores the raw text, the corrected text and exactly the corrections it
applied, and refuses to store a row whose raw text plus recorded corrections does not reproduce its text (a defect in
the correction stage would throw, never be stored).

This changes the send body of the contract: `{text, note, source}` for a typed message, and `{source: "spoken",
rawText, note}` for a spoken one. Task B has no send command, so it is not affected.

Tests:
- `FactoryInboxStoreTests.SendAsync_SpokenMessage_IsTheRawTranscriptPlusOnlyTheAccountsListedCorrections`: "Do not ask
  dev throttle for the totals now" becomes "Do not ask DevThrottle for the totals now"; "not" and "now" stay as said;
  every recorded correction is one of the account's listed wrong forms; raw plus the recorded corrections equals the
  stored text.
- `SendAsync_SpokenMessageWithNothingListed_IsKeptExactlyAsSaid`: an account with an empty dictionary stores the raw
  text unchanged with no corrections.
- `SendAsync_SpokenMessageThatAlsoCarriesText_IsRefusedAndNothingKept`, and at the route
  `Send_WithCorrectionsFromTheClient_IsRefusedInWords` with the review's "do not deploy" / "not" -> "now" case, and
  `Send_SpokenRawTextOnly_GetsTheAccountsListedCorrectionsAndNoOthers`.
- The PostgreSQL test sends a spoken message as raw text only and reads back the listed correction applied.

### 3. The list and thread reads loaded everything - accepted, fixed

`Thread` now orders and takes in the database, on the `(tenant_id, Factory, SentAtUtc)` index. `CountsByFactory` now
counts and finds each factory's newest time in one grouped query, then reads only the one newest message of each
factory; no other message text is loaded.

Tests: `FactoryInboxStoreTests.Thread_ALimit_ReturnsTheNewestFirst_AndTheWholeCount` and
`CountsByFactory_EachFactory_HasItsOwnCountAndNewestMessage` on SQLite; the PostgreSQL test runs both reads on the
migrated schema.

### 4. A session with no history row yet read as "not yours" - accepted, fixed

The routes' `ParentOf` now answers "no history row" (null) apart from "a row with this parent". No row answers 409
`delegated_session_not_yet_known`: "The Gateway has no record of session ... yet. If you just started it, try again in a
moment; do not start another." A row whose parent is someone else, or no one, is still 403 `not_started_by_this_boss`.

Test: `FactoryInboxEndpointTests.Decide_DelegatedToASessionTheGatewayHasNoRecordOfYet_SaysTryAgain_NotStartAnother`
gets 409 for the unknown session, then 200 once its row arrives naming the Boss as parent.
`Decide_DelegatedWithoutAStartedSession_IsRefused` now pins the two 403 cases (someone else's session, and a row with no
parent) and the missing session.

### The observation on the PostgreSQL test - taken

`FactoryInboxPostgresTests` now asserts `HasPendingModelChanges()` is false on the schema the Gateway's own start-up
built, as its sibling migration tests do. The `Down` run stays out of tree, as the review ran it.

### Runs after the fixes

- The Factory Inbox, definition check, session key guard and migration tests in `CcDirector.Gateway.UnitTests`: 877
  passed, 0 failed.
- `scripts\test-database.ps1` for `FactoryInboxPostgresTests` and `PostgresProviderProofTests`: 7 passed, 0 failed,
  0 skipped.
- `scripts\test-local.ps1`, with the package audit switched off on the command line only (the advisory on the .NET
  ahead-of-time compiler blocks every build until the newer SDK is installed): see the commit message.
