# Review of task C - done, return and the owner (pull request 4278)

Reviewer: session "Mail Desk - Reviewer - done, return and the owner", opened by the phase 4 Tech Lead (session 128).
Date: 20 October 2026. Reviewed commit: `16f43c17b` (the diff `git diff 064422f72 16f43c17b`).

## Scope

**Read, in full:**

- `docs/qa/mail-desk/phase-4/CONTRACT.md` (sections 1, 3.1, 4 and 7), and the reviewer's section of the method.
- Every changed source file: `Api/MailDeskEndpoints.cs`, `Util/SessionKeyGuard.cs`, `MailDesk/MailDeskRoutedStore.cs`
  (the new code, and the task B code it calls: `Take`, `RecordStartFailure`, `Reroute`, `Redirect`, `HandToOwner`,
  `Queue`, `Refuse`), `MailDesk/MailDeskRoutedFold.cs`, `MailDesk/MailDeskProblems.cs`, `MailDesk/MailDeskEmailSection.cs`,
  `Running/CronRunResultService.cs` (the resolve path), `Factory/Email/FactoryEmailComposer.cs` (and its `E`, `Item`,
  `SubHeading` helpers), `Data/Entities/MailDeskEntities.cs`, `Data/GatewayDbContext.cs`, `GatewayHost.cs`,
  `Contracts/CronRunRecord.cs`, `tools/cc-devthrottle/src/cli.py` and `src/maildesk_ops.py`.
- Both migrations (SQLite and PostgreSQL), including the backfill of `OwnerUtc`; the designer files only at a glance.
- The tests: `MailDeskOwnerTests.cs` in full, `AddMailDeskRoutedOwnerColumnsMigrationTests.cs` and
  `MailDeskOwnerPostgresTests.cs` in outline, and every test added to `tests/test_maildesk_ops.py`.
- Supporting code to check a claim: `GatewayCaller.KindOf` (how a Director and the machine token are told from a
  session), the tenant query filter on `mail_desk_routed`, and that no code deletes a decision or a channel (the reads
  throw when a routed row's decision or channel is missing).

**Ran:**

- `dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter
  "FullyQualifiedName~MailDesk|FullyQualifiedName~CronRunResult|FullyQualifiedName~FactoryEmail"` on devlinux, with
  `DOTNET_USE_POLLING_FILE_WATCHER=1` and a scratch `LOCALAPPDATA`: **784 passed, 0 failed, 0 skipped.**
- `python -m pytest tests/test_maildesk_ops.py tests/test_axi_step_6c_help_and_errors.py` in `tools/cc-devthrottle`
  (pytest supplied through `uv`, because the machine's Python has none): **660 passed, 1 skipped.**

**Could not reach:**

- `MailDeskOwnerPostgresTests` and the PostgreSQL migration were not run: they need `scripts/test-database.ps1` and
  Docker, which were outside what I was allowed to run.
- Nothing was run against a real Gateway, Director or session: whether the session id the starter records on a routed
  row is the same id a session's key carries (which is what lets the holding session pass the check at all) is proved
  here only by the unit tests' stand-in authentication. The rig (task D) is where that is proved for real.

## Verdict

**Accept.** The contract's section 4 is met. Two minor findings, neither blocking; the seat that built the work decides
what happens to them.

What I looked for and did not find:

- **Who may answer.** `done` and `return` compare the caller's session id, read from its own key, with the row's
  `SessionId`; a person and a Director carry a device type and are never a session (`GatewayCaller.KindOf`), the
  machine token is neither, so all three pass no session and are refused 403. A previous holder after a re-route, or
  after its own first return, no longer matches (`Queue` clears `SessionId`). A session of another account cannot see
  the row at all (the tenant query filter), so it gets 404, never another account's row. All of these are tested
  except the other account, which follows from the filter.
- **Nothing recorded on a refusal.** Every refusal in `Done`, `Return` and `Resolve` returns before the row is touched
  and before `SaveChanges`. The endpoint reads the body before the id, so a bad body or bad id is refused before the
  store is reached.
- **A return never loses the email.** The first return re-queues it at the channel's Mail Desk seat with the note;
  the router's existing queue picks it up (tested end to end through the router: the Mail Desk agent's prompt and
  `check.json` carry the note). A second return, or one by the Mail Desk agent, hands it to the owner. Nothing moves a
  row out of `returned`, so it cannot be lost after that either.
- **The problem.** One per row handed to the owner, on the channel's factory, resolvable by hand; resolving sets only
  the resolve columns, the row stays `returned`, and since no code takes a row out of `returned` the problem cannot
  come back. The migration backfills `OwnerUtc` for a row handed to the owner before the column existed, so the fold's
  "no hand time" error cannot fire on existing data. With the switch off the account has no such problem.
- **Sender and subject.** No log line added in this diff carries a sender, subject or note: the endpoint, store, fold
  and resolve logs carry ids, states, seats and counts. In the 08:00 email every line goes through `E()` in the HTML
  part and `OneLine` in the text part; the problem's sentence passes sender and subject through `OneLine`.
- **The command (AXI).** An unknown flag fails with exit 2 and sends nothing; every filter is sent to the Gateway and so
  applies to `--json`, which prints the Gateway's answer in its own shape; an empty list prints `count: 0`; ids are
  printed in full; `return` without `--note`, or with a blank one, exits 2 before any request; a Gateway refusal prints
  its error and sentence and exits 1; the switch off says so and exits 1; a `done` answered with a row that is not
  done is not reported as done. `done` and `return` take no `--json`, the same as the existing `mode` and `set`.

## Findings

### 1. A return carries the old seat's start failures and re-route to the Mail Desk seat, so the owner gets it early and the decision is marked failed although it reached a seat

`src/CcDirector.Gateway/MailDesk/MailDeskRoutedStore.cs:442-447` (the first-return branch of `Return`, which calls
`Queue` at line 585) together with `RecordStartFailure` at lines 283-289 and `Reroute` at lines 313-320.

**The harm.** A first return moves the row to a new seat (the channel's Mail Desk seat) but keeps `StartFailures` and
`RerouteCount` from the seat that returned it; neither is ever reset, and `Take` does not reset `StartFailures` on a
start that worked. The contract gives each email three failed starts and one re-route before the owner (sections 3.2
step 3 and 3.4); after a return, the Mail Desk agent gets whatever the first seat left over.

**Failure scenarios.**

- The clerk's machine refuses two starts, the third works (`StartFailures` 2). The clerk returns the email ("not an
  invoice"). The first start of the Mail Desk agent fails once - the machine is briefly away. `StartFailures` becomes 3
  and the email is handed to the owner with "its session could not be started 3 times: <error>", although the Mail
  Desk seat was tried once. And because that path calls `ToOwner` with `failDecision: true`, the decision's outcome,
  which was `seat-started` ("session ... (qa-books/clerk)"), is overwritten with `failed` - the record then says the
  email never reached a seat, when a seat had it and returned it.
- The clerk's first session ends without answering (`RerouteCount` 1); the second clerk session returns it. The Mail
  Desk agent's session then ends without answering - for example its Director restarts and the session is gone. The
  email goes straight to the owner with "its session ended twice", and the Mail Desk agent never gets the one re-route
  the contract gives a seat.

The email is not lost either way - it reaches the owner - so this is minor. Whether the counts are per email or per
seat is not written in the contract; if they are meant per seat, `Return`'s re-queue should reset both counts; if per
email, the `failDecision` overwrite of a decision that did reach a seat is still wrong. No test covers a return
followed by a failed start or an ended session.

### 2. The "records nothing" tests do not look at the columns this task adds

`src/CcDirector.Gateway.UnitTests/MailDesk/MailDeskOwnerTests.cs:151-158` (`Recorded()`, used by every
`...RecordsNothing` test through `AssertRefused` at line 160).

**The harm.** The snapshot a refusal is compared against holds state, seat, session, return note, returned count,
owner reason, done note, `ChangedUtc` and `ResolvedUtc` - but not `ReturnedUtc`, `ReturnedFrom`, `EarlierReturnedUtc`,
`EarlierReturnedFrom`, `EarlierReturnNote`, `OwnerUtc`, `StartFailures` or `RerouteCount`. The tests are named for
"records nothing" and the contract requires exactly that, but they would not see a write to most of the columns this
task added.

**Failure scenario.** A later change moves the "keep the earlier return" block in `Return` (lines 423-433) above the
ownership check, or saves a refused row while recording when the refusal happened. A refused `return` from another
session then overwrites `ReturnedFrom` and `EarlierReturnNote`, so the 08:00 email names the wrong seat and loses the
first return's note - and `Return_AnotherSession_Is403AndRecordsNothing` still passes. Today's code is correct (every
refusal returns before any change and before `SaveChanges`); only the guard is weaker than its name.
