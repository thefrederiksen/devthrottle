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

Owner routes (device key; refused to every session key by the guard):

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
- `director-stopped` counts as ended, per the plan. That refuses owner sends to a session that may later be
  restored. Flagged for the Manager to rule.
- The owner routes are refused to session keys by the guard alone, by design; the routes themselves do not
  re-check the credential kind.
- A body above the 128 MB transport limit gets the server's own 413, not the dev report body.
- The title is capped at 200 characters (not in the plan).
- The Postgres migration was applied only inside the `-Parked` rig; no hosted database has run it.
