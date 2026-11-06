# Review: Factory Inbox phase 3, Your inbox (pull request 4608)

Reviewed by a separate review session on 6 November 2026, worktree `D:\ReposFred\devthrottle-fi-p3`,
branch `factory-inbox/p3-your-inbox` at `9b05a8c00`, merge base `0a9123023` (origin/main, phase 2 merged).
The worktree was read and tested, never edited: `git status --porcelain` is empty after the review.

## Verdict

**Approve. No finding proves a harm.** The change does what the mission's sections 4, 5 and 7 ask for phase 3,
the migrations are clean and ordered, the guard and the routes agree on who may call what, every word is
folded on the Gateway, and the new tests drive the real stores so they would go red if the behaviour broke.
Four observations below are notes for the author, none of them blocks the merge.

## Scope

What was read, in full, from the branch: the Your inbox endpoints, store, fold, words and entities; the two
migrations and their designers and both model snapshots; the Factory inbox store's Decide method and its words;
the Gateway host wiring; the session key guard; the four new Gateway test classes; the Postgres provider census;
the `cc-devthrottle inbox send` command and its tests; the command line reference. Also read for context: the
Factory inbox wake and its store, the fleet message service and its limits, the authentication middleware
helpers, the session history store, the dev report liveness, naming and seat helpers.

What was run:

| Run | Result |
|---|---|
| `dotnet test CcDirector.Gateway.UnitTests --filter FullyQualifiedName~YourInbox` (the four new classes) | 80 passed, 0 failed |
| `pytest tests/test_inbox_ops.py` | 7 passed |
| The two short-help coverage tests in `test_help_and_errors_axi.py` that the one-line change touches | 2 passed |

The whole `test_help_and_errors_axi.py` file is red in this review's environment (253 failed), almost all with
`ValueError: stderr not separately captured` from the command runner, and the rest reading the live session
environment. None of them is an inbox test and the error is not reachable from this change. I did not get that
file green and I am not claiming it is; the two tests that cover the change pass.

Not done: a mutation of the worktree to confirm the "10 of 17 fail with the write disabled" claim in the pull
request body. The worktree was not to be edited. The reasoning on why the tests would go red is below.

## The checks asked for

### Migrations touch only the two tables and order after AddFactoryInboxWake

Confirmed. Both `Up` methods create `your_inbox_messages` and `your_inbox_replies` and four indexes on them, and
nothing else; both `Down` methods drop the two tables. SQLite: `20261106090441_AddYourInbox` after
`20261105041734_AddFactoryInboxWake`. Postgres: `20261106090535_AddYourInbox` after `20261105041949_AddFactoryInboxWake`.
Each designer's model is byte-identical to its project's model snapshot (a `diff` of the model bodies gives zero
lines), so there is no pending model change hiding behind these files. The snapshot diffs against origin/main add
exactly the two entities. Postgres puts collation "C" on the closed words and the exact keys (state, sender kind,
routed-to, session ids, factory), and the provider census in `PostgresProviderProofTests` lists those seven
columns, so the census test would go red if one were dropped.

### Every new route is in SessionKeyGuard where it must be, and refused where it must not

Confirmed. `POST /sessions/{sid}/inbox` is added as `case "inbox"` in the three-segment `sessions` switch, beside
`dev-reports`. Nothing under `/gateway/inbox` is added. `YourInboxSessionKeyGuardTests` proves the send is allowed
and that GET, PUT, DELETE on the send path, the four-segment shape, every one of the five owner routes, and a
DELETE on a message are refused, and that a raised session is still refused the owner's mail.

The routes refuse a second time on their own: `Owner()` returns 403 `session_key_out_of_scope` for a session key
and 403 `owner_device_required` for the machine token or no device; the send returns 403 `session_key_required`
without a session key and 403 `not_your_session` when the path names another session. `YourInboxEndpointTests`
covers each of these over the real test server.

### The Gateway owns every verdict (rule 7)

Confirmed. `YourInboxFold` produces every string a client shows: the state label, the from label, the link labels
and targets, the actions offered per state with their labels (including "Move to inbox" on an archived message),
where a reply goes, the reply labels, the session-ended sentence, the offer labels, and the unread count label
(empty at zero). The reply route answers 409 with finished `offers`, each carrying what the client needs to act
(director, folder, first prompt, or factory). A client has nothing to decide.

### No fallback code

Confirmed. A reply the fleet inbox refuses throws `InvalidOperationException` (a 500 with the reason) rather than
being quietly swallowed; a Boss decision body over the stored limit throws rather than truncating; the command
line fails with exit 1 when the Gateway's answer has no message, so an answer without the row never prints
"sent". The one place that reads like a default, a factory with no title shown by its id, is a display choice,
not a hidden failure. The reply text limit (8,000) plus the subject (200) plus the fixed prefix stays under the
fleet message cap (16,000), so the "well under the limit" comment in the store is true and the 500 path is not
reachable from the owner's own text.

### Exactly one Your inbox message per Declined, Cannot be done, Pushed back; written in the decision's transaction

Confirmed. In `FactoryInboxStore.Decide`, the row from `YourInboxStore.ForBossDecision` is added to the same
context between the decision rows and the single `SaveChanges`, inside the transaction opened for the decision.
It is added once per decision, not once per repeat merged into it. `reason!` is safe: `NeedsWhy` and
`TellsTheOwner` name the same three kinds. A refused decision throws before the transaction; a failed
`SaveChanges` rolls back both. `ForBossDecision` refuses to build a row for Delegated, To do and Noted.

### The tests would go red if the behaviour broke

Yes, for the behaviour the mission names. `YourInboxFromBossDecisionTests` drives the real Factory inbox store
(send, receive, hand, decide) and counts rows in the real Your inbox table with the expected count written as a
literal per decision kind; removing the write fails the three `expected = 1` rows, the body test, the two subject
tests, the repeats test, the twice test, the account test and the limits test. The endpoint tests boot the routes
on a test server over the real stores and the real fleet message service, so a reply that stopped reaching a
session's inbox, an ended session that got a delivery, a factory reply that did not land in the Factory inbox,
or a refusal that stopped refusing would each fail an assertion that reads the stored rows, not the response
alone. The guard tests call the real guard. The command line tests pin the route and the body as literals.

### No new query on a hot path or a poll without a bound

Confirmed. The change adds no timer, no sweep, and no per-tick work. The Decide transaction gains one insert.
The list route runs one count and one ordered `Take(limit)` (limit 1 to 500, default 100) plus one unread count
and one factory registry read; the count route runs one indexed count on `(tenant_id, State, SentAtUtc)`, which
is the query a phone would poll. The send route reads the roster, the history row, the cron run and the report
once each per call. The reply route reads liveness and, for an ended session, the history row once.

## Observations (no harm proven; for the author to weigh)

1. **A reply left in a Factory inbox from Your inbox does not call the wake's `MessageSent`.** The owner's own
   send route calls `parts.MessageSent(...)` after the store write so the wake looks at the factory when the gather
   window closes; `ReplyIntoFactoryAsync` writes through `FactoryInbox.SendAsync` and schedules nothing. The
   heartbeat covers it: `FactoriesWithWork` lists any factory with a Sent or Received unbundled message, and the
   look honours the window, so the Secretary is started or rung within the window plus at most 15 seconds. The
   cost is a lag of up to one heartbeat and one more place that must stay in step with the wake. One line in the
   host wiring (`SendToFactory` calling the wake after the write) would make the two sends identical.
2. **A reply is delivered before it is recorded.** `DeliverToSession` and `SendToFactory` run before
   `AddReply`. If the second write fails (the database write timeouts of tonight are exactly that shape), the
   reply has reached the session or the factory and the owner sees an error and no record. The mirror ordering
   (record, then deliver) would be worse: a record of a reply that never went. The present order fails towards
   the owner being told. Worth one sentence in the route's comment so the next reader does not "fix" it.
3. **`GET /gateway/inbox/count` writes no entry log line.** Every other route does. For a route a phone polls
   this is a defensible choice; saying so in a comment would stop it reading as an omission under rule 2.
4. **The command line reference names the routes.** `docs/cli-reference.md` lists the five owner routes and the
   send route for `inbox send`. That is more than the other command sections carry and will go stale if a route
   is renamed; no test ties the two together. Harmless today.

## Mission coverage, phase 3

| Mission item | Where |
|---|---|
| Store, `cc-devthrottle inbox send` | `YourInboxStore`, `inbox_ops.py`, `cli.py` |
| Unread, Read, Archived; nothing deleted | `SetState`, three states, no delete anywhere; `NothingIsDeleted_...` test |
| Inbox count | `UnreadCount`, `GET /gateway/inbox/count`, `CountLabel` |
| Who sent it, links to session, report, factory | `YourInboxSender` from the Gateway's records; `YourInboxFold.Links` |
| Reply to a factory's message goes into its Factory inbox | `ReplyIntoFactoryAsync`, as the owner's typed message, refused when archived or Boss-less |
| Reply to a running session goes to it | `DeliverToSession` as a Gateway notice into its fleet inbox |
| Reply to an ended session says so and offers a new session or the factory | 409 `session_ended` with offers; `to: "factory"` takes the offer |
| Section 7: one message for the three telling decisions, none for the other three | `YourInboxFromBossDecisionTests` |

Out of this phase and not reviewed: the morning summary as a Your inbox message (phase 5), the screens (phase 4),
the terminology entries (phase 0).

## The author's answers to the four notes (6 November 2026, on the rebase onto main)

1. **A reply left in a Factory inbox does not call the wake's `MessageSent`. Accepted.** `YourInboxEndpoints.Parts`
   gains a required `MessageSent` part, the same shape as the Factory inbox routes' own, and `ReplyIntoFactoryAsync`
   calls it after the store write with the left message's factory and sent time. The host wires it to
   `TheFactoryInboxWake().MessageSent` exactly as the owner's own send does, without waiting for the look. So the two
   sends are now identical, and the heartbeat is a backstop again rather than the only path.
   `Reply_ToAFactorysMessage_IsLeftInThatFactorysInbox_AsTheOwnersOwnTypedMessage` now asserts the wake is told once,
   with the tenant, the factory and the stored sent time; the archived-factory refusal asserts it is not told.
2. **A reply is delivered before it is recorded. Accepted, as a comment.** The order stays. A comment above the
   running-session delivery in `YourInboxEndpoints` says the order is deliberate for both routes, why the other
   order would be worse (a record of a reply that never went), and "Do not swap them."
3. **`GET /gateway/inbox/count` writes no entry log line. Accepted, as a comment.** The route stays silent; a comment
   above it says that is on purpose because a phone polls it, that a refusal is still answered with its reason, and
   that every write route logs.
4. **The command line reference names the routes. Accepted.** The paragraph in `docs/cli-reference.md` under
   `inbox send` now says what a session may and may not do in plain words - the owner's routes take only his phone or
   Cockpit, a session key and the machine token are refused, a session sends only as itself - with no route names, so
   a renamed route cannot leave it stale.

The rebase itself: the old `AddYourInbox` migrations (SQLite `20261106090441`, Postgres `20261106090535`) were dropped
and regenerated with `dotnet ef migrations add AddYourInbox` after main's `AddFactoryBossStarts`: SQLite
`20261106145000_AddYourInbox`, Postgres `20261106145006_AddYourInbox`. Both `Up` and `Down` bodies are byte-identical
to the old ones (no table shape changed), and each new designer's model is identical to its project's snapshot.

## Re-review after the migration rebase

Reviewed by a separate review session on 6 November 2026, worktree `/home/soren/ReposFred/devthrottle-fi-review-4608`
on devlinux, detached at `da645066e`. Only the two commits added since the approval above were read:
`cfca4a7b1` (the migrations regenerated after main's `AddFactoryBossStarts`) and `da645066e` (the answers to the four
notes). The worktree was read and tested, never edited apart from this section.

### Verdict

**Approve. No finding proves a harm.** The regenerated migrations are correct and complete for both databases, the
new wake call is wired exactly as the owner's own send and is tested, and the four answers do what they say.

### Scope

What was read: the six migration files of `cfca4a7b1` (both `AddYourInbox` migrations, their designers, both model
snapshots), compared against the dropped migrations at the old branch head `9b05a8c00`, against the base `cbce2401f`
and against the `AddFactoryBossStarts` designers; the `MessageSent` part added to `YourInboxEndpoints.Parts`, its call in
`ReplyIntoFactoryAsync`, its host wiring, and the owner's own send in `FactoryInboxEndpoints` and its host wiring for
comparison; `FactoryInboxWake.MessageSent`, `LookLaterAsync` and `LookSafeAsync`; the two changed tests in
`YourInboxEndpointTests`; the two new comments; the paragraph in `docs/cli-reference.md`; the newest-designer test in
`FleetOutcomeStopIdentityMigrationTests`.

What was run, on devlinux with `~/.dotnet/dotnet`, polling file watcher on, Windows targeting enabled:

| Run | Result |
|---|---|
| `dotnet test CcDirector.Gateway.UnitTests --filter FullyQualifiedName~Migration` | 38 passed, 0 failed |
| `dotnet test CcDirector.Gateway.UnitTests --filter FullyQualifiedName~YourInbox` | 80 passed, 0 failed |

Not reached: no real Postgres was started, so the Postgres `Up` was not executed against a server; the Postgres proof
here is the designer and snapshot comparison plus the newest-designer test, which diffs the designed model against the
live model for the Postgres provider with no database. The old branch's migrations were read from `9b05a8c00`, which is
reachable in this clone.

### The migrations are correct and complete

- **No table shape changed.** A `diff` of the new SQLite `20261106145000_AddYourInbox.cs` against the dropped
  `20261106090441_AddYourInbox.cs`, and of the new Postgres `20261106145006_AddYourInbox.cs` against the dropped
  `20261106090535_AddYourInbox.cs`, gives zero lines for both: the whole file, `Up` and `Down`, is byte-identical.
- **They order last.** SQLite `20261106145000` sorts after `20261106094441_AddFactoryBossStarts`; Postgres
  `20261106145006` after `20261106094559_AddFactoryBossStarts`. The old ids (`090441`, `090535`) are gone from both
  folders, so there is no duplicate `AddYourInbox` and nothing sorts between the Boss starts migration and this one.
- **Each designer carries the whole model.** The model body of each new designer is identical to its project's
  snapshot (zero diff lines, both providers). Each snapshot differs from the base `cbce2401f` by additions only
  (zero removed lines) and those additions are exactly the two entities `YourInboxMessageEntity` and
  `YourInboxReplyEntity`. Each snapshot differs from its `AddFactoryBossStarts` designer by the same two entities and
  nothing else, so the chain Boss starts plus Your inbox equals the Your inbox designer. `FactoryBossStartEntity` is
  present in both new designers. Against the old branch's snapshots the only difference is the `factory_boss_starts`
  table main added, which is what a rebase should show.
- **The live model agrees.** `TheNewestMigrationsDesigner_IsDiscovered_AndCarriesTheCurrentModel` passed for both
  providers: the newest migration is found by attribute, the model differ finds no difference between its designed
  model and the context's live model, and `HasPendingModelChanges` is false. The designer attributes name
  `GatewayDbContext` and the right migration ids, and all six files carry `ProductVersion` 9.0.2, the same as the
  Boss starts designers.

### The wake call is right and tested

- The host wires `MessageSent` to `_ = TheFactoryInboxWake().MessageSent(tenant, factory, sentAtUtc)`, the same line
  as the Factory inbox routes use, so the two sends are identical in what they tell the wake and in not waiting for
  the look.
- Discarding the task is safe: `LookLaterAsync` catches the cancellation of the delay, and `LookSafeAsync` catches
  everything else and logs it, so a scheduled look never faults an unobserved task.
- It is called after the store write and before the Your inbox reply row, which matches the deliberate
  deliver-then-record order and matches the owner's send, where the wake is told after the write.
- Both ways into a factory inbox go through `ReplyIntoFactoryAsync`: a reply to a factory's own message and the
  `to: "factory"` offer after a session has ended. Both therefore tell the wake.
- The test asserts the wake was told exactly once with the tenant, the factory and the stored message's sent time, for
  the phone and the Cockpit; the archived-factory refusal asserts it was not told. `Parts` is constructed in exactly
  two places (the host and this test class), both updated, and the project builds.

### The answers to the notes are sound

1. Wake call: accepted and done, as above.
2. Deliver before record: the comment sits above the running-session delivery, says it covers the factory reply
   below, gives the reason the other order would be worse, and says not to swap them. That is what the note asked.
3. Count route: the comment says the silence is on purpose because a phone polls it, that a refusal still carries its
   reason, and that the write routes log. That is what the note asked.
4. Command line reference: the paragraph now names no route and says in words who may call what. A renamed route
   can no longer leave it stale.

### Observations (no harm; nothing to change)

- The two ended-session tests that take the `to: "factory"` offer do not assert on the wake. They share the code path
  with the asserted test, so the wake is told there too; an assertion would only pin the shared path twice.
