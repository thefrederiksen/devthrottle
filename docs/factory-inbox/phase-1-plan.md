# Factory Inbox - phase 1 plan (Tech Lead, 4 November 2026)

Mission document: MISSION.md beside this file. It wins over this plan. Mockups: report.html.

## Section 10 open items, settled from origin/main 31db15d92

- **To-do record kind: does NOT exist.** Factory records today are documents, attachments, journal and memory
  (`Factory/Records`, `Factory/Memory`). "nothing-to-do" in `FactoryMapStore` is an activity outcome, not a list.
  Phase 1 adds a factory to-do item (its own table in the same migration). Test: a `todo` decision writes exactly
  one to-do row for that factory, and the owner's to-do read returns it.
- **Limiting a Secretary's key: SessionKeyGuard cannot do it** - it is a pure method-and-path allow list, and the
  generic session spawn is deliberately allowed to every session key. `FactorySeatDelegation.RefuseCaller` already
  refuses a non-Boss for the factory seat start. So the limit is a per-session check at EVERY start door
  (generic spawn, factory seat start, Boss start door), keyed on the caller's recorded history row marking it a
  Secretary - never on the request. Phase 2 builds it; test: a Secretary's key is refused at each start door.
  Phase 1 only records the marker (see Task A).
- **Night-start Boss rung mid-run:** phase 2 test, not phase 1.

## Two tasks, two worktrees, no shared files

Task A owns ALL C# and BOTH migrations. Task B owns ONLY Python under `tools/cc-devthrottle` and
`docs/cli-reference.md`. So migrations cannot collide. Task B builds against the route contract below with
faked HTTP (the existing test style in `tools/cc-devthrottle/tests`), and merges after Task A.

### Task A - store, migrations, routes, guard (worktree devthrottle-fi-store, branch factory-inbox/store)

- Entities + ONE migration per provider, generated in the same pull request (SQLite `Data/Migrations`, Postgres
  `CcDirector.Gateway.Migrations.Postgres/Migrations`): message, bundle, decision, factory to-do item.
- Message: tenant, factory, id, text, raw text (spoken), corrections applied, note kind (None/Good/Bad), source
  (typed/spoken), surface (phone/cockpit/session), sent at, state (Sent, Received, With the Boss, decided),
  received-by session, claim (session id + claimed at), bundle id, links (run/report/record) set by the Secretary.
- Claims lapse when the claiming session ends - modelled on the dev report action claims
  (migration AddDevReportActionClaimsAndSeatIdentity). Test: a claim held by an ended session is taken by the next.
- Decisions: delegated (session id REQUIRED and must be a session whose history ParentSessionId is the deciding
  Boss session - refuse otherwise, in words), todo (writes one to-do row), noted, declined/cannot/pushback (--why
  required). Phase 3 will add the Your inbox write for the last three; leave one clearly named seam, no stub writes.
- Every state word and trail line is decided on the Gateway (CLAUDE.md rule 7): DTOs carry finished labels.
- Refused in words: unknown, archived, Boss-less factory (reuse the checks in `Api/SpawnFactory.cs` TryStartTheBoss).
- Routes (exact shapes, Task B codes against these):
  - Owner: `POST /gateway/factory/{factory}/inbox` (send: text, note, source), `GET /gateway/factory/inbox`
    (every factory: unread counts, last message), `GET /gateway/factory/{factory}/inbox` (thread with trail),
    `GET /gateway/factory/{factory}/inbox/summary?since=` (built from the log, no agent),
    `GET /gateway/factory/{factory}/todo`.
  - Session (the caller's factory is read from its history row, never from the request):
    `GET /sessions/{sid}/factory-inbox` (new messages), `POST /sessions/{sid}/factory-inbox/receive`
    (claim + mark Received, with merges and links), `POST /sessions/{sid}/factory-inbox/bundle` (Secretary hands
    one bundle), `GET /sessions/{sid}/factory-inbox/bundle` (Boss reads its open bundle),
    `POST /sessions/{sid}/factory-inbox/{messageId}/decide` (kind, session, why).
  - SessionKeyGuard: the session routes added deliberately, each with a guard test. The owner's READS (list,
    thread, summary, todo) are allowed to a session key - a Boss reads its summary. The owner's SEND stays refused
    to a session key (a session never leaves a message as the owner; fleet messaging is out of scope), with a test.
  - If any shape must change, tell the Tech Lead BEFORE changing it - Task B depends on it.
- Gateway tests owned by phase 1 (from MISSION section 7): delegated without a started session refused; a claim
  held by an ended session taken by the next; a spoken message equals raw transcript plus only listed corrections
  (store level: raw kept beside stored text). Plus the to-do test and the guard tests above.
- Check: `.\scripts\test-local.ps1` green and `.\scripts\test-local.ps1 -Parked` green (Docker).

### Task B - cc-devthrottle factory inbox commands + docs (worktree devthrottle-fi-cli, branch factory-inbox/cli)

- No `send` command: the owner's send is refused to a session key (Task A). Commands: `list`, `thread <factory>`,
  `summary <factory> [--since]`, `todo <factory>`; seat commands `inbox new`, `receive`, `bundle` (hand / read),
  `decide <id> delegated --session <id> | todo | noted | declined --why | cannot --why | pushback --why`.
- AXI standard (`docs/axi-standard.md`): `--json` keeps its shape and every filter applies to it, unknown flag
  fails, empty says count: 0, ids never cut short.
- Documented in `docs/cli-reference.md`. Tests in `tools/cc-devthrottle/tests` with faked HTTP.
- Check: the cc-devthrottle Python tests green (they are NOT in test-local.ps1), plus `.\scripts\test-local.ps1`.

## JSON contract (FINAL, 4 November 2026 - Task A's shapes; replaces the earlier Task B proposal)

camelCase. Errors: {error: sentence, code}. Paths exactly as above.
Message: {id, factory, text, rawText, note none|good|bad, noteLabel, source typed|spoken, surface
phone|cockpit|session, sentAtUtc, state sent|received|with-boss|decided, stateLabel, mergedInto, links [{kind,ref}],
bundleId, decision {kind,label,why,sessionId,todoId,decidedBySessionId,decidedAtUtc} or null, trail
[{step,label,atUtc}]}.
GET /gateway/factory/inbox -> {count, factories [{factory,title,waiting,lastMessage}]}.
GET thread -> {factory,title,count,messages}.
GET summary?since= -> {factory,sinceUtc,untilUtc,count,line,byDecision [{kind,label,count}],items}.
GET todo -> {factory,count,items [{id,text,fromMessageId,addedBySessionId,addedAtUtc}]}.
GET /sessions/{sid}/factory-inbox -> {factory,count,messages}.
POST receive body {ids?, merges? [{id,into}], links? [{id,kind,ref}]} -> {factory,count,received}.
POST bundle body {ids?} -> {bundle {id,factory,handedBySessionId,handedAtUtc,count,items}}.
GET bundle -> {factory,count,bundles} (none open = count 0, empty list; never a 404).
POST decide body {kind delegated|todo|noted|declined|cannot|pushback, session?, why?} -> {message, todo?}.

Secretary marker: history row Factory=<f>, FactorySeat=secretary, FactoryKit=seat. The seat name "secretary" is
RESERVED: the definition check refuses a defined seat of that name (test). Phase 2 keys the start-door refusal on
this same marker.

Send body (changed by the Task A review, 5 November 2026): typed {text, note, source: typed}; spoken
{source: spoken, rawText, note} - a client never sends corrections or text for a spoken message; the Gateway applies
the account's own listed corrections (TenantGlossary, deterministic). Refusals corrections_not_accepted,
spoken_with_text. Decide adds 409 delegated_session_not_yet_known when the delegated session has no history row yet.
